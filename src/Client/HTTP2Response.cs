namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

/// <summary>
/// A completed HTTP/2 response, as assembled by <see cref="HTTP2ClientConnection"/>.
/// </summary>
public sealed class HTTP2Response
{
    public required int                               Status   { get; init; }
    public required List<(string Name, string Value)> Headers  { get; init; }
    public required byte[]                            Body     { get; init; }

    /// <summary>Trailer fields, if the server sent a trailing HEADERS block (RFC 9113, Section 8.1). Empty otherwise.</summary>
    public          List<(string Name, string Value)> Trailers { get; init; } = [];

    /// <summary>
    /// Interim (1xx) responses received before the final response, in order —
    /// e.g. a <c>100 Continue</c> (RFC 9110) or a <c>103 Early Hints</c>
    /// (RFC 8297) with <c>Link</c> preload headers. Empty if none were sent.
    /// </summary>
    public          List<(int Status, List<(string Name, string Value)> Headers)> InformationalResponses { get; init; } = [];

    public string? HeaderValue(string Name)
        => Headers.FirstOrDefault(h => h.Name == Name).Value;
}
