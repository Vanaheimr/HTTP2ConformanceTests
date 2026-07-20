namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

using System.Threading.Channels;

/// <summary>
/// The client side of an accepted CONNECT tunnel (RFC 9113 §8.5 / RFC 8441) —
/// a raw bidirectional byte stream over one HTTP/2 stream, the mirror of the
/// server's <c>HTTP2Tunnel</c>. Implements <see cref="IHTTP2Tunnel"/>, so a
/// protocol layered on top (e.g. RFC 6455 WebSocket framing via
/// <see cref="WebSocketConnection"/>) works over it unchanged.
/// </summary>
public sealed class HTTP2ClientTunnel : IHTTP2Tunnel
{

    private readonly HTTP2ClientConnection connection;
    private readonly HTTP2Stream           stream;

    internal HTTP2ClientTunnel(HTTP2ClientConnection Connection, HTTP2Stream Stream, IReadOnlyList<(string Name, string Value)> ResponseHeaders)
    {
        connection           = Connection;
        stream               = Stream;
        this.ResponseHeaders = ResponseHeaders;
    }

    /// <summary>The tunnel's stream ID.</summary>
    public UInt32 StreamId => stream.StreamId;

    /// <summary>
    /// The headers the server sent on the accepting (2xx) CONNECT response —
    /// e.g. <c>sec-websocket-extensions</c> echoing back a negotiated
    /// permessage-deflate (RFC 7692), or any sub-protocol/extension the server
    /// selected.
    /// </summary>
    public IReadOnlyList<(string Name, string Value)> ResponseHeaders { get; }

    /// <summary>Read the next chunk the peer sent, or null once the tunnel ends (END_STREAM / reset).</summary>
    public async Task<byte[]?> ReadAsync(CancellationToken CancellationToken)
    {
        var reader = stream.TunnelInbound!.Reader;

        if (await reader.WaitToReadAsync(CancellationToken) && reader.TryRead(out var chunk))
            return chunk;

        return null;
    }

    /// <summary>Send a chunk of bytes to the peer as flow-controlled DATA frame(s).</summary>
    public Task WriteAsync(byte[] Data, CancellationToken CancellationToken)
        => connection.SendTunnelDataAsync(stream, Data, CancellationToken);

    /// <summary>End our side of the tunnel (a zero-length END_STREAM DATA frame).</summary>
    public Task CloseAsync()
        => connection.EndTunnelAsync(stream);

}
