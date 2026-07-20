namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

/// <summary>
/// A complete (defragmented) application message surfaced to the caller by
/// <see cref="WebSocketConnection.ReceiveAsync"/>. Only ever Text or Binary —
/// control frames (ping/pong/close) are handled internally and never returned
/// as a message.
/// </summary>
public sealed class WebSocketMessage
{
    public required WebSocketOpcode Opcode  { get; init; }
    public required byte[]          Payload { get; init; }
}
