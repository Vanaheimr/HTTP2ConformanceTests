namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

/// <summary>
/// Callback delegate invoked when a CONNECT or extended-CONNECT (RFC 8441)
/// request has passed framing-layer validation. The handler decides whether
/// to accept the tunnel (2xx status + a body to run once accepted) or refuse
/// it (any other status, no body) — this decision is made *before* any
/// response is sent, so it can inspect ":authority"/":protocol"/":path" first
/// (e.g. to route a WebSocket sub-protocol or an authorized proxy target).
/// </summary>
public delegate Task<HTTP2ConnectResult>
    HTTP2ConnectHandler(
        UInt32                           StreamId,
        List<(string Name, string Value)> RequestHeaders,
        CancellationToken                CancellationToken
    );


/// <summary>
/// The connect handler's accept/refuse decision for a CONNECT tunnel. If
/// <see cref="StatusCode"/> is 2xx and <see cref="RunAsync"/> is non-null, the
/// framing layer sends the 2xx response and then runs it against the
/// accepted <see cref="HTTP2Tunnel"/>; otherwise it sends the given status
/// (plus optional headers) and ends the stream without a body.
/// </summary>
public sealed class HTTP2ConnectResult
{
    public required UInt16                             StatusCode    { get; init; }
    public           List<(string Name, string Value)>? ExtraHeaders  { get; init; }
    public           Func<HTTP2Tunnel, CancellationToken, Task>? RunAsync { get; init; }
}
