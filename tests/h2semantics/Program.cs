using System.Net;
using System.Text;

// Verifies HTTPSemantics.cs (RFC 9110 "core mechanics": GET/HEAD/OPTIONS,
// conditional requests, Range requests) against a strict client
// (SocketsHttpHandler / HttpClient) driving real HTTP/2 requests at
// /files/resource.txt.

AppContext.SetSwitch("System.Net.SocketsHttpHandler.Http2FlowControl.DisableDynamicWindowSizing", true);

var handler = new SocketsHttpHandler {
    SslOptions = new System.Net.Security.SslClientAuthenticationOptions {
        RemoteCertificateValidationCallback = (_, _, _, _) => true
    }
};

using var client = new HttpClient(handler) {
    DefaultRequestVersion = HttpVersion.Version20,
    DefaultVersionPolicy  = HttpVersionPolicy.RequestVersionExact,
    Timeout               = TimeSpan.FromSeconds(8)
};

const string Url = "https://127.0.0.1:8443/files/resource.txt";

var passed = 0;
var failed = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"  ✓ {name}" + (detail.Length > 0 ? $"  ({detail})" : "")); }
    else    { failed++; Console.WriteLine($"  ✗ {name}" + (detail.Length > 0 ? $"  — {detail}" : "")); }
}

async Task<HttpResponseMessage> Send(HttpMethod method, string url, Action<HttpRequestMessage>? configure = null)
{
    // A fresh HttpRequestMessage defaults to HTTP/1.1 + VersionPolicy.RequestVersionOrLower
    // regardless of the HttpClient's own DefaultRequestVersion/DefaultVersionPolicy —
    // those are only applied by HttpClient's own GetAsync/PostAsync/... convenience
    // methods, not by SendAsync(HttpRequestMessage). Must set both explicitly here,
    // or the ALPN offer ends up being for http/1.1 and the server logs "Unknown ALPN
    // protocol" and closes the connection.
    var req = new HttpRequestMessage(method, url) {
        Version       = HttpVersion.Version20,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact
    };
    configure?.Invoke(req);
    return await client.SendAsync(req);
}

Console.WriteLine("=== Baseline GET ===");
var baseline = await Send(HttpMethod.Get, Url);
var baselineBody = await baseline.Content.ReadAsByteArrayAsync();
var etag = baseline.Headers.ETag?.Tag ?? "";
var lastModified = baseline.Content.Headers.LastModified;

Check("200 OK",                    baseline.StatusCode == HttpStatusCode.OK, $"{(int) baseline.StatusCode}");
Check("has ETag",                  etag.Length > 0, etag);
Check("has Last-Modified",         lastModified is not null, lastModified?.ToString() ?? "(none)");
Check("Accept-Ranges: bytes",      baseline.Headers.AcceptRanges.Contains("bytes"));
Check("body non-empty",            baselineBody.Length > 0, $"{baselineBody.Length} bytes");

Console.WriteLine("\n=== HEAD ===");
var head = await Send(HttpMethod.Head, Url);
var headBody = await head.Content.ReadAsByteArrayAsync();
Check("200 OK",                    head.StatusCode == HttpStatusCode.OK);
Check("Content-Length matches GET", head.Content.Headers.ContentLength == baselineBody.Length,
      $"HEAD={head.Content.Headers.ContentLength} GET={baselineBody.Length}");
Check("empty body",                headBody.Length == 0, $"{headBody.Length} bytes");

Console.WriteLine("\n=== OPTIONS ===");
var options = await Send(HttpMethod.Options, Url);
Check("204 No Content",            options.StatusCode == HttpStatusCode.NoContent, $"{(int) options.StatusCode}");
Check("Allow: GET, HEAD, OPTIONS",  options.Content.Headers.Allow.SequenceEqual(["GET", "HEAD", "OPTIONS"]),
      string.Join(", ", options.Content.Headers.Allow));

Console.WriteLine("\n=== POST (unsupported method) ===");
var post = await Send(HttpMethod.Post, Url, r => r.Content = new StringContent("x"));
Check("405 Method Not Allowed",    post.StatusCode == HttpStatusCode.MethodNotAllowed, $"{(int) post.StatusCode}");
Check("Allow header present",      post.Content.Headers.Allow.Count > 0 || post.Headers.Contains("Allow"));

