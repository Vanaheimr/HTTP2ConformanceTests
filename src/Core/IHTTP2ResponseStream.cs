namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;


/// <summary>
/// The response side of a streaming exchange: send the response headers once,
/// then any number of body chunks, then complete — optionally with trailer
/// fields (e.g. gRPC's <c>grpc-status</c>). Each body chunk is flow-controlled
/// and prioritized exactly like a buffered response's bytes (it goes through the
/// same per-connection writer loop), so a slow peer applies natural backpressure
/// via the returned tasks.
/// </summary>
public interface IHTTP2ResponseStream
{

    /// <summary>
    /// Send an interim (1xx) response — e.g. <c>103 Early Hints</c> (RFC 8297)
    /// carrying <c>Link</c> preload hints — before the final response. May be
    /// called any number of times, but only before <see cref="WriteHeadersAsync"/>;
    /// each is a HEADERS block that does not end the stream (RFC 9110, Section
    /// 15.2). <paramref name="Status"/> must be in the 1xx range.
    /// </summary>
    Task WriteInterimResponseAsync(int Status, IEnumerable<(string Name, string Value)> Headers, CancellationToken CancellationToken = default);

    /// <summary>
    /// Send the response header fields (must include <c>:status</c>). Call once,
    /// before any <see cref="WriteAsync"/>. Does not end the stream.
    /// </summary>
    Task WriteHeadersAsync(IEnumerable<(string Name, string Value)> Headers, CancellationToken CancellationToken = default);

    /// <summary>
    /// Send one response-body chunk as DATA frame(s). Awaiting the returned task
    /// waits until the chunk has actually been handed to the wire (backpressure).
    /// </summary>
    Task WriteAsync(byte[] Data, CancellationToken CancellationToken = default);

    /// <summary>
    /// End the response. With trailers, they are sent as a trailing HEADERS
    /// block carrying END_STREAM (RFC 9113, Section 8.1); without, the stream is
    /// simply ended. Idempotent — a second call is a no-op.
    /// </summary>
    Task CompleteAsync(IEnumerable<(string Name, string Value)>? Trailers = null, CancellationToken CancellationToken = default);

}
