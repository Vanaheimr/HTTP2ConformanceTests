namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

/// <summary>Which end of a WebSocket a <see cref="WebSocketConnection"/> is.</summary>
public enum WebSocketRole
{
    /// <summary>Server: sends unmasked frames, requires inbound frames to be masked.</summary>
    Server,
    /// <summary>Client: masks every outbound frame (RFC 6455 §5.3), requires inbound frames to be unmasked.</summary>
    Client
}
