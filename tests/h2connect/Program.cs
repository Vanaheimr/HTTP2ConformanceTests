/*
 * Copyright (c) 2010-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Vanaheimr Hermod <https://www.github.com/Vanaheimr/Hermod>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;

// Raw HTTP/2 test client for Track B: CONNECT (RFC 9113 §8.5), extended
// CONNECT (RFC 8441), and WebSocket-over-HTTP/2 (RFC 6455 framing on top).
//
// Modes:
//   settings              - verify the server advertises ENABLE_CONNECT_PROTOCOL=1
//   loopback              - plain CONNECT, verify the raw byte-tunnel echoes back
//   ws-echo               - extended CONNECT :protocol=websocket -> WS message exchange
//   ws-fragmented         - a text message split across CONTINUATION-style WS frames
//   ws-ping               - unsolicited ping -> expect a matching pong
//   ws-close              - client-initiated close handshake
//   reject                - unknown :protocol/:path -> 404, not a stream error
//   cancel                - RST_STREAM mid-tunnel -> connection stays usable after
//   malformed <case>      - CONNECT/extended-CONNECT header violations -> RST_STREAM/PROTOCOL_ERROR
//     cases: scheme-on-connect, path-on-connect, missing-authority,
//            missing-scheme-extended, missing-path-extended, protocol-on-get

var mode = args.Length > 0 ? args[0] : "settings";

using var tcp = new TcpClient();
await tcp.ConnectAsync("127.0.0.1", 8443);

var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
    TargetHost           = "localhost",
    ApplicationProtocols = [SslApplicationProtocol.Http2]
});

await ssl.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"));
await ssl.WriteAsync(HTTP2Frame.CreateSettings().Serialize());
await ssl.FlushAsync();

var goaway              = new TaskCompletionSource<HTTP2ErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
var serverSettings      = new TaskCompletionSource<Dictionary<HTTP2SettingsParameter, uint>>(TaskCreationOptions.RunContinuationsAsynchronously);
var rstStreams          = new Dictionary<uint, TaskCompletionSource<HTTP2ErrorCode>>();
var statusByStream      = new Dictionary<uint, TaskCompletionSource<string>>();
var tunnelDataByStream  = System.Threading.Channels.Channel.CreateUnbounded<(uint StreamId, byte[] Data, bool EndStream)>();

TaskCompletionSource<HTTP2ErrorCode> RstFor(uint sid)
{
    lock (rstStreams)
    {
        if (!rstStreams.TryGetValue(sid, out var tcs))
            rstStreams[sid] = tcs = new TaskCompletionSource<HTTP2ErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        return tcs;
    }
}

TaskCompletionSource<string> StatusFor(uint sid)
{
    lock (statusByStream)
    {
        if (!statusByStream.TryGetValue(sid, out var tcs))
            statusByStream[sid] = tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        return tcs;
    }
}

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

_ = Task.Run(async () =>
{
    var hdr = new byte[9];
    var decoder = new HPACKDecoder();
    var gotFirstSettings = false;
    try
    {
        while (true)
        {
            await ReadExact(hdr, 9);
            var f = HTTP2Frame.ParseHeader(hdr);
            if (f.Length > 0) { f.Payload = new byte[f.Length]; await ReadExact(f.Payload, (int) f.Length); }

            switch (f.Type)
            {
                case HTTP2FrameType.GOAWAY:
                {
                    var code = (HTTP2ErrorCode) BinaryPrimitives.ReadUInt32BigEndian(f.Payload.AsSpan(4, 4));
                    goaway.TrySetResult(code);
                    break;
                }
                case HTTP2FrameType.RST_STREAM:
                {
                    var code = (HTTP2ErrorCode) BinaryPrimitives.ReadUInt32BigEndian(f.Payload.AsSpan(0, 4));
                    RstFor(f.StreamId).TrySetResult(code);
                    break;
                }
                case HTTP2FrameType.HEADERS:
                {
                    var hs = decoder.DecodeHeaderBlock(f.Payload);
                    var st = hs.FirstOrDefault(h => h.Name == ":status").Value ?? "?";
                    StatusFor(f.StreamId).TrySetResult(st);
                    break;
                }
                case HTTP2FrameType.DATA:
                {
                    await tunnelDataByStream.Writer.WriteAsync((f.StreamId, f.Payload, f.EndStream));
                    break;
                }
                case HTTP2FrameType.SETTINGS when !f.IsAck && !gotFirstSettings:
                {
                    gotFirstSettings = true;
                    var dict = new Dictionary<HTTP2SettingsParameter, uint>();
                    for (var i = 0; i < f.Payload.Length; i += 6)
                    {
                        var id  = (HTTP2SettingsParameter) BinaryPrimitives.ReadUInt16BigEndian(f.Payload.AsSpan(i, 2));
                        var val = BinaryPrimitives.ReadUInt32BigEndian(f.Payload.AsSpan(i + 2, 4));
                        dict[id] = val;
                    }
                    serverSettings.TrySetResult(dict);

                    // ACK it so the server's own handshake completes normally.
                    await ssl.WriteAsync(HTTP2Frame.CreateSettingsAck().Serialize());
                    await ssl.FlushAsync();
                    break;
                }
            }
        }
    }
    catch { goaway.TrySetException(new Exception("connection closed")); }
});

var encoder = new HPACKEncoder();

HTTP2Frame RawHeaders(uint sid, (string Name, string Value)[] headers, bool endStream) => new() {
    Type = HTTP2FrameType.HEADERS, StreamId = sid,
    Flags = HTTP2FrameFlags.END_HEADERS | (endStream ? HTTP2FrameFlags.END_STREAM : HTTP2FrameFlags.NONE),
    Payload = encoder.EncodeHeaderBlock(headers)
};

HTTP2Frame RawData(uint sid, byte[] data, bool endStream) => new() {
    Type = HTTP2FrameType.DATA, StreamId = sid,
    Flags = endStream ? HTTP2FrameFlags.END_STREAM : HTTP2FrameFlags.NONE, Payload = data
};

async Task Send(HTTP2Frame f)
{
    try { await ssl.WriteAsync(f.Serialize()); await ssl.FlushAsync(); }
    catch { /* server may have closed — expected for some scenarios */ }
}

