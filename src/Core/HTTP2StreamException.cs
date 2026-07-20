namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;


public class HTTP2StreamException(HTTP2ErrorCode ErrorCode, UInt32 StreamId, string Message)
    : Exception(Message)
{
    public HTTP2ErrorCode  ErrorCode  { get; } = ErrorCode;
    public UInt32          StreamId   { get; } = StreamId;
}
