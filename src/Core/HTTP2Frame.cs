namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;


#region Enums & Constants

/// <summary>
/// HTTP/2 frame types as defined in RFC 9113, Section 6.
/// </summary>
public enum HTTP2FrameType : byte
{
    DATA          = 0x00,
    HEADERS       = 0x01,
    PRIORITY      = 0x02,
    RST_STREAM    = 0x03,
    SETTINGS      = 0x04,
    PUSH_PROMISE  = 0x05,
    PING          = 0x06,
    GOAWAY        = 0x07,
    WINDOW_UPDATE = 0x08,
    CONTINUATION  = 0x09,

    /// <summary>
    /// RFC 9218 (Extensible Prioritization Scheme for HTTP), Section 7.1: a
    /// connection-level frame (its own Stream Identifier is always 0) that
    /// reprioritizes another stream, named by a "Prioritized Stream ID" in
    /// the payload, without needing a fresh HEADERS/request on it.
    /// </summary>
    PRIORITY_UPDATE = 0x10
}

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

/// <summary>
/// HTTP/2 error codes as defined in RFC 9113, Section 7.
/// </summary>
public enum HTTP2ErrorCode : uint
{
    NO_ERROR              = 0x00,
    PROTOCOL_ERROR        = 0x01,
    INTERNAL_ERROR        = 0x02,
    FLOW_CONTROL_ERROR    = 0x03,
    SETTINGS_TIMEOUT      = 0x04,
    STREAM_CLOSED         = 0x05,
    FRAME_SIZE_ERROR      = 0x06,
    REFUSED_STREAM        = 0x07,
    CANCEL                = 0x08,
    COMPRESSION_ERROR     = 0x09,
    CONNECT_ERROR         = 0x0A,
    ENHANCE_YOUR_CALM     = 0x0B,
    INADEQUATE_SECURITY   = 0x0C,
    HTTP_1_1_REQUIRED     = 0x0D
}

/// <summary>
/// HTTP/2 settings parameters as defined in RFC 9113, Section 6.5.2.
/// </summary>
public enum HTTP2SettingsParameter : ushort
{
    HEADER_TABLE_SIZE        = 0x01,
    ENABLE_PUSH              = 0x02,
    MAX_CONCURRENT_STREAMS   = 0x03,
    INITIAL_WINDOW_SIZE      = 0x04,
    MAX_FRAME_SIZE           = 0x05,
    MAX_HEADER_LIST_SIZE     = 0x06,

    /// <summary>
    /// RFC 8441, Section 3: a value of 1 tells the peer that extended CONNECT
    /// (the ":protocol" pseudo-header, used to bootstrap e.g. WebSocket) is
    /// supported on this connection.
    /// </summary>
    ENABLE_CONNECT_PROTOCOL = 0x08,

    /// <summary>
    /// RFC 9218 (Extensible Prioritization Scheme for HTTP), Section 3: a
    /// value of 1 tells the peer the sender will not use RFC 7540's
    /// deprecated stream-dependency/weight priority signaling (the PRIORITY
    /// frame, and the PRIORITY flag on HEADERS) — this server already
    /// ignores both unconditionally, so it is always advertised.
    /// </summary>
    NO_RFC7540_PRIORITIES = 0x09
}

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

/// <summary>
/// Which end of a connection this endpoint is (RFC 9113, Section 5.1.1). The
/// role decides stream-ID parity: a client initiates odd-numbered streams and
/// its peer (a server) would initiate even ones (server push); a server is the
/// mirror. Used by <see cref="HTTP2StreamManager"/> so the same stream
/// bookkeeping serves both the server and the (Track E) client without
/// hardcoding "the peer's streams are odd".
/// </summary>
public enum HTTP2Role
{
    Server,
    Client
}

#endregion


/// <summary>
/// Represents a parsed HTTP/2 frame with its 9-byte header and payload.
/// 
/// HTTP/2 frame layout (RFC 9113, Section 4.1):
/// +-----------------------------------------------+
/// |                 Length (24)                    |
/// +---------------+---------------+---------------+
/// |   Type (8)    |   Flags (8)   |
/// +-+-------------+---------------+-------------------------------+
/// |R|                 Stream Identifier (31)                      |
/// +=+=============================================================+
/// |                   Frame Payload (0...)                      ...
/// +---------------------------------------------------------------+
/// </summary>
public sealed class HTTP2Frame
{

