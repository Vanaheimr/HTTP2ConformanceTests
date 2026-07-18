namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;


/// <summary>
/// Demo: starts an HTTP/2 server on port 8443 with a self-signed certificate.
/// Test with:  curl --http2 -k https://localhost:8443/
///             curl --http2 -k https://localhost:8443/echo -d "Hello HTTP/2!"
/// </summary>
public static class Program
{

    public static async Task Main(string[] args)
    {

        var port  = 8443;   // HTTP/2 over TLS ("h2")
        var portC = 8080;   // cleartext HTTP/2 with prior knowledge ("h2c")
        var cert  = CreateSelfSignedCertificate("localhost");

        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   HTTP/2 Server — pure SslStream + binary framing       ║");
        Console.WriteLine("╠══════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║   TLS  (h2):  https://localhost:{port}                     ║");
        Console.WriteLine($"║   h2c       :  http://localhost:{portC}  (prior knowledge)  ║");
        Console.WriteLine("║                                                          ║");
        Console.WriteLine("║   Test with:                                             ║");
        Console.WriteLine($"║     curl --http2 -k https://localhost:{port}/             ║");
        Console.WriteLine($"║     curl --http2-prior-knowledge http://localhost:{portC}/ ║");
        Console.WriteLine("║                                                          ║");
        Console.WriteLine("║   Press Ctrl+C to stop.                                  ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Listen on BOTH loopback addresses: clients resolving "localhost"
        // usually try ::1 first — if only 127.0.0.1 is bound, every connection
        // pays a multi-second IPv6 connect timeout before falling back to IPv4.
        var server4 = new HTTP2Server(IPAddress.Loopback,     port, cert, HandleRequest, HandleConnect);
        var server6 = new HTTP2Server(IPAddress.IPv6Loopback, port, cert, HandleRequest, HandleConnect);

        // Cleartext h2c listeners (prior knowledge, RFC 9113 §3.3): same handlers,
        // no TLS. Pass Certificate: null with Cleartext: true. Useful behind a
        // TLS-terminating proxy, or for tooling like `h2spec` / curl's
        // `--http2-prior-knowledge`.
        var server4c = new HTTP2Server(IPAddress.Loopback,     portC, Certificate: null, RequestHandler: HandleRequest, ConnectHandler: HandleConnect, Cleartext: true);
        var server6c = new HTTP2Server(IPAddress.IPv6Loopback, portC, Certificate: null, RequestHandler: HandleRequest, ConnectHandler: HandleConnect, Cleartext: true);

        // Ctrl+C triggers a graceful shutdown (GOAWAY to every connected peer)
        // instead of the runtime's default abrupt process kill.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n[HTTP/2] Ctrl+C received — shutting down gracefully...");
            _ = Task.Run(() => Task.WhenAll(
                server4.StopAsync(),  server6.StopAsync(),
                server4c.StopAsync(), server6c.StopAsync()));
        };

        await Task.WhenAll(
            server4.RunAsync(),
            server6.RunAsync(),
            server4c.RunAsync(),
            server6c.RunAsync()
        );

        Console.WriteLine("[HTTP/2] Shutdown complete.");

    }


    /// <summary>
    /// A static resource under "/files/", demonstrating the RFC 9110 "core
    /// mechanics" layer (HTTPSemantics.cs): GET/HEAD/OPTIONS, conditional
    /// requests (ETag + Last-Modified), and Range requests — all handled by
    /// HTTPSemantics without HTTP2Connection.cs needing to know any of this
    /// exists. Routing by prefix (rather than one exact path) means a
    /// request for anything else under "/files/" (e.g. "/files/missing.txt")
    /// exercises HTTPSemantics' own 404, distinct from HandleRequest's
    /// unrelated default-case 404 below. LastModified is fixed at process
    /// start, so restarting the demo server is the only way to make a
    /// cached If-Modified-Since go stale.
    /// </summary>
    private static readonly byte[] resourceBody = Encoding.UTF8.GetBytes(
        "This is a static resource demonstrating RFC 9110 conditional requests and " +
        "range requests over HTTP/2 — ETag/Last-Modified, If-Match/If-None-Match, " +
        "If-Modified-Since/If-Unmodified-Since, and Range/If-Range/Accept-Ranges.\n"
    );

    private static readonly DateTimeOffset resourceLastModified = DateTimeOffset.UtcNow;

    private static readonly HTTP2RequestHandler resourceHandler = HTTPSemantics.Wrap(HandleResourceRequest);

