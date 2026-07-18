using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;

// Slowloris / timeout hardening tests. Spins up a real HTTP2Server on a private
// port with DELIBERATELY SHORT timeouts, then drives raw TCP/TLS clients that
// stall at each stage — verifying the server reclaims each one promptly instead
// of being tied up. A normal request confirms the short timeouts don't break
// legitimate traffic.

var passed = 0;
var failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"  ✓ {name}" + (detail.Length > 0 ? $"  ({detail})" : "")); }
    else    { failed++; Console.WriteLine($"  ✗ {name}" + (detail.Length > 0 ? $"  — {detail}" : "")); }
}

const int port = 9455;
var preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

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

// Short timeouts so the tests run fast but aren't flaky.
var timeouts = new HTTP2Timeouts
{
    Handshake   = TimeSpan.FromSeconds(2),
    Preface     = TimeSpan.FromSeconds(1),
    SettingsAck = TimeSpan.FromSeconds(1),
    Idle        = TimeSpan.FromSeconds(30),
    InProgress  = TimeSpan.FromSeconds(1),
};

Task<(List<(string, string)>, byte[]?)> Handler(uint sid, List<(string Name, string Value)> h, byte[]? body, CancellationToken ct)
{
    var reply = Encoding.UTF8.GetBytes("ok");
    return Task.FromResult<(List<(string, string)>, byte[]?)>(
        ([(":status", "200"), ("content-length", reply.Length.ToString())], reply));
}

var server = new HTTP2Server(IPAddress.Loopback, port, MakeCert(), Handler, Timeouts: timeouts);
var serverTask = server.RunAsync();
await Task.Delay(500);

// ---- helpers ---------------------------------------------------------------

async Task<SslStream> TlsConnectAsync()
{
    var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, port);
    var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
    {
        TargetHost           = "localhost",
        ApplicationProtocols = [SslApplicationProtocol.Http2]
    });
    return ssl;
}

// Read one frame; returns null if the connection closes or nothing arrives in time.
async Task<HTTP2Frame?> ReadFrameAsync(Stream s, TimeSpan timeout)
{
    try
    {
        using var cts = new CancellationTokenSource(timeout);
        var header = new byte[9];
        if (!await ReadExactAsync(s, header, cts.Token)) return null;
        var frame = HTTP2Frame.ParseHeader(header);
        if (frame.Length > 0)
        {
            frame.Payload = new byte[frame.Length];
            if (!await ReadExactAsync(s, frame.Payload, cts.Token)) return null;
        }
        return frame;
    }
    catch { return null; }
}

static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
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

// Returns true if the connection closes within 'within' (a read returns 0/errors).
async Task<bool> ClosesWithinAsync(Stream s, TimeSpan within)
{
    try
    {
        using var cts = new CancellationTokenSource(within);
        var buf = new byte[4096];
        while (true)
        {
            var n = await s.ReadAsync(buf, cts.Token);
            if (n == 0) return true;   // clean close
            // else keep draining (GOAWAY etc.) until close or timeout
        }
    }
    catch (OperationCanceledException) { return false; }   // still open after 'within'
    catch { return true; }                                 // reset / error == closed
}

// Drain the server preface handshake; optionally ACK the server's SETTINGS.
async Task HandshakeAsync(SslStream ssl, bool ackServerSettings)
{
    await ssl.WriteAsync(preface);
    await ssl.WriteAsync(HTTP2Frame.CreateSettings().Serialize());
    await ssl.FlushAsync();

    // Read frames until we've seen the server's (non-ACK) SETTINGS.
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < TimeSpan.FromSeconds(3))
    {
        var f = await ReadFrameAsync(ssl, TimeSpan.FromSeconds(2));
        if (f is null) break;
        if (f.Type == HTTP2FrameType.SETTINGS && !f.IsAck)
        {
            if (ackServerSettings)
            {
                await ssl.WriteAsync(HTTP2Frame.CreateSettingsAck().Serialize());
                await ssl.FlushAsync();
            }
            return;
        }
    }
}

// =========================================================================
// 1. Normal request still works with short timeouts (baseline)
// =========================================================================
Console.WriteLine("=== baseline: normal request under short timeouts ===");
{
    var ssl = await TlsConnectAsync();
    await HandshakeAsync(ssl, ackServerSettings: true);

    var enc = new HPACKEncoder();
    var block = enc.EncodeHeaderBlock(
    [
        (":method", "GET"), (":scheme", "https"), (":authority", $"localhost:{port}"), (":path", "/")
    ]);
    await ssl.WriteAsync(HTTP2Frame.CreateHeaders(1, block, EndStream: true, EndHeaders: true).Serialize());
    await ssl.FlushAsync();

    var dec = new HPACKDecoder();
    string? status = null;
    var deadline = Stopwatch.StartNew();
    while (status is null && deadline.Elapsed < TimeSpan.FromSeconds(3))
    {
        var f = await ReadFrameAsync(ssl, TimeSpan.FromSeconds(2));
        if (f is null) break;
        if (f.Type == HTTP2FrameType.HEADERS)
            status = dec.DecodeHeaderBlock(f.Payload).FirstOrDefault(h => h.Name == ":status").Value;
    }
    Check("normal GET returns :status 200", status == "200", status ?? "(none)");
    try { ssl.Close(); } catch { }
}

// =========================================================================
// 2. Stalled preface: connect + TLS, then send nothing
// =========================================================================
Console.WriteLine("\n=== stalled preface (send nothing after TLS) ===");
{
    var ssl = await TlsConnectAsync();
    var sw = Stopwatch.StartNew();
    var closed = await ClosesWithinAsync(ssl, timeouts.Preface + TimeSpan.FromSeconds(2));
    Check("connection closed after preface timeout", closed, $"{sw.ElapsedMilliseconds} ms");
    try { ssl.Close(); } catch { }
}

