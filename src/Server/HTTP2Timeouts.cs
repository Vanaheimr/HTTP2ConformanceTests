namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

/// <summary>
/// Timeouts that bound how long a peer may keep a connection (or a partially
/// received message) open without making progress — the defense against
/// Slowloris-style attacks, where a client trickles or withholds bytes to tie
/// up server resources cheaply. All values are generous enough that a
/// well-behaved client never trips them.
/// </summary>
public sealed record HTTP2Timeouts
{

    /// <summary>TLS handshake (applied by <see cref="HTTP2Server"/> before the connection starts).</summary>
    public TimeSpan Handshake  { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Client connection preface (magic string + first SETTINGS) after TLS.</summary>
    public TimeSpan Preface    { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum wait for the peer to ACK our SETTINGS (RFC 9113, Section 6.5.3 —
    /// SETTINGS_TIMEOUT). Kept generous, as this is a "MAY" in the spec.
    /// </summary>
    public TimeSpan SettingsAck { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum time an otherwise-quiet connection may sit between frames before
    /// it is closed as idle. Long, since HTTP/2 connections are meant to be
    /// reused; this only reclaims genuinely abandoned connections.
    /// </summary>
    public TimeSpan Idle       { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Maximum time to finish something already in progress: a frame's payload
    /// once its header has arrived, or a header block once a CONTINUATION is
    /// pending. This is the tight bound that stops a trickle attack (a partial
    /// frame, or HEADERS without END_HEADERS followed by silence).
    /// </summary>
    public TimeSpan InProgress { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The default timeout set.</summary>
    public static readonly HTTP2Timeouts Default = new();

}
