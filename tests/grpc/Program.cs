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
using Grpc.Core;
using Grpc.Net.Client;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;
using OurServer = org.GraphDefined.Vanaheimr.Hermod.HTTP2.HTTP2Server;

// =============================================================================
// gRPC over our from-scratch HTTP/2 stack.
//
// gRPC is a de-facto standard (not an RFC): HTTP/2 as transport, POST to
// /{service}/{method}, content-type application/grpc, each message framed as
// [1-byte compressed-flag][4-byte big-endian length][message], and — the
// headline — the RPC status (grpc-status) travels in HTTP/2 TRAILERS after the
// response body. That is exactly what the streaming seam (HTTP2StreamingHandler
// + IHTTP2ResponseStream.CompleteAsync(trailers)) was built to enable.
//
// This hosts a Greeter service covering all four gRPC call types — unary
// SayHello (1->1), server-streaming SayHelloStream (1->N), client-streaming
// SayHelloClientStream (N->1), and bidirectional SayHelloBidi (N->N) — on our
// server, with a hand-rolled minimal protobuf codec (one string field), and
// drives it from BOTH our own HTTP2Client AND the real .NET gRPC client
// (Grpc.Net.Client) — the production-peer interop proof. The client-streaming
// and bidi legs exercise HTTP2ClientConnection.StartStreamingRequestAsync — the
// client's incremental streaming *request* path, whose absence was the last
// streaming asymmetry between our server and client.
// =============================================================================

var passed = 0;
var failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"  ✓ {name}" + (detail.Length > 0 ? $"  ({detail})" : "")); }
    else    { failed++; Console.WriteLine($"  ✗ {name}" + (detail.Length > 0 ? $"  — {detail}" : "")); }
}

// --- minimal protobuf: a message with a single string field #1 ---------------
static void WriteVarint(List<byte> buf, ulong v) { while (v >= 0x80) { buf.Add((byte) (v | 0x80)); v >>= 7; } buf.Add((byte) v); }
static ulong ReadVarint(byte[] b, ref int pos) { ulong r = 0; var sh = 0; while (true) { var by = b[pos++]; r |= (ulong) (by & 0x7F) << sh; if ((by & 0x80) == 0) break; sh += 7; } return r; }

static byte[] EncodeStr(string s)
{
    var utf8 = Encoding.UTF8.GetBytes(s);
    var buf  = new List<byte> { 0x0A };   // field 1, wire type 2 (length-delimited)
    WriteVarint(buf, (ulong) utf8.Length);
    buf.AddRange(utf8);
    return buf.ToArray();
}

static string DecodeStr(byte[] msg)
{
    var pos = 0; var val = "";
    while (pos < msg.Length)
    {
        var tag = ReadVarint(msg, ref pos); var field = (int) (tag >> 3); var wire = (int) (tag & 7);
        if (field == 1 && wire == 2) { var len = (int) ReadVarint(msg, ref pos); val = Encoding.UTF8.GetString(msg, pos, len); pos += len; }
        else switch (wire) { case 0: ReadVarint(msg, ref pos); break; case 2: pos += (int) ReadVarint(msg, ref pos); break; case 5: pos += 4; break; case 1: pos += 8; break; default: pos = msg.Length; break; }
    }
    return val;
}

// --- gRPC length-prefixed framing --------------------------------------------
static byte[] Frame(byte[] message)
{
    var f = new byte[5 + message.Length];
    f[0] = 0;   // not compressed
    BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(1, 4), (uint) message.Length);
    message.CopyTo(f, 5);
    return f;
}

// Split a complete buffer (a whole response body) into its framed messages.
static List<byte[]> Deframe(byte[] data)
{
    var msgs = new List<byte[]>(); var pos = 0;
    while (pos + 5 <= data.Length)
    {
        var len = (int) BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 1, 4));
        pos += 5;
        if (pos + len > data.Length) break;
        msgs.Add(data[pos..(pos + len)]); pos += len;
    }
    return msgs;
}