    private static Task<HTTPResource?> HandleResourceRequest(
        string                             Path,
        List<(string Name, string Value)> RequestHeaders,
        CancellationToken                 CancellationToken)
    {

        if (Path != "/files/resource.txt")
            return Task.FromResult<HTTPResource?>(null);

        return Task.FromResult<HTTPResource?>(new HTTPResource {
            Body         = resourceBody,
            ContentType  = "text/plain; charset=utf-8",
            LastModified = resourceLastModified
        });

    }


    /// <summary>
    /// A content-negotiated resource at "/files/greeting" demonstrating the
    /// RFC 9110 §12 proactive negotiation layer: the same greeting in two
    /// languages (Accept-Language) and two media types (Accept), so a client's
    /// Accept / Accept-Language headers select which representation it gets,
    /// and the response carries a matching Vary. Variant order expresses the
    /// server's own preference — the English text/plain form is the default
    /// when the client has no (satisfiable) preference.
    /// </summary>
    private static readonly HTTP2RequestHandler negotiatedHandler = HTTPSemantics.Wrap(HandleGreetingVariants);

    private static Task<IReadOnlyList<HTTPResource>> HandleGreetingVariants(
        string                             Path,
        List<(string Name, string Value)> RequestHeaders,
        CancellationToken                 CancellationToken)
    {

        if (Path != "/files/greeting")
            return Task.FromResult<IReadOnlyList<HTTPResource>>([]);

        return Task.FromResult<IReadOnlyList<HTTPResource>>(
        [
            new HTTPResource {
                Body         = Encoding.UTF8.GetBytes("Hello, world!\n"),
                ContentType  = "text/plain; charset=utf-8",
                Language     = "en",
                LastModified = resourceLastModified
            },
            new HTTPResource {
                Body         = Encoding.UTF8.GetBytes("Hallo, Welt!\n"),
                ContentType  = "text/plain; charset=utf-8",
                Language     = "de",
                LastModified = resourceLastModified
            },
            new HTTPResource {
                Body         = Encoding.UTF8.GetBytes("{\"greeting\":\"Hello, world!\",\"lang\":\"en\"}\n"),
                ContentType  = "application/json; charset=utf-8",
                Language     = "en",
                LastModified = resourceLastModified
            }
        ]);

    }


    /// <summary>
    /// A QUERY endpoint at "/search" demonstrating RFC 10008 (the HTTP QUERY
    /// method): a safe, idempotent, cacheable read whose query travels in the
    /// request body instead of the URL. GET /search returns the whole corpus;
    /// QUERY /search filters it by the term in the request body — the same
    /// endpoint, the search parameters just moving out of the query string. The
    /// QUERY result carries a Content-Location naming a GET-able URI for that
    /// exact result set (RFC 10008 §3), and — being safe — supports ETag/304
    /// conditional revalidation exactly like GET, all from HTTPSemantics.
    /// </summary>
    private static readonly string[] searchCorpus =
        ["apple", "apricot", "avocado", "banana", "blueberry", "cherry", "date", "fig", "grape", "mango"];

    private static readonly HTTP2RequestHandler searchHandler =
        HTTPSemantics.Wrap(HandleSearchResource, QueryHandler: HandleSearchQuery);

    private static byte[] SearchResults(string Term)
    {
        var matches = searchCorpus.Where(x => x.Contains(Term, StringComparison.OrdinalIgnoreCase));
        return Encoding.UTF8.GetBytes("[" + string.Join(",", matches.Select(x => $"\"{x}\"")) + "]\n");
    }

    // GET /search -> the whole corpus (no query).
    private static Task<HTTPResource?> HandleSearchResource(
        string Path, List<(string Name, string Value)> RequestHeaders, CancellationToken CancellationToken)
        => Task.FromResult<HTTPResource?>(
               Path != "/search" ? null : new HTTPResource {
                   Body        = SearchResults(""),
                   ContentType = "application/json; charset=utf-8"
               });

    // QUERY /search -> the corpus filtered by the term in the request body.
    private static Task<HTTPResource?> HandleSearchQuery(
        string Path, List<(string Name, string Value)> RequestHeaders,
        byte[]? QueryContent, string? ContentType, CancellationToken CancellationToken)
    {

        if (Path != "/search")
            return Task.FromResult<HTTPResource?>(null);

        var term = (QueryContent is null ? "" : Encoding.UTF8.GetString(QueryContent)).Trim();
        var body = SearchResults(term);

        // A stable GET-able URI for this exact result set (RFC 10008 §3): the
        // client could GET it later instead of resending the query content.
        var key = Convert.ToHexString(SHA256.HashData(QueryContent ?? []))[..8].ToLowerInvariant();

        return Task.FromResult<HTTPResource?>(new HTTPResource {
            Body            = body,
            ContentType     = "application/json; charset=utf-8",
            ContentLocation = $"/search/results/{key}"
        });

    }


