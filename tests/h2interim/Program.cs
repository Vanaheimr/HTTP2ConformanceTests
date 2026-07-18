using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;
using OurServer = org.GraphDefined.Vanaheimr.Hermod.HTTP2.HTTP2Server;

// 1xx interim responses (RFC 9110 §15.2): automatic 100-continue (server sends
// :status 100 when a body-bearing request carries Expect: 100-continue), and
// 103 Early Hints (RFC 8297) sent by a streaming handler before the final
// response. Verified against our own client (which surfaces interim responses
// via HTTP2Response.InformationalResponses) and .NET HttpClient (ExpectContinue).

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

// -------------------------------------------------------------------------
// Server 1: buffered handler (echoes the body) — exercises automatic 100-continue.
// -------------------------------------------------------------------------
const int bufferedPort = 9460;
Task<(List<(string, string)>, byte[]?)> Echo(uint s, List<(string Name, string Value)> h, byte[]? body, CancellationToken ct)
{
    var b = body ?? [];
    return Task.FromResult<(List<(string, string)>, byte[]?)>(
        ([(":status", "200"), ("content-length", b.Length.ToString())], b));
}
var bufferedServer = new OurServer(IPAddress.Loopback, bufferedPort, MakeCert(), Echo);
var bufferedTask = bufferedServer.RunAsync();

// -------------------------------------------------------------------------
// Server 2: streaming handler — sends 103 Early Hints before the 200.
// -------------------------------------------------------------------------
const int streamingPort = 9461;
Task<(List<(string, string)>, byte[]?)> Unused(uint s, List<(string Name, string Value)> h, byte[]? body, CancellationToken ct)
    => Task.FromResult<(List<(string, string)>, byte[]?)>(([(":status", "404")], null));

async Task Streaming(IHTTP2RequestStream req, IHTTP2ResponseStream resp, CancellationToken ct)
{
    // 103 Early Hints with Link preload headers, then the final response.
    await resp.WriteInterimResponseAsync(103,
        [("link", "</style.css>; rel=preload; as=style"), ("link", "</app.js>; rel=preload; as=script")], ct);
    await resp.WriteHeadersAsync([(":status", "200"), ("content-type", "text/html")], ct);
    await resp.WriteAsync(Encoding.UTF8.GetBytes("<html>hi</html>"), ct);
    await resp.CompleteAsync(null, ct);
}
var streamingServer = new OurServer(IPAddress.Loopback, streamingPort, MakeCert(), Unused, StreamingHandler: Streaming);
var streamingTask = streamingServer.RunAsync();

await Task.Delay(500);

// =========================================================================
// 1. Our client — 100-continue
// =========================================================================
Console.WriteLine("=== our client: 100-continue ===");
{
    var conn = await HTTP2Client.ConnectAsync("localhost", bufferedPort, acceptAny);

    var body = Encoding.UTF8.GetBytes("the request body");
    var resp = await conn.SendRequestAsync("POST", "https", $"localhost:{bufferedPort}", "/echo",
        new List<(string, string)> { ("expect", "100-continue") }, body);

    Check("request succeeds (body echoed)", resp.Status == 200 && Encoding.UTF8.GetString(resp.Body) == "the request body");
    Check("received an interim 100 Continue",
          resp.InformationalResponses.Any(i => i.Status == 100),
          string.Join(",", resp.InformationalResponses.Select(i => i.Status)));

    // A request WITHOUT expect gets no interim 100.
    var plain = await conn.SendRequestAsync("POST", "https", $"localhost:{bufferedPort}", "/echo", null, body);
    Check("no expect -> no interim 100", plain.InformationalResponses.All(i => i.Status != 100));

    await conn.CloseAsync();
}

// =========================================================================
// 2. Our client — 103 Early Hints
// =========================================================================
Console.WriteLine("\n=== our client: 103 Early Hints ===");
{
    var conn = await HTTP2Client.ConnectAsync("localhost", streamingPort, acceptAny);
    var resp = await conn.SendRequestAsync("GET", "https", $"localhost:{streamingPort}", "/page");

    Check("final response is 200 with body", resp.Status == 200 && Encoding.UTF8.GetString(resp.Body) == "<html>hi</html>");

    var early = resp.InformationalResponses.FirstOrDefault(i => i.Status == 103);
    var links = early.Headers?.Where(h => h.Name == "link").Select(h => h.Value).ToList() ?? [];
    Check("received 103 Early Hints before the final response", early.Status == 103, $"{resp.InformationalResponses.Count} interim");
    Check("103 carried the Link preload hints", links.Count == 2 && links.Any(l => l.Contains("style.css")) && links.Any(l => l.Contains("app.js")),
          string.Join(" | ", links));

    await conn.CloseAsync();
}

// =========================================================================
// 3. .NET HttpClient — Expect: 100-continue interop
// =========================================================================
Console.WriteLine("\n=== .NET HttpClient: Expect 100-continue ===");
{
    var handler = new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    };
    using var http = new HttpClient(handler) { DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };

    var req = new HttpRequestMessage(HttpMethod.Post, $"https://localhost:{bufferedPort}/echo")
    {
        Content = new StringContent("hello from httpclient"),
        Version = HttpVersion.Version20,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact
    };
    req.Headers.ExpectContinue = true;   // triggers the 100-continue handshake

    var resp = await http.SendAsync(req);
    var text = await resp.Content.ReadAsStringAsync();
    Check("HttpClient Expect:100-continue POST round-trips", (int) resp.StatusCode == 200 && text == "hello from httpclient", text);
}

await bufferedServer.StopAsync();
await streamingServer.StopAsync();
try { await bufferedTask; } catch { }
try { await streamingTask; } catch { }

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
