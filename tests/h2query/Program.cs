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
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;
using OurServer = org.GraphDefined.Vanaheimr.Hermod.HTTP2.HTTP2Server;

// RFC 10008 — the HTTP QUERY method, via HTTPSemantics.Wrap(..., QueryHandler):
// a safe, idempotent, cacheable read whose query travels in the request body.
// Driven by .NET HttpClient (production client) AND our own HTTP2Client.

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

const int port = 9468;

// The QUERY-backed /search resource (mirrors the demo): GET returns the whole
// corpus, QUERY filters it by the term in the request body.
string[] corpus = ["apple", "apricot", "avocado", "banana", "blueberry", "cherry", "date", "fig", "grape", "mango"];
byte[] Results(string term) =>
    Encoding.UTF8.GetBytes("[" + string.Join(",", corpus.Where(x => x.Contains(term, StringComparison.OrdinalIgnoreCase)).Select(x => $"\"{x}\"")) + "]");

Task<HTTPResource?> ResourceHandler(string path, List<(string, string)> h, CancellationToken ct)
    => Task.FromResult<HTTPResource?>(path != "/search" ? null
        : new HTTPResource { Body = Results(""), ContentType = "application/json" });

Task<HTTPResource?> QueryHandler(string path, List<(string, string)> h, byte[]? content, string? contentType, CancellationToken ct)
{
    if (path != "/search")
        return Task.FromResult<HTTPResource?>(null);
    var term = (content is null ? "" : Encoding.UTF8.GetString(content)).Trim();
    var key  = Convert.ToHexString(SHA256.HashData(content ?? [])).ToLowerInvariant()[..8];
    return Task.FromResult<HTTPResource?>(new HTTPResource {
        Body = Results(term), ContentType = "application/json", ContentLocation = $"/search/results/{key}"
    });
}

var handler = HTTPSemantics.Wrap((HTTPResourceHandler) ResourceHandler, QueryHandler: QueryHandler);

var server = new OurServer(IPAddress.Loopback, port, MakeCert(), handler);
var serverTask = server.RunAsync();
await Task.Delay(400);

// =========================================================================
Console.WriteLine("=== QUERY via .NET HttpClient ===");
{
    var httpHandler = new SocketsHttpHandler {
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    };
    using var http = new HttpClient(httpHandler) {
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy  = HttpVersionPolicy.RequestVersionExact,
        Timeout               = TimeSpan.FromSeconds(8)
    };
    var baseUri = $"https://localhost:{port}";

    async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null, Action<HttpRequestMessage>? tweak = null)
    {
        var req = new HttpRequestMessage(method, baseUri + path)
        {
            Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "text/plain");
        tweak?.Invoke(req);
        return await http.SendAsync(req);
    }

    var query = new HttpMethod("QUERY");

    // GET /search -> the whole corpus.
    var get = await Send(HttpMethod.Get, "/search");
    var getBody = await get.Content.ReadAsStringAsync();
    Check("GET /search -> 200 with the full corpus", (int) get.StatusCode == 200 && getBody.Contains("banana") && getBody.Contains("mango"), getBody);

    // QUERY /search with a term in the body -> filtered results.
    var q = await Send(query, "/search", "ap");
    var qBody = await q.Content.ReadAsStringAsync();
    Check("QUERY /search body=\"ap\" -> 200 filtered", (int) q.StatusCode == 200 && qBody == "[\"apple\",\"apricot\",\"grape\"]", qBody);
    Check("QUERY result carries a Content-Location (RFC 10008 §3)",
          q.Content.Headers.ContentLocation is not null, q.Content.Headers.ContentLocation?.ToString() ?? "(none)");
    Check("QUERY result carries an ETag", q.Headers.ETag is not null, q.Headers.ETag?.ToString() ?? "(none)");

    // A different term -> a different result set.
    var q2 = await Send(query, "/search", "berry");
    Check("QUERY /search body=\"berry\" -> only the berries", (await q2.Content.ReadAsStringAsync()) == "[\"blueberry\"]");

    // QUERY is safe/cacheable -> conditional revalidation with If-None-Match yields 304.
    var etag = q.Headers.ETag!.ToString();
    var cond = await Send(query, "/search", "ap", r => r.Headers.TryAddWithoutValidation("If-None-Match", etag));
    Check("QUERY + If-None-Match (matching ETag) -> 304 Not Modified", (int) cond.StatusCode == 304, cond.StatusCode.ToString());

    // OPTIONS advertises QUERY in Allow.
    var opt = await Send(HttpMethod.Options, "/search");
    var allow = opt.Content.Headers.Allow.Count > 0 ? string.Join(", ", opt.Content.Headers.Allow) : string.Join(", ", opt.Headers.GetValues("Allow"));
    Check("OPTIONS /search -> 204, Allow lists QUERY", (int) opt.StatusCode == 204 && allow.Contains("QUERY"), allow);

    // POST (unsupported) -> 405, Allow still lists QUERY.
    var post = await Send(HttpMethod.Post, "/search", "x");
    var postAllow = post.Content.Headers.Allow.Count > 0 ? string.Join(", ", post.Content.Headers.Allow) : "";
    Check("POST /search -> 405, Allow lists QUERY", (int) post.StatusCode == 405 && postAllow.Contains("QUERY"), $"{(int) post.StatusCode} allow={postAllow}");
}

// =========================================================================
Console.WriteLine("=== QUERY via our own HTTP2Client ===");
{
    var conn = await HTTP2Client.ConnectAsync("localhost", port, (_, _, _, _) => true);
    var authority = $"localhost:{port}";

    // QUERY with a content-type + body.
    var q = await conn.SendRequestAsync("QUERY", "https", authority, "/search",
                ExtraHeaders: [("content-type", "text/plain")], Body: Encoding.UTF8.GetBytes("ap"));
    Check("our client: QUERY -> 200 filtered", q.Status == 200 && Encoding.UTF8.GetString(q.Body) == "[\"apple\",\"apricot\",\"grape\"]",
          Encoding.UTF8.GetString(q.Body));
    Check("our client: sees Content-Location", q.HeaderValue("content-location") is not null, q.HeaderValue("content-location") ?? "(none)");

    // RFC 10008 §4: a QUERY with content but no Content-Type MUST fail (400).
    var noCt = await conn.SendRequestAsync("QUERY", "https", authority, "/search", Body: Encoding.UTF8.GetBytes("ap"));
    Check("our client: QUERY body without Content-Type -> 400", noCt.Status == 400, noCt.Status.ToString());

    // QUERY to an unknown path -> the handler returns null -> 404.
    var missing = await conn.SendRequestAsync("QUERY", "https", authority, "/nope",
                      ExtraHeaders: [("content-type", "text/plain")], Body: Encoding.UTF8.GetBytes("x"));
    Check("our client: QUERY on an unknown path -> 404", missing.Status == 404, missing.Status.ToString());

    await conn.CloseAsync();
}

await server.StopAsync();
try { await serverTask; } catch { }

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