Console.WriteLine("\n=== 404 via HTTPSemantics (distinct from HandleRequest's own default 404) ===");
var missing = await Send(HttpMethod.Get, "https://127.0.0.1:8443/files/missing.txt");
Check("404 Not Found",             missing.StatusCode == HttpStatusCode.NotFound, $"{(int) missing.StatusCode}");

Console.WriteLine("\n=== If-None-Match ===");
var inm304 = await Send(HttpMethod.Get, Url, r => r.Headers.TryAddWithoutValidation("If-None-Match", etag));
Check("matching ETag -> 304",      inm304.StatusCode == HttpStatusCode.NotModified, $"{(int) inm304.StatusCode}");
Check("304 has no body",           (await inm304.Content.ReadAsByteArrayAsync()).Length == 0);

var inmMismatch = await Send(HttpMethod.Get, Url, r => r.Headers.TryAddWithoutValidation("If-None-Match", "\"bogus-etag\""));
Check("mismatched ETag -> 200",    inmMismatch.StatusCode == HttpStatusCode.OK, $"{(int) inmMismatch.StatusCode}");

var inmStar = await Send(HttpMethod.Get, Url, r => r.Headers.TryAddWithoutValidation("If-None-Match", "*"));
Check("\"*\" (resource exists) -> 304", inmStar.StatusCode == HttpStatusCode.NotModified, $"{(int) inmStar.StatusCode}");

Console.WriteLine("\n=== If-Match ===");
var imMatch = await Send(HttpMethod.Get, Url, r => r.Headers.TryAddWithoutValidation("If-Match", etag));
Check("matching ETag -> 200",      imMatch.StatusCode == HttpStatusCode.OK, $"{(int) imMatch.StatusCode}");

var imMismatch = await Send(HttpMethod.Get, Url, r => r.Headers.TryAddWithoutValidation("If-Match", "\"bogus-etag\""));
Check("mismatched ETag -> 412",    imMismatch.StatusCode == HttpStatusCode.PreconditionFailed, $"{(int) imMismatch.StatusCode}");

Console.WriteLine("\n=== If-Modified-Since / If-Unmodified-Since ===");
var future = DateTimeOffset.UtcNow.AddDays(1).ToString("r");
var past   = DateTimeOffset.UtcNow.AddDays(-365).ToString("r");

var imsFuture = await Send(HttpMethod.Get, Url, r => r.Headers.TryAddWithoutValidation("If-Modified-Since", future));
Check("If-Modified-Since: future -> 304", imsFuture.StatusCode == HttpStatusCode.NotModified, $"{(int) imsFuture.StatusCode}");

var imsPast = await Send(HttpMethod.Get, Url, r => r.Headers.TryAddWithoutValidation("If-Modified-Since", past));
Check("If-Modified-Since: past -> 200",   imsPast.StatusCode == HttpStatusCode.OK, $"{(int) imsPast.StatusCode}");

var iusPast = await Send(HttpMethod.Get, Url, r => r.Headers.TryAddWithoutValidation("If-Unmodified-Since", past));
Check("If-Unmodified-Since: past -> 412", iusPast.StatusCode == HttpStatusCode.PreconditionFailed, $"{(int) iusPast.StatusCode}");

var iusFuture = await Send(HttpMethod.Get, Url, r => r.Headers.TryAddWithoutValidation("If-Unmodified-Since", future));
Check("If-Unmodified-Since: future -> 200", iusFuture.StatusCode == HttpStatusCode.OK, $"{(int) iusFuture.StatusCode}");

Console.WriteLine("\n=== Range ===");
var range1 = await Send(HttpMethod.Get, Url, r => r.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 9));
var range1Body = await range1.Content.ReadAsByteArrayAsync();
Check("bytes=0-9 -> 206",          range1.StatusCode == HttpStatusCode.PartialContent, $"{(int) range1.StatusCode}");
Check("10 bytes returned",         range1Body.Length == 10, $"{range1Body.Length} bytes");
Check("matches baseline prefix",   range1Body.SequenceEqual(baselineBody[..10]));
Check("Content-Range present",     range1.Content.Headers.ContentRange is not null,
      range1.Content.Headers.ContentRange?.ToString() ?? "(none)");

