namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;


/// <summary>
/// Represents a negotiated set of HTTP/2 connection settings (RFC 9113, Section 6.5).
/// Both peers maintain their own settings; the "local" settings are what WE advertise
/// and the "remote" settings are what the PEER advertised. Direction-neutral — used by
/// both the server and the client connection.
/// </summary>
public sealed class HTTP2Settings
{
    public UInt32  HeaderTableSize      { get; set; } = 4096;
    public bool    EnablePush           { get; set; } = false;   // We don't push
    public UInt32  MaxConcurrentStreams { get; set; } = 100;
    // A larger-than-default receive window (RFC 9113 default is 65535). Combined
    // with batched WINDOW_UPDATEs, this lets a large transfer flow with only
    // occasional flow-control frames instead of one per DATA frame.
    public UInt32  InitialWindowSize    { get; set; } = 1024 * 1024;   // 1 MiB
    public UInt32  MaxFrameSize         { get; set; } = 16384;   // 2^14
    public UInt32  MaxHeaderListSize    { get; set; } = 8192;
}