    public const int    HeaderSize      = 9;
    public const int    DefaultMaxSize  = 16384;      // 2^14
    public const int    MaxAllowedSize  = 16777215;   // 2^24 - 1

    public UInt32            Length      { get; set; }
    public HTTP2FrameType    Type       { get; set; }
    public HTTP2FrameFlags   Flags      { get; set; }
    public UInt32            StreamId   { get; set; }
    public byte[]            Payload    { get; set; } = [];


    #region Flag helpers

    public bool HasFlag(HTTP2FrameFlags Flag)
        => (Flags & Flag) != 0;

    public bool EndStream
        => HasFlag(HTTP2FrameFlags.END_STREAM);

    public bool EndHeaders
        => HasFlag(HTTP2FrameFlags.END_HEADERS);

    public bool IsAck
        => HasFlag(HTTP2FrameFlags.ACK);

    public bool IsPadded
        => HasFlag(HTTP2FrameFlags.PADDED);

    public bool HasPriority
        => HasFlag(HTTP2FrameFlags.PRIORITY);

    #endregion


    #region Serialization

    /// <summary>
    /// Parse the 9-byte frame header. Returns the payload length to read next.
    /// </summary>
    public static HTTP2Frame ParseHeader(ReadOnlySpan<byte> HeaderBytes)
    {

        if (HeaderBytes.Length < HeaderSize)
            throw new HTTP2ConnectionException(HTTP2ErrorCode.FRAME_SIZE_ERROR,
                                               "Incomplete frame header");

        var length   = (UInt32) (HeaderBytes[0] << 16 | HeaderBytes[1] << 8 | HeaderBytes[2]);
        var type     = (HTTP2FrameType)  HeaderBytes[3];
        var flags    = (HTTP2FrameFlags) HeaderBytes[4];
        var streamId = BinaryPrimitives.ReadUInt32BigEndian(HeaderBytes[5..]) & 0x7FFFFFFFu;  // Clear reserved bit

        return new HTTP2Frame {
            Length   = length,
            Type     = type,
            Flags    = flags,
            StreamId = streamId
        };

    }

    /// <summary>
    /// Serialize this frame (header + payload) into a byte array.
    /// </summary>
    public byte[] Serialize()
    {

        var payloadLength  = Payload?.Length ?? 0;
        var buffer         = new byte[HeaderSize + payloadLength];

        // 3-byte length
        buffer[0] = (byte) ((payloadLength >> 16) & 0xFF);
        buffer[1] = (byte) ((payloadLength >>  8) & 0xFF);
        buffer[2] = (byte) ((payloadLength      ) & 0xFF);

        buffer[3] = (byte) Type;
        buffer[4] = (byte) Flags;

        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(5), StreamId & 0x7FFFFFFFu);

        if (payloadLength > 0)
            Payload!.CopyTo(buffer, HeaderSize);

