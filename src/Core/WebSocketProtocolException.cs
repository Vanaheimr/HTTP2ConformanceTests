namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

/// <summary>
/// Raised for any violation of RFC 6455's framing rules (bad mask bit,
/// fragmented/oversized control frame, reserved bits set, oversized payload).
/// Caught by <see cref="WebSocketConnection.ReceiveAsync"/>, which answers
/// with a Close frame (code 1002, "protocol error") and ends the connection.
/// </summary>
public sealed class WebSocketProtocolException(string Message) : Exception(Message);