// --- the Greeter service, on our streaming seam ------------------------------
// Four method types: unary (1->1), server-streaming (1->N), client-streaming
// (N->1), and bidi (N->N). The last two exercise the client's new streaming
// *request* path — reading many request messages off IHTTP2RequestStream as they
// arrive (a GrpcMessageReader reassembles gRPC frames from DATA chunks that don't
// align with message boundaries).
HTTP2StreamingHandler greeter = async (req, resp, ct) =>
{
    var path   = req.Headers.FirstOrDefault(h => h.Name == ":path").Value ?? "";
    var reader = new GrpcMessageReader(req.ReadAsync);

    await resp.WriteHeadersAsync([(":status", "200"), ("content-type", "application/grpc")], ct);

    if (path.EndsWith("/SayHello"))   // unary
    {
        var name = DecodeStr(await reader.NextAsync(ct) ?? []);
        await resp.WriteAsync(Frame(EncodeStr($"Hello, {name}!")), ct);
        await resp.CompleteAsync([("grpc-status", "0")], ct);
    }
    else if (path.EndsWith("/SayHelloStream"))   // server-streaming
    {
        var name = DecodeStr(await reader.NextAsync(ct) ?? []);
        for (var i = 1; i <= 3; i++)
            await resp.WriteAsync(Frame(EncodeStr($"Hello #{i}, {name}!")), ct);
        await resp.CompleteAsync([("grpc-status", "0")], ct);
    }
    else if (path.EndsWith("/SayHelloClientStream"))   // client-streaming: read all, reply once
    {
        var names = new List<string>();
        byte[]? m;
        while ((m = await reader.NextAsync(ct)) is not null)
            names.Add(DecodeStr(m));
        await resp.WriteAsync(Frame(EncodeStr($"Hello, {string.Join(" and ", names)}!")), ct);
        await resp.CompleteAsync([("grpc-status", "0")], ct);
    }
    else if (path.EndsWith("/SayHelloBidi"))   // bidi: reply to each request message as it arrives
    {
        byte[]? m;
        while ((m = await reader.NextAsync(ct)) is not null)
            await resp.WriteAsync(Frame(EncodeStr($"Hello, {DecodeStr(m)}!")), ct);
        await resp.CompleteAsync([("grpc-status", "0")], ct);
    }
    else
        // gRPC status 12 = UNIMPLEMENTED (in trailers; a valid gRPC error response).
        await resp.CompleteAsync([("grpc-status", "12"), ("grpc-message", "Method not found")], ct);
};

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

const int port = 9469;

// A buffered request handler is required by the ctor but unused on the streaming path.
var server = new OurServer(IPAddress.Loopback, port, MakeCert(),
                 (s, h, b, c) => Task.FromResult<(List<(string, string)>, byte[]?)>(([(":status", "200")], null)),
                 StreamingHandler: greeter);
var serverTask = server.RunAsync();
await Task.Delay(400);

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var authority = $"localhost:{port}";

