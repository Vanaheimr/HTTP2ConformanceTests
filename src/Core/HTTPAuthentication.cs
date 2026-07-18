namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

using System.Security.Cryptography;
using System.Text;


/// <summary>
/// The outcome of a successful authentication: who the caller is, plus any
/// scheme-specific claims an app wants to carry forward (e.g. scopes from a
/// bearer token). Deliberately minimal — this framework authenticates
/// (proves identity), it doesn't authorize (decide permissions); that's the
/// app's job with the identity in hand.
/// </summary>
public sealed class HTTPAuthenticatedIdentity
{
    public required string                      Name   { get; init; }
    public          IReadOnlyDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>();
}


/// <summary>
/// One HTTP authentication scheme (RFC 9110, Section 11.1) — Basic, Bearer, … —
/// plugged into the generic framework. A scheme knows its name, how to phrase
/// its <c>WWW-Authenticate</c> challenge, and how to turn the raw credentials
/// from an <c>Authorization</c> header into an identity (by decoding them and
/// deferring the actual "are these valid?" decision to an app-provided
/// validator). The framework itself never validates anything.
/// </summary>
public interface IHTTPAuthenticationScheme
{
    /// <summary>The auth-scheme token, e.g. "Basic" (compared case-insensitively per RFC 9110, Section 11.1).</summary>
    string SchemeName { get; }

    /// <summary>The <c>WWW-Authenticate</c> challenge value this scheme advertises for the given protection space.</summary>
    string BuildChallenge(string Realm);

    /// <summary>
    /// Decode Credentials (everything after the scheme token in the
    /// <c>Authorization</c> header) and validate them, returning the identity
    /// on success or null on any failure (malformed, or the app rejected them).
    /// <paramref name="Method"/> and <paramref name="RequestTarget"/> (the
    /// request's <c>:method</c> and <c>:path</c>) are supplied because some
    /// schemes bind the credentials to them — Digest hashes
    /// <c>method:request-target</c> into its response (RFC 7616, Section 3.4.3);
    /// Basic and Bearer ignore them.
    /// </summary>
    Task<HTTPAuthenticatedIdentity?> AuthenticateAsync(
        string            Credentials,
        string            Method,
        string            RequestTarget,
        CancellationToken CancellationToken);
}


/// <summary>
/// RFC 7617 "Basic" HTTP authentication: credentials are
/// <c>base64(userid ":" password)</c>. The scheme decodes them; the supplied
/// validator decides whether that user/password pair is valid (this framework
/// stays BCL-only and store-agnostic — no password database baked in).
///
/// Basic transmits the password on every request, protected only by TLS —
/// which this stack always uses. A validator SHOULD compare secrets in
/// constant time to avoid timing oracles; that's the app's responsibility.
/// </summary>
public sealed class BasicAuthenticationScheme : IHTTPAuthenticationScheme
{

    private readonly Func<string, string, CancellationToken, Task<HTTPAuthenticatedIdentity?>> validate;

    public BasicAuthenticationScheme(Func<string, string, CancellationToken, Task<HTTPAuthenticatedIdentity?>> Validate)
    {
        validate = Validate;
    }

    public string SchemeName => "Basic";

    // charset="UTF-8" (RFC 7617, Section 2.1) tells the client how to encode
    // non-ASCII credentials before base64.
    public string BuildChallenge(string Realm)
        => $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";

    public async Task<HTTPAuthenticatedIdentity?> AuthenticateAsync(
        string Credentials, string Method, string RequestTarget, CancellationToken CancellationToken)
    {

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(Credentials.Trim()));
        }
        catch (FormatException)
        {
            return null;   // Not valid base64 — malformed credentials.
        }

        // RFC 7617: split on the FIRST colon; the password may itself contain colons.
        var colon = decoded.IndexOf(':');
        if (colon < 0)
            return null;

        return await validate(decoded[..colon], decoded[(colon + 1)..], CancellationToken);

    }

}


/// <summary>
/// RFC 6750 "Bearer" HTTP authentication: the credentials are an opaque bearer
/// token (an OAuth 2.0 access token / JWT / session token). The scheme just
/// hands the token to the app validator — decoding/verifying a JWT signature,
/// checking a token store, etc. is the validator's job and stays out of Core.
/// </summary>
public sealed class BearerAuthenticationScheme : IHTTPAuthenticationScheme
{

    private readonly Func<string, CancellationToken, Task<HTTPAuthenticatedIdentity?>> validate;

    public BearerAuthenticationScheme(Func<string, CancellationToken, Task<HTTPAuthenticatedIdentity?>> Validate)
    {
        validate = Validate;
    }

