namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;


/// <summary>
/// HTTP/2 stream states as defined in RFC 9113, Section 5.1.
/// </summary>
public enum HTTP2StreamState
{
    Idle,
    ReservedLocal,
    ReservedRemote,
    Open,
    HalfClosedLocal,
    HalfClosedRemote,
    Closed
}