// =========================================================================
Console.WriteLine("=== gRPC via our own HTTP2Client (hand-rolled framing) ===");
{
    var conn = await HTTP2Client.ConnectAsync("localhost", port, (_, _, _, _) => true);

    // Unary SayHello.
    var resp = await conn.SendRequestAsync("POST", "https", authority, "/helloworld.Greeter/SayHello",
                   ExtraHeaders: [("content-type", "application/grpc"), ("te", "trailers")],
                   Body: Frame(EncodeStr("Ada")));
    var reply = Deframe(resp.Body);
    Check("unary: :status 200 + content-type application/grpc",
          resp.Status == 200 && (resp.HeaderValue("content-type") ?? "").StartsWith("application/grpc"));
    Check("unary: response message is 'Hello, Ada!'", reply.Count == 1 && DecodeStr(reply[0]) == "Hello, Ada!", reply.Count > 0 ? DecodeStr(reply[0]) : "(none)");
    Check("unary: grpc-status 0 in trailers", resp.Trailers.Any(t => t is { Name: "grpc-status", Value: "0" }),
          string.Join(",", resp.Trailers.Select(t => $"{t.Name}={t.Value}")));

    // Server-streaming SayHelloStream — three framed messages in one response body.
    var sresp = await conn.SendRequestAsync("POST", "https", authority, "/helloworld.Greeter/SayHelloStream",
                    ExtraHeaders: [("content-type", "application/grpc"), ("te", "trailers")],
                    Body: Frame(EncodeStr("Ada")));
    var msgs = Deframe(sresp.Body).Select(DecodeStr).ToList();
    Check("server-streaming: 3 messages received",
          msgs.SequenceEqual(["Hello #1, Ada!", "Hello #2, Ada!", "Hello #3, Ada!"]), string.Join(" | ", msgs));
    Check("server-streaming: grpc-status 0 in trailers", sresp.Trailers.Any(t => t is { Name: "grpc-status", Value: "0" }));

    // Client-streaming SayHelloClientStream — write three request messages over
    // time via the new streaming request API, then read the single reply.
    var cs = await conn.StartStreamingRequestAsync("POST", "https", authority, "/helloworld.Greeter/SayHelloClientStream",
                 ExtraHeaders: [("content-type", "application/grpc"), ("te", "trailers")]);
    foreach (var n in new[] { "Ada", "Grace", "Lin" })
        await cs.WriteAsync(Frame(EncodeStr(n)));
    await cs.CompleteRequestAsync();

    var csHead = await cs.GetResponseAsync();
    var csBody = new List<byte>();
    byte[]? csChunk;
    while ((csChunk = await cs.ReadAsync()) is not null)
        csBody.AddRange(csChunk);
    var csReply    = Deframe(csBody.ToArray()).Select(DecodeStr).ToList();
    var csTrailers = await cs.GetTrailersAsync();
    Check("client-streaming: head :status 200", csHead.Status == 200, csHead.Status.ToString());
    Check("client-streaming: single joined reply", csReply.Count == 1 && csReply[0] == "Hello, Ada and Grace and Lin!",
          csReply.Count > 0 ? csReply[0] : "(none)");
    Check("client-streaming: grpc-status 0 in trailers", csTrailers.Any(t => t is { Name: "grpc-status", Value: "0" }));

    // Bidi SayHelloBidi — true ping-pong: write a message, read its reply, repeat.
    var bd = await conn.StartStreamingRequestAsync("POST", "https", authority, "/helloworld.Greeter/SayHelloBidi",
                 ExtraHeaders: [("content-type", "application/grpc"), ("te", "trailers")]);
    var bdHead   = await bd.GetResponseAsync();   // server writes response HEADERS before reading
    var bdReader = new GrpcMessageReader(ct => new ValueTask<byte[]?>(bd.ReadAsync(ct)));
    var bdReplies = new List<string>();
    foreach (var n in new[] { "Ada", "Grace", "Lin" })
    {
        await bd.WriteAsync(Frame(EncodeStr(n)));
        var r = await bdReader.NextAsync(CancellationToken.None);
        if (r is not null) bdReplies.Add(DecodeStr(r));
    }
    await bd.CompleteRequestAsync();
    var bdEnd      = await bdReader.NextAsync(CancellationToken.None);   // drains to end-of-stream (null)
    var bdTrailers = await bd.GetTrailersAsync();
    Check("bidi: head :status 200", bdHead.Status == 200, bdHead.Status.ToString());
    Check("bidi: a reply per request message, interleaved",
          bdReplies.SequenceEqual(["Hello, Ada!", "Hello, Grace!", "Hello, Lin!"]), string.Join(" | ", bdReplies));
    Check("bidi: stream ends cleanly + grpc-status 0",
          bdEnd is null && bdTrailers.Any(t => t is { Name: "grpc-status", Value: "0" }));

    await conn.CloseAsync();
}

