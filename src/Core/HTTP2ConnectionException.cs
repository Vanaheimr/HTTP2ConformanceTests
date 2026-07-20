namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;


public class HTTP2ConnectionException(HTTP2ErrorCode ErrorCode, string Message)
    : Exception(Message)
{
    public HTTP2ErrorCode ErrorCode { get; } = ErrorCode;
}
