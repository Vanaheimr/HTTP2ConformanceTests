using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;
using OurServer = org.GraphDefined.Vanaheimr.Hermod.HTTP2.HTTP2Server;

// Streaming bodies + response trailers (the bidirectional-streaming / gRPC
// enabler). A server with a STREAMING handler is driven by both our own client
// and .NET HttpClient (the interop proof for streamed content + trailers):
//   - server-streaming response (chunks produced over time)
//   - streamed request body read chunk-by-chunk by the handler
//   - bidirectional echo (read + write concurrently)
//   - response trailers (e.g. gRPC's grpc-status)

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

const int port = 9456;

// A required-but-unused buffered handler (streaming handler handles all requests).
Task<(List<(string, string)>, byte[]?)> Unused(uint s, List<(string Name, string Value)> h, byte[]? b, CancellationToken ct)
    => Task.FromResult<(List<(string, string)>, byte[]?)>(([(":status", "500")], null));

// The streaming handler: routes by :path.
async Task Streaming(IHTTP2RequestStream req, IHTTP2ResponseStream resp, CancellationToken ct)
{
    var path = req.Headers.First(h => h.Name == ":path").Value;

    switch (path)
    {
        case "/stream-response":
            await resp.WriteHeadersAsync([(":status", "200"), ("content-type", "text/plain")], ct);
            for (var i = 0; i < 5; i++)
            {
                await resp.WriteAsync(Encoding.UTF8.GetBytes($"chunk{i}"), ct);
                await Task.Delay(15, ct);
            }
            await resp.CompleteAsync(null, ct);
            break;

        case "/echo-stream":
            // Bidirectional: read each request chunk and echo it back as it arrives.
            await resp.WriteHeadersAsync([(":status", "200")], ct);
            byte[]? chunk;
            while ((chunk = await req.ReadAsync(ct)) is not null)
                await resp.WriteAsync(chunk, ct);
            await resp.CompleteAsync(null, ct);
            break;

        case "/trailers":
            await resp.WriteHeadersAsync([(":status", "200"), ("content-type", "text/plain")], ct);
            await resp.WriteAsync(Encoding.UTF8.GetBytes("hello"), ct);
            await resp.CompleteAsync([("grpc-status", "0"), ("x-checksum", "abc123")], ct);
            break;

        case "/count-body":
            long total = 0;
            byte[]? c;
            while ((c = await req.ReadAsync(ct)) is not null)
                total += c.Length;
            await resp.WriteHeadersAsync([(":status", "200")], ct);
            await resp.WriteAsync(Encoding.UTF8.GetBytes(total.ToString()), ct);
            // A request-trailer echo confirms request trailers were plumbed through.
            var reqTrailer = req.Trailers.FirstOrDefault(t => t.Name == "x-request-trailer").Value;
            await resp.CompleteAsync([("x-received-bytes", total.ToString()), ("x-saw-request-trailer", reqTrailer ?? "none")], ct);
            break;

        default:
            await resp.WriteHeadersAsync([(":status", "404")], ct);
            await resp.CompleteAsync(null, ct);
            break;
    }
}

var server = new OurServer(IPAddress.Loopback, port, MakeCert(), Unused, StreamingHandler: Streaming);
var serverTask = server.RunAsync();
await Task.Delay(400);

// =========================================================================
// 1. Our own client
// =========================================================================
Console.WriteLine("=== our client -> streaming server ===");
{
    var conn = await HTTP2Client.ConnectAsync("localhost", port, acceptAny);

    var sr = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/stream-response");
    Check("server-streaming response assembles correctly",
          sr.Status == 200 && Encoding.UTF8.GetString(sr.Body) == "chunk0chunk1chunk2chunk3chunk4",
          Encoding.UTF8.GetString(sr.Body));

    var echo = await conn.SendRequestAsync("POST", "https", $"localhost:{port}", "/echo-stream",
                                           Body: Encoding.UTF8.GetBytes("hello streaming world"));
    Check("streamed request body echoed back", echo.Status == 200 && Encoding.UTF8.GetString(echo.Body) == "hello streaming world",
          Encoding.UTF8.GetString(echo.Body));

    var tr = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/trailers");
    var grpcStatus = tr.Trailers.FirstOrDefault(t => t.Name == "grpc-status").Value;
    var checksum   = tr.Trailers.FirstOrDefault(t => t.Name == "x-checksum").Value;
    Check("response body + trailers received", tr.Status == 200 && Encoding.UTF8.GetString(tr.Body) == "hello",
          Encoding.UTF8.GetString(tr.Body));
    Check("response trailers exposed (grpc-status=0, x-checksum=abc123)",
          grpcStatus == "0" && checksum == "abc123", $"grpc-status={grpcStatus} x-checksum={checksum}");

    // A large streamed request body (exercises flow control on the streaming input path).
    var big = new byte[200_000];
    new Random(7).NextBytes(big);
    var bc = await conn.SendRequestAsync("POST", "https", $"localhost:{port}", "/count-body", Body: big);
    Check("large streamed request body counted correctly",
          bc.Status == 200 && Encoding.UTF8.GetString(bc.Body) == big.Length.ToString(),
          Encoding.UTF8.GetString(bc.Body));

    await conn.CloseAsync();
}

// =========================================================================
// 2. .NET HttpClient (interop: streamed content + trailers against a real client)
// =========================================================================
Console.WriteLine("\n=== .NET HttpClient -> streaming server ===");
{
    var handler = new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    };
    using var http = new HttpClient(handler) { DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
    var baseUri = $"https://localhost:{port}";

    // Server-streaming: read the response as a stream.
    {
        var resp = await http.GetAsync($"{baseUri}/stream-response", HttpCompletionOption.ResponseHeadersRead);
        var body = await resp.Content.ReadAsStringAsync();
        Check("HttpClient reads server-streamed response", (int) resp.StatusCode == 200 && body == "chunk0chunk1chunk2chunk3chunk4", body);
    }

    // Response trailers via HttpResponseMessage.TrailingHeaders.
    {
        var resp = await http.GetAsync($"{baseUri}/trailers");
        var body = await resp.Content.ReadAsStringAsync();   // trailers populate after the body is read
        var hasGrpc = resp.TrailingHeaders.TryGetValues("grpc-status", out var vals) && vals.First() == "0";
        Check("HttpClient exposes response trailers", (int) resp.StatusCode == 200 && body == "hello" && hasGrpc,
              hasGrpc ? "grpc-status=0" : "no grpc-status trailer");
    }

    // Streamed request body (unknown length -> DATA frames): a custom content
    // that writes chunks with flushes, echoed back by the server.
    {
        var content = new ChunkedContent();
        var resp = await http.PostAsync($"{baseUri}/echo-stream", content);
        var body = await resp.Content.ReadAsStringAsync();
        Check("HttpClient streamed request body echoed", (int) resp.StatusCode == 200 && body == "part0part1part2", body);
    }
}

await server.StopAsync();
try { await serverTask; } catch { }

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);


// A request body of unknown length that writes 3 chunks with flushes, so it
// goes out as multiple DATA frames rather than one buffered content-length body.
sealed class ChunkedContent : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        for (var i = 0; i < 3; i++)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"part{i}"));
            await stream.FlushAsync();
            await Task.Delay(20);
        }
    }

    protected override bool TryComputeLength(out long length) { length = 0; return false; }
}