async Task<string> OpenAndWait(uint sid, (string Name, string Value)[] headers)
{
    await Send(RawHeaders(sid, headers, endStream: true));
    return await StatusFor(sid).Task.WaitAsync(TimeSpan.FromSeconds(5));
}

// --- WebSocket client-side framing (opposite masking direction from the
//     server's WebSocketConnection — client frames MUST be masked, server
//     frames MUST NOT be, and we verify both directions here). -----------

byte[] BuildClientWsFrame(byte opcode, byte[] payload)
{
    var rnd = new Random();
    var maskKey = new byte[4];
    rnd.NextBytes(maskKey);

    var header = new List<byte> { (byte) (0x80 | opcode) }; // FIN=1

    if (payload.Length <= 125)
        header.Add((byte) (0x80 | payload.Length)); // MASK=1
    else if (payload.Length <= 65535)
    {
        header.Add(0x80 | 126);
        var ext = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(ext, (ushort) payload.Length);
        header.AddRange(ext);
    }
    else throw new NotSupportedException("test client doesn't need >64KiB frames");

    header.AddRange(maskKey);

    var masked = new byte[payload.Length];
    for (var i = 0; i < payload.Length; i++)
        masked[i] = (byte) (payload[i] ^ maskKey[i % 4]);

    var frame = new byte[header.Count + masked.Length];
    header.CopyTo(frame, 0);
    masked.CopyTo(frame, header.Count);
    return frame;
}

// Buffers raw tunnel DATA bytes for a specific stream and lets us read exact
// counts, same buffering problem the server-side WebSocketConnection solves.
async Task<byte[]> ReadTunnelExact(uint sid, int count, Queue<byte> pending)
{
    while (pending.Count < count)
    {
        (uint StreamId, byte[] Data, bool EndStream) item;
        do { item = await tunnelDataByStream.Reader.ReadAsync(); } while (item.StreamId != sid);
        foreach (var b in item.Data) pending.Enqueue(b);
    }
    var result = new byte[count];
    for (var i = 0; i < count; i++) result[i] = pending.Dequeue();
    return result;
}

async Task<(byte Opcode, bool Fin, byte[] Payload)> ReadServerWsFrame(uint sid, Queue<byte> pending)
{
    var header = await ReadTunnelExact(sid, 2, pending);
    var fin    = (header[0] & 0x80) != 0;
    var opcode = (byte) (header[0] & 0x0F);
    var masked = (header[1] & 0x80) != 0;
    var len7   = header[1] & 0x7F;

    if (masked) throw new Exception("PROTOCOL VIOLATION: server-to-client frame was masked!");

    long len = len7;
    if (len7 == 126)
    {
        var ext = await ReadTunnelExact(sid, 2, pending);
        len = BinaryPrimitives.ReadUInt16BigEndian(ext);
    }

    var payload = len > 0 ? await ReadTunnelExact(sid, (int) len, pending) : [];
    return (opcode, fin, payload);
}

