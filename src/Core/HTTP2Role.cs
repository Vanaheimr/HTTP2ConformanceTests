namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;


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
