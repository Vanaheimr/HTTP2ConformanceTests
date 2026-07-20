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

// RFC polish checks — three MUST-level details h2spec does not cover:
//
//  1. RFC 9113 Section 6.1: the ENTIRE DATA payload counts against flow
//     control, including the Pad Length byte and the padding itself. Verified
//     by uploading padded DATA and asserting the first WINDOW_UPDATE fires at
//     the padded (not the stripped) byte count.
//  2. RFC 9113 Section 6.9: DATA on a closed stream — answered with a mere
//     stream error while the connection lives — must still be counted against
//     and returned to the CONNECTION flow-control window. Verified by spraying
//     DATA at a closed stream and asserting a connection WINDOW_UPDATE credits
//     it back (while each frame still draws RST_STREAM/STREAM_CLOSED and the
//     connection stays usable).
//  3. RFC 9113 Section 8.2.3: multiple cookie field lines ("crumbled" for
//     HPACK efficiency) must be reassembled into a single "; "-joined field
//     before reaching a generic HTTP application.

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

const int port = 9464;

// /cookie echoes the received cookie header line(s), "|"-separated — so a
// request whose crumbs were NOT reassembled would show up as "a=1|b=2".
// Everything else discards the body and answers "ok".
Task<(List<(string, string)>, byte[]?)> Handler(uint s, List<(string Name, string Value)> h, byte[]? body, CancellationToken ct)
{
    var path  = h.First(x => x.Name == ":path").Value;
    var reply = path == "/cookie"
                    ? Encoding.UTF8.GetBytes(String.Join("|", h.Where(x => x.Name == "cookie").Select(x => x.Value)))
                    : Encoding.UTF8.GetBytes("ok");
    return Task.FromResult<(List<(string, string)>, byte[]?)>(
        ([(":status", "200"), ("content-length", reply.Length.ToString())], reply));
}

var server = new OurServer(IPAddress.Loopback, port, MakeCert(), Handler);
var serverTask = server.RunAsync();
await Task.Delay(400);

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

static async Task<SslStream> OpenRawConnection(CancellationToken ct)
{
    var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
    var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
    {
        TargetHost           = "localhost",
        ApplicationProtocols = [SslApplicationProtocol.Http2]
    }, ct);

    var preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
    await ssl.WriteAsync(preface, ct);
    await ssl.WriteAsync(HTTP2Frame.CreateSettings().Serialize(), ct);
    await ssl.FlushAsync(ct);

    // Drain the server preface: its SETTINGS (ack it) plus the startup
    // connection-window bump, so later WINDOW_UPDATE observations are clean.
    var sawSettings = false;
    var sawBump     = false;
    while (!sawSettings || !sawBump)
    {
        var f = await ReadFrame(ssl, ct) ?? throw new InvalidOperationException("connection ended during preface");
        if (f.Type == HTTP2FrameType.WINDOW_UPDATE && f.StreamId == 0)
            sawBump = true;
        if (f.Type == HTTP2FrameType.SETTINGS && !f.IsAck)
        {
            sawSettings = true;
            await ssl.WriteAsync(HTTP2Frame.CreateSettingsAck().Serialize(), ct);
            await ssl.FlushAsync(ct);
        }
    }
    return ssl;
}

// A padded DATA frame: [Pad Length][data][padding], PADDED flag set. The
// padding contributes to flow control (Section 6.1) but not to the body.
static HTTP2Frame CreatePaddedData(uint streamId, byte[] data, byte padLength, bool endStream)
{
    var payload = new byte[1 + data.Length + padLength];
    payload[0] = padLength;
    data.CopyTo(payload, 1);
    var f = HTTP2Frame.CreateData(streamId, payload, endStream);
    f.Flags |= HTTP2FrameFlags.PADDED;
    return f;
}

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

const int halfWindow = 1_048_576 / 2;   // half of the advertised 1 MiB initial window

// =========================================================================
Console.WriteLine("--- 1. padded DATA counts fully against flow control (Section 6.1) ---");
{
    // 33 padded frames of 16384 payload each: 16128 data + 1 pad-length byte
    // + 255 padding. Padded bytes cross the half-window replenish threshold
    // (524288) at frame 32 exactly (32 x 16384); stripped data alone would
    // only cross at frame 33 (33 x 16128 = 532224). So the increment of the
    // FIRST WINDOW_UPDATE tells us which length was accounted:
    //   524288 -> padding counted (correct);  532224 -> stripped-only (bug).
    const int  dataSize   = 16128;
    const byte padLength  = 255;
    const int  frameCount = 33;

    var ssl = await OpenRawConnection(cts.Token);

    var encoder = new HPACKEncoder();
    var headerBlock = encoder.EncodeHeaderBlock(
    [
        (":method", "POST"), (":scheme", "https"), (":authority", $"localhost:{port}"),
        (":path", "/upload"), ("content-length", (dataSize * frameCount).ToString())
    ]);
    await ssl.WriteAsync(HTTP2Frame.CreateHeaders(1, headerBlock, EndStream: false, EndHeaders: true).Serialize(), cts.Token);

    var data = new byte[dataSize];
    new Random(7).NextBytes(data);
    for (var i = 0; i < frameCount; i++)
        await ssl.WriteAsync(CreatePaddedData(1, data, padLength, endStream: i == frameCount - 1).Serialize(), cts.Token);
    await ssl.FlushAsync(cts.Token);

    long    firstStreamInc = 0, firstConnInc = 0;
    string? status         = null;
    while (status is null)
    {
        var f = await ReadFrame(ssl, cts.Token);
        if (f is null) break;
        if (f.Type == HTTP2FrameType.WINDOW_UPDATE)
        {
            var inc = BinaryPrimitives.ReadUInt32BigEndian(f.Payload) & 0x7FFFFFFFu;
            if (f.StreamId == 1 && firstStreamInc == 0) firstStreamInc = inc;
            if (f.StreamId == 0 && firstConnInc   == 0) firstConnInc   = inc;
        }
        else if (f.Type == HTTP2FrameType.HEADERS)
            status = new HPACKDecoder().DecodeHeaderBlock(f.Payload).FirstOrDefault(h => h.Name == ":status").Value;
    }

    Check("padded upload succeeds (:status 200, content-length matches stripped data)",
          status == "200", status ?? "(none)");
    Check($"first stream WINDOW_UPDATE at the padded byte count ({halfWindow})",
          firstStreamInc == halfWindow, $"increment {firstStreamInc}");
    Check($"first connection WINDOW_UPDATE at the padded byte count ({halfWindow})",
          firstConnInc == halfWindow, $"increment {firstConnInc}");

    try { ssl.Close(); } catch { }
}

