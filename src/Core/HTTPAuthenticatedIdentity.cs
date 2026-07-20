namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

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
