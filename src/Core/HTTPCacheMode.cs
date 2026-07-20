namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

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
