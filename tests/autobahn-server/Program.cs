using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;

// =============================================================================
// Autobahn TestSuite echo server.
//
// The Autobahn TestSuite (https://github.com/crossbario/autobahn-testsuite) is
// the canonical RFC 6455 WebSocket conformance suite. Its "fuzzingclient" mode
// connects to a WebSocket ECHO server and drives ~500 test cases (framing,
// fragmentation, UTF-8 handling, close handshake, ...), then writes an HTML/JSON
// report on how the server behaved.
//
// The wrinkle: Autobahn's client speaks WebSocket over the classic HTTP/1.1
// Upgrade handshake — it does NOT do RFC 8441 WebSocket-over-HTTP/2, which is
// how this project's WebSockets run in production. But the thing Autobahn
// actually tests is the FRAMING layer (WebSocketConnection in Core), and that
// layer is deliberately transport-agnostic: it sits on top of IHTTP2Tunnel
// (byte-in, byte-out) and knows nothing about HTTP/2. So this harness exercises
// the exact same WebSocketConnection code Autobahn is designed to test, just
// over a plain-TCP tunnel behind a minimal HTTP/1.1 Upgrade handshake instead
// of an HTTP/2 tunnel — precisely the reuse the IHTTP2Tunnel seam exists for
// (the same argument by which WebSocket.cs would serve a WebSocket-over-HTTP/3
// endpoint unchanged). The HTTP/1.1 handshake below is test-only glue; not a
// single line of the framing under test is test-specific.
// =============================================================================

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 9010;

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
Console.WriteLine($"[autobahn-server] WebSocket echo server listening on ws://127.0.0.1:{port}/");

// RFC 6455 Section 4.2.2: the server's opening-handshake accept token is the
// base64 of SHA-1(Sec-WebSocket-Key + this fixed GUID).
const string wsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

while (true)
{
    TcpClient client;
    try { client = await listener.AcceptTcpClientAsync(); }
    catch { break; }

    _ = Task.Run(() => HandleAsync(client));
}

return;


async Task HandleAsync(TcpClient Client)
{
    using (Client)
    {
        try
        {
            Client.NoDelay = true;
            var stream = Client.GetStream();

            var request = await ReadHandshakeAsync(stream);
            var key     = request is null ? null : HeaderValue(request, "Sec-WebSocket-Key");
            if (key is null)
                return;   // not a well-formed WebSocket upgrade

            var accept = Convert.ToBase64String(
                             SHA1.HashData(Encoding.ASCII.GetBytes(key + wsGuid)));

            // permessage-deflate (RFC 7692): if the client offers it, accept —
            // via the shared Core negotiation helper (forcing no-context-takeover
            // both ways, which is all a fixed-window codec can do) and echoing
            // that back in Sec-WebSocket-Extensions. Same negotiation the
            // production HTTP/2 (RFC 8441) path uses, just carried over the
            // HTTP/1.1 Upgrade response instead.
            var offered  = HeaderValue(request!, "Sec-WebSocket-Extensions");
            var deflate  = WebSocketDeflate.ShouldAccept(offered, out var responseExt);
            var extLine  = deflate ? $"Sec-WebSocket-Extensions: {responseExt}\r\n" : "";

            var response = "HTTP/1.1 101 Switching Protocols\r\n" +
                           "Upgrade: websocket\r\n"              +
                           "Connection: Upgrade\r\n"            +
                           $"Sec-WebSocket-Accept: {accept}\r\n" +
                           extLine +
                           "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            await stream.FlushAsync();

            // Hand the raw TCP byte stream to the SAME WebSocketConnection framing
            // used in production, as a server-role endpoint, and echo every
            // application message straight back — the behavior Autobahn's
            // fuzzingclient expects of an echo server.
            var tunnel = new NetworkStreamTunnel(stream);
            var ws     = new WebSocketConnection(tunnel, WebSocketRole.Server, PerMessageDeflate: deflate);

            while (true)
            {
                WebSocketMessage? message;
                try
                {
                    message = await ws.ReceiveAsync(CancellationToken.None);
                }
                catch (IOException)      { break; }
                catch (WebSocketProtocolException) { break; }

                if (message is null)
                    break;   // close handshake completed, or the peer went away

                if (message.Opcode == WebSocketOpcode.Text)
                    // Payload was already validated as UTF-8 by ReceiveAsync, so the
                    // String round-trip is lossless — echo it back as a Text frame.
                    await ws.SendTextAsync(Encoding.UTF8.GetString(message.Payload), CancellationToken.None);
                else
                    await ws.SendBinaryAsync(message.Payload, CancellationToken.None);
            }
        }
        catch
        {
            // Autobahn opens and tears down connections rapidly and abruptly;
            // a per-connection failure is never fatal to the harness.
        }
    }
}


// Read the HTTP/1.1 request headers up to the blank line, byte by byte so we
// never consume into the first WebSocket frame that follows. Returns the raw
// header lines (or null if the request never terminated).
async Task<string[]?> ReadHandshakeAsync(NetworkStream Stream)
{
    var sb  = new StringBuilder();
    var one = new byte[1];

    while (sb.Length < 16 * 1024)
    {
        var n = await Stream.ReadAsync(one);
        if (n == 0)
            return null;
        sb.Append((char) one[0]);
        if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r')
            return sb.ToString().Split("\r\n");
    }

    return null;
}

// Case-insensitive header lookup over the raw request lines.
static string? HeaderValue(string[] Lines, string Name)
{
    foreach (var line in Lines)
    {
        var colon = line.IndexOf(':');
        if (colon > 0 && line[..colon].Trim().Equals(Name, StringComparison.OrdinalIgnoreCase))
            return line[(colon + 1)..].Trim();
    }
    return null;
}


/// <summary>
/// The minimal <see cref="IHTTP2Tunnel"/> over a raw TCP stream: read whatever
/// the peer sent next, write bytes back. WebSocketConnection buffers across
/// these reads itself (WebSocket frame boundaries have no relation to TCP
/// segment boundaries), so returning any non-empty chunk is fine.
/// </summary>
sealed class NetworkStreamTunnel(NetworkStream Stream) : IHTTP2Tunnel
{

    private readonly byte[] readBuffer = new byte[64 * 1024];

    public async Task<byte[]?> ReadAsync(CancellationToken CancellationToken)
    {
        var n = await Stream.ReadAsync(readBuffer, CancellationToken);
        if (n == 0)
            return null;
        return readBuffer[..n];
    }

    public Task WriteAsync(byte[] Data, CancellationToken CancellationToken)
        => Stream.WriteAsync(Data, CancellationToken).AsTask();

}
