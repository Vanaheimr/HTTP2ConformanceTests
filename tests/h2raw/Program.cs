using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;

// Raw HTTP/2 client: speaks the framing layer directly over SslStream and
// logs every received frame with a timestamp. Sends GET /slow, GET /, GET /large
// as three concurrent streams, then reads until all three are END_STREAM'd.

var sw = Stopwatch.StartNew();

void Log(string msg) => Console.WriteLine($"[{sw.ElapsedMilliseconds,5} ms] {msg}");

using var tcp = new TcpClient();
await tcp.ConnectAsync("127.0.0.1", 8443);

var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);

await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
    TargetHost           = "localhost",
    ApplicationProtocols = [SslApplicationProtocol.Http2]
});

Log($"TLS done, ALPN = {ssl.NegotiatedApplicationProtocol}");

// Client connection preface + empty SETTINGS
await ssl.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"));
await ssl.WriteAsync(HTTP2Frame.CreateSettings().Serialize());

// Big connection window so flow control never throttles us
await ssl.WriteAsync(HTTP2Frame.CreateWindowUpdate(0, 1_000_000).Serialize());
await ssl.FlushAsync();

// Three concurrent request streams
var encoder = new HPACKEncoder();

byte[] Request(string path) =>
    encoder.EncodeHeaderBlock(
    [
        (":method",    "GET"),
        (":scheme",    "https"),
        (":authority", "localhost:8443"),
        (":path",      path)
    ]);

async Task SendHeaders(uint streamId, string path)
{
    var f = HTTP2Frame.CreateHeaders(streamId, Request(path), EndStream: true, EndHeaders: true);
    await ssl.WriteAsync(f.Serialize());
    await ssl.FlushAsync();
    Log($"sent HEADERS stream={streamId} {path}");
}

await SendHeaders(1, "/slow");
await SendHeaders(3, "/");
await SendHeaders(5, "/large");

// Read loop: log every frame, ACK SETTINGS, replenish windows, count END_STREAMs
var done      = new HashSet<uint>();
var headerBuf = new byte[9];

async Task ReadExact(byte[] buf, int len)
{
    var o = 0;
    while (o < len)
    {
        var n = await ssl.ReadAsync(buf.AsMemory(o, len - o));
        if (n == 0) throw new IOException("EOF");
        o += n;
    }
}

while (done.Count < 3)
{
    await ReadExact(headerBuf, 9);
    var frame = HTTP2Frame.ParseHeader(headerBuf);

    if (frame.Length > 0)
    {
        frame.Payload = new byte[frame.Length];
        await ReadExact(frame.Payload, (int) frame.Length);
    }

    Log($"recv {frame}");

    if (frame.Type == HTTP2FrameType.SETTINGS && !frame.IsAck)
    {
        await ssl.WriteAsync(HTTP2Frame.CreateSettingsAck().Serialize());
        await ssl.FlushAsync();
    }

    if (frame.Type == HTTP2FrameType.DATA && frame.Length > 0)
    {
        // Replenish stream + connection windows immediately
        await ssl.WriteAsync(HTTP2Frame.CreateWindowUpdate(frame.StreamId, frame.Length).Serialize());
        await ssl.WriteAsync(HTTP2Frame.CreateWindowUpdate(0, frame.Length).Serialize());
        await ssl.FlushAsync();
    }

    if ((frame.Type == HTTP2FrameType.HEADERS || frame.Type == HTTP2FrameType.DATA) && frame.EndStream)
    {
        done.Add(frame.StreamId);
        Log($"stream {frame.StreamId} complete");
    }
}

Log("all 3 streams complete");

