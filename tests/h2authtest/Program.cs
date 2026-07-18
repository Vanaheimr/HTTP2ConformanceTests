using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;
using OurServer = org.GraphDefined.Vanaheimr.Hermod.HTTP2.HTTP2Server;

// Track D auth-framework tests + Track E mTLS:
//   1. RFC 9110 §11 framework with Basic (RFC 7617) + Bearer (RFC 6750),
//      driven by .NET HttpClient AND our own HTTP2Client.
//   2. Mutual TLS: our server requires a client cert; verify present->ok,
//      absent->handshake fails, and the cert subject is surfaced to handlers.

var passed = 0;
var failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"  ✓ {name}" + (detail.Length > 0 ? $"  ({detail})" : "")); }
    else    { failed++; Console.WriteLine($"  ✗ {name}" + (detail.Length > 0 ? $"  — {detail}" : "")); }
}

static X509Certificate2 MakeCert(string cn, bool clientAuth = false)
{
    using var rsa = RSA.Create(2048);
    var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var san = new SubjectAlternativeNameBuilder();
    san.AddDnsName("localhost");
    san.AddIpAddress(IPAddress.Loopback);
    san.AddIpAddress(IPAddress.IPv6Loopback);
    req.CertificateExtensions.Add(san.Build());
    // clientAuth (1.3.6.1.5.5.7.3.2) or serverAuth (.3.1) EKU
    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
        [new Oid(clientAuth ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1")], false));
    var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.UserKeySet);
}

HttpClient MakeHttpClient(X509Certificate2? clientCert = null)
{
    var handler = new SocketsHttpHandler {
        SslOptions = new SslClientAuthenticationOptions {
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        }
    };
    if (clientCert is not null)
        handler.SslOptions.ClientCertificates = [clientCert];
    return new HttpClient(handler) {
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy  = HttpVersionPolicy.RequestVersionExact,
        Timeout               = TimeSpan.FromSeconds(8)
    };
}

// The auth framework under test: Basic alice:secret + Bearer valid-token-123.
var authenticator = new HTTPAuthenticator("demo",
    new BasicAuthenticationScheme((u, p, _) => Task.FromResult<HTTPAuthenticatedIdentity?>(
        u == "alice" && p == "secret" ? new HTTPAuthenticatedIdentity { Name = "alice" } : null)),
    new BearerAuthenticationScheme((t, _) => Task.FromResult<HTTPAuthenticatedIdentity?>(
        t == "valid-token-123" ? new HTTPAuthenticatedIdentity { Name = "token-user" } : null)));

var secretHandler = HTTPAuthentication.RequireAuthentication(authenticator,
    (identity, sid, h, b, ct) =>
    {
        var body = Encoding.UTF8.GetBytes($"Authenticated as: {identity.Name}");
        return Task.FromResult<(List<(string, string)>, byte[]?)>(
            ([(":status", "200"), ("content-type", "text/plain"), ("content-length", body.Length.ToString())], body));
    });

RemoteCertificateValidationCallback acceptAnyServerCert = (_, _, _, _) => true;

// =========================================================================
// Part 1 — Basic + Bearer, via HttpClient
// =========================================================================
Console.WriteLine("=== RFC 9110 §11 auth framework (Basic + Bearer) ===");
{
    const int port = 9445;
    var cert = MakeCert("localhost");
    var server = new OurServer(IPAddress.Loopback, port, cert, secretHandler);
    var serverTask = server.RunAsync();
    await Task.Delay(400);

    using var http = MakeHttpClient();
    var baseUri = $"https://127.0.0.1:{port}/secret";

    // No credentials -> 401 + WWW-Authenticate for both schemes.
    var anon = await http.GetAsync(baseUri);
    var challenges = anon.Headers.WwwAuthenticate.Select(h => h.Scheme).ToList();
    Check("no creds -> 401", anon.StatusCode == HttpStatusCode.Unauthorized, $"{(int) anon.StatusCode}");
    Check("challenge advertises Basic + Bearer",
          challenges.Contains("Basic") && challenges.Contains("Bearer"),
          string.Join(", ", challenges));

    // Basic, correct.
    async Task<HttpResponseMessage> Send(string uri, AuthenticationHeaderValue auth)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri) {
            Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        req.Headers.Authorization = auth;
        return await http.SendAsync(req);
    }

    var basicOk = await Send(baseUri, new AuthenticationHeaderValue("Basic",
        Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secret"))));
    Check("Basic alice:secret -> 200", basicOk.StatusCode == HttpStatusCode.OK, $"{(int) basicOk.StatusCode}");
    Check("identity surfaced", (await basicOk.Content.ReadAsStringAsync()) == "Authenticated as: alice",
          await basicOk.Content.ReadAsStringAsync());

    var basicBad = await Send(baseUri, new AuthenticationHeaderValue("Basic",
        Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:wrong"))));
    Check("Basic wrong password -> 401", basicBad.StatusCode == HttpStatusCode.Unauthorized, $"{(int) basicBad.StatusCode}");

    var basicGarbage = await Send(baseUri, new AuthenticationHeaderValue("Basic", "not-base64!!"));
    Check("Basic malformed -> 401", basicGarbage.StatusCode == HttpStatusCode.Unauthorized, $"{(int) basicGarbage.StatusCode}");

    var bearerOk = await Send(baseUri, new AuthenticationHeaderValue("Bearer", "valid-token-123"));
    Check("Bearer valid -> 200", bearerOk.StatusCode == HttpStatusCode.OK, $"{(int) bearerOk.StatusCode}");
    Check("bearer identity surfaced", (await bearerOk.Content.ReadAsStringAsync()) == "Authenticated as: token-user",
          await bearerOk.Content.ReadAsStringAsync());

    var bearerBad = await Send(baseUri, new AuthenticationHeaderValue("Bearer", "nope"));
    Check("Bearer invalid -> 401", bearerBad.StatusCode == HttpStatusCode.Unauthorized, $"{(int) bearerBad.StatusCode}");

    var unknownScheme = await Send(baseUri, new AuthenticationHeaderValue("Digest", "whatever"));
    Check("unsupported scheme -> 401", unknownScheme.StatusCode == HttpStatusCode.Unauthorized, $"{(int) unknownScheme.StatusCode}");

    // Same, via OUR client — proves the framework isn't HttpClient-specific.
    var conn = await HTTP2Client.ConnectAsync("localhost", port, acceptAnyServerCert);
    var basicViaOurClient = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/secret",
        ExtraHeaders: [("authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secret")))]);
    Check("our client + Basic -> 200", basicViaOurClient.Status == 200, basicViaOurClient.Status.ToString());
    var anonViaOurClient = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/secret");
    Check("our client, no creds -> 401", anonViaOurClient.Status == 401, anonViaOurClient.Status.ToString());
    await conn.CloseAsync();

    await server.StopAsync();
    try { await serverTask; } catch { }
}

// =========================================================================
// Part 2 — mutual TLS
// =========================================================================
Console.WriteLine("\n=== mutual TLS (client certificates) ===");
{
    const int port = 9446;
    var serverCert = MakeCert("localhost");
    var clientCert = MakeCert("test-client", clientAuth: true);

    // mTLS handler echoes the surfaced client-cert subject.
    Task<(List<(string, string)>, byte[]?)> Handle(uint sid, List<(string Name, string Value)> h, byte[]? b, CancellationToken ct)
    {
        var subject = h.FirstOrDefault(x => x.Name == "x-client-cert-subject").Value ?? "(none)";
        var body = Encoding.UTF8.GetBytes(subject);
        return Task.FromResult<(List<(string, string)>, byte[]?)>(
            ([(":status", "200"), ("content-length", body.Length.ToString())], body));
    }

    // Require a client cert; accept any that's actually presented (reject none).
    RemoteCertificateValidationCallback requireCert = (_, cert, _, _) => cert is not null;
    var server = new OurServer(IPAddress.Loopback, port, serverCert, Handle,
        RequireClientCertificate: true, ValidateClientCertificate: requireCert);
    var serverTask = server.RunAsync();
    await Task.Delay(400);

    // HttpClient WITH a client cert -> ok, subject surfaced.
    using (var http = MakeHttpClient(clientCert))
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/") {
            Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        var resp = await http.SendAsync(req);
        var subject = await resp.Content.ReadAsStringAsync();
        Check("HttpClient + client cert -> 200", resp.StatusCode == HttpStatusCode.OK, $"{(int) resp.StatusCode}");
        Check("x-client-cert-subject surfaced", subject.Contains("test-client"), subject);
    }

    // HttpClient WITHOUT a client cert -> handshake fails.
    using (var http = MakeHttpClient())
    {
        var threw = false;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/") {
                Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            await http.SendAsync(req);
        }
        catch { threw = true; }
        Check("HttpClient without client cert -> rejected", threw);
    }

    // OUR client WITH a client cert -> ok.
    var conn = await HTTP2Client.ConnectAsync("localhost", port, acceptAnyServerCert, ClientCertificate: clientCert);
    var ok = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/");
    Check("our client + client cert -> 200", ok.Status == 200, ok.Status.ToString());
    Check("our client sees cert subject", Encoding.UTF8.GetString(ok.Body).Contains("test-client"), Encoding.UTF8.GetString(ok.Body));
    await conn.CloseAsync();

    // OUR client WITHOUT a client cert -> fails.
    var ourThrew = false;
    try { await HTTP2Client.ConnectAsync("localhost", port, acceptAnyServerCert); }
    catch { ourThrew = true; }
    Check("our client without cert -> rejected", ourThrew);

    await server.StopAsync();
    try { await serverTask; } catch { }
}

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
