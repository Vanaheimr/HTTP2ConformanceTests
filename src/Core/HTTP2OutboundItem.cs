namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

using System.Threading.Channels;

/// <summary>
/// One item of body/tunnel bytes queued on a stream's <see cref="HTTP2OutboundQueue"/>,
/// waiting for the connection's single writer loop to send it as DATA frame(s).
/// </summary>
internal sealed class HTTP2OutboundItem
{
    public required byte[]               Data        { get; init; }
    public required bool                 EndStream   { get; init; }
    public          int                  Offset;
    public required TaskCompletionSource Completion  { get; init; }

    /// <summary>
    /// Trailer fields to send as a trailing HEADERS block (with END_STREAM)
    /// once this item's DATA has gone out — set only on the final,
    /// end-of-stream item of a response that carries trailers (RFC 9113,
    /// Section 8.1). Null for an ordinary item.
    /// </summary>
    public List<(string Name, string Value)>? Trailers { get; init; }
}
