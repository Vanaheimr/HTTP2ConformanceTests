namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

/// <summary>
/// The head of a response — its <c>:status</c> and header fields — surfaced by a
/// streaming exchange (<see cref="HTTP2ClientStream"/>) as soon as the response
/// HEADERS arrive, before (and independently of) its body.
/// </summary>
public sealed record HTTP2ResponseHead(int Status, List<(string Name, string Value)> Headers)
{
    public string? HeaderValue(string Name)
        => Headers.FirstOrDefault(h => h.Name == Name).Value;
}