    /// <summary>
    /// A protected resource at "/secret" demonstrating the RFC 9110 §11
    /// authentication framework with two schemes wired in — Basic (RFC 7617)
    /// and Bearer (RFC 6750). Demo credentials: Basic <c>alice:secret</c>, or
    /// Bearer token <c>valid-token-123</c>. Anything else gets 401 + a
    /// WWW-Authenticate challenge for both schemes. (Toy validators — a real
    /// server would hit a user store / verify a JWT signature; the framework
    /// stays store-agnostic in Core.)
    /// </summary>
    private static readonly HTTPAuthenticator authenticator = new(
        Realm: "demo",
        new BasicAuthenticationScheme((user, password, _) =>
            Task.FromResult<HTTPAuthenticatedIdentity?>(
                user == "alice" && password == "secret"
                    ? new HTTPAuthenticatedIdentity { Name = "alice" }
                    : null)),
        new BearerAuthenticationScheme((token, _) =>
            Task.FromResult<HTTPAuthenticatedIdentity?>(
                token == "valid-token-123"
                    ? new HTTPAuthenticatedIdentity { Name = "token-user" }
                    : null)),
        // "Token" — non-standard but common (Rails / GitHub-style). Accepts both
        // the bare "Token <token>" and the "Token token=\"…\"" parameterized form.
        new TokenAuthenticationScheme((token, parameters, _) =>
            Task.FromResult<HTTPAuthenticatedIdentity?>(
                token == "secret-token-abc"
                    ? new HTTPAuthenticatedIdentity { Name = "api-user" }
                    : null)));

    private static readonly HTTP2RequestHandler secretHandler =
        HTTPAuthentication.RequireAuthentication(authenticator, (identity, streamId, reqHeaders, reqBody, ct) =>
        {
            var body = Encoding.UTF8.GetBytes($"Authenticated as: {identity.Name}\n");
            return Task.FromResult<(List<(string Name, string Value)>, byte[]?)>(
            ([
                (":status",        "200"),
                ("content-type",   "text/plain; charset=utf-8"),
                ("content-length", body.Length.ToString())
            ], body));
        });


    /// <summary>
    /// A resource at "/digest" protected with RFC 7616 Digest authentication —
    /// the challenge-response scheme that never sends the password over the wire
    /// (the server issues a nonce, the client answers with a keyed hash). Kept on
    /// its own endpoint (rather than added to "/secret" alongside Basic/Bearer)
    /// so a client is forced to exercise Digest specifically. Demo credentials:
    /// <c>alice:secret</c>. A real deployment would store H(user:realm:password),
    /// not the plaintext this toy lookup returns.
    /// </summary>
    private static readonly HTTPAuthenticator digestAuthenticator = new(
        Realm: "demo",
        new DigestAuthenticationScheme("demo",
            (user, _) => Task.FromResult<string?>(user == "alice" ? "secret" : null)));

    private static readonly HTTP2RequestHandler digestHandler =
        HTTPAuthentication.RequireAuthentication(digestAuthenticator, (identity, streamId, reqHeaders, reqBody, ct) =>
        {
            var body = Encoding.UTF8.GetBytes($"Digest-authenticated as: {identity.Name}\n");
            return Task.FromResult<(List<(string Name, string Value)>, byte[]?)>(
            ([
                (":status",        "200"),
                ("content-type",   "text/plain; charset=utf-8"),
                ("content-length", body.Length.ToString())
            ], body));
        });