// =========================================================================
Console.WriteLine("=== gRPC via the real .NET client (Grpc.Net.Client) ===");
{
    var handler = new SocketsHttpHandler {
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    };
    using var channel = GrpcChannel.ForAddress($"https://localhost:{port}",
                            new GrpcChannelOptions { HttpHandler = handler });
    var invoker = channel.CreateCallInvoker();

    // Custom marshallers = our hand-rolled protobuf (no codegen). Grpc.Net.Client
    // adds/strips the 5-byte gRPC length prefix itself.
    var marsh = Marshallers.Create<string>(EncodeStr, DecodeStr);
    var opts  = new CallOptions(cancellationToken: cts.Token);

    var sayHello = new Method<string, string>(MethodType.Unary, "helloworld.Greeter", "SayHello", marsh, marsh);
    var reply = await invoker.AsyncUnaryCall(sayHello, null, opts, "World").ResponseAsync;
    Check("real gRPC unary: 'Hello, World!'", reply == "Hello, World!", reply);

    var sayHelloStream = new Method<string, string>(MethodType.ServerStreaming, "helloworld.Greeter", "SayHelloStream", marsh, marsh);
    using var call = invoker.AsyncServerStreamingCall(sayHelloStream, null, opts, "World");
    var got = new List<string>();
    while (await call.ResponseStream.MoveNext(cts.Token)) got.Add(call.ResponseStream.Current);
    Check("real gRPC server-streaming: 3 messages",
          got.SequenceEqual(["Hello #1, World!", "Hello #2, World!", "Hello #3, World!"]), string.Join(" | ", got));

    // An unknown method -> UNIMPLEMENTED (our trailer grpc-status 12).
    var unknown = new Method<string, string>(MethodType.Unary, "helloworld.Greeter", "Nope", marsh, marsh);
    var unimplemented = false;
    try { await invoker.AsyncUnaryCall(unknown, null, opts, "x").ResponseAsync; }
    catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented) { unimplemented = true; }
    Check("real gRPC: unknown method -> RpcException UNIMPLEMENTED", unimplemented);

    // Client-streaming: the real client streams N request messages, our server
    // reads them all off IHTTP2RequestStream and replies once.
    var clientStream = new Method<string, string>(MethodType.ClientStreaming, "helloworld.Greeter", "SayHelloClientStream", marsh, marsh);
    using (var csCall = invoker.AsyncClientStreamingCall(clientStream, null, opts))
    {
        foreach (var n in new[] { "Ada", "Grace" })
            await csCall.RequestStream.WriteAsync(n);
        await csCall.RequestStream.CompleteAsync();
        var csReply = await csCall.ResponseAsync;
        Check("real gRPC client-streaming: joined reply", csReply == "Hello, Ada and Grace!", csReply);
    }

    // Bidi: the real client streams request messages and reads a reply per
    // message — full duplex over our stack.
    var bidi = new Method<string, string>(MethodType.DuplexStreaming, "helloworld.Greeter", "SayHelloBidi", marsh, marsh);
    using (var bidiCall = invoker.AsyncDuplexStreamingCall(bidi, null, opts))
    {
        var bidiGot = new List<string>();
        foreach (var n in new[] { "Ada", "Grace", "Lin" })
        {
            await bidiCall.RequestStream.WriteAsync(n);
            if (await bidiCall.ResponseStream.MoveNext(cts.Token))
                bidiGot.Add(bidiCall.ResponseStream.Current);
        }
        await bidiCall.RequestStream.CompleteAsync();
        Check("real gRPC bidi: a reply per request message",
              bidiGot.SequenceEqual(["Hello, Ada!", "Hello, Grace!", "Hello, Lin!"]), string.Join(" | ", bidiGot));
    }
}

await server.StopAsync();
try { await serverTask; } catch { }

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);


// Reassembles gRPC length-prefixed messages ([1-byte flag][4-byte BE length]
// [message]) from a stream of arbitrary byte chunks (DATA frame payloads on
// either side of the wire don't align with gRPC message boundaries). Retains a
// running buffer across calls, so it works for multi-message client-streaming
// and bidi. NextAsync returns the next whole message, or null at end-of-stream.
sealed class GrpcMessageReader(Func<CancellationToken, ValueTask<byte[]?>> Read)
{

    private readonly List<byte> buffer = [];

    public async Task<byte[]?> NextAsync(CancellationToken CancellationToken)
    {
        while (true)
        {
            if (buffer.Count >= 5)
            {
                var len = (buffer[1] << 24) | (buffer[2] << 16) | (buffer[3] << 8) | buffer[4];
                if (buffer.Count >= 5 + len)
                {
                    var msg = buffer.GetRange(5, len).ToArray();
                    buffer.RemoveRange(0, 5 + len);
                    return msg;
                }
            }

            var chunk = await Read(CancellationToken);
            if (chunk is null)
                return null;   // end-of-stream (any trailing partial is discarded)
            buffer.AddRange(chunk);
        }
    }

}