    public string SchemeName => "Bearer";

    public string BuildChallenge(string Realm)
        => $"Bearer realm=\"{Realm}\"";

    public Task<HTTPAuthenticatedIdentity?> AuthenticateAsync(
        string Credentials, string Method, string RequestTarget, CancellationToken CancellationToken)
        => validate(Credentials.Trim(), CancellationToken);

}


/// <summary>
/// "Token" HTTP authentication — <b>not an IETF standard</b> (the
/// draft-hammer-http-token-auth I-D expired), but a widely used convention,
/// popularized by Rails' <c>ActionController::HttpAuthentication::Token</c> and
/// GitHub-style <c>Authorization: token &lt;token&gt;</c> APIs. Two on-the-wire
/// forms are accepted:
/// <list type="bullet">
///   <item>the bare form <c>Token &lt;token&gt;</c> (GitHub-style), where the
///   whole credential is the token; and</item>
///   <item>the parameterized form <c>Token token="&lt;token&gt;", nonce="…", …</c>
///   (Rails-style), an RFC 7235 auth-param list carrying a mandatory
///   <c>token</c> plus optional extra params.</item>
/// </list>
/// Functionally close to Bearer (RFC 6750) — a single opaque credential, no
/// challenge-response — but with the <c>Token</c> scheme name and the optional
/// structured parameters, which are handed to the validator alongside the token
/// (e.g. to read a Rails-style <c>nonce</c>). Store-agnostic like the others:
/// the app decides whether the token (and params) are valid.
/// </summary>
public sealed class TokenAuthenticationScheme : IHTTPAuthenticationScheme
{

    private readonly Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<HTTPAuthenticatedIdentity?>> validate;

    /// <param name="Validate">
    /// Decides whether a token is valid; also receives any extra auth-params from
    /// the parameterized form (empty for the bare form), so an app can honor a
    /// Rails-style <c>nonce</c> or similar.
    /// </param>
    public TokenAuthenticationScheme(
        Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<HTTPAuthenticatedIdentity?>> Validate)
    {
        validate = Validate;
    }

    public string SchemeName => "Token";

    public string BuildChallenge(string Realm)
        => $"Token realm=\"{Realm}\"";

    public async Task<HTTPAuthenticatedIdentity?> AuthenticateAsync(
        string Credentials, string Method, string RequestTarget, CancellationToken CancellationToken)
    {

        var creds = Credentials.Trim();
        if (creds.Length == 0)
            return null;

        var p = HTTPAuthParams.Parse(creds);

        string token;
        if (p.TryGetValue("token", out var t))
            token = t;                 // Rails-style: Token token="…", …
        else if (p.Count == 0)
            token = creds;             // GitHub-style bare form: Token <token>
        else
            return null;               // structured params but no token= — malformed

        if (token.Length == 0)
            return null;

        return await validate(token, p, CancellationToken);

    }

}


/// <summary>
/// RFC 7616 "Digest" HTTP authentication: a challenge-response scheme that,
/// unlike Basic, never sends the password over the wire. The server issues a
/// one-time <c>nonce</c> in its <c>WWW-Authenticate</c> challenge; the client
/// answers with <c>response = H(HA1:nonce:nc:cnonce:qop:HA2)</c> where
/// <c>HA1 = H(username:realm:password)</c> and <c>HA2 = H(method:request-target)</c>
/// (Section 3.4). The server recomputes the same hash from the password it looks
/// up for that user and compares — so it proves knowledge of the password
/// without ever receiving it.
///
/// Store-agnostic like the other schemes: the app supplies a lookup from
/// username to that user's password (a real deployment would more likely store
/// the precomputed <c>H(username:realm:password)</c>; the plaintext lookup keeps
/// the demo simple). <c>qop=auth</c> with client <c>nc</c>/<c>cnonce</c> is
/// supported (and the legacy RFC 2069 no-<c>qop</c> form); <c>auth-int</c> is not
/// advertised. The nonce is stateless — <c>base64(ticks ":" HMAC(secret, ticks))</c>
/// — so the server validates its own integrity and age (default 5 min) without
/// keeping per-nonce state. Default hash is SHA-256 (RFC 7616); MD5 (RFC 2617)
/// is accepted for interop if a client echoes <c>algorithm=MD5</c>.
/// </summary>
public sealed class DigestAuthenticationScheme : IHTTPAuthenticationScheme
{

    private readonly Func<string, CancellationToken, Task<string?>> lookupPassword;
    private readonly string   realm;
    private readonly string   algorithm;      // advertised in the challenge (default SHA-256)
    private readonly TimeSpan nonceMaxAge;
    private readonly byte[]   nonceSecret = RandomNumberGenerator.GetBytes(32);

