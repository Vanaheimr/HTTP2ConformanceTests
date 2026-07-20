/*
 * Copyright (c) 2010-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Vanaheimr Hermod <https://www.github.com/Vanaheimr/Hermod>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;
using OurServer = org.GraphDefined.Vanaheimr.Hermod.HTTP2.HTTP2Server;

// WINDOW_UPDATE batching: a raw client uploads a large request body and counts
// the flow-control frames the server sends back. The old strategy emitted two
// WINDOW_UPDATEs per DATA frame; the batched one replenishes only once
// accumulated consumption crosses half the window (and a transfer smaller than
// that half sends none at all), so the count should be a tiny fraction of the
// DATA-frame count. Also verifies the server raises the connection window above
// the 65535 default with an initial WINDOW_UPDATE.

var passed = 0;
var failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"  ✓ {name}" + (detail.Length > 0 ? $"  ({detail})" : "")); }
    else    { failed++; Console.WriteLine($"  ✗ {name}" + (detail.Length > 0 ? $"  — {detail}" : "")); }
}

static X509Certificate2 MakeCert()
{
    using var rsa = RSA.Create(2048);
    var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var san = new SubjectAlternativeNameBuilder();
    san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); san.AddIpAddress(IPAddress.IPv6Loopback);
    req.CertificateExtensions.Add(san.Build());
    var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.UserKeySet);
}

const int port = 9458;

// A handler that reads (and discards) the body and returns a tiny response, so
// the response direction needs no client-side flow control.
Task<(List<(string, string)>, byte[]?)> Handler(uint s, List<(string Name, string Value)> h, byte[]? body, CancellationToken ct)
{
    var reply = Encoding.UTF8.GetBytes("ok");
    return Task.FromResult<(List<(string, string)>, byte[]?)>(
        ([(":status", "200"), ("content-length", reply.Length.ToString())], reply));
}

var server = new OurServer(IPAddress.Loopback, port, MakeCert(), Handler);
var serverTask = server.RunAsync();
await Task.Delay(400);

var preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

static async Task<bool> ReadExact(Stream s, byte[] buf, CancellationToken ct)
{
    var off = 0;
    while (off < buf.Length)
    {
        var n = await s.ReadAsync(buf.AsMemory(off, buf.Length - off), ct);
        if (n == 0) return false;
        off += n;
    }
    return true;
}

static async Task<HTTP2Frame?> ReadFrame(Stream s, CancellationToken ct)
{
    var header = new byte[9];
    if (!await ReadExact(s, header, ct)) return null;
    var f = HTTP2Frame.ParseHeader(header);
    if (f.Length > 0)
    {
        f.Payload = new byte[f.Length];
        if (!await ReadExact(s, f.Payload, ct)) return null;
    }
    return f;
}

// =========================================================================
using var tcp = new TcpClient();
await tcp.ConnectAsync(IPAddress.Loopback, port);
var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
{
    TargetHost           = "localhost",
    ApplicationProtocols = [SslApplicationProtocol.Http2]
});

await ssl.WriteAsync(preface);
await ssl.WriteAsync(HTTP2Frame.CreateSettings().Serialize());
await ssl.FlushAsync();

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

// Drain the server preface — its SETTINGS plus the initial connection-window
// bump (which follows the SETTINGS). Loop until we've seen both.
long startupConnectionBump = 0;
var  sawSettings           = false;
while (startupConnectionBump == 0 || !sawSettings)
{
    var f = await ReadFrame(ssl, cts.Token);
    if (f is null) break;
    if (f.Type == HTTP2FrameType.WINDOW_UPDATE && f.StreamId == 0)
        startupConnectionBump = BinaryPrimitives.ReadUInt32BigEndian(f.Payload) & 0x7FFFFFFFu;
    if (f.Type == HTTP2FrameType.SETTINGS && !f.IsAck)
    {
        sawSettings = true;
        await ssl.WriteAsync(HTTP2Frame.CreateSettingsAck().Serialize());
        await ssl.FlushAsync();
    }
}

Check("server raised the connection window at startup (WINDOW_UPDATE > 65535)",
      startupConnectionBump > 65535, $"+{startupConnectionBump}");

// POST an 800 KiB body as 50 × 16 KiB DATA frames (under the 1 MiB window, so no
// client-side flow control is needed to complete the send).
const int frameSize   = 16384;
const int frameCount  = 50;
const int bodyLength  = frameSize * frameCount;   // 800 KiB

var encoder = new HPACKEncoder();
var headerBlock = encoder.EncodeHeaderBlock(
[
    (":method", "POST"), (":scheme", "https"), (":authority", $"localhost:{port}"),
    (":path", "/upload"), ("content-length", bodyLength.ToString())
]);
await ssl.WriteAsync(HTTP2Frame.CreateHeaders(1, headerBlock, EndStream: false, EndHeaders: true).Serialize());

var chunk = new byte[frameSize];
new Random(3).NextBytes(chunk);
for (var i = 0; i < frameCount; i++)
    await ssl.WriteAsync(HTTP2Frame.CreateData(1, chunk, EndStream: i == frameCount - 1).Serialize());
await ssl.FlushAsync();

// Read the response, counting WINDOW_UPDATE frames the server sends.
var windowUpdates = 0;
string? status = null;
while (status is null)
{
    var f = await ReadFrame(ssl, cts.Token);
    if (f is null) break;
    if (f.Type == HTTP2FrameType.WINDOW_UPDATE)
        windowUpdates++;
    else if (f.Type == HTTP2FrameType.HEADERS)
    {
        var dec = new HPACKDecoder();
        status = dec.DecodeHeaderBlock(f.Payload).FirstOrDefault(h => h.Name == ":status").Value;
    }
}

Check("large upload succeeds (:status 200)", status == "200", status ?? "(none)");
Check($"WINDOW_UPDATEs are batched (<= 10 for {frameCount} DATA frames)", windowUpdates <= 10,
      $"{windowUpdates} WINDOW_UPDATEs for {frameCount} DATA frames");
Check("far fewer WINDOW_UPDATEs than the old 2-per-DATA-frame strategy would send",
      windowUpdates < frameCount, $"{windowUpdates} vs old ~{frameCount * 2}");

try { ssl.Close(); } catch { }
await server.StopAsync();
try { await serverTask; } catch { }

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