        return buffer;

    }

    #endregion


    #region Factory methods for common frame types

    public static HTTP2Frame CreateSettings(params (HTTP2SettingsParameter Id, UInt32 Value)[] Settings)
    {

        var payload = new byte[Settings.Length * 6];

        for (int i = 0; i < Settings.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(i * 6),     (UInt16) Settings[i].Id);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(i * 6 + 2), Settings[i].Value);
        }

        return new HTTP2Frame {
            Type     = HTTP2FrameType.SETTINGS,
            Flags    = HTTP2FrameFlags.NONE,
            StreamId = 0,
            Length   = (UInt32) payload.Length,
            Payload  = payload
        };

    }

    public static HTTP2Frame CreateSettingsAck()
        => new() {
               Type     = HTTP2FrameType.SETTINGS,
               Flags    = HTTP2FrameFlags.ACK,
               StreamId = 0,
               Payload  = []
           };

    public static HTTP2Frame CreatePing(byte[] OpaqueData)
        => new() {
               Type     = HTTP2FrameType.PING,
               Flags    = HTTP2FrameFlags.NONE,
               StreamId = 0,
               Payload  = OpaqueData
           };

    public static HTTP2Frame CreatePingAck(byte[] OpaqueData)
        => new() {
               Type     = HTTP2FrameType.PING,
               Flags    = HTTP2FrameFlags.ACK,
               StreamId = 0,
               Payload  = OpaqueData
           };

    public static HTTP2Frame CreateGoAway(UInt32 LastStreamId, HTTP2ErrorCode ErrorCode, string? DebugMessage = null)
    {

        var debugBytes     = DebugMessage is not null ? System.Text.Encoding.UTF8.GetBytes(DebugMessage) : [];
        var payload        = new byte[8 + debugBytes.Length];

        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0), LastStreamId & 0x7FFFFFFFu);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (UInt32) ErrorCode);

        if (debugBytes.Length > 0)
            debugBytes.CopyTo(payload, 8);

        return new HTTP2Frame {
            Type     = HTTP2FrameType.GOAWAY,
            Flags    = HTTP2FrameFlags.NONE,
            StreamId = 0,
            Payload  = payload
        };

    }

    public static HTTP2Frame CreateRstStream(UInt32 StreamId, HTTP2ErrorCode ErrorCode)
    {

        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (UInt32) ErrorCode);

        return new HTTP2Frame {
            Type     = HTTP2FrameType.RST_STREAM,
            Flags    = HTTP2FrameFlags.NONE,
            StreamId = StreamId,
            Payload  = payload
        };

    }

    public static HTTP2Frame CreateWindowUpdate(UInt32 StreamId, UInt32 WindowSizeIncrement)
    {

        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, WindowSizeIncrement & 0x7FFFFFFFu);

        return new HTTP2Frame {
            Type     = HTTP2FrameType.WINDOW_UPDATE,
            Flags    = HTTP2FrameFlags.NONE,
            StreamId = StreamId,
            Payload  = payload
        };

    }

    public static HTTP2Frame CreateHeaders(UInt32 StreamId, byte[] HeaderBlock, bool EndStream = false, bool EndHeaders = true)
    {

        var flags = HTTP2FrameFlags.NONE;

        if (EndStream)
            flags |= HTTP2FrameFlags.END_STREAM;

        if (EndHeaders)
            flags |= HTTP2FrameFlags.END_HEADERS;

        return new HTTP2Frame {
            Type     = HTTP2FrameType.HEADERS,
            Flags    = flags,
            StreamId = StreamId,
            Length   = (UInt32) HeaderBlock.Length,
            Payload  = HeaderBlock
        };

    }

    public static HTTP2Frame CreateData(UInt32 StreamId, byte[] Data, bool EndStream = false)
        => new() {
               Type     = HTTP2FrameType.DATA,
               Flags    = EndStream ? HTTP2FrameFlags.END_STREAM : HTTP2FrameFlags.NONE,
               StreamId = StreamId,
               Length   = (UInt32) Data.Length,
               Payload  = Data
           };

    /// <summary>
    /// RFC 9218, Section 7.1: a connection-level PRIORITY_UPDATE frame (own
    /// Stream Identifier always 0) reprioritizing PrioritizedStreamId. The
    /// payload is the 31-bit prioritized stream ID followed by the ASCII
    /// Priority Field Value (the same <c>u=&lt;n&gt;, i</c> Structured-Fields
    /// grammar as the <c>priority</c> header).
    /// </summary>
    public static HTTP2Frame CreatePriorityUpdate(UInt32 PrioritizedStreamId, string PriorityFieldValue)
    {

        var valueBytes = System.Text.Encoding.ASCII.GetBytes(PriorityFieldValue);
        var payload    = new byte[4 + valueBytes.Length];

        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0), PrioritizedStreamId & 0x7FFFFFFFu);
        valueBytes.CopyTo(payload, 4);

        return new HTTP2Frame {
            Type     = HTTP2FrameType.PRIORITY_UPDATE,
            Flags    = HTTP2FrameFlags.NONE,
            StreamId = 0,
            Length   = (UInt32) payload.Length,
            Payload  = payload
        };

    }

    #endregion


    public override string ToString()
        => $"[{Type} stream={StreamId} length={Length} flags={Flags}]";

}


#region Exceptions

public class HTTP2ConnectionException(HTTP2ErrorCode ErrorCode, string Message)
    : Exception(Message)
{
    public HTTP2ErrorCode ErrorCode { get; } = ErrorCode;
}

public class HTTP2StreamException(HTTP2ErrorCode ErrorCode, UInt32 StreamId, string Message)
    : Exception(Message)
{
    public HTTP2ErrorCode  ErrorCode  { get; } = ErrorCode;
    public UInt32          StreamId   { get; } = StreamId;
}

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

#endregion