    /// <param name="Realm">The protection space; folded into HA1, so it must match what the client used.</param>
    /// <param name="LookupPassword">Maps a username to that user's password (null ⇒ unknown user).</param>
    /// <param name="Algorithm">The algorithm advertised in the challenge — "SHA-256" (default) or "MD5".</param>
    /// <param name="NonceMaxAge">How long a nonce stays valid (default 5 minutes).</param>
    public DigestAuthenticationScheme(
        string                                        Realm,
        Func<string, CancellationToken, Task<string?>> LookupPassword,
        string                                        Algorithm   = "SHA-256",
        TimeSpan?                                     NonceMaxAge = null)
    {
        realm          = Realm;
        lookupPassword = LookupPassword;
        algorithm      = Algorithm;
        nonceMaxAge    = NonceMaxAge ?? TimeSpan.FromMinutes(5);
    }

    public string SchemeName => "Digest";

    // A fresh nonce every challenge; qop="auth" so clients include nc/cnonce
    // (protecting against replay of a captured response). No opaque/domain — both
    // optional (RFC 7616, Section 3.3).
    public string BuildChallenge(string Realm)
        => $"Digest realm=\"{Realm}\", qop=\"auth\", algorithm={algorithm}, nonce=\"{CreateNonce()}\"";

    public async Task<HTTPAuthenticatedIdentity?> AuthenticateAsync(
        string Credentials, string Method, string RequestTarget, CancellationToken CancellationToken)
    {

        var p = HTTPAuthParams.Parse(Credentials);

        // Required fields (RFC 7616, Section 3.4). username, realm, nonce, uri
        // and response must all be present.
        if (!p.TryGetValue("username", out var username) || username.Length == 0 ||
            !p.TryGetValue("nonce",    out var nonce)    ||
            !p.TryGetValue("uri",      out var uri)      ||
            !p.TryGetValue("response", out var clientResponse))
            return null;

        // The realm the client hashed must be our protection space, and the
        // digest-uri must be the request-target it actually sent — otherwise the
        // response was computed for a different scope/resource.
        if (p.TryGetValue("realm", out var clientRealm) && clientRealm != realm)
            return null;
        if (uri != RequestTarget)
            return null;

        // Reject a nonce we didn't issue, tampered with, or that has expired.
        if (!ValidateNonce(nonce))
            return null;

        // The algorithm the client used (defaults to MD5 when absent, RFC 2617);
        // accept only SHA-256 or MD5 (and their -sess variants).
        var alg = p.TryGetValue("algorithm", out var a) ? a : "MD5";
        if (!IsSupportedAlgorithm(alg))
            return null;

        var password = await lookupPassword(username, CancellationToken);
        if (password is null)
            return null;   // Unknown user — indistinguishable to the client from a wrong password.

        var ha1 = H(alg, $"{username}:{realm}:{password}");

        // algorithm "-sess" (Section 3.4.2) mixes the nonce/cnonce into HA1.
        if (alg.EndsWith("-sess", StringComparison.OrdinalIgnoreCase))
        {
            if (!p.TryGetValue("cnonce", out var sessCnonce))
                return null;
            ha1 = H(alg, $"{ha1}:{nonce}:{sessCnonce}");
        }

        var ha2 = H(alg, $"{Method}:{uri}");

        string expected;
        if (p.TryGetValue("qop", out var qop) && qop.Length > 0)
        {
            // qop=auth (Section 3.4.1). We only advertise auth; reject auth-int.
            if (!string.Equals(qop, "auth", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!p.TryGetValue("nc", out var nc) || !p.TryGetValue("cnonce", out var cnonce))
                return null;
            expected = H(alg, $"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");
        }
        else
            // Legacy RFC 2069 (no qop): response = H(HA1:nonce:HA2).
            expected = H(alg, $"{ha1}:{nonce}:{ha2}");

        // Constant-time compare of the two hex digests (equal length per algorithm).
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(clientResponse.ToLowerInvariant())))
            return null;

