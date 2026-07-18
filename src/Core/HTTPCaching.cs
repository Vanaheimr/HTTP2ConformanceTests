namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

using System.Globalization;


/// <summary>
/// Whether a cache is shared (a proxy/CDN serving many users) or private (a
/// browser-style single-user cache). RFC 9111 treats several directives
/// differently by mode: a shared cache honors <c>s-maxage</c> and must not
/// store <c>private</c> responses or (usually) responses to authenticated
/// requests, while a private cache may.
/// </summary>
public enum HTTPCacheMode
{
    Private,
    Shared
}


/// <summary>
/// A parsed <c>Cache-Control</c> field value (RFC 9111, Section 5.2) — the
/// same grammar serves request and response contexts, so this holds the union
/// of both; a given field only ever sets the directives valid for its context.
/// Unknown directives are ignored (Section 5.2.3).
/// </summary>
public sealed class HTTPCacheControl
{

    public bool  NoStore              { get; private set; }
    public bool  NoCache              { get; private set; }
    public bool  Private              { get; private set; }
    public bool  Public               { get; private set; }
    public bool  MustRevalidate       { get; private set; }
    public bool  ProxyRevalidate      { get; private set; }
    public bool  Immutable            { get; private set; }
    public bool  OnlyIfCached         { get; private set; }   // request
    public bool  NoTransform          { get; private set; }

    public long? MaxAge               { get; private set; }
    public long? SMaxAge              { get; private set; }
    public long? MinFresh             { get; private set; }   // request
    public long? StaleWhileRevalidate { get; private set; }   // RFC 5861
    public long? StaleIfError         { get; private set; }   // RFC 5861

    /// <summary>Request <c>max-stale</c> with a value (seconds); see <see cref="MaxStaleAny"/> for the bare form.</summary>
    public long? MaxStale             { get; private set; }
    /// <summary>Request bare <c>max-stale</c> (no value) — accept a stale response of any age.</summary>
    public bool  MaxStaleAny          { get; private set; }

    private HTTPCacheControl() { }

    public static readonly HTTPCacheControl Empty = new();

    /// <summary>Parse the first <c>cache-control</c> field in a header list (comma-joining multiples).</summary>
    public static HTTPCacheControl FromHeaders(List<(string Name, string Value)> Headers)
    {
        var values = Headers.Where(h => h.Name == "cache-control").Select(h => h.Value).ToList();
        return values.Count == 0 ? Empty : Parse(string.Join(",", values));
    }

    public static HTTPCacheControl Parse(string? Value)
    {

        var cc = new HTTPCacheControl();
        if (string.IsNullOrWhiteSpace(Value))
            return cc;

        foreach (var raw in Value.Split(','))
        {

            var directive = raw.Trim();
            if (directive.Length == 0)
                continue;

            var eq    = directive.IndexOf('=');
            var name  = (eq < 0 ? directive : directive[..eq]).Trim().ToLowerInvariant();
            var value = eq < 0 ? null : directive[(eq + 1)..].Trim().Trim('"');

            long? Seconds() => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : null;

            switch (name)
            {
                case "no-store":               cc.NoStore = true;                 break;
                case "no-cache":               cc.NoCache = true;                 break;
                case "private":                cc.Private = true;                 break;
                case "public":                 cc.Public = true;                  break;
                case "must-revalidate":        cc.MustRevalidate = true;          break;
                case "proxy-revalidate":       cc.ProxyRevalidate = true;         break;
                case "immutable":              cc.Immutable = true;               break;
                case "only-if-cached":         cc.OnlyIfCached = true;            break;
                case "no-transform":           cc.NoTransform = true;             break;
                case "max-age":                cc.MaxAge = Seconds();             break;
                case "s-maxage":               cc.SMaxAge = Seconds();            break;
                case "min-fresh":              cc.MinFresh = Seconds();           break;
                case "stale-while-revalidate": cc.StaleWhileRevalidate = Seconds(); break;
                case "stale-if-error":         cc.StaleIfError = Seconds();       break;
                case "max-stale":
                    if (value is null) cc.MaxStaleAny = true;
                    else               cc.MaxStale = Seconds();
                    break;
            }

        }

        return cc;

    }

}


/// <summary>
/// A response stored in an <see cref="HTTP2CachingClient"/>'s cache, with the
/// timing metadata RFC 9111 Section 4.2.3 needs to compute its age, and the
/// request-header values that select it (for <c>Vary</c> matching).
/// </summary>
public sealed class HTTPStoredResponse
{

