using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;
using Http2ServerImpl = org.GraphDefined.Vanaheimr.Hermod.HTTP2.HTTP2Server;

// Verifies HTTP2Server.StopAsync sends a GOAWAY to connected peers instead of
// just cancelling the token silently, and that the listener actually stops.

const int port = 8444;

using var rsa = RSA.Create(2048);
var req  = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var san  = new SubjectAlternativeNameBuilder();
san.AddDnsName("localhost");
san.AddIpAddress(IPAddress.Loopback);
req.CertificateExtensions.Add(san.Build());
var selfSigned = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
var cert = X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx, "temp"), "temp", X509KeyStorageFlags.UserKeySet);

Task<(List<(string Name, string Value)> ResponseHeaders, byte[]? ResponseBody)> Handler(
    UInt32 streamId, List<(string Name, string Value)> headers, byte[]? body, CancellationToken ct)
{
    var respBody = Encoding.UTF8.GetBytes("ok");
    return Task.FromResult<(List<(string, string)>, byte[]?)>((
        [(":status", "200"), ("content-length", "2")],
        respBody
    ));
}

var server = new Http2ServerImpl(IPAddress.Loopback, port, cert, Handler);
var serverTask = server.RunAsync();

await Task.Delay(300);   // let the listener come up

using var tcp = new TcpClient();
await tcp.ConnectAsync("127.0.0.1", port);
var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
    TargetHost           = "localhost",
    ApplicationProtocols = [SslApplicationProtocol.Http2]
});

await ssl.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"));
await ssl.WriteAsync(HTTP2Frame.CreateSettings().Serialize());
await ssl.FlushAsync();

var goawayReceived = new TaskCompletionSource<(HTTP2ErrorCode Code, uint LastStreamId)>(TaskCreationOptions.RunContinuationsAsynchronously);
var status200      = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

async Task ReadExact(byte[] buf, int len)
{
    var o = 0;
    while (o < len)
    {
        var n = await ssl.ReadAsync(buf.AsMemory(o, len - o));
        if (n == 0) throw new IOException("EOF");
        o += n;
    }
}

_ = Task.Run(async () =>
{
    var hdr = new byte[9];
    var decoder = new HPACKDecoder();
    try
    {
        while (true)
        {
            await ReadExact(hdr, 9);
            var f = HTTP2Frame.ParseHeader(hdr);
            if (f.Length > 0) { f.Payload = new byte[f.Length]; await ReadExact(f.Payload, (int) f.Length); }

            if (f.Type == HTTP2FrameType.GOAWAY)
            {
                var code = (HTTP2ErrorCode) BinaryPrimitives.ReadUInt32BigEndian(f.Payload.AsSpan(4, 4));
                var last = BinaryPrimitives.ReadUInt32BigEndian(f.Payload.AsSpan(0, 4)) & 0x7FFFFFFFu;
                goawayReceived.TrySetResult((code, last));
            }
            else if (f.Type == HTTP2FrameType.HEADERS)
            {
                var hs = decoder.DecodeHeaderBlock(f.Payload);
                if (hs.Any(h => h.Name == ":status" && h.Value == "200"))
                    status200.TrySetResult();
            }
        }
    }
    catch { /* connection closed — expected once shutdown completes */ }
});

var encoder = new HPACKEncoder();
var reqBlock = encoder.EncodeHeaderBlock(
[
    (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/")
]);

await ssl.WriteAsync(new HTTP2Frame {
    Type = HTTP2FrameType.HEADERS, StreamId = 1,
    Flags = HTTP2FrameFlags.END_HEADERS | HTTP2FrameFlags.END_STREAM, Payload = reqBlock
}.Serialize());
await ssl.FlushAsync();

await status200.Task.WaitAsync(TimeSpan.FromSeconds(5));
Console.WriteLine("[test] normal request completed -> 200 (baseline OK)");

var sw = System.Diagnostics.Stopwatch.StartNew();
var stopTask = server.StopAsync();

try
{
    var (code, lastStreamId) = await goawayReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var pass = code == HTTP2ErrorCode.NO_ERROR && lastStreamId == 1;
    Console.WriteLine($"[test] GOAWAY received after {sw.ElapsedMilliseconds} ms: " +
                      $"error={code} lastStreamId={lastStreamId}  {(pass ? "✓ PASS" : "✗ unexpected values")}");
}
catch (TimeoutException)
{
    Console.WriteLine("[test] ✗ FAIL: no GOAWAY received within 5 s (Stop() is still silent)");
}

await stopTask;
Console.WriteLine($"[test] StopAsync() completed after {sw.ElapsedMilliseconds} ms");

var listenerStopped = await Task.WhenAny(serverTask, Task.Delay(3000)) == serverTask;
Console.WriteLine(listenerStopped
    ? "[test] ✓ PASS: RunAsync() returned — listener actually stopped"
    : "[test] ✗ FAIL: RunAsync() still running 3 s after StopAsync()");
