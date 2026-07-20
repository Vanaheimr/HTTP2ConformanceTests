namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

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