// =========================================================================
Console.WriteLine("--- 2. DATA on a closed stream still replenishes the connection window (Section 6.9) ---");
{
    var ssl     = await OpenRawConnection(cts.Token);
    var decoder = new HPACKDecoder();   // one decoder for the whole connection
    var encoder = new HPACKEncoder();   // one encoder for the whole connection

    // Open + cleanly finish stream 1, so it ends up closed but known.
    var headerBlock = encoder.EncodeHeaderBlock(
    [
        (":method", "GET"), (":scheme", "https"), (":authority", $"localhost:{port}"), (":path", "/")
    ]);
    await ssl.WriteAsync(HTTP2Frame.CreateHeaders(1, headerBlock, EndStream: true, EndHeaders: true).Serialize(), cts.Token);
    await ssl.FlushAsync(cts.Token);

    while (true)
    {
        var f = await ReadFrame(ssl, cts.Token) ?? throw new InvalidOperationException("connection ended");
        if (f.Type == HTTP2FrameType.HEADERS) decoder.DecodeHeaderBlock(f.Payload);
        if (f.StreamId == 1 && f.EndStream) break;
    }

    // Spray 33 x 16 KiB DATA at the now-closed stream 1 (540672 bytes — past
    // the half-window threshold of 524288), then a fresh request on stream 3.
    // Correct behavior: every spray frame draws RST_STREAM/STREAM_CLOSED, a
    // connection WINDOW_UPDATE credits the sprayed bytes back at frame 32,
    // and stream 3 still completes normally.
    const int sprayFrames = 33;
    var chunk = new byte[16384];
    for (var i = 0; i < sprayFrames; i++)
        await ssl.WriteAsync(HTTP2Frame.CreateData(1, chunk, EndStream: false).Serialize(), cts.Token);

    var headerBlock3 = encoder.EncodeHeaderBlock(
    [
        (":method", "GET"), (":scheme", "https"), (":authority", $"localhost:{port}"), (":path", "/")
    ]);
    await ssl.WriteAsync(HTTP2Frame.CreateHeaders(3, headerBlock3, EndStream: true, EndHeaders: true).Serialize(), cts.Token);
    await ssl.FlushAsync(cts.Token);

    var     rstStreamClosed = 0;
    long    connInc         = 0;
    string? status3         = null;
    while (status3 is null)
    {
        var f = await ReadFrame(ssl, cts.Token);
        if (f is null) break;
        if (f.Type == HTTP2FrameType.RST_STREAM && f.StreamId == 1 &&
            (BinaryPrimitives.ReadUInt32BigEndian(f.Payload) == (uint) HTTP2ErrorCode.STREAM_CLOSED))
            rstStreamClosed++;
        if (f.Type == HTTP2FrameType.WINDOW_UPDATE && f.StreamId == 0 && connInc == 0)
            connInc = BinaryPrimitives.ReadUInt32BigEndian(f.Payload) & 0x7FFFFFFFu;
        if (f.Type == HTTP2FrameType.HEADERS && f.StreamId == 3)
            status3 = decoder.DecodeHeaderBlock(f.Payload).FirstOrDefault(h => h.Name == ":status").Value;
    }

    Check("closed-stream DATA draws RST_STREAM/STREAM_CLOSED", rstStreamClosed >= 1, $"{rstStreamClosed} RSTs");
    Check($"closed-stream DATA is credited back to the connection window ({halfWindow})",
          connInc == halfWindow, $"increment {connInc}");
    Check("connection still usable afterwards (stream 3 -> 200)", status3 == "200", status3 ?? "(none)");

    try { ssl.Close(); } catch { }
}

// =========================================================================
Console.WriteLine("--- 3. crumbled cookie headers are reassembled (Section 8.2.3) ---");
{
    var client = await HTTP2Client.ConnectAsync("localhost", port, (_, _, _, _) => true);

    var two = await client.SendRequestAsync("GET", "https", $"localhost:{port}", "/cookie",
                  ExtraHeaders: [("cookie", "a=1"), ("cookie", "b=2")]);
    var twoBody = Encoding.UTF8.GetString(two.Body);
    Check("two cookie crumbs arrive as ONE \"; \"-joined field",
          two.Status == 200 && twoBody == "a=1; b=2", $"handler saw: \"{twoBody}\"");

    var one = await client.SendRequestAsync("GET", "https", $"localhost:{port}", "/cookie",
                  ExtraHeaders: [("cookie", "x=9")]);
    var oneBody = Encoding.UTF8.GetString(one.Body);
    Check("a single cookie line stays untouched",
          one.Status == 200 && oneBody == "x=9", $"handler saw: \"{oneBody}\"");

    await client.CloseAsync();
}

await server.StopAsync();
try { await serverTask; } catch { }

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
