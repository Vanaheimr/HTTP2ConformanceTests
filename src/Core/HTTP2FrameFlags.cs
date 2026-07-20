namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;


/// <summary>
/// HTTP/2 frame flags (shared across frame types, not all combinations are valid).
/// </summary>
[Flags]
public enum HTTP2FrameFlags : byte
{
    NONE          = 0x00,
    ACK           = 0x01,   // SETTINGS, PING
    END_STREAM    = 0x01,   // DATA, HEADERS
    END_HEADERS   = 0x04,   // HEADERS, PUSH_PROMISE, CONTINUATION
    PADDED        = 0x08,   // DATA, HEADERS
    PRIORITY      = 0x20    // HEADERS
}