var range2 = await Send(HttpMethod.Get, Url, r => r.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(null, 10));
var range2Body = await range2.Content.ReadAsByteArrayAsync();
Check("suffix bytes=-10 -> 206",   range2.StatusCode == HttpStatusCode.PartialContent, $"{(int) range2.StatusCode}");
Check("last 10 bytes match",       range2Body.SequenceEqual(baselineBody[^10..]));

var range3 = await Send(HttpMethod.Get, Url, r => r.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(999_999, 9_999_999));
Check("out-of-bounds range -> 416", range3.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable, $"{(int) range3.StatusCode}");
Check("416 Content-Range: bytes */len", (range3.Content.Headers.ContentRange?.ToString() ?? "").Contains($"*/{baselineBody.Length}"),
      range3.Content.Headers.ContentRange?.ToString() ?? "(none)");

Console.WriteLine("\n=== Multi-Range (multipart/byteranges) ===");
static bool ContainsSeq(byte[] hay, byte[] needle)
{
    for (var i = 0; i + needle.Length <= hay.Length; i++)
    {
        var ok = true;
        for (var j = 0; j < needle.Length; j++)
            if (hay[i + j] != needle[j]) { ok = false; break; }
        if (ok) return true;
    }
    return false;
}

// Two disjoint satisfiable ranges -> a 206 multipart/byteranges body.
var multi = await Send(HttpMethod.Get, Url, r =>
{
    r.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue();
    r.Headers.Range.Ranges.Add(new System.Net.Http.Headers.RangeItemHeaderValue(0, 9));
    r.Headers.Range.Ranges.Add(new System.Net.Http.Headers.RangeItemHeaderValue(20, 29));
});
var multiBody = await multi.Content.ReadAsByteArrayAsync();
Check("multi-range -> 206", multi.StatusCode == HttpStatusCode.PartialContent, $"{(int) multi.StatusCode}");
Check("content-type multipart/byteranges",
      (multi.Content.Headers.ContentType?.MediaType ?? "") == "multipart/byteranges",
      multi.Content.Headers.ContentType?.ToString() ?? "(none)");
var boundary = multi.Content.Headers.ContentType?.Parameters.FirstOrDefault(p => p.Name == "boundary")?.Value ?? "";
Check("multipart carries a boundary", boundary.Length > 0, boundary);
Check("both parts carry a Content-Range",
      ContainsSeq(multiBody, System.Text.Encoding.ASCII.GetBytes("Content-Range: bytes 0-9/")) &&
      ContainsSeq(multiBody, System.Text.Encoding.ASCII.GetBytes("Content-Range: bytes 20-29/")));
Check("part payloads match the resource bytes",
      ContainsSeq(multiBody, baselineBody[0..10]) && ContainsSeq(multiBody, baselineBody[20..30]));

// A set with one satisfiable + one out-of-bounds range -> the unsatisfiable one
// is dropped, leaving a single-part 206 (not multipart).
var multiPartial = await Send(HttpMethod.Get, Url, r =>
{
    r.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue();
    r.Headers.Range.Ranges.Add(new System.Net.Http.Headers.RangeItemHeaderValue(0, 9));
    r.Headers.Range.Ranges.Add(new System.Net.Http.Headers.RangeItemHeaderValue(999_999, 9_999_999));
});
Check("partly-satisfiable multi-range -> 206", multiPartial.StatusCode == HttpStatusCode.PartialContent, $"{(int) multiPartial.StatusCode}");
Check("single satisfiable range -> single-part 206 (not multipart)",
      (multiPartial.Content.Headers.ContentType?.MediaType ?? "") != "multipart/byteranges" &&
      multiPartial.Content.Headers.ContentRange?.ToString() == $"bytes 0-9/{baselineBody.Length}",
      multiPartial.Content.Headers.ContentRange?.ToString() ?? "(none)");

