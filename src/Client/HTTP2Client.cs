namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;


/// <summary>
/// Dials an HTTP/2 server over TLS with ALPN `h2` and returns a ready
/// <see cref="HTTP2ClientConnection"/> — the client-side counterpart of
/// <see cref="HTTP2Server"/>'s accept path. Everything protocol-level lives in
/// <see cref="HTTP2ClientConnection"/>; this type only owns the TCP connect and
/// the TLS/ALPN handshake.
/// </summary>
public static class HTTP2Client
{

    /// <summary>
    /// Connect to Host:Port, negotiate TLS + ALPN `h2`, exchange the HTTP/2
    /// preface, and return a connection ready for <c>SendRequestAsync</c>.
    /// </summary>
    /// <param name="Host">Host name (also the TLS SNI / ALPN target host).</param>
    /// <param name="Port">TCP port.</param>
    /// <param name="ValidateServerCertificate">
    /// Optional certificate validation callback. Pass a permissive one to accept
    /// the demo server's self-signed certificate; leave null for normal chain
    /// validation.
    /// </param>
    /// <param name="ClientCertificate">
    /// Optional client certificate to present for mutual TLS (mTLS), when the
    /// server requires one. Null for ordinary (server-auth-only) TLS.
    /// </param>
    public static async Task<HTTP2ClientConnection> ConnectAsync(
        string                                Host,
        int                                   Port,
        RemoteCertificateValidationCallback?  ValidateServerCertificate = null,
        X509Certificate2?                     ClientCertificate         = null,
        HTTP2ClientOptions?                   Options                   = null,
        CancellationToken                     CancellationToken         = default)
    {

        var tcp = new TcpClient();
        await tcp.ConnectAsync(Host, Port, CancellationToken);

        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, ValidateServerCertificate);

        var clientOptions = new SslClientAuthenticationOptions {
            TargetHost           = Host,
            ApplicationProtocols = [SslApplicationProtocol.Http2],
            EnabledSslProtocols  = SslProtocols.Tls12 | SslProtocols.Tls13
        };

        if (ClientCertificate is not null)
            clientOptions.ClientCertificates = [ClientCertificate];

        await ssl.AuthenticateAsClientAsync(clientOptions, CancellationToken);

        if (ssl.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
            throw new HTTP2ConnectionException(HTTP2ErrorCode.PROTOCOL_ERROR,
                $"Server did not negotiate HTTP/2 over ALPN (got '{ssl.NegotiatedApplicationProtocol}')");

        var connection = new HTTP2ClientConnection(ssl, CancellationToken, Options);
        await connection.StartAsync();

        return connection;

    }

}
