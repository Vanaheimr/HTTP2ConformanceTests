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
// This hosts a Greeter service (unary SayHello + server-streaming
// SayHelloStream) on our server, with a hand-rolled minimal protobuf codec (one
// string field), and drives it from BOTH our own HTTP2Client AND the real
// .NET gRPC client (Grpc.Net.Client) — the production-peer interop proof.
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

// Server-side: reassemble the first framed message from the streaming request's
// DATA chunks (which don't align with gRPC message boundaries).
static async Task<byte[]?> ReadOneMessageAsync(IHTTP2RequestStream req, CancellationToken ct)
{
    var buf = new List<byte>();
    while (true)
    {
        if (buf.Count >= 5)
        {
            var len = (buf[1] << 24) | (buf[2] << 16) | (buf[3] << 8) | buf[4];
            if (buf.Count >= 5 + len) return buf.GetRange(5, len).ToArray();
        }
        var chunk = await req.ReadAsync(ct);
        if (chunk is null) return null;
        buf.AddRange(chunk);
    }
}

// --- the Greeter service, on our streaming seam ------------------------------
HTTP2StreamingHandler greeter = async (req, resp, ct) =>
{
    var path = req.Headers.FirstOrDefault(h => h.Name == ":path").Value ?? "";
    var reqBytes = await ReadOneMessageAsync(req, ct);
    var name = reqBytes is null ? "" : DecodeStr(reqBytes);

    await resp.WriteHeadersAsync([(":status", "200"), ("content-type", "application/grpc")], ct);

    if (path.EndsWith("/SayHello"))
    {
        await resp.WriteAsync(Frame(EncodeStr($"Hello, {name}!")), ct);
        await resp.CompleteAsync([("grpc-status", "0")], ct);
    }
    else if (path.EndsWith("/SayHelloStream"))
    {
        for (var i = 1; i <= 3; i++)
            await resp.WriteAsync(Frame(EncodeStr($"Hello #{i}, {name}!")), ct);
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
}

await server.StopAsync();
try { await serverTask; } catch { }

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