// A set where every range is out of bounds -> 416.
var multiUnsat = await Send(HttpMethod.Get, Url, r =>
{
    r.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue();
    r.Headers.Range.Ranges.Add(new System.Net.Http.Headers.RangeItemHeaderValue(999_999, 9_999_999));
    r.Headers.Range.Ranges.Add(new System.Net.Http.Headers.RangeItemHeaderValue(888_888, 999_999));
});
Check("all-unsatisfiable multi-range -> 416", multiUnsat.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable, $"{(int) multiUnsat.StatusCode}");

Console.WriteLine("\n=== Range + If-Range ===");
var rangeIfRangeMatch = await Send(HttpMethod.Get, Url, r =>
{
    r.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 9);
    r.Headers.TryAddWithoutValidation("If-Range", etag);
});
Check("matching If-Range -> 206",  rangeIfRangeMatch.StatusCode == HttpStatusCode.PartialContent, $"{(int) rangeIfRangeMatch.StatusCode}");

var rangeIfRangeMismatch = await Send(HttpMethod.Get, Url, r =>
{
    r.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 9);
    r.Headers.TryAddWithoutValidation("If-Range", "\"bogus-etag\"");
});
var rangeIfRangeMismatchBody = await rangeIfRangeMismatch.Content.ReadAsByteArrayAsync();
Check("mismatched If-Range -> 200 (full body)", rangeIfRangeMismatch.StatusCode == HttpStatusCode.OK, $"{(int) rangeIfRangeMismatch.StatusCode}");
Check("full body returned",        rangeIfRangeMismatchBody.Length == baselineBody.Length, $"{rangeIfRangeMismatchBody.Length} bytes");

// ---------------------------------------------------------------------------
// Content negotiation (RFC 9110 §12) at /files/greeting — three variants:
//   en text/plain (server default), de text/plain, en application/json.
// ---------------------------------------------------------------------------
const string NegUrl = "https://127.0.0.1:8443/files/greeting";

string? FirstHeader(HttpResponseMessage r, string name)
{
    if (r.Headers.TryGetValues(name, out var v1)) return string.Join(", ", v1);
    if (r.Content.Headers.TryGetValues(name, out var v2)) return string.Join(", ", v2);
    return null;
}

Console.WriteLine("\n=== Negotiation: baseline (no Accept*) -> server-default variant ===");
var negDefault = await Send(HttpMethod.Get, NegUrl);
var negDefaultBody = Encoding.UTF8.GetString(await negDefault.Content.ReadAsByteArrayAsync());
Check("200 OK",                    negDefault.StatusCode == HttpStatusCode.OK, $"{(int) negDefault.StatusCode}");
Check("default is en text/plain",  negDefaultBody.Contains("Hello, world!"), negDefaultBody.Trim());
Check("Content-Type text/plain",   (negDefault.Content.Headers.ContentType?.MediaType ?? "") == "text/plain");
Check("Content-Language: en",      FirstHeader(negDefault, "Content-Language") == "en", FirstHeader(negDefault, "Content-Language") ?? "(none)");
Check("Vary lists accept + accept-language",
      (FirstHeader(negDefault, "Vary") ?? "").Contains("accept") && (FirstHeader(negDefault, "Vary") ?? "").Contains("accept-language"),
      FirstHeader(negDefault, "Vary") ?? "(none)");

Console.WriteLine("\n=== Negotiation: Accept-Language ===");
var negDe = await Send(HttpMethod.Get, NegUrl, r => r.Headers.TryAddWithoutValidation("Accept-Language", "de"));
var negDeBody = Encoding.UTF8.GetString(await negDe.Content.ReadAsByteArrayAsync());
Check("Accept-Language: de -> German body", negDeBody.Contains("Hallo, Welt!"), negDeBody.Trim());
Check("Content-Language: de",      FirstHeader(negDe, "Content-Language") == "de", FirstHeader(negDe, "Content-Language") ?? "(none)");

