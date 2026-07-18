using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;
using OurServer = org.GraphDefined.Vanaheimr.Hermod.HTTP2.HTTP2Server;

// Consumption-driven backpressure + bounded buffered body (F3).
//
//  1. Streaming/tunnel receive window is returned only as the handler CONSUMES
//     body chunks, not on receipt — so a handler that hasn't read yet leaves
//     the peer's window depleted (no WINDOW_UPDATE) instead of buffering the
//     body without bound. Verified with a gate-held streaming handler: while
//     gated, a >half-window upload draws ZERO WINDOW_UPDATEs (the old
//     replenish-on-receipt strategy would have emitted one); once the gate
//     opens and the handler drains, the window is credited back.
//  2. The buffered path (no streaming handler, whole body handed over at
//     END_STREAM) has no incremental consumer, so it is bounded by
//     MaxRequestBodySize instead: an over-cap body — declared up front, or
//     discovered as DATA accumulates — resets the stream (ENHANCE_YOUR_CALM)
//     while the connection stays usable.

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

static async Task<SslStream> Connect(int port, CancellationToken ct)
{
    var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
    var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
    {
        TargetHost           = "localhost",
        ApplicationProtocols = [SslApplicationProtocol.Http2]
    }, ct);

    await ssl.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"), ct);
    await ssl.WriteAsync(HTTP2Frame.CreateSettings().Serialize(), ct);
    await ssl.FlushAsync(ct);

    // Drain the server preface (SETTINGS + startup connection-window bump), ack.
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

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

// =========================================================================
Console.WriteLine("--- 1. streaming: window is returned on consumption, not receipt ---");
{
    const int streamingPort = 9465;
    const int halfWindow     = 1_048_576 / 2;   // half the 1 MiB initial stream window

    // A streaming handler held shut by a gate: it consumes nothing until the
    // test opens the gate, then drains the whole body and replies 200.
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    HTTP2StreamingHandler streamingHandler = async (req, resp, ct) =>
    {
        await gate.Task.WaitAsync(ct);
        while (await req.ReadAsync(ct) is not null) { }   // drain
        await resp.WriteHeadersAsync([(":status", "200"), ("content-length", "2")], ct);
        await resp.WriteAsync(Encoding.UTF8.GetBytes("ok"), ct);
        await resp.CompleteAsync(null, ct);
    };

    var server = new OurServer(IPAddress.Loopback, streamingPort, MakeCert(),
                     // Buffered handler is unused on the streaming path but required by the ctor.
                     (s, h, b, c) => Task.FromResult<(List<(string, string)>, byte[]?)>(([(":status", "200")], null)),
                     StreamingHandler: streamingHandler);
    var serverTask = server.RunAsync();
    await Task.Delay(400);

    var ssl = await Connect(streamingPort, cts.Token);

    var encoder = new HPACKEncoder();
    var headerBlock = encoder.EncodeHeaderBlock(
    [
        (":method", "POST"), (":scheme", "https"), (":authority", $"localhost:{streamingPort}"), (":path", "/stream")
    ]);
    // Streaming request, body to follow (no END_STREAM, no content-length).
    await ssl.WriteAsync(HTTP2Frame.CreateHeaders(1, headerBlock, EndStream: false, EndHeaders: true).Serialize(), cts.Token);

    // Upload 34 x 16 KiB = 544 KiB — past half the window (524288) but under the
    // full 1 MiB window, so the raw client needs no WINDOW_UPDATE to send it all,
    // and the OLD replenish-on-receipt strategy WOULD have emitted a stream
    // WINDOW_UPDATE once the accumulator crossed 524288.
    const int frameSize  = 16384;
    const int frameCount = 34;
    var chunk = new byte[frameSize];
    new Random(11).NextBytes(chunk);
    for (var i = 0; i < frameCount; i++)
        await ssl.WriteAsync(HTTP2Frame.CreateData(1, chunk, EndStream: false).Serialize(), cts.Token);
    await ssl.FlushAsync(cts.Token);

    // Phase 1: gate still shut → handler consumed nothing → assert NO
    // WINDOW_UPDATE arrives within a generous window. Read frames until a short
    // idle timeout expires; any WINDOW_UPDATE here is a failure.
    var windowUpdatesWhileGated = 0;
    try
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        idle.CancelAfter(TimeSpan.FromMilliseconds(700));
        while (true)
        {
            var f = await ReadFrame(ssl, idle.Token);
            if (f is null) break;
            if (f.Type == HTTP2FrameType.WINDOW_UPDATE) windowUpdatesWhileGated++;
        }
    }
    catch (OperationCanceledException) { /* expected: idle timeout, no frames */ }

    Check("no WINDOW_UPDATE while the handler has consumed nothing (backpressure holds)",
          windowUpdatesWhileGated == 0, $"{windowUpdatesWhileGated} WINDOW_UPDATE(s)");

    // Phase 2: open the gate → handler drains all 544 KiB → window is credited
    // back. Then end the request so the handler completes with 200.
    gate.SetResult();
    await ssl.WriteAsync(HTTP2Frame.CreateData(1, [], EndStream: true).Serialize(), cts.Token);
    await ssl.FlushAsync(cts.Token);

    long    credited = 0;
    string? status   = null;
    while (status is null)
    {
        var f = await ReadFrame(ssl, cts.Token);
        if (f is null) break;
        if (f.Type == HTTP2FrameType.WINDOW_UPDATE)
            credited += BinaryPrimitives.ReadUInt32BigEndian(f.Payload) & 0x7FFFFFFFu;
        else if (f.Type == HTTP2FrameType.HEADERS)
            status = new HPACKDecoder().DecodeHeaderBlock(f.Payload).FirstOrDefault(h => h.Name == ":status").Value;
    }

    Check("window is credited back once the handler consumes the body",
          credited >= halfWindow, $"credited {credited} bytes after the gate opened");
    Check("streaming request completes (:status 200)", status == "200", status ?? "(none)");

    try { ssl.Close(); } catch { }
    await server.StopAsync();
    try { await serverTask; } catch { }
}