        return new HTTPAuthenticatedIdentity { Name = username };

    }

    private static bool IsSupportedAlgorithm(string Algorithm)
        => Algorithm.Equals("SHA-256",      StringComparison.OrdinalIgnoreCase)
        || Algorithm.Equals("SHA-256-sess", StringComparison.OrdinalIgnoreCase)
        || Algorithm.Equals("MD5",          StringComparison.OrdinalIgnoreCase)
        || Algorithm.Equals("MD5-sess",     StringComparison.OrdinalIgnoreCase);

    /// <summary>Hex-encode H(input) with the algorithm's hash (SHA-256 unless the client chose MD5).</summary>
    private static string H(string Algorithm, string Input)
    {
        var bytes  = Encoding.UTF8.GetBytes(Input);
        var digest = Algorithm.StartsWith("SHA-256", StringComparison.OrdinalIgnoreCase)
                         ? SHA256.HashData(bytes)
                         : MD5.HashData(bytes);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>A stateless nonce: <c>base64(ticks ":" base64(HMAC-SHA256(secret, ticks)))</c>.</summary>
    private string CreateNonce()
    {
        var ticks = DateTimeOffset.UtcNow.UtcTicks.ToString();
        var mac   = HMACSHA256.HashData(nonceSecret, Encoding.ASCII.GetBytes(ticks));
        return Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ticks}:{Convert.ToBase64String(mac)}"));
    }

    /// <summary>Validate a nonce we issued: HMAC integrity + not older than <see cref="nonceMaxAge"/>.</summary>
    private bool ValidateNonce(string Nonce)
    {
        try
        {
            var raw   = Encoding.ASCII.GetString(Convert.FromBase64String(Nonce));
            var colon = raw.IndexOf(':');
            if (colon < 0)
                return false;

            var ticksStr = raw[..colon];
            if (!long.TryParse(ticksStr, out var ticks))
                return false;

            var expectedMac = HMACSHA256.HashData(nonceSecret, Encoding.ASCII.GetBytes(ticksStr));
            var providedMac = Convert.FromBase64String(raw[(colon + 1)..]);
            if (!CryptographicOperations.FixedTimeEquals(expectedMac, providedMac))
                return false;

            var age = DateTimeOffset.UtcNow.UtcTicks - ticks;
            return age >= 0 && age <= nonceMaxAge.Ticks;
        }
        catch
        {
            return false;   // Not base64 / malformed — not a nonce we issued.
        }
    }

}


/// <summary>
/// Shared parser for an RFC 7235 auth-param list — comma-separated
/// <c>key=value</c> pairs, values optionally double-quoted (with backslash
/// escapes and embedded commas). Used by the Digest (RFC 7616) and Token
/// schemes, both of which carry their credentials as such a list.
/// </summary>
internal static class HTTPAuthParams
{
    public static Dictionary<string, string> Parse(string Credentials)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i      = 0;
        var n      = Credentials.Length;

        while (i < n)
        {
            while (i < n && (Credentials[i] == ' ' || Credentials[i] == ','))
                i++;

            var keyStart = i;
            while (i < n && Credentials[i] != '=')
                i++;
            if (i >= n)
                break;

            var key = Credentials[keyStart..i].Trim();
            i++;   // skip '='

            string value;
            if (i < n && Credentials[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < n && Credentials[i] != '"')
                {
                    if (Credentials[i] == '\\' && i + 1 < n) { sb.Append(Credentials[i + 1]); i += 2; }
                    else                                     { sb.Append(Credentials[i]);     i++;    }
                }
                i++;   // skip closing quote
                value = sb.ToString();
            }
            else
            {
                var valStart = i;
                while (i < n && Credentials[i] != ',')
                    i++;
                value = Credentials[valStart..i].Trim();
            }

            if (key.Length > 0)
                result[key] = value;
        }

        return result;
    }
}


/// <summary>
/// Handler shape for a request that has already been authenticated — the same
/// as <see cref="HTTP2RequestHandler"/> but with the caller's identity threaded
/// in. Produced for you by <see cref="HTTPAuthentication.RequireAuthentication"/>.
/// </summary>
public delegate Task<(List<(string Name, string Value)> ResponseHeaders, byte[]? ResponseBody)>
    HTTPAuthenticatedRequestHandler(
        HTTPAuthenticatedIdentity         Identity,
        UInt32                            StreamId,
        List<(string Name, string Value)> RequestHeaders,
        byte[]?                           RequestBody,
        CancellationToken                 CancellationToken
    );


/// <summary>
/// The generic RFC 9110 Section 11 authentication framework — the scheme-agnostic
/// plumbing that sits in front of application logic: it reads the request's
/// <c>Authorization</c> header, dispatches to whichever registered
/// <see cref="IHTTPAuthenticationScheme"/> matches, and — when no credentials
/// are present or none validate — answers <c>401 Unauthorized</c> with a
/// <c>WWW-Authenticate</c> challenge for every registered scheme (RFC 9110,
/// Sections 11.6.1 / 15.5.2). It never validates credentials itself; each
/// scheme defers that to an app callback, so `Core` stays BCL-only and free of
/// any credential store.
///
/// Like <see cref="HTTPSemantics"/> this is version-independent (RFC 9110), so
/// it lives in the shared library and is reusable in front of any transport.
/// It composes with `HTTPSemantics` by wrapping the final
/// <see cref="HTTP2RequestHandler"/>.
/// </summary>
public sealed class HTTPAuthenticator
{