// =========================================================================
// 3. Partial header block: HEADERS without END_HEADERS, then silence
// =========================================================================
Console.WriteLine("\n=== partial header block (HEADERS, no END_HEADERS, then silence) ===");
{
    var ssl = await TlsConnectAsync();
    await HandshakeAsync(ssl, ackServerSettings: true);

    var enc = new HPACKEncoder();
    var block = enc.EncodeHeaderBlock([(":method", "GET"), (":scheme", "https"), (":authority", $"localhost:{port}"), (":path", "/")]);
    // END_HEADERS deliberately NOT set — server now expects CONTINUATION.
    await ssl.WriteAsync(HTTP2Frame.CreateHeaders(1, block, EndStream: false, EndHeaders: false).Serialize());
    await ssl.FlushAsync();

    var sw = Stopwatch.StartNew();
    var closed = await ClosesWithinAsync(ssl, timeouts.InProgress + TimeSpan.FromSeconds(2));
    Check("connection closed after in-progress (header block) timeout", closed, $"{sw.ElapsedMilliseconds} ms");
    try { ssl.Close(); } catch { }
}

// =========================================================================
// 4. Withheld frame payload: send a frame header, never the payload
// =========================================================================
Console.WriteLine("\n=== withheld frame payload (header claims 100 bytes, none sent) ===");
{
    var ssl = await TlsConnectAsync();
    await HandshakeAsync(ssl, ackServerSettings: true);

    // A DATA frame header on stream 1 declaring 100 payload bytes — then nothing.
    var hdr = new byte[9];
    hdr[0] = 0; hdr[1] = 0; hdr[2] = 100;          // length = 100
    hdr[3] = (byte) HTTP2FrameType.DATA;           // type = DATA
    hdr[4] = 0;                                     // flags
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(5), 1);   // stream 1
    await ssl.WriteAsync(hdr);
    await ssl.FlushAsync();

    var sw = Stopwatch.StartNew();
    var closed = await ClosesWithinAsync(ssl, timeouts.InProgress + TimeSpan.FromSeconds(2));
    Check("connection closed after in-progress (payload) timeout", closed, $"{sw.ElapsedMilliseconds} ms");
    try { ssl.Close(); } catch { }
}

// =========================================================================
// 5. SETTINGS ACK timeout: full preface but never ACK our SETTINGS
// =========================================================================
Console.WriteLine("\n=== SETTINGS ACK timeout (never ACK the server's SETTINGS) ===");
{
    var ssl = await TlsConnectAsync();
    await HandshakeAsync(ssl, ackServerSettings: false);   // deliberately no ACK

    // Look for a GOAWAY with SETTINGS_TIMEOUT before the connection closes.
    HTTP2ErrorCode? goawayCode = null;
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < timeouts.SettingsAck + TimeSpan.FromSeconds(2))
    {
        var f = await ReadFrameAsync(ssl, TimeSpan.FromSeconds(2));
        if (f is null) break;
        if (f.Type == HTTP2FrameType.GOAWAY)
        {
            goawayCode = (HTTP2ErrorCode) BinaryPrimitives.ReadUInt32BigEndian(f.Payload.AsSpan(4, 4));
            break;
        }
    }
    Check("GOAWAY with SETTINGS_TIMEOUT when ACK withheld", goawayCode == HTTP2ErrorCode.SETTINGS_TIMEOUT,
          goawayCode?.ToString() ?? "(no GOAWAY)");
    try { ssl.Close(); } catch { }
}

// =========================================================================
// 6. TLS handshake timeout: raw TCP, never start TLS
// =========================================================================
Console.WriteLine("\n=== TLS handshake timeout (raw TCP, no ClientHello) ===");
{
    var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, port);
    var raw = tcp.GetStream();
    var sw = Stopwatch.StartNew();
    var closed = await ClosesWithinAsync(raw, timeouts.Handshake + TimeSpan.FromSeconds(2));
    Check("raw TCP dropped after handshake timeout", closed, $"{sw.ElapsedMilliseconds} ms");
    try { tcp.Close(); } catch { }
}

// =========================================================================
// 7. A second normal request after all the abuse — server still healthy
// =========================================================================
Console.WriteLine("\n=== server still serving normal requests afterward ===");
{
    var ssl = await TlsConnectAsync();
    await HandshakeAsync(ssl, ackServerSettings: true);
    var enc = new HPACKEncoder();
    var block = enc.EncodeHeaderBlock([(":method", "GET"), (":scheme", "https"), (":authority", $"localhost:{port}"), (":path", "/")]);
    await ssl.WriteAsync(HTTP2Frame.CreateHeaders(1, block, EndStream: true, EndHeaders: true).Serialize());
    await ssl.FlushAsync();

    var dec = new HPACKDecoder();
    string? status = null;
    var deadline = Stopwatch.StartNew();
    while (status is null && deadline.Elapsed < TimeSpan.FromSeconds(3))
    {
        var f = await ReadFrameAsync(ssl, TimeSpan.FromSeconds(2));
        if (f is null) break;
        if (f.Type == HTTP2FrameType.HEADERS)
            status = dec.DecodeHeaderBlock(f.Payload).FirstOrDefault(h => h.Name == ":status").Value;
    }
    Check("follow-up GET still returns 200", status == "200", status ?? "(none)");
    try { ssl.Close(); } catch { }
}

await server.StopAsync();
try { await serverTask; } catch { }

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
