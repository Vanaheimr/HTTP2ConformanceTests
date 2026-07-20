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

using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;
using OurServer = org.GraphDefined.Vanaheimr.Hermod.HTTP2.HTTP2Server;

// RFC 9111 caching tests: an origin server that counts how often each path is
// actually fetched (so a cache HIT is provably a no-origin-round-trip) and
// answers conditional revalidations with 304, fronted by our HTTP2CachingClient
// in both Private and Shared modes.

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

const int port = 9447;
const string authority = "localhost:9447";
var originHits = new ConcurrentDictionary<string, int>();

Task<(List<(string Name, string Value)>, byte[]?)> Origin(
    uint sid, List<(string Name, string Value)> h, byte[]? body, CancellationToken ct)
{
    var path        = h.First(x => x.Name == ":path").Value;
    var ifNoneMatch = h.FirstOrDefault(x => x.Name == "if-none-match").Value;
    var acceptLang  = h.FirstOrDefault(x => x.Name == "accept-language").Value;
    originHits.AddOrUpdate(path, 1, (_, c) => c + 1);
    var date = DateTimeOffset.UtcNow.ToString("r");

    (List<(string, string)>, byte[]?) Make(int status, byte[]? bodyOut, params (string, string)[] hdrs)
    {
        var headers = new List<(string, string)> { (":status", status.ToString()), ("date", date) };
        headers.AddRange(hdrs);
        if (bodyOut is not null) headers.Add(("content-length", bodyOut.Length.ToString()));
        return (headers, bodyOut);
    }

    // A cacheable resource with an ETag; answers a matching If-None-Match with 304.
    (List<(string, string)>, byte[]?) Cacheable(string cc, string etag, string bodyText, params (string, string)[] extra)
        => ifNoneMatch == etag
            ? Make(304, null, [("cache-control", cc), ("etag", etag), .. extra])
            : Make(200, Encoding.UTF8.GetBytes(bodyText), [("cache-control", cc), ("etag", etag), .. extra]);

    return Task.FromResult<(List<(string, string)>, byte[]?)>(path switch
    {
        "/max-age-60"     => Cacheable("max-age=60",           "\"ma60\"",  "fresh"),
        "/revalidate"     => Cacheable("max-age=0",            "\"rev\"",   "revalidated"),
        "/no-store"       => Make(200, Encoding.UTF8.GetBytes("nostore"), ("cache-control", "no-store")),
        "/private"        => Cacheable("private, max-age=60",  "\"prv\"",   "private-body"),
        "/s-maxage"       => Cacheable("max-age=0, s-maxage=60", "\"sm\"",  "shared-body"),
        "/auth-cacheable" => Cacheable("max-age=60",           "\"auth\"",  "auth-body"),
        "/swr"            => Cacheable("max-age=0, stale-while-revalidate=60", "\"swr\"", "swr-body"),
        "/invalidate"     => Cacheable("max-age=60",           "\"inv\"",   "inv-body"),
        "/vary"           => Cacheable("max-age=60",           "\"vary-" + acceptLang + "\"",
                                       "lang=" + (acceptLang ?? "none"), ("vary", "accept-language")),
        "/heuristic"      => Make(200, Encoding.UTF8.GetBytes("heur"),
                                  ("last-modified", DateTimeOffset.UtcNow.AddHours(-1).ToString("r"))),
        _                 => Make(404, Encoding.UTF8.GetBytes("nope")),
    });
}

var server = new OurServer(IPAddress.Loopback, port, MakeCert(), Origin);
var serverTask = server.RunAsync();
await Task.Delay(400);

RemoteCertificateValidationCallback acceptAny = (_, _, _, _) => true;
async Task<HTTP2CachingClient> NewCache(HTTPCacheMode mode)
    => new(await HTTP2Client.ConnectAsync("localhost", port, acceptAny), "https", authority, mode);

int OriginHits(string path) => originHits.TryGetValue(path, out var c) ? c : 0;

var cache = await NewCache(HTTPCacheMode.Private);

Console.WriteLine("=== Freshness: max-age (MISS then HIT) ===");
var r1 = await cache.GetAsync("/max-age-60");
var r2 = await cache.GetAsync("/max-age-60");
Check("both 200", r1.Status == 200 && r2.Status == 200);
Check("only one origin fetch (2nd served fresh)", OriginHits("/max-age-60") == 1, $"origin hits = {OriginHits("/max-age-60")}");
Check("cached body identical", Encoding.UTF8.GetString(r2.Body) == "fresh");
Check("served response carries Age", r2.HeaderValue("age") is not null, r2.HeaderValue("age") ?? "(none)");

Console.WriteLine("\n=== Revalidation: max-age=0 -> conditional -> 304 -> served from cache ===");
var v1 = await cache.GetAsync("/revalidate");
var revalBefore = cache.Revalidations;
var v2 = await cache.GetAsync("/revalidate");
Check("both 200 to caller", v1.Status == 200 && v2.Status == 200, $"{v1.Status}/{v2.Status}");
Check("origin was contacted twice (revalidation)", OriginHits("/revalidate") == 2, $"origin hits = {OriginHits("/revalidate")}");
Check("counted as a revalidation", cache.Revalidations == revalBefore + 1);
Check("body served from cache after 304", Encoding.UTF8.GetString(v2.Body) == "revalidated");

