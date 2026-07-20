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
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;
using OurServer = org.GraphDefined.Vanaheimr.Hermod.HTTP2.HTTP2Server;

// WebSocket / CONNECT-capable client. Drives our client's new CONNECT tunnel +
// WebSocket support against our OWN server (which already implements the server
// side of CONNECT / extended CONNECT / RFC 6455): plain-CONNECT byte loopback,
// and a full WebSocket session (text, binary, fragmentation-agnostic, ping,
// close handshake) — proving both ends of the wire are hand-rolled.

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

const int port = 9457;

Task<(List<(string, string)>, byte[]?)> Unused(uint s, List<(string Name, string Value)> h, byte[]? b, CancellationToken ct)
    => Task.FromResult<(List<(string, string)>, byte[]?)>(([(":status", "404")], null));

// Server-side CONNECT handler: WebSocket echo on extended CONNECT, raw byte
// loopback on plain CONNECT.
Task<HTTP2ConnectResult> Connect(uint sid, List<(string Name, string Value)> headers, CancellationToken ct)
{
    var protocol = headers.FirstOrDefault(h => h.Name == ":protocol").Value;
    var path     = headers.FirstOrDefault(h => h.Name == ":path").Value;

    if (protocol == "websocket" && path == "/ws-echo")
    {
        // Negotiate permessage-deflate (RFC 7692) over the production HTTP/2
        // (RFC 8441) CONNECT path exactly as the demo server does: accept the
        // client's offer, echo the acceptance back in sec-websocket-extensions,
        // and flip the framing's flag on.
        var offer   = headers.FirstOrDefault(h => h.Name == "sec-websocket-extensions").Value;
        var deflate = WebSocketDeflate.ShouldAccept(offer, out var responseExt);

        return Task.FromResult(new HTTP2ConnectResult
        {
            StatusCode   = 200,
            ExtraHeaders = deflate ? [("sec-websocket-extensions", responseExt!)] : null,
            RunAsync     = async (tunnel, ct2) =>
            {
                var ws = new WebSocketConnection(tunnel, WebSocketRole.Server, PerMessageDeflate: deflate);
                while (true)
                {
                    var msg = await ws.ReceiveAsync(ct2);
                    if (msg is null) break;
                    if (msg.Opcode == WebSocketOpcode.Text)
                        await ws.SendTextAsync(Encoding.UTF8.GetString(msg.Payload), ct2);
                    else
                        await ws.SendBinaryAsync(msg.Payload, ct2);
                }
            }
        });
    }

    if (protocol is null)   // plain CONNECT: raw byte loopback
        return Task.FromResult(new HTTP2ConnectResult
        {
            StatusCode = 200,
            RunAsync   = async (tunnel, ct2) =>
            {
                byte[]? chunk;
                while ((chunk = await tunnel.ReadAsync(ct2)) is not null)
                    await tunnel.WriteAsync(chunk, ct2);
            }
        });

    return Task.FromResult(new HTTP2ConnectResult { StatusCode = 404 });
}

var server = new OurServer(IPAddress.Loopback, port, MakeCert(), Unused, ConnectHandler: Connect);
var serverTask = server.RunAsync();
await Task.Delay(400);

RemoteCertificateValidationCallback acceptAny = (_, _, _, _) => true;

static async Task<byte[]> ReadExactTunnelAsync(IHTTP2Tunnel t, int n, CancellationToken ct)
{
    var buf = new List<byte>();
    while (buf.Count < n)
    {
        var chunk = await t.ReadAsync(ct);
        if (chunk is null) break;
        buf.AddRange(chunk);
    }
    return buf.ToArray();
}

// =========================================================================
// 1. Plain CONNECT byte loopback
// =========================================================================
Console.WriteLine("=== plain CONNECT loopback ===");
{
    var conn   = await HTTP2Client.ConnectAsync("localhost", port, acceptAny);
    var tunnel = await conn.OpenTunnelAsync("echo.internal");
    Check("plain CONNECT accepted (odd stream id)", tunnel.StreamId % 2 == 1, tunnel.StreamId.ToString());

    foreach (var payload in new[] { "hello tunnel", "second round", "third 🎉 unicode" })
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await tunnel.WriteAsync(bytes, CancellationToken.None);
        var echo = await ReadExactTunnelAsync(tunnel, bytes.Length, CancellationToken.None);
        Check($"loopback echoes '{payload}'", echo.AsSpan().SequenceEqual(bytes), Encoding.UTF8.GetString(echo));
    }

    await tunnel.CloseAsync();
    await conn.CloseAsync();
}