    private readonly string                          realm;
    private readonly IReadOnlyList<IHTTPAuthenticationScheme> schemes;

    /// <param name="Realm">The protection space (RFC 9110, Section 11.5) named in every challenge.</param>
    /// <param name="Schemes">The accepted schemes, in the order they'll be advertised in the challenge.</param>
    public HTTPAuthenticator(string Realm, params IHTTPAuthenticationScheme[] Schemes)
    {
        if (Schemes.Length == 0)
            throw new ArgumentException("At least one authentication scheme is required", nameof(Schemes));

        realm   = Realm;
        schemes = Schemes;
    }

    /// <summary>
    /// Attempt to authenticate a request from its headers. Returns the identity
    /// if a registered scheme validated the credentials, or null if there were
    /// no credentials, the scheme is unsupported, or validation failed — in
    /// which case the caller should reply with <see cref="BuildChallengeHeaders"/>.
    /// </summary>
    public async Task<HTTPAuthenticatedIdentity?> AuthenticateAsync(
        List<(string Name, string Value)> RequestHeaders,
        CancellationToken                 CancellationToken)
    {

        var authorization = RequestHeaders.FirstOrDefault(h => h.Name == "authorization").Value;

        if (string.IsNullOrEmpty(authorization))
            return null;

        // "auth-scheme SP credentials" (RFC 9110, Section 11.6.2).
        var space       = authorization.IndexOf(' ');
        var schemeName  = space < 0 ? authorization : authorization[..space];
        var credentials = space < 0 ? ""            : authorization[(space + 1)..];

        var scheme = schemes.FirstOrDefault(s => string.Equals(s.SchemeName, schemeName, StringComparison.OrdinalIgnoreCase));

        if (scheme is null)
            return null;   // Unsupported scheme — challenge with what we do support.

        // The request's method + target, which a challenge-binding scheme (Digest)
        // folds into its hash; Basic/Bearer ignore them.
        var method = RequestHeaders.FirstOrDefault(h => h.Name == ":method").Value ?? "GET";
        var target = RequestHeaders.FirstOrDefault(h => h.Name == ":path").Value   ?? "/";

        return await scheme.AuthenticateAsync(credentials, method, target, CancellationToken);

    }

    /// <summary>
    /// The <c>WWW-Authenticate</c> response header fields for a 401 — one per
    /// registered scheme (RFC 9110, Section 11.6.1 permits, and this uses,
    /// multiple challenges so the client can pick a scheme it supports).
    /// </summary>
    public List<(string Name, string Value)> BuildChallengeHeaders()
        => schemes.Select(s => ("www-authenticate", s.BuildChallenge(realm))).ToList();

}


/// <summary>
/// Convenience helpers for putting an <see cref="HTTPAuthenticator"/> in front
/// of application logic.
/// </summary>
public static class HTTPAuthentication
{

    /// <summary>
    /// Wrap an authenticated handler with the RFC 9110 challenge/response flow:
    /// an unauthenticated request gets <c>401 Unauthorized</c> + the
    /// <c>WWW-Authenticate</c> challenge(s); an authenticated one is passed
    /// through to Inner with its identity. The result is an ordinary
    /// <see cref="HTTP2RequestHandler"/>, so it drops into the server (or in
    /// front of an <see cref="HTTPSemantics"/>-wrapped handler) unchanged.
    /// </summary>
    public static HTTP2RequestHandler RequireAuthentication(
        HTTPAuthenticator               Authenticator,
        HTTPAuthenticatedRequestHandler Inner)
        => async (streamId, requestHeaders, requestBody, cancellationToken) =>
        {

            var identity = await Authenticator.AuthenticateAsync(requestHeaders, cancellationToken);

            if (identity is null)
            {
                var body    = "401 Unauthorized"u8.ToArray();
                var headers = new List<(string Name, string Value)> { (":status", "401") };
                headers.AddRange(Authenticator.BuildChallengeHeaders());
                headers.Add(("content-type",   "text/plain; charset=utf-8"));
                headers.Add(("content-length", body.Length.ToString()));
                return (headers, body);
            }

            return await Inner(identity, streamId, requestHeaders, requestBody, cancellationToken);

        };

}
