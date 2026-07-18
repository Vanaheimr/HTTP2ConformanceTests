using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;

// =============================================================================
// Self-contained RFC 6455 conformance harness — the Autobahn TestSuite's
// critical cases (framing, fragmentation, UTF-8 handling, close handling),
// driven directly against our own WebSocketConnection framing so the fixes are
// verified in the pass/fail gate WITHOUT needing the external Docker suite.
// The full Autobahn run stays available via tests/autobahn.{ps1,sh} for a
// deeper (~500-case) check on a machine with Docker — see
// tests/TestingAgainst_Autobahn.md. This harness spins up the SAME echo server
// (server-role WebSocketConnection over a plain-TCP tunnel behind an HTTP/1.1
// Upgrade handshake) that Autobahn would drive, then plays a raw WebSocket
// client — including deliberately malformed frames a well-behaved client would
// never send — and checks the server's framing-level response.
// =============================================================================

var passed = 0;
var failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"  ✓ {name}" + (detail.Length > 0 ? $"  ({detail})" : "")); }
    else    { failed++; Console.WriteLine($"  ✗ {name}" + (detail.Length > 0 ? $"  — {detail}" : "")); }
}

const int port = 9467;

// --- in-process echo server (same shape as tests/autobahn-server) ------------
var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
const string wsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

var serverLoop = Task.Run(async () =>
{
    while (true)
    {
        TcpClient c;
        try { c = await listener.AcceptTcpClientAsync(); } catch { break; }
        _ = Task.Run(async () =>
        {
            using (c)
            {
                try
                {
                    var s     = c.GetStream();
                    var lines = await ReadHeaders(s);
                    if (lines is null) return;
                    var key = Header(lines, "Sec-WebSocket-Key");
                    if (key is null) return;
                    var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + wsGuid)));

                    // Negotiate permessage-deflate (no-context-takeover) if offered.
                    var deflate = (Header(lines, "Sec-WebSocket-Extensions") ?? "").Contains("permessage-deflate");
                    var extLine = deflate ? "Sec-WebSocket-Extensions: permessage-deflate; server_no_context_takeover; client_no_context_takeover\r\n" : "";

                    await s.WriteAsync(Encoding.ASCII.GetBytes(
                        "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                        $"Sec-WebSocket-Accept: {accept}\r\n{extLine}\r\n"));
                    var ws = new WebSocketConnection(new TcpTunnel(s), WebSocketRole.Server, PerMessageDeflate: deflate);
                    while (true)
                    {
                        WebSocketMessage? m;
                        try { m = await ws.ReceiveAsync(CancellationToken.None); }
                        catch { break; }
                        if (m is null) break;
                        if (m.Opcode == WebSocketOpcode.Text)
                            await ws.SendTextAsync(Encoding.UTF8.GetString(m.Payload), CancellationToken.None);
                        else
                            await ws.SendBinaryAsync(m.Payload, CancellationToken.None);
                    }
                }
                catch { }
            }
        });
    }
});

async Task<string[]?> ReadHeaders(NetworkStream s)
{
    var sb = new StringBuilder(); var one = new byte[1];
    while (sb.Length < 16 * 1024)
    {
        if (await s.ReadAsync(one) == 0) return null;
        sb.Append((char) one[0]);
        if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r')
            return sb.ToString().Split("\r\n");
    }
    return null;
}