    public required int                               Status         { get; set; }
    public required List<(string Name, string Value)> Headers        { get; set; }
    public required byte[]                            Body           { get; set; }

    /// <summary>When the cache sent the request that produced this response (for the age correction, Section 4.2.3).</summary>
    public required DateTimeOffset                    RequestTime    { get; set; }
    /// <summary>When the cache received this response (the reference point for resident time).</summary>
    public required DateTimeOffset                    ResponseTime   { get; set; }

    /// <summary>The values of the request-header fields named by this response's <c>Vary</c>, captured at store time.</summary>
    public required List<(string Name, string Value)> VaryKeyHeaders { get; set; }

    public string? Header(string Name) => Headers.FirstOrDefault(h => h.Name == Name).Value;

    public string?             ETag         => Header("etag");
    public string?             LastModified => Header("last-modified");
    public HTTPCacheControl    CacheControl => HTTPCacheControl.FromHeaders(Headers);

    public DateTimeOffset?     DateValue    => HTTPCache.TryParseHttpDate(Header("date"));
    public long                AgeValue     => long.TryParse(Header("age"), out var a) && a >= 0 ? a : 0;

}


/// <summary>
/// The direction-neutral RFC 9111 caching *logic* — storability, age and
/// freshness computation, revalidation, <c>Vary</c> keying — with no notion of
/// an actual store or transport. The store and the "when do I go to the
/// origin?" plumbing live in <see cref="HTTP2CachingClient"/> (client-side);
/// this class is the reusable brain, sitting in the shared library next to
/// <see cref="HTTPSemantics"/> (whose conditional-request handling this is the
/// cache/client counterpart of — a cache is what *generates* the
/// If-None-Match/If-Modified-Since revalidations that HTTPSemantics answers).
/// </summary>
public static class HTTPCache
{

    /// <summary>
    /// Status codes a response can be stored under heuristically, absent
    /// explicit freshness (RFC 9110, Section 15.1 "heuristically cacheable").
    /// </summary>
    private static readonly HashSet<int> HeuristicallyCacheableStatus =
        [200, 203, 204, 206, 300, 301, 308, 404, 405, 410, 414, 451, 501];

    #region Storability (Section 3)

    /// <summary>
    /// May a cache in Mode store this response to a request with the given
    /// method + request Cache-Control? (RFC 9111, Section 3, plus the shared-cache
    /// Authorization rule of Section 3.5.)
    /// </summary>
    public static bool IsStorable(
        string                            Method,
        bool                              RequestHasAuthorization,
        HTTPCacheControl                  RequestCacheControl,
        int                               Status,
        List<(string Name, string Value)> ResponseHeaders,
        HTTPCacheControl                  ResponseCacheControl,
        HTTPCacheMode                     Mode)
    {

        // Only responses to safe, cacheable methods (GET/HEAD here).
        if (Method is not ("GET" or "HEAD"))
            return false;

        // no-store on either side forbids storage.
        if (RequestCacheControl.NoStore || ResponseCacheControl.NoStore)
            return false;

        // A shared cache must not store a "private" response.
        if (Mode == HTTPCacheMode.Shared && ResponseCacheControl.Private)
            return false;

        // A shared cache must not store a response to an authenticated request
        // unless explicitly allowed (Section 3.5).
        if (Mode == HTTPCacheMode.Shared && RequestHasAuthorization &&
            !(ResponseCacheControl.Public || ResponseCacheControl.SMaxAge is not null || ResponseCacheControl.MustRevalidate))
            return false;

        // Storable if there's explicit freshness info or a validator or the
        // status is heuristically cacheable (Section 3).
        var hasExplicitFreshness = ResponseCacheControl.MaxAge is not null
                                || ResponseCacheControl.SMaxAge is not null
                                || ResponseHeaders.Any(h => h.Name == "expires");
        var hasValidator = ResponseHeaders.Any(h => h.Name is "etag" or "last-modified");

        return hasExplicitFreshness || hasValidator || HeuristicallyCacheableStatus.Contains(Status);

    }

    #endregion


    #region Age + freshness (Section 4.2)

    /// <summary>Current age of a stored response (RFC 9111, Section 4.2.3).</summary>
    public static TimeSpan CurrentAge(HTTPStoredResponse Stored, DateTimeOffset Now)
    {

        var dateValue    = Stored.DateValue ?? Stored.ResponseTime;
        var apparentAge  = Max(TimeSpan.Zero, Stored.ResponseTime - dateValue);
        var responseDelay = Stored.ResponseTime - Stored.RequestTime;
        var correctedAge = TimeSpan.FromSeconds(Stored.AgeValue) + responseDelay;
        var correctedInitialAge = Max(apparentAge, correctedAge);
        var residentTime = Now - Stored.ResponseTime;

        return correctedInitialAge + residentTime;

    }

