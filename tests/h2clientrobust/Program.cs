using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;

// Client-robustness tests. Each spins up a small raw HTTP/2 *mock server* that
// misbehaves in a specific way (refuses a stream, advertises a tiny
// MAX_CONCURRENT_STREAMS, sends GOAWAY, or goes silent), and checks the client
// reacts correctly: auto-retry, concurrency gating, retry-safe failure, and
// keepalive-based dead-connection detection.

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
RemoteCertificateValidationCallback acceptAny = (_, _, _, _) => true;
var cert = MakeCert();
var preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

// ---- raw frame I/O helpers -------------------------------------------------

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

static async Task WriteFrame(Stream s, HTTP2Frame f)
{
    await s.WriteAsync(f.Serialize());
    await s.FlushAsync();
}

// A mock server: accept one TLS+h2 connection, do the preface handshake
// (advertising the given SETTINGS), then hand control to a per-connection
// handler that reacts to client frames.
async Task<(TcpListener listener, Task serving)> StartMock(
    int mcs,
    Func<SslStream, HTTP2Frame, HPACKEncoder, Task> onFrame)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();

    var serving = Task.Run(async () =>
    {
        try
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate    = cert,
                ApplicationProtocols = [new SslApplicationProtocol("h2")]
            });

            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            // Read the 24-byte client preface.
            var magic = new byte[preface.Length];
            if (!await ReadExact(ssl, magic, ct.Token)) return;

            // Server preface: our SETTINGS (with the requested MCS).
            await WriteFrame(ssl, mcs > 0
                ? HTTP2Frame.CreateSettings((HTTP2SettingsParameter.MAX_CONCURRENT_STREAMS, (uint) mcs))
                : HTTP2Frame.CreateSettings());

            var encoder = new HPACKEncoder();

            while (!ct.IsCancellationRequested)
            {
                var f = await ReadFrame(ssl, ct.Token);
                if (f is null) break;

                if (f.Type == HTTP2FrameType.SETTINGS && !f.IsAck)
                    await WriteFrame(ssl, HTTP2Frame.CreateSettingsAck());

                await onFrame(ssl, f, encoder);
            }
        }
        catch { /* connection ended / cancelled */ }
    });

    return (listener, serving);
}

static int MockPort(TcpListener l) => ((IPEndPoint) l.LocalEndpoint).Port;

// Send a 200 "ok" response on a stream.
static async Task Respond200(SslStream ssl, HPACKEncoder enc, uint streamId)
{
    var block = enc.EncodeHeaderBlock([(":status", "200"), ("content-length", "2")]);
    await WriteFrame(ssl, HTTP2Frame.CreateHeaders(streamId, block, EndStream: false, EndHeaders: true));
    await WriteFrame(ssl, HTTP2Frame.CreateData(streamId, Encoding.ASCII.GetBytes("ok"), EndStream: true));
}

// =========================================================================
// 1. REFUSED_STREAM auto-retry: refuse the first stream, accept the retry
// =========================================================================
Console.WriteLine("=== REFUSED_STREAM auto-retry ===");
{
    var refusedOnce = false;
    var refusedStreamId = 0u;
    var servedStreamId = 0u;

    var (listener, serving) = await StartMock(0, async (ssl, f, enc) =>
    {
        if (f.Type != HTTP2FrameType.HEADERS) return;
        if (!refusedOnce)
        {
            refusedOnce = true;
            refusedStreamId = f.StreamId;
            await WriteFrame(ssl, HTTP2Frame.CreateRstStream(f.StreamId, HTTP2ErrorCode.REFUSED_STREAM));
        }
        else
        {
            servedStreamId = f.StreamId;
            await Respond200(ssl, enc, f.StreamId);
        }
    });

    var conn = await HTTP2Client.ConnectAsync("localhost", MockPort(listener), acceptAny);
    var resp = await conn.SendRequestAsync("GET", "https", "localhost", "/");
    Check("request succeeds despite first-stream refusal", resp.Status == 200, $"status {resp.Status}");
    Check("server refused stream 1 and served the retry on stream 3",
          refusedStreamId == 1 && servedStreamId == 3, $"refused={refusedStreamId} served={servedStreamId}");
    await conn.CloseAsync();
    listener.Stop();
}

// =========================================================================
// 2. REFUSED_STREAM beyond the retry budget -> not-processed exception
// =========================================================================
Console.WriteLine("\n=== REFUSED_STREAM past retry budget ===");
{
    var (listener, serving) = await StartMock(0, async (ssl, f, enc) =>
    {
        if (f.Type == HTTP2FrameType.HEADERS)
            await WriteFrame(ssl, HTTP2Frame.CreateRstStream(f.StreamId, HTTP2ErrorCode.REFUSED_STREAM));
    });

    var conn = await HTTP2Client.ConnectAsync("localhost", MockPort(listener), acceptAny,
        Options: new HTTP2ClientOptions { MaxRefusedStreamRetries = 2 });

    Exception? caught = null;
    try { await conn.SendRequestAsync("GET", "https", "localhost", "/"); }
    catch (Exception ex) { caught = ex; }

    Check("persistent refusal throws HTTP2RequestNotProcessedException",
          caught is HTTP2RequestNotProcessedException, caught?.GetType().Name ?? "(none)");
    await conn.CloseAsync();
    listener.Stop();
}

