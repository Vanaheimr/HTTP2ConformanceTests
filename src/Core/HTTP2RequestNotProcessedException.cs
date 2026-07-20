namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;


/// <summary>
/// Thrown for a request the peer provably did <b>not</b> process, so it is safe
/// to retry — verbatim — on a fresh connection without risking duplicate
/// side effects. This is the case for a stream refused with
/// <see cref="HTTP2ErrorCode.REFUSED_STREAM"/> (RFC 9113, Section 8.1) and for
/// any stream with an ID greater than a GOAWAY's Last-Stream-ID (Section 6.8).
/// </summary>
public class HTTP2RequestNotProcessedException(HTTP2ErrorCode ErrorCode, string Message)
    : Exception(Message)
{
    public HTTP2ErrorCode  ErrorCode  { get; } = ErrorCode;
}