    /// <summary>
    /// Simple request handler that demonstrates how the higher-level HTTP semantics
    /// plug into the HTTP/2 framing layer. The pseudo-headers (:method, :path, :scheme,
    /// :authority) have already been decoded from HPACK.
    /// </summary>
    private static async Task<(List<(string Name, string Value)> ResponseHeaders, byte[]? ResponseBody)>
        HandleRequest(
            UInt32                            StreamId,
            List<(string Name, string Value)> RequestHeaders,
            byte[]?                           RequestBody,
            CancellationToken                 CancellationToken)
    {

        // Extract pseudo-headers
        var method    = RequestHeaders.FirstOrDefault(h => h.Name == ":method").Value    ?? "GET";
        var path      = RequestHeaders.FirstOrDefault(h => h.Name == ":path").Value      ?? "/";
        var authority = RequestHeaders.FirstOrDefault(h => h.Name == ":authority").Value  ?? "unknown";

        Console.WriteLine($"[Request] stream={StreamId} {method} {path} (host: {authority})");

        byte[] body;
        List<(string Name, string Value)> headers;

        if (path == "/secret")
            return await secretHandler(StreamId, RequestHeaders, RequestBody, CancellationToken);

        if (path == "/digest")
            return await digestHandler(StreamId, RequestHeaders, RequestBody, CancellationToken);

        if (path == "/files/greeting")
            return await negotiatedHandler(StreamId, RequestHeaders, RequestBody, CancellationToken);

        if (path == "/search")
            return await searchHandler(StreamId, RequestHeaders, RequestBody, CancellationToken);

        if (path.StartsWith("/files/", StringComparison.Ordinal))
            return await resourceHandler(StreamId, RequestHeaders, RequestBody, CancellationToken);

        switch (path)
        {

            case "/":
                body = Encoding.UTF8.GetBytes(
                    $"Hello from HTTP/2!\n\n" +
                    $"Stream ID:  {StreamId}\n" +
                    $"Method:     {method}\n" +
                    $"Path:       {path}\n" +
                    $"Authority:  {authority}\n\n" +
                    $"Request headers:\n" +
                    string.Join("\n", RequestHeaders.Select(h => $"  {h.Name}: {h.Value}"))
                );

                headers =
                [
                    (":status",       "200"),
                    ("content-type",  "text/plain; charset=utf-8"),
                    ("content-length", body.Length.ToString()),
                    ("server",        "HTTP2Server/1.0")
                ];
                break;

            case "/echo":
                body = RequestBody ?? Encoding.UTF8.GetBytes("(no body)");

                headers =
                [
                    (":status",        "200"),
                    ("content-type",   "application/octet-stream"),
                    ("content-length", body.Length.ToString()),
                    ("server",         "HTTP2Server/1.0")
                ];
                break;

            case "/slow":
                // Simulates a slow application handler. With real multiplexing
                // other streams on the same connection must not be blocked
                // while this one is waiting. Passing the token means a peer
                // RST_STREAM on this stream aborts the wait immediately instead
                // of running the full 2 s for a client that already left.
                var slowStopwatch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"[Request] stream={StreamId} /slow cancelled after " +
                                      $"{slowStopwatch.ElapsedMilliseconds} ms (would've been ~2000 ms)");
                    throw;
                }

                body = Encoding.UTF8.GetBytes("Slow response after 2 s delay.\n");

                headers =
                [
                    (":status",        "200"),
                    ("content-type",   "text/plain; charset=utf-8"),
                    ("content-length", body.Length.ToString()),
                    ("server",         "HTTP2Server/1.0")
                ];
                break;

            case "/large":
                // Test flow control with a larger response
                body = new byte[1024 * 128];  // 128 KiB
                Random.Shared.NextBytes(body);

                headers =
                [
                    (":status",        "200"),
                    ("content-type",   "application/octet-stream"),
                    ("content-length", body.Length.ToString()),
                    ("server",         "HTTP2Server/1.0")
                ];
                break;

