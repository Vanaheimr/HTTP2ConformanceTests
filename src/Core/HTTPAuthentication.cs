namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

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
    /// </summary>
    Task<HTTPAuthenticatedIdentity?> AuthenticateAsync(string Credentials, CancellationToken CancellationToken);
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

    public async Task<HTTPAuthenticatedIdentity?> AuthenticateAsync(string Credentials, CancellationToken CancellationToken)
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

    public Task<HTTPAuthenticatedIdentity?> AuthenticateAsync(string Credentials, CancellationToken CancellationToken)
        => validate(Credentials.Trim(), CancellationToken);

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

        return await scheme.AuthenticateAsync(credentials, CancellationToken);

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