var negQuality = await Send(HttpMethod.Get, NegUrl, r => r.Headers.TryAddWithoutValidation("Accept-Language", "de;q=0.3, en;q=0.9"));
var negQualityBody = Encoding.UTF8.GetString(await negQuality.Content.ReadAsByteArrayAsync());
Check("de;q=0.3, en;q=0.9 -> English wins on q", negQualityBody.Contains("Hello, world!"), negQualityBody.Trim());

var negSubtag = await Send(HttpMethod.Get, NegUrl, r => r.Headers.TryAddWithoutValidation("Accept-Language", "de-AT"));
var negSubtagBody = Encoding.UTF8.GetString(await negSubtag.Content.ReadAsByteArrayAsync());
Check("de-AT matches de variant (basic filtering)", negSubtagBody.Contains("Hallo, Welt!"), negSubtagBody.Trim());

Console.WriteLine("\n=== Negotiation: Accept (media type) ===");
var negJson = await Send(HttpMethod.Get, NegUrl, r => r.Headers.TryAddWithoutValidation("Accept", "application/json"));
Check("Accept: application/json -> JSON variant",
      (negJson.Content.Headers.ContentType?.MediaType ?? "") == "application/json",
      negJson.Content.Headers.ContentType?.ToString() ?? "(none)");

var negWildcard = await Send(HttpMethod.Get, NegUrl, r => r.Headers.TryAddWithoutValidation("Accept", "text/*"));
Check("Accept: text/* -> a text variant",
      (negWildcard.Content.Headers.ContentType?.MediaType ?? "").StartsWith("text/"),
      negWildcard.Content.Headers.ContentType?.ToString() ?? "(none)");

var negSpecificity = await Send(HttpMethod.Get, NegUrl, r =>
    r.Headers.TryAddWithoutValidation("Accept", "text/*;q=0.3, application/json;q=0.9"));
Check("text/*;q=0.3, application/json;q=0.9 -> JSON wins",
      (negSpecificity.Content.Headers.ContentType?.MediaType ?? "") == "application/json",
      negSpecificity.Content.Headers.ContentType?.ToString() ?? "(none)");

Console.WriteLine("\n=== Negotiation: q=0 vs. no-match policy ===");
// text/plain;q=0 hard-forbids both text variants; only application/json survives.
var negForbidText = await Send(HttpMethod.Get, NegUrl, r =>
    r.Headers.TryAddWithoutValidation("Accept", "text/plain;q=0, application/json"));
Check("text/plain;q=0 -> falls to JSON (not 406)",
      negForbidText.StatusCode == HttpStatusCode.OK &&
      (negForbidText.Content.Headers.ContentType?.MediaType ?? "") == "application/json",
      $"{(int) negForbidText.StatusCode} {negForbidText.Content.Headers.ContentType?.MediaType}");

// */*;q=0 forbids EVERY variant -> genuine 406.
var neg406 = await Send(HttpMethod.Get, NegUrl, r => r.Headers.TryAddWithoutValidation("Accept", "*/*;q=0"));
Check("*/*;q=0 -> 406 Not Acceptable", neg406.StatusCode == HttpStatusCode.NotAcceptable, $"{(int) neg406.StatusCode}");
Check("406 still carries Vary",    (FirstHeader(neg406, "Vary") ?? "").Contains("accept"), FirstHeader(neg406, "Vary") ?? "(none)");

// An unsatisfiable-but-not-forbidden Accept-Language falls back to default (disregard policy), not 406.
var negUnsat = await Send(HttpMethod.Get, NegUrl, r => r.Headers.TryAddWithoutValidation("Accept-Language", "fr"));
Check("Accept-Language: fr (no match, no q=0) -> default, not 406",
      negUnsat.StatusCode == HttpStatusCode.OK, $"{(int) negUnsat.StatusCode}");

Console.WriteLine("\n=== Negotiation: distinct ETags per variant keep conditionals correct ===");
var etagEn = negDefault.Headers.ETag?.Tag ?? "";
var etagDe = negDe.Headers.ETag?.Tag ?? "";
Check("en and de variants have distinct ETags", etagEn.Length > 0 && etagDe.Length > 0 && etagEn != etagDe,
      $"en={etagEn} de={etagDe}");

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