Console.WriteLine("\n=== no-store: never cached ===");
await cache.GetAsync("/no-store");
await cache.GetAsync("/no-store");
Check("origin fetched every time", OriginHits("/no-store") == 2, $"origin hits = {OriginHits("/no-store")}");

Console.WriteLine("\n=== Vary: variants cached separately by Accept-Language ===");
await cache.GetAsync("/vary", [("accept-language", "en")]);
await cache.GetAsync("/vary", [("accept-language", "en")]);           // HIT (en)
var deResp = await cache.GetAsync("/vary", [("accept-language", "de")]); // MISS (de variant)
await cache.GetAsync("/vary", [("accept-language", "de")]);           // HIT (de)
Check("one fetch per distinct variant", OriginHits("/vary") == 2, $"origin hits = {OriginHits("/vary")}");
Check("de variant body correct", Encoding.UTF8.GetString(deResp.Body) == "lang=de", Encoding.UTF8.GetString(deResp.Body));

Console.WriteLine("\n=== Heuristic freshness (Last-Modified, no Cache-Control) ===");
await cache.GetAsync("/heuristic");
await cache.GetAsync("/heuristic");
Check("heuristically fresh -> 2nd served from cache", OriginHits("/heuristic") == 1, $"origin hits = {OriginHits("/heuristic")}");

Console.WriteLine("\n=== only-if-cached with nothing stored -> 504 ===");
var oic = await cache.GetAsync("/never-seen", [("cache-control", "only-if-cached")]);
Check("504 Gateway Timeout", oic.Status == 504, oic.Status.ToString());

Console.WriteLine("\n=== Invalidation on unsafe method ===");
await cache.GetAsync("/invalidate");
await cache.GetAsync("/invalidate");                                   // HIT
Check("cached before POST", OriginHits("/invalidate") == 1, $"origin hits = {OriginHits("/invalidate")}");
await cache.SendRequestAsync("POST", "/invalidate", Body: Encoding.UTF8.GetBytes("x"));
await cache.GetAsync("/invalidate");                                   // MISS again
Check("re-fetched after POST invalidation", OriginHits("/invalidate") == 3, $"origin hits = {OriginHits("/invalidate")}");

Console.WriteLine("\n=== stale-while-revalidate: serve stale, refresh in background ===");
await cache.GetAsync("/swr");                                          // MISS, store (max-age=0)
var swrHitsBefore = cache.Hits;
var swr = await cache.GetAsync("/swr");                                // stale -> served + bg revalidate
Check("stale served immediately (a hit)", cache.Hits == swrHitsBefore + 1 && Encoding.UTF8.GetString(swr.Body) == "swr-body");
await Task.Delay(500);                                                 // let the background revalidation land
Check("background revalidation contacted origin", OriginHits("/swr") >= 2, $"origin hits = {OriginHits("/swr")}");

Console.WriteLine("\n=== Shared vs private: the 'private' directive ===");
var shared = await NewCache(HTTPCacheMode.Shared);
await shared.GetAsync("/private");
await shared.GetAsync("/private");
Check("shared cache does NOT store a 'private' response", OriginHits("/private") == 2, $"origin hits = {OriginHits("/private")}");
var privateCache = await NewCache(HTTPCacheMode.Private);
await privateCache.GetAsync("/private");
await privateCache.GetAsync("/private");
Check("private cache DOES store it", OriginHits("/private") == 3, $"origin hits = {OriginHits("/private")}");

Console.WriteLine("\n=== Shared cache honors s-maxage over max-age=0 ===");
await shared.GetAsync("/s-maxage");
await shared.GetAsync("/s-maxage");
Check("shared: s-maxage=60 -> fresh HIT", OriginHits("/s-maxage") == 1, $"origin hits = {OriginHits("/s-maxage")}");
var privS = await NewCache(HTTPCacheMode.Private);
await privS.GetAsync("/s-maxage");
await privS.GetAsync("/s-maxage");
Check("private: ignores s-maxage, max-age=0 -> revalidate", OriginHits("/s-maxage") == 3, $"origin hits = {OriginHits("/s-maxage")}");

Console.WriteLine("\n=== Shared cache + Authorization (Section 3.5) ===");
var auth = new List<(string Name, string Value)> { ("authorization", "Bearer t") };
await shared.GetAsync("/auth-cacheable", auth);
await shared.GetAsync("/auth-cacheable", auth);
Check("shared cache does NOT store authenticated response", OriginHits("/auth-cacheable") == 2, $"origin hits = {OriginHits("/auth-cacheable")}");
var privA = await NewCache(HTTPCacheMode.Private);
await privA.GetAsync("/auth-cacheable", auth);
await privA.GetAsync("/auth-cacheable", auth);
Check("private cache stores it", OriginHits("/auth-cacheable") == 3, $"origin hits = {OriginHits("/auth-cacheable")}");

await server.StopAsync();
try { await serverTask; } catch { }

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