            default:
                body = Encoding.UTF8.GetBytes($"404 Not Found: {path}\n");
                headers =
                [
                    (":status",        "404"),
                    ("content-type",   "text/plain; charset=utf-8"),
                    ("content-length", body.Length.ToString()),
                    ("server",         "HTTP2Server/1.0")
                ];
                break;

        }

        return (headers, body);

    }


    /// <summary>
    /// Decides whether to accept a CONNECT / extended-CONNECT (RFC 8441)
    /// tunnel, and what to run once accepted. Two demos:
    ///   - Extended CONNECT with :protocol=websocket at /ws-echo: a WebSocket
    ///     echo server (RFC 6455 framing over the tunnel).
    ///   - Plain CONNECT (no :protocol): a raw byte-loopback echo, proving the
    ///     RFC 9113 §8.5 tunnel framing itself works. A real proxy would
    ///     instead open a TCP connection to :authority and splice the two.
    /// </summary>
    private static Task<HTTP2ConnectResult> HandleConnect(
        UInt32                            StreamId,
        List<(string Name, string Value)> RequestHeaders,
        CancellationToken                 CancellationToken)
    {

        var protocol  = RequestHeaders.FirstOrDefault(h => h.Name == ":protocol").Value;
        var path      = RequestHeaders.FirstOrDefault(h => h.Name == ":path").Value;
        var authority = RequestHeaders.FirstOrDefault(h => h.Name == ":authority").Value ?? "unknown";

        if (protocol == "websocket" && path == "/ws-echo")
        {

            // permessage-deflate (RFC 7692): if the client offered it on the
            // CONNECT request, accept (in no-context-takeover mode) and echo the
            // acceptance back in sec-websocket-extensions. Negotiation lives at
            // this handshake layer, not in the framing — the framing just gets a
            // flag. This is the production HTTP/2 (RFC 8441) counterpart of the
            // HTTP/1.1 Upgrade negotiation the Autobahn echo server does.
            var offer   = RequestHeaders.FirstOrDefault(h => h.Name == "sec-websocket-extensions").Value;
            var deflate = WebSocketDeflate.ShouldAccept(offer, out var responseExt);

            Console.WriteLine($"[Connect] stream={StreamId} extended CONNECT :protocol=websocket :path={path}" +
                              (deflate ? " (permessage-deflate)" : ""));

            return Task.FromResult(new HTTP2ConnectResult {
                StatusCode   = 200,
                ExtraHeaders = deflate ? [("sec-websocket-extensions", responseExt!)] : null,
                RunAsync     = (tunnel, ct) => RunWebSocketEchoAsync(tunnel, deflate, ct)
            });
        }

        if (protocol is null)
        {
            Console.WriteLine($"[Connect] stream={StreamId} plain CONNECT :authority={authority}");
            return Task.FromResult(new HTTP2ConnectResult {
                StatusCode = 200,
                RunAsync   = RunLoopbackEchoAsync
            });
        }

        Console.WriteLine($"[Connect] stream={StreamId} rejecting :protocol={protocol} :path={path}");
        return Task.FromResult(new HTTP2ConnectResult { StatusCode = 404 });

    }

    /// <summary>
    /// Demo tunnel body for a plain CONNECT: echoes back whatever bytes the
    /// peer sends, proving the raw duplex byte-tunnel framing works
    /// end-to-end independent of any higher-level protocol on top of it.
    /// </summary>
    private static async Task RunLoopbackEchoAsync(HTTP2Tunnel Tunnel, CancellationToken CancellationToken)
    {

        while (true)
        {

            var chunk = await Tunnel.ReadAsync(CancellationToken);

            if (chunk is null)
                break;   // Peer ended their side of the tunnel

            await Tunnel.WriteAsync(chunk, CancellationToken);

        }

    }

    /// <summary>
    /// Demo tunnel body for the extended-CONNECT WebSocket endpoint: echoes
    /// text/binary messages back verbatim. Ping/pong and the close handshake
    /// are handled transparently by WebSocketConnection itself. When
    /// <paramref name="PerMessageDeflate"/> was negotiated at the handshake, the
    /// framing transparently (de)compresses each message (RFC 7692).
    /// </summary>
    private static async Task RunWebSocketEchoAsync(HTTP2Tunnel Tunnel, bool PerMessageDeflate, CancellationToken CancellationToken)
    {

        var webSocket = new WebSocketConnection(Tunnel, WebSocketRole.Server, PerMessageDeflate);

        while (true)
        {

            var message = await webSocket.ReceiveAsync(CancellationToken);

            if (message is null)
                break;   // Close handshake completed, or the tunnel ended

            switch (message.Opcode)
            {

                case WebSocketOpcode.Text:
                    var text = Encoding.UTF8.GetString(message.Payload);
                    Console.WriteLine($"[WebSocket] echoing text message: \"{text}\"");
                    await webSocket.SendTextAsync(text, CancellationToken);
                    break;

                case WebSocketOpcode.Binary:
                    Console.WriteLine($"[WebSocket] echoing binary message ({message.Payload.Length} bytes)");
                    await webSocket.SendBinaryAsync(message.Payload, CancellationToken);
                    break;

            }

        }

    }


    /// <summary>
    /// Create a self-signed X.509 certificate for development/testing.
    /// </summary>
    private static X509Certificate2 CreateSelfSignedCertificate(string HostName)
    {

        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={HostName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );

        // Add Subject Alternative Name
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(HostName);
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1)
        );

        // The PFX round-trip gives the cert a provider-backed private key that
        // SslStream's server-side handshake can use for signing. EphemeralKeySet
        // (key held in memory only) would be the cleanest choice, but Windows'
        // SChannel-backed SslStream rejects it for server auth ("the platform
        // does not support ephemeral keys") — it needs a CAPI/CNG key container.
        // UserKeySet persists to the current user's key store: unlike
        // MachineKeySet it needs no elevated rights and isn't shared machine-wide,
        // and it works unchanged on Linux (key-storage flags are largely no-ops
        // there since there's no OS-level certificate store).
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx, "temp"),
            "temp",
            X509KeyStorageFlags.UserKeySet
        );

    }

}