    /// <summary>
    /// Freshness lifetime of a stored response (RFC 9111, Section 4.2.1), in
    /// the given cache mode. Returns null if none can be determined and the
    /// status isn't heuristically cacheable — such a response is always
    /// treated as needing revalidation.
    /// </summary>
    public static TimeSpan? FreshnessLifetime(HTTPStoredResponse Stored, HTTPCacheMode Mode)
    {

        var cc = Stored.CacheControl;

        // s-maxage takes precedence for a shared cache.
        if (Mode == HTTPCacheMode.Shared && cc.SMaxAge is not null)
            return TimeSpan.FromSeconds(cc.SMaxAge.Value);

        if (cc.MaxAge is not null)
            return TimeSpan.FromSeconds(cc.MaxAge.Value);

        // Expires - Date (Section 4.2.1).
        var expires = TryParseHttpDate(Stored.Header("expires"));
        if (expires is not null)
        {
            var date = Stored.DateValue ?? Stored.ResponseTime;
            return expires.Value - date;
        }

        // Heuristic: 10% of the interval since Last-Modified (Section 4.2.2).
        var lastModified = TryParseHttpDate(Stored.LastModified);
        if (lastModified is not null && HeuristicallyCacheableStatus.Contains(Stored.Status))
        {
            var date = Stored.DateValue ?? Stored.ResponseTime;
            var interval = date - lastModified.Value;
            if (interval > TimeSpan.Zero)
                return interval * 0.1;
        }

        return null;

    }

    /// <summary>
    /// Decide how a stored response may be used for a request right now:
    /// served as fresh, served stale (max-stale / stale-while-revalidate),
    /// or must be revalidated first. Encodes the request/response directive
    /// interplay of RFC 9111 Sections 4.2 / 5.2.
    /// </summary>
    public static HTTPCacheDecision Evaluate(
        HTTPStoredResponse Stored,
        HTTPCacheControl   RequestCacheControl,
        HTTPCacheMode      Mode,
        DateTimeOffset     Now)
    {

        var responseCC = Stored.CacheControl;

        // no-cache (on request or response) and must-revalidate force validation.
        var mustRevalidate = RequestCacheControl.NoCache
                          || responseCC.NoCache
                          || responseCC.MustRevalidate
                          || (Mode == HTTPCacheMode.Shared && responseCC.ProxyRevalidate);

        var age      = CurrentAge(Stored, Now);
        var lifetime = FreshnessLifetime(Stored, Mode);

        // immutable: fresh within its lifetime, never revalidate early (we still
        // respect the lifetime; immutable mainly suppresses conditional requests
        // a client might otherwise send — modeled as "don't force revalidation").
        var freshnessLifetime = lifetime ?? TimeSpan.Zero;
        var staleness         = age - freshnessLifetime;   // >0 means stale by that much
        var isFresh           = staleness < TimeSpan.Zero;

        // Request min-fresh: the response must stay fresh for at least N more seconds.
        if (isFresh && RequestCacheControl.MinFresh is not null &&
            (freshnessLifetime - age) < TimeSpan.FromSeconds(RequestCacheControl.MinFresh.Value))
            isFresh = false;

        if (isFresh && !mustRevalidate)
            return new HTTPCacheDecision(HTTPCacheUsability.Fresh, age);

        // Stale. A stale response may still be served if the request allows it
        // via max-stale and the response doesn't demand revalidation.
        if (!mustRevalidate && staleness >= TimeSpan.Zero)
        {
            if (RequestCacheControl.MaxStaleAny)
                return new HTTPCacheDecision(HTTPCacheUsability.Stale, age);

            if (RequestCacheControl.MaxStale is not null &&
                staleness <= TimeSpan.FromSeconds(RequestCacheControl.MaxStale.Value))
                return new HTTPCacheDecision(HTTPCacheUsability.Stale, age);

            // RFC 5861 stale-while-revalidate: serve stale now, revalidate in
            // the background, within the SWR window past expiry.
            if (responseCC.StaleWhileRevalidate is not null &&
                staleness <= TimeSpan.FromSeconds(responseCC.StaleWhileRevalidate.Value))
                return new HTTPCacheDecision(HTTPCacheUsability.StaleWhileRevalidate, age);
        }

        return new HTTPCacheDecision(HTTPCacheUsability.MustRevalidate, age);

    }

    #endregion


