using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;
using OurServer = org.GraphDefined.Vanaheimr.Hermod.HTTP2.HTTP2Server;

// On-the-fly content coding (RFC 9110 §8.4): HTTPSemantics.Wrap(..., CompressResponses:true)
// compresses a compressible 200 body with the best coding the client's
// Accept-Encoding accepts. Verified against our own client (exact control over
// Accept-Encoding + manual decompression) and .NET HttpClient (transparent
// AutomaticDecompression — the production-client interop proof).

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

const int port = 9459;

// A highly compressible ~600-byte text body, plus a tiny one (< MinCompressSize).
var bigText   = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 14));
var bigBody   = Encoding.UTF8.GetBytes(bigText);
var tinyBody  = "tiny"u8.ToArray();

HTTPResourceHandler Resource = (path, headers, ct) => Task.FromResult<HTTPResource?>(path switch
{
    "/big"   => new HTTPResource { Body = bigBody,  ContentType = "text/plain; charset=utf-8" },
    "/tiny"  => new HTTPResource { Body = tinyBody, ContentType = "text/plain; charset=utf-8" },
    _        => null
});

var server = new OurServer(IPAddress.Loopback, port, MakeCert(), HTTPSemantics.Wrap(Resource, CompressResponses: true));
var serverTask = server.RunAsync();
await Task.Delay(400);

static byte[] Decompress(byte[] data, string coding)
{
    using var input  = new MemoryStream(data);
    using var output = new MemoryStream();
    using (Stream d = coding switch
    {
        "br"      => new BrotliStream (input, CompressionMode.Decompress),
        "gzip"    => new GZipStream   (input, CompressionMode.Decompress),
        "deflate" => new DeflateStream(input, CompressionMode.Decompress),
        _         => throw new InvalidOperationException()
    })
        d.CopyTo(output);
    return output.ToArray();
}

// =========================================================================
// 1. Our client — precise control over Accept-Encoding
// =========================================================================
Console.WriteLine("=== our client ===");
{
    var conn = await HTTP2Client.ConnectAsync("localhost", port, acceptAny);

    async Task<HTTP2Response> Get(string path, string? acceptEncoding)
    {
        var extra = acceptEncoding is null ? null : new List<(string, string)> { ("accept-encoding", acceptEncoding) };
        return await conn.SendRequestAsync("GET", "https", $"localhost:{port}", path, extra);
    }

    // gzip
    {
        var r = await Get("/big", "gzip");
        var enc = r.HeaderValue("content-encoding");
        var vary = r.HeaderValue("vary") ?? "";
        var etag = r.HeaderValue("etag") ?? "";
        var ok = enc == "gzip" && r.Body.Length < bigBody.Length
                 && Decompress(r.Body, "gzip").AsSpan().SequenceEqual(bigBody);
        Check("gzip: content-encoding + smaller + decodes to original", ok, $"{r.Body.Length} vs {bigBody.Length} bytes");
        Check("gzip: Vary includes accept-encoding", vary.Contains("accept-encoding"), vary);
        Check("gzip: ETag weakened", etag.StartsWith("W/"), etag);
    }

    // brotli
    {
        var r = await Get("/big", "br");
        Check("br: content-encoding + decodes to original",
              r.HeaderValue("content-encoding") == "br" && Decompress(r.Body, "br").AsSpan().SequenceEqual(bigBody));
    }

    // server preference: both gzip and deflate offered -> gzip (br not listed)
    {
        var r = await Get("/big", "deflate, gzip");
        Check("preference: deflate+gzip offered -> gzip chosen", r.HeaderValue("content-encoding") == "gzip",
              r.HeaderValue("content-encoding") ?? "(none)");
    }

    // no Accept-Encoding -> identity
    {
        var r = await Get("/big", null);
        Check("no Accept-Encoding -> identity (no content-encoding, full body)",
              r.HeaderValue("content-encoding") is null && r.Body.AsSpan().SequenceEqual(bigBody));
    }

    // gzip;q=0 -> forbidden -> identity
    {
        var r = await Get("/big", "gzip;q=0");
        Check("gzip;q=0 -> identity", r.HeaderValue("content-encoding") is null && r.Body.AsSpan().SequenceEqual(bigBody));
    }

    // tiny body -> not compressed even with gzip
    {
        var r = await Get("/tiny", "gzip");
        Check("tiny body not compressed", r.HeaderValue("content-encoding") is null && r.Body.AsSpan().SequenceEqual(tinyBody));
    }

    // conditional revalidation against the weak ETag -> 304
    {
        var first = await Get("/big", "gzip");
        var etag  = first.HeaderValue("etag")!;
        var second = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/big",
            new List<(string, string)> { ("accept-encoding", "gzip"), ("if-none-match", etag) });
        Check("revalidation with weak ETag -> 304", second.Status == 304, second.Status.ToString());
    }

    await conn.CloseAsync();
}

// =========================================================================
// 2. .NET HttpClient — transparent AutomaticDecompression (interop)
// =========================================================================
Console.WriteLine("\n=== .NET HttpClient (AutomaticDecompression) ===");
{
    var handler = new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli | DecompressionMethods.Deflate,
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    };
    using var http = new HttpClient(handler) { DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };

    var resp = await http.GetAsync($"https://localhost:{port}/big");
    var body = await resp.Content.ReadAsStringAsync();
    // With AutomaticDecompression, HttpClient sent Accept-Encoding and transparently
    // decompressed — the body we see must be the original text.
    Check("HttpClient transparently decompresses to the original body", (int) resp.StatusCode == 200 && body == bigText,
          $"{body.Length} chars");
    // HttpClient surfaces the decoded content — Content-Encoding is consumed, but the
    // Vary should still reflect that the server negotiated on accept-encoding.
    var vary = resp.Headers.Vary.Count > 0 ? string.Join(",", resp.Headers.Vary) : (resp.Content.Headers.TryGetValues("vary", out var v) ? string.Join(",", v) : "");
    Check("HttpClient response carried Vary: accept-encoding", vary.Contains("accept-encoding", StringComparison.OrdinalIgnoreCase), vary);
}

await server.StopAsync();
try { await serverTask; } catch { }

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
