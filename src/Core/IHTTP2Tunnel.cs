namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;


/// <summary>
/// A transport-agnostic bidirectional byte tunnel — the minimal surface a
/// protocol layered on top of a CONNECT stream (e.g. RFC 6455 WebSocket
/// framing, see <see cref="WebSocketConnection"/>) needs: read the next chunk
/// the peer sent, write a chunk back. Lives in the shared library so those
/// consumers don't depend on the server-coupled concrete implementation
/// (the server's <c>HTTP2Tunnel</c>, which implements this). A client-side
/// tunnel could implement the same interface and reuse the framing unchanged.
/// </summary>
public interface IHTTP2Tunnel
{
    /// <summary>Read the next chunk from the peer, or null once the peer has ended its side.</summary>
    Task<byte[]?> ReadAsync(CancellationToken CancellationToken);

    /// <summary>Send a chunk of bytes to the peer.</summary>
    Task WriteAsync(byte[] Data, CancellationToken CancellationToken);
}