static string? Header(string[] lines, string name)
{
    foreach (var line in lines)
    {
        var i = line.IndexOf(':');
        if (i > 0 && line[..i].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            return line[(i + 1)..].Trim();
    }
    return null;
}

await Task.Delay(300);

// --- raw WebSocket client (masks its frames; can craft malformed ones) -------
async Task<NetworkStream> Handshake()
{
    var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, port);
    var s = tcp.GetStream();
    var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
    await s.WriteAsync(Encoding.ASCII.GetBytes(
        $"GET / HTTP/1.1\r\nHost: localhost:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
        $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n"));
    // Read the 101 response headers (up to the blank line).
    var sb = new StringBuilder(); var one = new byte[1];
    while (true)
    {
        if (await s.ReadAsync(one) == 0) throw new IOException("handshake ended");
        sb.Append((char) one[0]);
        if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r') break;
    }
    if (!sb.ToString().StartsWith("HTTP/1.1 101")) throw new IOException("no 101");
    return s;
}

// Send one client frame (always masked, per RFC 6455 5.3). rsv is the 3-bit
// RSV field in the low bits; opcode is raw (so a reserved opcode can be sent).
async Task SendRaw(NetworkStream s, bool fin, int rsv, int opcode, byte[] payload)
{
    var h = new List<byte> { (byte) ((fin ? 0x80 : 0) | ((rsv & 0x7) << 4) | (opcode & 0x0F)) };
    if (payload.Length <= 125) h.Add((byte) (0x80 | payload.Length));
    else if (payload.Length <= 65535) { h.Add(0x80 | 126); var e = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(e, (ushort) payload.Length); h.AddRange(e); }
    else { h.Add(0x80 | 127); var e = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(e, (ulong) payload.Length); h.AddRange(e); }
    var key = RandomNumberGenerator.GetBytes(4);
    h.AddRange(key);
    var masked = new byte[payload.Length];
    for (var i = 0; i < payload.Length; i++) masked[i] = (byte) (payload[i] ^ key[i % 4]);
    var frame = new byte[h.Count + masked.Length];
    h.CopyTo(frame); masked.CopyTo(frame, h.Count);
    await s.WriteAsync(frame);
    await s.FlushAsync();
}

async Task<bool> ReadExact(NetworkStream s, byte[] buf)
{
    var off = 0;
    while (off < buf.Length)
    {
        var n = await s.ReadAsync(buf.AsMemory(off, buf.Length - off));
        if (n == 0) return false;
        off += n;
    }
    return true;
}

// Read one server frame (server->client is unmasked). Returns null on EOF.
async Task<(int Opcode, byte[] Payload)?> ReadFrame(NetworkStream s)
{
    var h2 = new byte[2];
    if (!await ReadExact(s, h2)) return null;
    var opcode = h2[0] & 0x0F;
    long len = h2[1] & 0x7F;
    if (len == 126) { var e = new byte[2]; if (!await ReadExact(s, e)) return null; len = BinaryPrimitives.ReadUInt16BigEndian(e); }
    else if (len == 127) { var e = new byte[8]; if (!await ReadExact(s, e)) return null; len = (long) BinaryPrimitives.ReadUInt64BigEndian(e); }
    var payload = new byte[len];
    if (len > 0 && !await ReadExact(s, payload)) return null;
    return (opcode, payload);
}

// Expect the server to answer with a Close frame carrying the given code.
async Task<ushort?> ExpectClose(NetworkStream s)
{
    while (true)
    {
        var f = await ReadFrame(s);
        if (f is null) return null;
        if (f.Value.Opcode == 0x8)
            return f.Value.Payload.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(f.Value.Payload) : (ushort) 0;
        // ignore any non-close frame (e.g. an echoed text) while waiting for close
    }
}

// Read one server frame including its RSV1 (compression) bit. Null on EOF.
async Task<(bool Rsv1, int Opcode, byte[] Payload)?> ReadFrameFull(NetworkStream s)
{
    var h2 = new byte[2];
    if (!await ReadExact(s, h2)) return null;
    var rsv1 = (h2[0] & 0x40) != 0;
    var opcode = h2[0] & 0x0F;
    long len = h2[1] & 0x7F;
    if (len == 126) { var e = new byte[2]; if (!await ReadExact(s, e)) return null; len = BinaryPrimitives.ReadUInt16BigEndian(e); }
    else if (len == 127) { var e = new byte[8]; if (!await ReadExact(s, e)) return null; len = (long) BinaryPrimitives.ReadUInt64BigEndian(e); }
    var payload = new byte[len];
    if (len > 0 && !await ReadExact(s, payload)) return null;
    return (rsv1, opcode, payload);
}

// A handshake that offers permessage-deflate; returns the stream and whether
// the server accepted the extension in its 101 response.
async Task<(NetworkStream Stream, bool Deflate)> HandshakeOfferingDeflate()
{
    var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, port);
    var s = tcp.GetStream();
    var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
    await s.WriteAsync(Encoding.ASCII.GetBytes(
        $"GET / HTTP/1.1\r\nHost: localhost:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
        $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Extensions: permessage-deflate\r\n\r\n"));
    var sb = new StringBuilder(); var one = new byte[1];
    while (true)
    {
        if (await s.ReadAsync(one) == 0) throw new IOException("handshake ended");
        sb.Append((char) one[0]);
        if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r') break;
    }
    var head = sb.ToString();
    if (!head.StartsWith("HTTP/1.1 101")) throw new IOException("no 101");
    return (s, head.Contains("permessage-deflate", StringComparison.OrdinalIgnoreCase));
}

// permessage-deflate wire codec (RFC 7692 §7.2), client side — mirrors Core.
static byte[] DeflateBody(byte[] data)
{
    using var o = new MemoryStream();
    using (var d = new DeflateStream(o, CompressionLevel.Optimal, leaveOpen: true)) d.Write(data, 0, data.Length);
    var b = o.ToArray();
    if (b.Length >= 4 && b[^4] == 0 && b[^3] == 0 && b[^2] == 0xFF && b[^1] == 0xFF) b = b[..^4];
    return b;
}
static byte[] InflateBody(byte[] comp)
{
    using var i = new MemoryStream(); i.Write(comp, 0, comp.Length); i.Write([0, 0, 0xFF, 0xFF], 0, 4); i.Position = 0;
    using var d = new DeflateStream(i, CompressionMode.Decompress);
    using var o = new MemoryStream(); d.CopyTo(o); return o.ToArray();
}

const int Text = 0x1, Binary = 0x2, Close = 0x8, Ping = 0x9, Pong = 0xA, Cont = 0x0;

// =========================================================================
Console.WriteLine("--- Section 1/2/5: framing, ping/pong, fragmentation ---");
{
    var s = await Handshake();

    await SendRaw(s, true, 0, Text, Encoding.UTF8.GetBytes("Hello"));
    var echo = await ReadFrame(s);
    Check("1.1 text message is echoed", echo is { Opcode: Text } && Encoding.UTF8.GetString(echo.Value.Payload) == "Hello");

    var bin = new byte[] { 1, 2, 3, 0, 255, 128 };
    await SendRaw(s, true, 0, Binary, bin);
    var be = await ReadFrame(s);
    Check("1.2 binary message is echoed", be is { Opcode: Binary } && be.Value.Payload.SequenceEqual(bin));

    await SendRaw(s, true, 0, Ping, Encoding.UTF8.GetBytes("pingdata"));
    var pong = await ReadFrame(s);
    Check("2.1 ping is answered with a matching pong",
          pong is { Opcode: Pong } && Encoding.UTF8.GetString(pong.Value.Payload) == "pingdata");

    // Fragmented text: "Hel" + "lo!" across a Text(FIN=0) + Continuation(FIN=1).
    await SendRaw(s, false, 0, Text, Encoding.UTF8.GetBytes("Hel"));
    await SendRaw(s, true, 0, Cont, Encoding.UTF8.GetBytes("lo!"));
    var frag = await ReadFrame(s);
    Check("5.x fragmented text is reassembled and echoed",
          frag is { Opcode: Text } && Encoding.UTF8.GetString(frag.Value.Payload) == "Hello!");

    s.Close();
}

// =========================================================================
Console.WriteLine("--- Section 3/4: reserved bits and opcodes ---");
{
    var s = await Handshake();
    await SendRaw(s, true, 0b100, Text, Encoding.UTF8.GetBytes("x"));   // RSV1 set, no extension negotiated
    Check("3.x a set reserved bit fails the connection (Close 1002)", await ExpectClose(s) == 1002);
    s.Close();

    s = await Handshake();
    await SendRaw(s, true, 0, 0x3, [1, 2, 3]);   // reserved non-control opcode
    Check("4.x a reserved opcode fails the connection (Close 1002)", await ExpectClose(s) == 1002);
    s.Close();

    s = await Handshake();
    await SendRaw(s, true, 0, Cont, Encoding.UTF8.GetBytes("orphan"));  // continuation with no start
    Check("5.x an unexpected continuation frame fails the connection (Close 1002)", await ExpectClose(s) == 1002);
    s.Close();
}

// =========================================================================
Console.WriteLine("--- Section 6: UTF-8 handling ---");
{
    var s = await Handshake();
    var utf8 = Encoding.UTF8.GetBytes("Ĥéłłö 世界 🌍");   // multi-byte incl. an astral char
    await SendRaw(s, true, 0, Text, utf8);
    var echo = await ReadFrame(s);
    Check("6.x valid multi-byte UTF-8 text is echoed", echo is { Opcode: Text } && echo.Value.Payload.SequenceEqual(utf8));
    s.Close();

    // A valid code point (é = 0xC3 0xA9) split across two text fragments.
    s = await Handshake();
    await SendRaw(s, false, 0, Text, [0xC3]);
    await SendRaw(s, true, 0, Cont, [0xA9]);
    var split = await ReadFrame(s);
    Check("6.x a UTF-8 code point split across fragments is valid and echoed",
          split is { Opcode: Text } && split.Value.Payload.SequenceEqual(new byte[] { 0xC3, 0xA9 }));
    s.Close();

    // Invalid UTF-8 in a single text frame (0xFF is never valid) → 1007.
    s = await Handshake();
    await SendRaw(s, true, 0, Text, [0x48, 0xFF, 0x69]);
    Check("6.x invalid UTF-8 in a text message fails the connection (Close 1007)", await ExpectClose(s) == 1007);
    s.Close();

    // Invalid byte introduced in the second fragment → 1007.
    s = await Handshake();
    await SendRaw(s, false, 0, Text, Encoding.UTF8.GetBytes("ok"));
    await SendRaw(s, true, 0, Cont, [0xC0, 0xAF]);   // overlong "/" — invalid
    Check("6.x invalid UTF-8 in a text fragment fails the connection (Close 1007)", await ExpectClose(s) == 1007);
    s.Close();

    // An incomplete code point at end-of-message (0xC3 with no continuation) → 1007.
    s = await Handshake();
    await SendRaw(s, true, 0, Text, [0x41, 0xC3]);
    Check("6.x a truncated trailing UTF-8 sequence fails the connection (Close 1007)", await ExpectClose(s) == 1007);
    s.Close();
}

// =========================================================================
Console.WriteLine("--- Section 7: close handling ---");
{
    // Valid close (1000) is echoed back with the same code.
    var s = await Handshake();
    var p = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(p, 1000);
    await SendRaw(s, true, 0, Close, p);
    Check("7.3.1 a valid close (1000) is echoed", await ExpectClose(s) == 1000);
    s.Close();

    // Empty close payload is valid → echoed empty (code 0 here means "no code").
    s = await Handshake();
    await SendRaw(s, true, 0, Close, []);
    var emptyClose = await ExpectClose(s);
    Check("7.3.1 an empty close is answered with a close", emptyClose is not null);
    s.Close();

    // 1-byte close payload is malformed → 1002.
    s = await Handshake();
    await SendRaw(s, true, 0, Close, [0x03]);
    Check("7.3.2 a 1-byte close payload fails the connection (Close 1002)", await ExpectClose(s) == 1002);
    s.Close();

    // Close with a valid code + reason is echoed.
    s = await Handshake();
    var pr = new byte[2 + 3]; BinaryPrimitives.WriteUInt16BigEndian(pr, 1000); Encoding.ASCII.GetBytes("bye").CopyTo(pr, 2);
    await SendRaw(s, true, 0, Close, pr);
    Check("7.3.3 a close with code + reason is echoed", await ExpectClose(s) == 1000);
    s.Close();

    // Reserved/invalid close codes → 1002.
    foreach (var (code, label) in new[] { (999, "999"), (1004, "1004"), (1005, "1005"), (1006, "1006"), (1016, "1016"), (2000, "2000"), (65535, "65535") })
    {
        s = await Handshake();
        var cp = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(cp, (ushort) code);
        await SendRaw(s, true, 0, Close, cp);
        Check($"7.9.x invalid close code {label} fails the connection (Close 1002)", await ExpectClose(s) == 1002);
        s.Close();
    }

    // Valid application-range close codes (3000, 4999) are accepted/echoed.
    foreach (var code in new[] { 3000, 4999 })
    {
        s = await Handshake();
        var cp = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(cp, (ushort) code);
        await SendRaw(s, true, 0, Close, cp);
        Check($"7.7.x valid close code {code} is echoed", await ExpectClose(s) == (ushort) code);
        s.Close();
    }

    // Close with a valid code but invalid UTF-8 in the reason → 1007.
    s = await Handshake();
    var badReason = new byte[] { 0x03, 0xE8, 0xFF, 0xFF };   // code 1000, then invalid UTF-8
    await SendRaw(s, true, 0, Close, badReason);
    Check("7.5.1 invalid UTF-8 in a close reason fails the connection (Close 1007)", await ExpectClose(s) == 1007);
    s.Close();
}

// =========================================================================
Console.WriteLine("--- RFC 7692: permessage-deflate ---");
{
    var (s, accepted) = await HandshakeOfferingDeflate();
    Check("permessage-deflate is negotiated when offered", accepted);

    // Send a compressed text message: deflate the payload, set RSV1.
    var text = "Hello, permessage-deflate! " + new string('x', 200);   // compressible
    await SendRaw(s, true, 0b100, Text, DeflateBody(Encoding.UTF8.GetBytes(text)));
    var echo = await ReadFrameFull(s);
    Check("compressed text round-trips (echo is RSV1-compressed, inflates to original)",
          echo is { Rsv1: true, Opcode: Text } && Encoding.UTF8.GetString(InflateBody(echo.Value.Payload)) == text);

    // Compressed binary.
    var bin = Enumerable.Range(0, 300).Select(i => (byte) (i % 7)).ToArray();   // compressible
    await SendRaw(s, true, 0b100, Binary, DeflateBody(bin));
    var be = await ReadFrameFull(s);
    Check("compressed binary round-trips", be is { Rsv1: true, Opcode: Binary } && InflateBody(be.Value.Payload).SequenceEqual(bin));

    // A compressed message split across two frames: RSV1 only on the first.
    var frag = Encoding.UTF8.GetBytes("fragmented compressed message payload, reasonably long");
    var comp = DeflateBody(frag);
    var half = comp.Length / 2;
    await SendRaw(s, false, 0b100, Text, comp[..half]);   // first frame: RSV1 set, FIN=0
    await SendRaw(s, true,  0,     Cont, comp[half..]);   // continuation: RSV1 clear, FIN=1
    var fe = await ReadFrameFull(s);
    Check("compressed fragmented text round-trips", fe is { Opcode: Text } && Encoding.UTF8.GetString(InflateBody(fe.Value.Payload)) == Encoding.UTF8.GetString(frag));

    // RFC 7692 §5.1: even with the extension active, a message MAY be sent
    // uncompressed (RSV1 = 0) — the server must still handle it.
    await SendRaw(s, true, 0, Text, Encoding.UTF8.GetBytes("plain"));
    var pe = await ReadFrameFull(s);
    var plainText = pe!.Value.Rsv1 ? Encoding.UTF8.GetString(InflateBody(pe.Value.Payload)) : Encoding.UTF8.GetString(pe.Value.Payload);
    Check("an uncompressed message on a deflate connection still round-trips", plainText == "plain");

    s.Close();
}

listener.Stop();
try { await serverLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);


/// <summary>Minimal IHTTP2Tunnel over a raw TCP stream (mirrors tests/autobahn-server).</summary>
sealed class TcpTunnel(NetworkStream Stream) : IHTTP2Tunnel
{
    private readonly byte[] buf = new byte[64 * 1024];
    public async Task<byte[]?> ReadAsync(CancellationToken ct)
    {
        var n = await Stream.ReadAsync(buf, ct);
        return n == 0 ? null : buf[..n];
    }
    public Task WriteAsync(byte[] Data, CancellationToken ct) => Stream.WriteAsync(Data, ct).AsTask();
}
