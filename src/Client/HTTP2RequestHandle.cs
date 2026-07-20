namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

/// <summary>
/// A handle to an in-flight request, returned by
/// <see cref="HTTP2ClientConnection.StartRequestAsync"/> once its HEADERS are
/// sent: the allocated <see cref="StreamId"/> (usable with
/// <see cref="HTTP2ClientConnection.UpdatePriorityAsync"/> to reprioritize it)
/// and the <see cref="Response"/> task that completes when the response arrives.
/// </summary>
public sealed record HTTP2RequestHandle(UInt32 StreamId, Task<HTTP2Response> Response);
