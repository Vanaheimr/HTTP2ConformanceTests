using System.Net;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OurServer = org.GraphDefined.Vanaheimr.Hermod.HTTP2.HTTP2Server;

// h2c — cleartext HTTP/2 with prior knowledge (RFC 9113 §3.3): no TLS, no ALPN;
// the client sends the HTTP/2 connection preface straight over plain TCP. (The
// RFC 7540 "Upgrade: h2c" negotiation was removed in RFC 9113 §3.1 and is not
// implemented.) Verified on all three interop legs, mirroring how the TLS paths
// are tested:
//   1. our client   -> our server        (both cleartext, in-process)
//   2. .NET HttpClient (prior-knowledge)  -> our server
//   3. our client    -> .NET Kestrel h2c  server

var passed = 0;
var failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"  ✓ {name}" + (detail.Length > 0 ? $"  ({detail})" : "")); }
    else    { failed++; Console.WriteLine($"  ✗ {name}" + (detail.Length > 0 ? $"  — {detail}" : "")); }
}

// -------------------------------------------------------------------------
// Our cleartext server (no certificate) — a small buffered handler.
// -------------------------------------------------------------------------
const int ourPort = 9462;
Task<(List<(string, string)>, byte[]?)> Handle(uint s, List<(string Name, string Value)> h, byte[]? body, CancellationToken ct)
{
    var path = h.FirstOrDefault(x => x.Name == ":path").Value ?? "/";
    return Task.FromResult<(List<(string, string)>, byte[]?)>(path switch
    {
        "/"      => ([(":status", "200"), ("content-type", "text/plain")], Encoding.UTF8.GetBytes("Hello over h2c!")),
        "/echo"  => ([(":status", "200"), ("content-type", "application/octet-stream")], body ?? []),
        "/large" => ([(":status", "200"), ("content-type", "application/octet-stream")], LargeBody()),
        _        => ([(":status", "404")], Encoding.UTF8.GetBytes("nope")),
    });

    static byte[] LargeBody() { var b = new byte[128 * 1024]; Random.Shared.NextBytes(b); return b; }
}

var ourServer = new OurServer(IPAddress.Loopback, ourPort, Certificate: null, RequestHandler: Handle, Cleartext: true);
var ourServerTask = ourServer.RunAsync();
await Task.Delay(400);

// =========================================================================
// 1. our client -> our server (both cleartext)
// =========================================================================
Console.WriteLine("=== our client -> our server (cleartext h2c) ===");
{
    var conn = await HTTP2Client.ConnectAsync("localhost", ourPort, Cleartext: true);

    var get = await conn.SendRequestAsync("GET", "http", $"localhost:{ourPort}", "/");
    Check("GET / -> 200", get.Status == 200, get.Status.ToString());
    Check("body correct", Encoding.UTF8.GetString(get.Body) == "Hello over h2c!", Encoding.UTF8.GetString(get.Body));

    var payload = Encoding.UTF8.GetBytes("cleartext round-trip 🚀");
    var echo = await conn.SendRequestAsync("POST", "http", $"localhost:{ourPort}", "/echo", Body: payload);
    Check("POST /echo -> 200, byte-exact", echo.Status == 200 && echo.Body.SequenceEqual(payload), $"{echo.Body.Length} bytes");

    var large = await conn.SendRequestAsync("GET", "http", $"localhost:{ourPort}", "/large");
    Check("GET /large -> 128 KiB (flow control, no TLS)", large.Status == 200 && large.Body.Length == 128 * 1024, $"{large.Body.Length} bytes");

    // Multiplexing over a single cleartext connection.
    var t1 = conn.SendRequestAsync("GET", "http", $"localhost:{ourPort}", "/");
    var t2 = conn.SendRequestAsync("GET", "http", $"localhost:{ourPort}", "/large");
    var t3 = conn.SendRequestAsync("GET", "http", $"localhost:{ourPort}", "/");
    var r  = await Task.WhenAll(t1, t2, t3);
    Check("3 concurrent requests all 200", r.All(x => x.Status == 200), string.Join(",", r.Select(x => x.Status)));

    await conn.CloseAsync();
}