    #region Revalidation (Section 4.3)

    /// <summary>
    /// Build the conditional request-header fields to revalidate Stored
    /// (RFC 9111, Section 4.3.1): <c>If-None-Match</c> from its ETag and/or
    /// <c>If-Modified-Since</c> from its Last-Modified.
    /// </summary>
    public static List<(string Name, string Value)> ConditionalHeaders(HTTPStoredResponse Stored)
    {

        var headers = new List<(string Name, string Value)>();

        if (Stored.ETag is not null)
            headers.Add(("if-none-match", Stored.ETag));

        if (Stored.LastModified is not null)
            headers.Add(("if-modified-since", Stored.LastModified));

        return headers;

    }

    /// <summary>
    /// Apply a 304 (Not Modified) revalidation result to a stored response
    /// (RFC 9111, Section 3.2): refresh its stored header fields from the 304's
    /// (validators, Cache-Control, Date, …) and reset its timing so it counts
    /// as freshly validated.
    /// </summary>
    public static void UpdateFrom304(
        HTTPStoredResponse                Stored,
        List<(string Name, string Value)> NotModifiedHeaders,
        DateTimeOffset                    RequestTime,
        DateTimeOffset                    ResponseTime)
    {

        // Replace/refresh each header the 304 carries (except the framing pseudo :status).
        foreach (var (name, value) in NotModifiedHeaders)
        {
            if (name.StartsWith(':'))
                continue;
            Stored.Headers.RemoveAll(h => h.Name == name);
            Stored.Headers.Add((name, value));
        }

        Stored.RequestTime  = RequestTime;
        Stored.ResponseTime = ResponseTime;

    }

    #endregion


    #region Vary keying (Section 4.1)

    /// <summary>
    /// The request-header field names a response's <c>Vary</c> selects on
    /// (lowercased). A <c>Vary: *</c> is reported as the single name "*",
    /// which <see cref="SelectionMatches"/> treats as never-reusable.
    /// </summary>
    public static List<string> VaryFieldNames(List<(string Name, string Value)> ResponseHeaders)
        => ResponseHeaders.Where(h => h.Name == "vary")
                          .SelectMany(h => h.Value.Split(','))
                          .Select(v => v.Trim().ToLowerInvariant())
                          .Where(v => v.Length > 0)
                          .Distinct()
                          .ToList();

    /// <summary>Capture the values of the Vary-selected request headers, for storing alongside a response.</summary>
    public static List<(string Name, string Value)> SelectRequestHeaders(
        List<(string Name, string Value)> RequestHeaders,
        List<string>                      VaryNames)
        => VaryNames.Where(n => n != "*")
                    .Select(n => (n, RequestHeaders.FirstOrDefault(h => h.Name == n).Value ?? ""))
                    .ToList();

    /// <summary>
    /// Does a new request's selecting headers match those a stored variant was
    /// keyed on (RFC 9111, Section 4.1)? A stored variant whose Vary included
    /// "*" never matches.
    /// </summary>
    public static bool SelectionMatches(
        HTTPStoredResponse                Stored,
        List<(string Name, string Value)> RequestHeaders)
    {

        var varyNames = VaryFieldNames(Stored.Headers);

        if (varyNames.Contains("*"))
            return false;

        foreach (var (name, storedValue) in Stored.VaryKeyHeaders)
        {
            var current = RequestHeaders.FirstOrDefault(h => h.Name == name).Value ?? "";
            if (!string.Equals(current, storedValue, StringComparison.Ordinal))
                return false;
        }

        return true;

    }

    #endregion


    internal static DateTimeOffset? TryParseHttpDate(string? Value)
        => Value is not null &&
           (DateTimeOffset.TryParseExact(Value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var r)
            || DateTimeOffset.TryParse(Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out r))
                ? r
                : null;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

}


/// <summary>How a stored response may be used for the current request (result of <see cref="HTTPCache.Evaluate"/>).</summary>
public enum HTTPCacheUsability
{
    /// <summary>Fresh — serve directly, no origin contact.</summary>
    Fresh,
    /// <summary>Stale but the request permits it (max-stale) — serve as-is.</summary>
    Stale,
    /// <summary>Stale but within stale-while-revalidate — serve now, revalidate in the background.</summary>
    StaleWhileRevalidate,
    /// <summary>Must revalidate with the origin before use.</summary>
    MustRevalidate
}

/// <summary>The usability verdict plus the response's current age (added as the <c>Age</c> header when served).</summary>
public readonly record struct HTTPCacheDecision(HTTPCacheUsability Usability, TimeSpan Age);