Console.WriteLine($"[connect] mode = {mode}");

switch (mode)
{
    case "settings":
    {
        var settings = await serverSettings.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var has = settings.TryGetValue(HTTP2SettingsParameter.ENABLE_CONNECT_PROTOCOL, out var val);
        Console.WriteLine($"[connect] ENABLE_CONNECT_PROTOCOL present={has} value={(has ? val : 0)}  " +
                          (has && val == 1 ? "✓ PASS" : "✗ FAIL"));
        return;
    }

    case "loopback":
    {
        await Send(RawHeaders(1, [(":method", "CONNECT"), (":authority", "echo.test")], endStream: false));
        var status = await StatusFor(1).Task.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"[connect] plain CONNECT -> :status {status}  " + (status == "200" ? "✓" : "✗ FAIL"));

        var pending = new Queue<byte>();
        var ok = true;

        foreach (var msg in new[] { "hello", "second chunk", "🎉 unicode too" })
        {
            var payload = Encoding.UTF8.GetBytes(msg);
            await Send(RawData(1, payload, endStream: false));
            var echoed = await ReadTunnelExact(1, payload.Length, pending);
            var matches = echoed.SequenceEqual(payload);
            ok &= matches;
            Console.WriteLine($"[connect] sent \"{msg}\" -> echoed \"{Encoding.UTF8.GetString(echoed)}\"  " + (matches ? "✓" : "✗ MISMATCH"));
        }

        await Send(RawData(1, [], endStream: true));
        Console.WriteLine(ok ? "[connect] ✓ PASS: loopback tunnel echoed everything correctly" : "[connect] ✗ FAIL");
        return;
    }

    case "ws-echo":
    {
        await Send(RawHeaders(1,
            [(":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"), (":path", "/ws-echo"), (":authority", "localhost:8443")],
            endStream: false));

        var status = await StatusFor(1).Task.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"[connect] extended CONNECT -> :status {status}  " + (status == "200" ? "✓" : "✗ FAIL"));

        var pending = new Queue<byte>();

        // Text message
        await Send(RawData(1, BuildClientWsFrame(0x1, Encoding.UTF8.GetBytes("Hello WebSocket!")), endStream: false));
        var (op1, fin1, payload1) = await ReadServerWsFrame(1, pending);
        var text = Encoding.UTF8.GetString(payload1);
        Console.WriteLine($"[connect] text echo: opcode=0x{op1:x} fin={fin1} \"{text}\"  " +
                          (op1 == 0x1 && fin1 && text == "Hello WebSocket!" ? "✓" : "✗ FAIL"));

        // Binary message
        var binPayload = new byte[] { 1, 2, 3, 4, 250, 251, 252 };
        await Send(RawData(1, BuildClientWsFrame(0x2, binPayload), endStream: false));
        var (op2, fin2, payload2) = await ReadServerWsFrame(1, pending);
        Console.WriteLine($"[connect] binary echo: opcode=0x{op2:x} fin={fin2} bytes={payload2.Length}  " +
                          (op2 == 0x2 && fin2 && payload2.SequenceEqual(binPayload) ? "✓" : "✗ FAIL"));

        // Close handshake
        await Send(RawData(1, BuildClientWsFrame(0x8, [0x03, 0xE8]), endStream: false)); // 1000 = normal closure
        var (op3, fin3, _) = await ReadServerWsFrame(1, pending);
        Console.WriteLine($"[connect] close echoed back: opcode=0x{op3:x} fin={fin3}  " +
                          (op3 == 0x8 && fin3 ? "✓ PASS" : "✗ FAIL"));

        return;
    }

    case "ws-fragmented":
    {
        await Send(RawHeaders(1,
            [(":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"), (":path", "/ws-echo"), (":authority", "localhost:8443")],
            endStream: false));
        await StatusFor(1).Task.WaitAsync(TimeSpan.FromSeconds(5));

        var pending = new Queue<byte>();

        // "Hello, World!" split into three fragments: start(0x1,FIN=0), continuation(0x0,FIN=0), continuation(0x0,FIN=1).
        byte[] Frag(byte opcode, bool fin, string s)
        {
            var payload = Encoding.UTF8.GetBytes(s);
            var rnd = new Random(); var maskKey = new byte[4]; rnd.NextBytes(maskKey);
            var header = new List<byte> { (byte) ((fin ? 0x80 : 0x00) | opcode), (byte) (0x80 | payload.Length) };
            header.AddRange(maskKey);
            var masked = new byte[payload.Length];
            for (var i = 0; i < payload.Length; i++) masked[i] = (byte) (payload[i] ^ maskKey[i % 4]);
            var frame = new byte[header.Count + masked.Length];
            header.CopyTo(frame, 0); masked.CopyTo(frame, header.Count);
            return frame;
        }

        await Send(RawData(1, Frag(0x1, false, "Hello, "), endStream: false));
        await Send(RawData(1, Frag(0x0, false, "frag"), endStream: false));
        await Send(RawData(1, Frag(0x0, true,  "mented!"), endStream: false));

        var (op, fin, payload) = await ReadServerWsFrame(1, pending);
        var text = Encoding.UTF8.GetString(payload);
        Console.WriteLine($"[connect] reassembled fragmented message: \"{text}\"  " +
                          (op == 0x1 && fin && text == "Hello, fragmented!" ? "✓ PASS" : "✗ FAIL"));
        return;
    }

    case "ws-ping":
    {
        await Send(RawHeaders(1,
            [(":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"), (":path", "/ws-echo"), (":authority", "localhost:8443")],
            endStream: false));
        await StatusFor(1).Task.WaitAsync(TimeSpan.FromSeconds(5));

        var pending = new Queue<byte>();
        await Send(RawData(1, BuildClientWsFrame(0x9, Encoding.UTF8.GetBytes("ping-payload")), endStream: false));

        var (op, fin, payload) = await ReadServerWsFrame(1, pending);
        var text = Encoding.UTF8.GetString(payload);
        Console.WriteLine($"[connect] ping -> pong: opcode=0x{op:x} fin={fin} payload=\"{text}\"  " +
                          (op == 0xA && fin && text == "ping-payload" ? "✓ PASS" : "✗ FAIL"));
        return;
    }

    case "ws-close":
    {
        await Send(RawHeaders(1,
            [(":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"), (":path", "/ws-echo"), (":authority", "localhost:8443")],
            endStream: false));
        await StatusFor(1).Task.WaitAsync(TimeSpan.FromSeconds(5));

        var pending = new Queue<byte>();
        await Send(RawData(1, BuildClientWsFrame(0x8, [0x03, 0xE9]), endStream: false)); // 1001 = going away

        var (op, fin, closePayload) = await ReadServerWsFrame(1, pending);
        var code = closePayload.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(closePayload) : 0;
        Console.WriteLine($"[connect] client-initiated close echoed: opcode=0x{op:x} code={code}  " +
                          (op == 0x8 && code == 1001 ? "✓ PASS" : "✗ FAIL"));

        // Tunnel must end shortly after (server's RunAsync returns -> END_STREAM).
        var ended = await Task.WhenAny(
            Task.Run(async () => { (uint, byte[], bool) item; do { item = await tunnelDataByStream.Reader.ReadAsync(); } while (item.Item1 != 1 || !item.Item3); }),
            Task.Delay(3000)
        );
        Console.WriteLine(ended.IsCompleted ? "[connect] ✓ tunnel ended (END_STREAM) after close handshake" : "[connect] ✗ FAIL: tunnel never ended");
        return;
    }

    case "reject":
    {
        var status = await OpenAndWait(1,
            [(":method", "CONNECT"), (":protocol", "carrier-pigeon"), (":scheme", "https"), (":path", "/ws-echo"), (":authority", "localhost:8443")]);
        Console.WriteLine($"[connect] unknown :protocol -> :status {status}  " + (status == "404" ? "✓ PASS" : "✗ FAIL"));

        // Connection must stay usable — this is an application-level refusal, not a framing error.
        var followUp = await OpenAndWait(3, [(":method", "GET"), (":scheme", "https"), (":authority", "localhost:8443"), (":path", "/")]);
        Console.WriteLine($"[connect] connection still alive -> :status {followUp}  " + (followUp == "200" ? "✓ PASS" : "✗ FAIL"));
        return;
    }

    case "cancel":
    {
        await Send(RawHeaders(1, [(":method", "CONNECT"), (":authority", "echo.test")], endStream: false));
        await StatusFor(1).Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Task.Delay(200);
        await Send(HTTP2Frame.CreateRstStream(1, HTTP2ErrorCode.CANCEL));

        var followUp = await OpenAndWait(3, [(":method", "GET"), (":scheme", "https"), (":authority", "localhost:8443"), (":path", "/")]);
        Console.WriteLine($"[connect] connection still alive after mid-tunnel RST_STREAM -> :status {followUp}  " +
                          (followUp == "200" ? "✓ PASS" : "✗ FAIL"));
        return;
    }

    case "multiplex":
    {
        // Open a WebSocket tunnel and LEAVE IT OPEN (no close), then run
        // ordinary requests on the same connection concurrently — proves the
        // long-lived tunnel handler (running on its own background task,
        // same as StartRequestHandler) doesn't block the frame read loop
        // from servicing other streams.
        await Send(RawHeaders(1,
            [(":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"), (":path", "/ws-echo"), (":authority", "localhost:8443")],
            endStream: false));
        await StatusFor(1).Task.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine("[connect] WebSocket tunnel open on stream 1 (left open)");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Deliberately NOT /large: this raw test client never sends
        // WINDOW_UPDATE, so a response bigger than the 64 KiB default
        // connection window would correctly stall forever waiting for space
        // that never comes — a test-client limitation, not something to
        // exercise here (h2test's HttpClient-based multiplexing test already
        // covers /large + real flow control).
        var s3 = await OpenAndWait(3, [(":method", "GET"), (":scheme", "https"), (":authority", "localhost:8443"), (":path", "/")]);
        var s5 = await OpenAndWait(5, [(":method", "GET"), (":scheme", "https"), (":authority", "localhost:8443"), (":path", "/echo")]);

        Console.WriteLine($"[connect] stream 3 (/) -> {s3} at {sw.ElapsedMilliseconds} ms  " + (s3 == "200" ? "✓" : "✗"));
        Console.WriteLine($"[connect] stream 5 (/echo) -> {s5} at {sw.ElapsedMilliseconds} ms  " + (s5 == "200" ? "✓" : "✗"));
        Console.WriteLine((s3 == "200" && s5 == "200" && sw.ElapsedMilliseconds < 2000)
            ? "[connect] ✓ PASS: other streams completed quickly with the tunnel still open"
            : "[connect] ✗ FAIL");

        // Confirm the tunnel is still alive/responsive after all that.
        var pending = new Queue<byte>();
        await Send(RawData(1, BuildClientWsFrame(0x1, Encoding.UTF8.GetBytes("still here")), endStream: false));
        var (op, fin, payload) = await ReadServerWsFrame(1, pending);
        Console.WriteLine($"[connect] tunnel still responsive: \"{Encoding.UTF8.GetString(payload)}\"  " +
                          (op == 0x1 && fin && Encoding.UTF8.GetString(payload) == "still here" ? "✓ PASS" : "✗ FAIL"));
        return;
    }

    case "malformed":
    {
        var testCase = args.Length > 1 ? args[1] : "scheme-on-connect";

        (string Name, string Value)[] headers = testCase switch
        {
            "scheme-on-connect"      => [(":method", "CONNECT"), (":scheme", "https"), (":authority", "echo.test")],
            "path-on-connect"        => [(":method", "CONNECT"), (":path", "/"), (":authority", "echo.test")],
            "missing-authority"      => [(":method", "CONNECT")],
            "missing-scheme-extended"=> [(":method", "CONNECT"), (":protocol", "websocket"), (":path", "/ws-echo"), (":authority", "localhost:8443")],
            "missing-path-extended"  => [(":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"), (":authority", "localhost:8443")],
            "protocol-on-get"        => [(":method", "GET"), (":protocol", "websocket"), (":scheme", "https"), (":path", "/"), (":authority", "localhost:8443")],
            _ => throw new ArgumentException($"unknown malformed case '{testCase}'")
        };

        await Send(RawHeaders(1, headers, endStream: true));

        var rst = await RstFor(1).Task.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"[connect] '{testCase}' -> RST_STREAM {rst}  " + (rst == HTTP2ErrorCode.PROTOCOL_ERROR ? "✓" : "✗ unexpected code"));

        var followUp = await OpenAndWait(3, [(":method", "GET"), (":scheme", "https"), (":authority", "localhost:8443"), (":path", "/")]);
        Console.WriteLine($"[connect] connection still alive -> :status {followUp}  " + (followUp == "200" ? "✓ PASS" : "✗ FAIL"));
        return;
    }

    default:
        Console.WriteLine($"[connect] unknown mode '{mode}'");
        return;
}
