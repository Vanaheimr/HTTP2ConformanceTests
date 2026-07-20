namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

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