// =========================================================================
// 3. MAX_CONCURRENT_STREAMS=1 gating: 3 concurrent requests, never 2 open
// =========================================================================
Console.WriteLine("\n=== MAX_CONCURRENT_STREAMS gating ===");
{
    var openLock  = new object();
    var openNow   = 0;
    var maxOpen   = 0;
    var served    = 0;

    var (listener, serving) = await StartMock(1, async (ssl, f, enc) =>
    {
        if (f.Type != HTTP2FrameType.HEADERS) return;
        lock (openLock) { openNow++; maxOpen = Math.Max(maxOpen, openNow); }
        await Task.Delay(200);            // hold the stream open a while
        await Respond200(ssl, enc, f.StreamId);
        lock (openLock) { openNow--; served++; }
    });

    var conn = await HTTP2Client.ConnectAsync("localhost", MockPort(listener), acceptAny);
    var t1 = conn.SendRequestAsync("GET", "https", "localhost", "/a");
    var t2 = conn.SendRequestAsync("GET", "https", "localhost", "/b");
    var t3 = conn.SendRequestAsync("GET", "https", "localhost", "/c");
    var all = await Task.WhenAll(t1, t2, t3);

    Check("all 3 concurrent requests complete 200", all.All(r => r.Status == 200));
    Check("client never exceeded the server's MAX_CONCURRENT_STREAMS=1", maxOpen == 1, $"maxOpen={maxOpen}");
    await conn.CloseAsync();
    listener.Stop();
}

// =========================================================================
// 4. GOAWAY above lastStreamId -> retry-safe not-processed exception
// =========================================================================
Console.WriteLine("\n=== GOAWAY marks unprocessed streams retry-safe ===");
{
    var (listener, serving) = await StartMock(0, async (ssl, f, enc) =>
    {
        if (f.Type == HTTP2FrameType.HEADERS)
            // lastStreamId=0 => this stream (id 1) was NOT processed.
            await WriteFrame(ssl, HTTP2Frame.CreateGoAway(0, HTTP2ErrorCode.NO_ERROR, "go away"));
    });

    var conn = await HTTP2Client.ConnectAsync("localhost", MockPort(listener), acceptAny);
    Exception? caught = null;
    try { await conn.SendRequestAsync("GET", "https", "localhost", "/"); }
    catch (Exception ex) { caught = ex; }

    Check("GOAWAY-abandoned request throws HTTP2RequestNotProcessedException",
          caught is HTTP2RequestNotProcessedException, caught?.GetType().Name ?? "(none)");
    await conn.CloseAsync();
    listener.Stop();
}

// =========================================================================
// 5. Keepalive: a silent server is detected as dead
// =========================================================================
Console.WriteLine("\n=== keepalive detects a silent/dead connection ===");
{
    // The mock accepts the request's HEADERS but never responds, and never ACKs
    // a PING (it ignores everything after the handshake).
    var (listener, serving) = await StartMock(0, (ssl, f, enc) => Task.CompletedTask);

    var conn = await HTTP2Client.ConnectAsync("localhost", MockPort(listener), acceptAny,
        Options: new HTTP2ClientOptions
        {
            KeepAliveInterval = TimeSpan.FromMilliseconds(400),
            KeepAliveTimeout  = TimeSpan.FromMilliseconds(600)
        });

    var sw = System.Diagnostics.Stopwatch.StartNew();
    Exception? caught = null;
    try { await conn.SendRequestAsync("GET", "https", "localhost", "/"); }
    catch (Exception ex) { caught = ex; }
    sw.Stop();

    Check("request to a silent server fails (not hangs) via keepalive",
          caught is not null && sw.Elapsed < TimeSpan.FromSeconds(5), $"{sw.ElapsedMilliseconds} ms, {caught?.GetType().Name}");
    await conn.CloseAsync();
    listener.Stop();
}

// =========================================================================
// 6. Baseline: a well-behaved mock still serves a normal request
// =========================================================================
Console.WriteLine("\n=== baseline: normal request against the mock ===");
{
    var (listener, serving) = await StartMock(0, async (ssl, f, enc) =>
    {
        if (f.Type == HTTP2FrameType.HEADERS)
            await Respond200(ssl, enc, f.StreamId);
    });

    var conn = await HTTP2Client.ConnectAsync("localhost", MockPort(listener), acceptAny);
    var resp = await conn.SendRequestAsync("GET", "https", "localhost", "/");
    Check("normal request returns 200 with body", resp.Status == 200 && Encoding.ASCII.GetString(resp.Body) == "ok");
    await conn.CloseAsync();
    listener.Stop();
}

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