// =========================================================================
// 2. .NET HttpClient (prior-knowledge h2c) -> our server
// =========================================================================
Console.WriteLine("\n=== .NET HttpClient (prior-knowledge) -> our server ===");
{
    // http:// scheme + exact HTTP/2 => HttpClient speaks h2c with prior
    // knowledge (sends the preface directly, no Upgrade). The production-client
    // interop proof for our cleartext server.
    using var http = new HttpClient(new SocketsHttpHandler())
    {
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy  = HttpVersionPolicy.RequestVersionExact
    };

    var get = await http.GetAsync($"http://localhost:{ourPort}/");
    var getBody = await get.Content.ReadAsStringAsync();
    Check("HttpClient GET / -> 200 over HTTP/2", (int) get.StatusCode == 200 && get.Version == HttpVersion.Version20, $"{(int) get.StatusCode} HTTP/{get.Version}");
    Check("HttpClient body decoded", getBody == "Hello over h2c!", getBody);

    var payload = Encoding.UTF8.GetBytes("httpclient over cleartext");
    var echo = await http.PostAsync($"http://localhost:{ourPort}/echo", new ByteArrayContent(payload));
    var echoBody = await echo.Content.ReadAsByteArrayAsync();
    Check("HttpClient POST /echo -> 200, byte-exact", (int) echo.StatusCode == 200 && echoBody.SequenceEqual(payload), $"{echoBody.Length} bytes");
}

await ourServer.StopAsync();
try { await ourServerTask; } catch { }

// =========================================================================
// 3. our client -> .NET Kestrel h2c server
// =========================================================================
Console.WriteLine("\n=== our client -> .NET Kestrel (cleartext h2c) ===");
{
    const int kestrelPort = 9463;

    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenLocalhost(kestrelPort, listen =>
        {
            // HTTP/2 with NO UseHttps => cleartext h2c (prior-knowledge).
            listen.Protocols = HttpProtocols.Http2;
        });
    });

    var app = builder.Build();
    app.MapGet("/", () => "Hello from Kestrel h2c!");
    app.MapPost("/echo", async (HttpContext ctx) =>
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        ctx.Response.ContentType = "application/octet-stream";
        await ctx.Response.Body.WriteAsync(ms.ToArray());
    });

    await app.StartAsync();
    await Task.Delay(400);

    var conn = await HTTP2Client.ConnectAsync("localhost", kestrelPort, Cleartext: true);

    var get = await conn.SendRequestAsync("GET", "http", $"localhost:{kestrelPort}", "/");
    Check("GET / -> 200", get.Status == 200, get.Status.ToString());
    Check("Kestrel body decoded (HPACK/Huffman)", Encoding.UTF8.GetString(get.Body) == "Hello from Kestrel h2c!", Encoding.UTF8.GetString(get.Body));

    var payload = Encoding.UTF8.GetBytes("kestrel h2c round-trip äöü");
    var echo = await conn.SendRequestAsync("POST", "http", $"localhost:{kestrelPort}", "/echo", Body: payload);
    Check("POST /echo -> 200, byte-exact", echo.Status == 200 && echo.Body.SequenceEqual(payload), $"{echo.Body.Length} bytes");

    var t1 = conn.SendRequestAsync("GET", "http", $"localhost:{kestrelPort}", "/");
    var t2 = conn.SendRequestAsync("GET", "http", $"localhost:{kestrelPort}", "/");
    var rr = await Task.WhenAll(t1, t2);
    Check("2 concurrent requests all 200", rr.All(x => x.Status == 200), string.Join(",", rr.Select(x => x.Status)));

    await conn.CloseAsync();
    await app.StopAsync();
}

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
