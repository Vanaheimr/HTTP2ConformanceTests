/*
 * Copyright (c) 2010-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Vanaheimr Hermod <https://www.github.com/Vanaheimr/Hermod>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OurServer = org.GraphDefined.Vanaheimr.Hermod.HTTP2.HTTP2Server;

// Interop tests for the hand-rolled HTTP2Client (Track E):
//   1. our client  ->  our server   (in-process, loopback)
//   2. our client  ->  .NET Kestrel HTTP/2 server
// Both prove the client speaks real HTTP/2 (preface, SETTINGS, HPACK decode of
// a real encoder's output, DATA + flow control, multiplexing).

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
    san.AddDnsName("localhost");
    san.AddIpAddress(IPAddress.Loopback);
    san.AddIpAddress(IPAddress.IPv6Loopback);
    req.CertificateExtensions.Add(san.Build());
    var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.UserKeySet);
}

// Accept the self-signed cert in the client.
RemoteCertificateValidationCallback acceptAny = (_, _, _, _) => true;

// =========================================================================
// 1. our client -> our server
// =========================================================================
Console.WriteLine("=== our client -> our hand-rolled server ===");
{
    const int port = 9443;
    var cert = MakeCert();

    async Task<(List<(string Name, string Value)> ResponseHeaders, byte[]? ResponseBody)> Handle(
        uint streamId, List<(string Name, string Value)> reqHeaders, byte[]? reqBody, CancellationToken ct)
    {
        var path = reqHeaders.FirstOrDefault(h => h.Name == ":path").Value ?? "/";
        await Task.Yield();
        return path switch
        {
            "/"      => ([(":status", "200"), ("content-type", "text/plain")], Encoding.UTF8.GetBytes("Hello from our server!")),
            "/echo"  => ([(":status", "200"), ("content-type", "application/octet-stream")], reqBody ?? []),
            "/large" => ([(":status", "200"), ("content-type", "application/octet-stream")], LargeBody()),
            "/slow"  => await Slow(ct),
            _        => ([(":status", "404")], Encoding.UTF8.GetBytes("nope")),
        };

        static byte[] LargeBody() { var b = new byte[128 * 1024]; Random.Shared.NextBytes(b); return b; }
        static async Task<(List<(string, string)>, byte[]?)> Slow(CancellationToken ct)
        {
            await Task.Delay(1500, ct);
            return ([(":status", "200")], Encoding.UTF8.GetBytes("slow done"));
        }
    }

    var server = new OurServer(IPAddress.Loopback, port, cert, Handle);
    var serverTask = server.RunAsync();
    await Task.Delay(400);

    var conn = await HTTP2Client.ConnectAsync("localhost", port, acceptAny);

    var get = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/");
    Check("GET / -> 200", get.Status == 200, get.Status.ToString());
    Check("body correct", Encoding.UTF8.GetString(get.Body) == "Hello from our server!", Encoding.UTF8.GetString(get.Body));

    var payload = Encoding.UTF8.GetBytes("round-trip 🚀");
    var echo = await conn.SendRequestAsync("POST", "https", $"localhost:{port}", "/echo", Body: payload);
    Check("POST /echo -> 200", echo.Status == 200, echo.Status.ToString());
    Check("echo body byte-exact", echo.Body.SequenceEqual(payload), $"{echo.Body.Length} bytes");

    var large = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/large");
    Check("GET /large -> 200 with 128 KiB (flow control)", large.Status == 200 && large.Body.Length == 128 * 1024, $"{large.Body.Length} bytes");

    // Multiplexing: fire three at once; /slow must not block the fast ones.
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var tSlow = conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/slow");
    var tFast = conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/");
    var tLarge = conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/large");
    var fast = await tFast; var fastAt = sw.ElapsedMilliseconds;
    var largeR = await tLarge; var largeAt = sw.ElapsedMilliseconds;
    var slow = await tSlow; var slowAt = sw.ElapsedMilliseconds;
    Check("multiplexing: fast completes well before slow", fast.Status == 200 && fastAt < 800 && slowAt >= 1400,
          $"fast={fastAt}ms large={largeAt}ms slow={slowAt}ms");

    await conn.CloseAsync();
    await server.StopAsync();
    try { await serverTask; } catch { }
}

// =========================================================================
// 2. our client -> .NET Kestrel HTTP/2 server
// =========================================================================
Console.WriteLine("\n=== our client -> .NET Kestrel HTTP/2 server ===");
{
    const int port = 9444;
    var cert = MakeCert();

    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenLocalhost(port, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;   // HTTP/2 only — forces our client to speak it
            listen.UseHttps(cert);
        });
    });

    var app = builder.Build();
    app.MapGet("/", () => "Hello from Kestrel!");
    app.MapPost("/echo", async (HttpContext ctx) =>
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        ctx.Response.ContentType = "application/octet-stream";
        await ctx.Response.Body.WriteAsync(ms.ToArray());
    });
    app.MapGet("/big", async (HttpContext ctx) =>
    {
        var buf = new byte[64 * 1024];
        Random.Shared.NextBytes(buf);
        ctx.Response.ContentType = "application/octet-stream";
        await ctx.Response.Body.WriteAsync(buf);
    });

    await app.StartAsync();
    await Task.Delay(400);

    var conn = await HTTP2Client.ConnectAsync("localhost", port, acceptAny);

    var get = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/");
    Check("GET / -> 200", get.Status == 200, get.Status.ToString());
    Check("Kestrel body decoded (HPACK/Huffman)", Encoding.UTF8.GetString(get.Body) == "Hello from Kestrel!", Encoding.UTF8.GetString(get.Body));
    Check("content-type header decoded", (get.HeaderValue("content-type") ?? "").Contains("text/plain"), get.HeaderValue("content-type") ?? "(none)");

    var payload = Encoding.UTF8.GetBytes("kestrel round-trip äöü");
    var echo = await conn.SendRequestAsync("POST", "https", $"localhost:{port}", "/echo", Body: payload);
    Check("POST /echo -> 200", echo.Status == 200, echo.Status.ToString());
    Check("echo body byte-exact", echo.Body.SequenceEqual(payload), $"{echo.Body.Length} bytes");

    var big = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/big");
    Check("GET /big -> 64 KiB (flow control vs Kestrel)", big.Status == 200 && big.Body.Length == 64 * 1024, $"{big.Body.Length} bytes");

    // Concurrent requests against Kestrel
    var t1 = conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/");
    var t2 = conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/big");
    var t3 = conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/");
    var r = await Task.WhenAll(t1, t2, t3);
    Check("3 concurrent requests all 200", r.All(x => x.Status == 200), string.Join(",", r.Select(x => x.Status)));

    var missing = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/does-not-exist");
    Check("unknown path -> 404", missing.Status == 404, missing.Status.ToString());

    await conn.CloseAsync();
    await app.StopAsync();
}

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