// =========================================================================
// 2. Extended CONNECT WebSocket session
// =========================================================================
Console.WriteLine("\n=== extended CONNECT WebSocket ===");
{
    var conn = await HTTP2Client.ConnectAsync("localhost", port, acceptAny);
    var ws   = await conn.OpenWebSocketAsync("localhost", "https", "/ws-echo");

    // Text message round trip.
    await ws.SendTextAsync("hello websocket", CancellationToken.None);
    var m1 = await ws.ReceiveAsync(CancellationToken.None);
    Check("text message echoed", m1 is { Opcode: WebSocketOpcode.Text } && Encoding.UTF8.GetString(m1.Payload) == "hello websocket",
          m1 is null ? "(null)" : Encoding.UTF8.GetString(m1.Payload));

    // Binary message round trip.
    var bin = new byte[] { 1, 2, 3, 250, 251, 252 };
    await ws.SendBinaryAsync(bin, CancellationToken.None);
    var m2 = await ws.ReceiveAsync(CancellationToken.None);
    Check("binary message echoed", m2 is { Opcode: WebSocketOpcode.Binary } && m2.Payload.AsSpan().SequenceEqual(bin),
          m2 is null ? "(null)" : m2.Payload.Length.ToString());

    // A larger text message (exercises the 16-bit length path + masking).
    var big = new string('x', 1000);
    await ws.SendTextAsync(big, CancellationToken.None);
    var m3 = await ws.ReceiveAsync(CancellationToken.None);
    Check("1000-byte text echoed", m3 is { Opcode: WebSocketOpcode.Text } && Encoding.UTF8.GetString(m3.Payload) == big,
          m3 is null ? "(null)" : m3.Payload.Length.ToString());

    // Close handshake: we send Close, the server echoes it, ReceiveAsync returns null.
    await ws.CloseAsync(1000, "bye", CancellationToken.None);
    var end = await ws.ReceiveAsync(CancellationToken.None);
    Check("close handshake completes (ReceiveAsync -> null)", end is null);

    await conn.CloseAsync();
}

// =========================================================================
// 3. Rejected CONNECT surfaces as an exception
// =========================================================================
Console.WriteLine("\n=== rejected extended CONNECT ===");
{
    var conn = await HTTP2Client.ConnectAsync("localhost", port, acceptAny);
    Exception? caught = null;
    try { await conn.OpenWebSocketAsync("localhost", "https", "/nonexistent"); }
    catch (Exception ex) { caught = ex; }
    Check("unknown WebSocket path is rejected", caught is not null, caught?.GetType().Name ?? "(none)");

    // Connection still usable for an ordinary request afterward.
    var resp = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/");
    Check("connection healthy after a rejected CONNECT", resp.Status == 404, resp.Status.ToString());
    await conn.CloseAsync();
}

// =========================================================================
// 4. Extended CONNECT WebSocket with permessage-deflate (RFC 7692) over the
//    production HTTP/2 (RFC 8441) path — offered by the client, negotiated on
//    the CONNECT handshake, both ends compressing/inflating transparently.
// =========================================================================
Console.WriteLine("\n=== WebSocket permessage-deflate (RFC 7692) ===");
{
    // First open the tunnel manually with the deflate offer, so we can prove the
    // negotiation happened on the wire — the server MUST echo the acceptance
    // back in sec-websocket-extensions. (A successful round-trip alone wouldn't
    // prove compression, since a failed negotiation falls back transparently.)
    var conn   = await HTTP2Client.ConnectAsync("localhost", port, acceptAny);
    var tunnel = await conn.OpenTunnelAsync("localhost", "websocket", "https", "/ws-echo",
                     [("sec-websocket-extensions", WebSocketDeflate.Offer)]);
    var echoed = tunnel.ResponseHeaders.FirstOrDefault(h => h.Name == "sec-websocket-extensions").Value;
    Check("server echoes permessage-deflate acceptance", WebSocketDeflate.WasAccepted(echoed), echoed ?? "(none)");

    var ws = new WebSocketConnection(tunnel, WebSocketRole.Client, PerMessageDeflate: WebSocketDeflate.WasAccepted(echoed));

    // A highly compressible text message: proves the compressed round-trip works
    // end-to-end (offer → server accept → RSV1 deflate both ways → inflate).
    var compressible = new string('A', 2000) + "🎉 unicode survives compression 🎉";
    await ws.SendTextAsync(compressible, CancellationToken.None);
    var d1 = await ws.ReceiveAsync(CancellationToken.None);
    Check("deflate text message round-trips", d1 is { Opcode: WebSocketOpcode.Text } && Encoding.UTF8.GetString(d1.Payload) == compressible,
          d1 is null ? "(null)" : $"{d1.Payload.Length} bytes");

    // A binary message with repetitive content, likewise.
    var binData = new byte[4096];
    for (var i = 0; i < binData.Length; i++) binData[i] = (byte) (i % 7);
    await ws.SendBinaryAsync(binData, CancellationToken.None);
    var d2 = await ws.ReceiveAsync(CancellationToken.None);
    Check("deflate binary message round-trips", d2 is { Opcode: WebSocketOpcode.Binary } && d2.Payload.AsSpan().SequenceEqual(binData),
          d2 is null ? "(null)" : $"{d2.Payload.Length} bytes");

    await ws.CloseAsync(1000, "bye", CancellationToken.None);
    var d3 = await ws.ReceiveAsync(CancellationToken.None);
    Check("deflate close handshake completes", d3 is null);

    await conn.CloseAsync();
}

// The convenience OpenWebSocketAsync(PerMessageDeflate: true) wrapper (offer +
// read-acceptance + flag, all in one) round-trips a compressed message too.
{
    var conn = await HTTP2Client.ConnectAsync("localhost", port, acceptAny);
    var ws   = await conn.OpenWebSocketAsync("localhost", "https", "/ws-echo", PerMessageDeflate: true);

    var msg = new string('Z', 1500);
    await ws.SendTextAsync(msg, CancellationToken.None);
    var back = await ws.ReceiveAsync(CancellationToken.None);
    Check("convenience deflate API round-trips", back is { Opcode: WebSocketOpcode.Text } && Encoding.UTF8.GetString(back.Payload) == msg,
          back is null ? "(null)" : $"{back.Payload.Length} bytes");

    await conn.CloseAsync();
}

await server.StopAsync();
try { await serverTask; } catch { }

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