// =========================================================================
Console.WriteLine("--- 2. buffered body is bounded by MaxRequestBodySize ---");
{
    const int bufferedPort = 9466;
    const int cap          = 1024;   // tiny cap for the test

    // Buffered handler: echoes the received body length.
    var server = new OurServer(IPAddress.Loopback, bufferedPort, MakeCert(),
                     (s, h, body, c) =>
                     {
                         var reply = Encoding.UTF8.GetBytes($"got {body?.Length ?? 0}");
                         return Task.FromResult<(List<(string, string)>, byte[]?)>(
                             ([(":status", "200"), ("content-length", reply.Length.ToString())], reply));
                     },
                     MaxRequestBodySize: cap);
    var serverTask = server.RunAsync();
    await Task.Delay(400);

    var ssl     = await Connect(bufferedPort, cts.Token);
    var encoder = new HPACKEncoder();

    (List<(string, string)> Headers, uint StreamId) Req(string method, uint id, string? contentLength, bool endStream)
    {
        var hdrs = new List<(string, string)>
        {
            (":method", method), (":scheme", "https"), (":authority", $"localhost:{bufferedPort}"), (":path", "/upload")
        };
        if (contentLength is not null) hdrs.Add(("content-length", contentLength));
        return (hdrs, id);
    }

    async Task<(byte? RstCode, string? Status)> DriveResponse(uint streamId)
    {
        while (true)
        {
            var f = await ReadFrame(ssl, cts.Token);
            if (f is null) return (null, null);
            if (f.Type == HTTP2FrameType.RST_STREAM && f.StreamId == streamId)
                return ((byte) (BinaryPrimitives.ReadUInt32BigEndian(f.Payload)), null);
            if (f.Type == HTTP2FrameType.HEADERS && f.StreamId == streamId)
                return (null, new HPACKDecoder().DecodeHeaderBlock(f.Payload).FirstOrDefault(h => h.Name == ":status").Value);
        }
    }

    // (a) Declared content-length over the cap → refused up front.
    var (h1, id1) = Req("POST", 1, (cap * 100).ToString(), endStream: false);
    await ssl.WriteAsync(HTTP2Frame.CreateHeaders(id1, encoder.EncodeHeaderBlock(h1), EndStream: false, EndHeaders: true).Serialize(), cts.Token);
    await ssl.FlushAsync(cts.Token);
    var (rst1, _) = await DriveResponse(id1);
    Check("over-cap declared content-length is refused (RST_STREAM/ENHANCE_YOUR_CALM)",
          rst1 == (byte) HTTP2ErrorCode.ENHANCE_YOUR_CALM, $"RST code {rst1}");

    // (b) Undeclared length, body streamed past the cap → refused as it accumulates.
    var (h2, id2) = Req("POST", 3, null, endStream: false);
    await ssl.WriteAsync(HTTP2Frame.CreateHeaders(id2, encoder.EncodeHeaderBlock(h2), EndStream: false, EndHeaders: true).Serialize(), cts.Token);
    var body = new byte[512];
    new Random(5).NextBytes(body);
    // 4 x 512 = 2048 bytes > 1024 cap; no END_STREAM (the reset should fire mid-stream).
    for (var i = 0; i < 4; i++)
        await ssl.WriteAsync(HTTP2Frame.CreateData(id2, body, EndStream: false).Serialize(), cts.Token);
    await ssl.FlushAsync(cts.Token);
    var (rst2, _) = await DriveResponse(id2);
    Check("over-cap streamed body is refused (RST_STREAM/ENHANCE_YOUR_CALM)",
          rst2 == (byte) HTTP2ErrorCode.ENHANCE_YOUR_CALM, $"RST code {rst2}");

    // (c) A body under the cap still succeeds, and the connection is still usable.
    var (h3, id3) = Req("POST", 5, "10", endStream: false);
    await ssl.WriteAsync(HTTP2Frame.CreateHeaders(id3, encoder.EncodeHeaderBlock(h3), EndStream: false, EndHeaders: true).Serialize(), cts.Token);
    await ssl.WriteAsync(HTTP2Frame.CreateData(id3, Encoding.UTF8.GetBytes("0123456789"), EndStream: true).Serialize(), cts.Token);
    await ssl.FlushAsync(cts.Token);
    var (_, status3) = await DriveResponse(id3);
    Check("an under-cap body still succeeds (connection stayed usable)", status3 == "200", status3 ?? "(none)");

    try { ssl.Close(); } catch { }
    await server.StopAsync();
    try { await serverTask; } catch { }
}

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
