namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;


/// <summary>
/// The streaming counterpart of <see cref="HTTP2RequestHandler"/>. Where the
/// buffered handler takes a complete request body and returns a complete
/// response body, this one hands the application a <see cref="IHTTP2RequestStream"/>
/// to pull the request body from as it arrives, and a
/// <see cref="IHTTP2ResponseStream"/> to push the response into incrementally —
/// so neither side is ever fully buffered in memory. Both directions flow
/// concurrently, which is what makes bidirectional streaming (e.g. gRPC) work:
/// the handler can be reading request chunks and writing response chunks at the
/// same time.
///
/// The handler is invoked as soon as the request HEADERS are complete (not at
/// END_STREAM), so a long-lived or infinite stream never waits for the whole
/// request first. It is expected to send response headers (once), then any
/// number of body chunks, then complete — optionally with trailers. Returning
/// without completing auto-completes the response; throwing resets the stream.
/// </summary>
public delegate Task HTTP2StreamingHandler(
    IHTTP2RequestStream  Request,
    IHTTP2ResponseStream Response,
    CancellationToken    CancellationToken);


/// <summary>
/// The request side of a streaming exchange: the decoded request headers, the
/// body delivered chunk by chunk as DATA frames arrive, and — once the body
/// ends — any trailer fields (RFC 9113, Section 8.1) the peer sent.
/// </summary>
public interface IHTTP2RequestStream
{

    /// <summary>The decoded request header fields (pseudo-headers included).</summary>
    IReadOnlyList<(string Name, string Value)> Headers { get; }

    /// <summary>
    /// Read the next request-body chunk, in order, as it arrives. Returns
    /// <c>null</c> once the peer has ended the request body (END_STREAM) — after
    /// which <see cref="Trailers"/> is populated.
    /// </summary>
    ValueTask<byte[]?> ReadAsync(CancellationToken CancellationToken = default);

    /// <summary>
    /// The request's trailer fields, if any. Only meaningful once
    /// <see cref="ReadAsync"/> has returned <c>null</c> (the body has ended);
    /// empty if the peer sent no trailers.
    /// </summary>
    IReadOnlyList<(string Name, string Value)> Trailers { get; }

}
