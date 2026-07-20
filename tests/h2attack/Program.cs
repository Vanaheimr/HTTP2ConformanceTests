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

// Raw HTTP/2 flood-attack client. Verifies that the server tears the connection
// down with GOAWAY ENHANCE_YOUR_CALM for each of:
//   contcount  - a flood of empty CONTINUATION frames (no END_HEADERS)
//   contbytes  - a header block that overruns MAX_HEADER_LIST_SIZE via CONTINUATION
//   ping       - a PING flood
//   settings   - a SETTINGS flood
//   legit      - a normal request still succeeds (regression)
//   malformed <case> - a single malformed request must get RST_STREAM/PROTOCOL_ERROR
//                       (stream error, NOT a connection-wide GOAWAY), and the
//                       connection must stay usable for a following request.
//     cases: missingpath, missingmethod, missingscheme, uppercase, connection,
//            te-bad, te-ok, pseudo-after-regular, unknown-pseudo, duplicate-pseudo,
//            empty-path
//   trailers <case> - HEADERS(no ES) + DATA(no ES) + a second HEADERS block.
//     cases: ok (valid trailers -> 200), pseudo (pseudo-header in trailers ->
//            RST_STREAM/PROTOCOL_ERROR), no-endstream (trailers without
//            END_STREAM -> RST_STREAM/PROTOCOL_ERROR)
//   idle <case> - RFC 9113 Section 5.1.1 implicit closure of idle streams.
//     cases: even-stream (client opens an even stream ID -> connection-wide
//              GOAWAY/PROTOCOL_ERROR), idle-data (DATA on a stream ID never
//              touched and higher than any opened -> GOAWAY/PROTOCOL_ERROR),
//            implicit-close-data (DATA on a stream ID skipped over by a later
//              HEADERS -> RST_STREAM/STREAM_CLOSED, connection stays alive),
//            idle-windowupdate / implicit-close-windowupdate (same distinction
//              for WINDOW_UPDATE; the implicitly-closed case is silently
//              ignored per RFC 9113 Section 6.9)
//   rapidreset - CVE-2023-44487: open+RST_STREAM a stream repeatedly, never
//     completing one -> GOAWAY/ENHANCE_YOUR_CALM once the reset ratio trips.
//   streamid-exhaustion - jump a stream ID to the margin below 2^31-1 ->
//     still accepted; the NEXT new stream past the margin -> proactive
//     GOAWAY/NO_ERROR + RST_STREAM/REFUSED_STREAM, connection stays alive.
//   outbound-headerlimit - advertise a tiny MAX_HEADER_LIST_SIZE in our own
//     SETTINGS; the demo's normal "/" response headers exceed it (its 500
//     fallback doesn't) -> :status 500 instead of 200.

var mode = args.Length > 0 ? args[0] : "contcount";

using var tcp = new TcpClient();
await tcp.ConnectAsync("127.0.0.1", 8443);

var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
    TargetHost           = "localhost",
    ApplicationProtocols = [SslApplicationProtocol.Http2]
});

// Preface + client SETTINGS. Most modes send empty (default) SETTINGS; the
// outbound-headerlimit mode advertises a tiny MAX_HEADER_LIST_SIZE to test
// the server's outbound enforcement of our advertised limit.
await ssl.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"));

var initialSettings = mode == "outbound-headerlimit"
    ? HTTP2Frame.CreateSettings((HTTP2SettingsParameter.MAX_HEADER_LIST_SIZE, 150))
    : HTTP2Frame.CreateSettings();

await ssl.WriteAsync(initialSettings.Serialize());
await ssl.FlushAsync();

// Background reader: drains frames, reports the first GOAWAY / RST_STREAM,
// and resolves per-stream :status codes as HEADERS come back.
var goaway     = new TaskCompletionSource<HTTP2ErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
var legitStatus = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var rstStreams = new Dictionary<uint, TaskCompletionSource<HTTP2ErrorCode>>();
var statusByStream = new Dictionary<uint, TaskCompletionSource<string>>();

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

_ = Task.Run(async () =>
{
    var hdr = new byte[9];
    var decoder = new HPACKDecoder();
    try
    {
        while (true)
        {
            await ReadExact(hdr, 9);
            var f = HTTP2Frame.ParseHeader(hdr);
            if (f.Length > 0) { f.Payload = new byte[f.Length]; await ReadExact(f.Payload, (int) f.Length); }

            if (f.Type == HTTP2FrameType.GOAWAY)
            {
                var code = (HTTP2ErrorCode) BinaryPrimitives.ReadUInt32BigEndian(f.Payload.AsSpan(4, 4));
                goaway.TrySetResult(code);
            }
            else if (f.Type == HTTP2FrameType.RST_STREAM)
            {
                var code = (HTTP2ErrorCode) BinaryPrimitives.ReadUInt32BigEndian(f.Payload.AsSpan(0, 4));
                RstFor(f.StreamId).TrySetResult(code);
            }
            else if (f.Type == HTTP2FrameType.HEADERS)
            {
                var hs = decoder.DecodeHeaderBlock(f.Payload);
                var st = hs.FirstOrDefault(h => h.Name == ":status").Value ?? "?";
                legitStatus.TrySetResult(st);
                StatusFor(f.StreamId).TrySetResult(st);
            }
        }
    }
    catch { goaway.TrySetException(new Exception("connection closed without GOAWAY")); }
});

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

var encoder = new HPACKEncoder();
byte[] Req(string path) => encoder.EncodeHeaderBlock(
[
    (":method", "GET"), (":scheme", "https"), (":authority", "localhost:8443"), (":path", path)
]);

HTTP2Frame RawHeaders(uint sid, byte[] block, bool endHeaders) => new() {
    Type = HTTP2FrameType.HEADERS, StreamId = sid,
    Flags = endHeaders ? HTTP2FrameFlags.END_HEADERS : HTTP2FrameFlags.NONE, Payload = block
};
HTTP2Frame RawCont(uint sid, byte[] block, bool endHeaders) => new() {
    Type = HTTP2FrameType.CONTINUATION, StreamId = sid,
    Flags = endHeaders ? HTTP2FrameFlags.END_HEADERS : HTTP2FrameFlags.NONE, Payload = block
};
HTTP2Frame RawData(uint sid, byte[] data, bool endStream) => new() {
    Type = HTTP2FrameType.DATA, StreamId = sid,
    Flags = endStream ? HTTP2FrameFlags.END_STREAM : HTTP2FrameFlags.NONE, Payload = data
};

async Task<string> OpenAndWait(uint sid, string path)
{
    await Send(new HTTP2Frame {
        Type = HTTP2FrameType.HEADERS, StreamId = sid,
        Flags = HTTP2FrameFlags.END_HEADERS | HTTP2FrameFlags.END_STREAM, Payload = Req(path)
    });
    return await StatusFor(sid).Task.WaitAsync(TimeSpan.FromSeconds(5));
}

Console.WriteLine($"[attack] mode = {mode}");

switch (mode)
{
    case "contcount":
        // HEADERS (no END_HEADERS) + 200 empty CONTINUATION frames.
        await Send(RawHeaders(1, Req("/"), endHeaders: false));
        for (var i = 0; i < 200; i++)
            await Send(RawCont(1, [], endHeaders: false));
        break;

    case "contbytes":
        // HEADERS (no END_HEADERS) + CONTINUATION frames with 1 KiB payload each,
        // overrunning MAX_HEADER_LIST_SIZE (8192) after ~9 frames.
        await Send(RawHeaders(1, new byte[1024], endHeaders: false));
        for (var i = 0; i < 40; i++)
            await Send(RawCont(1, new byte[1024], endHeaders: false));
        break;

    case "ping":
        for (var i = 0; i < 1200; i++)
            await Send(new HTTP2Frame { Type = HTTP2FrameType.PING, StreamId = 0, Payload = new byte[8] });
        break;

    case "settings":
        for (var i = 0; i < 1200; i++)
            await Send(HTTP2Frame.CreateSettings((HTTP2SettingsParameter.HEADER_TABLE_SIZE, 4096)));
        break;

    case "legit":
        await Send(new HTTP2Frame {
            Type = HTTP2FrameType.HEADERS, StreamId = 1,
            Flags = HTTP2FrameFlags.END_HEADERS | HTTP2FrameFlags.END_STREAM, Payload = Req("/")
        });
        var status = await legitStatus.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"[attack] legit request -> :status {status}");
        return;

    case "malformed":
    {
        var testCase = args.Length > 1 ? args[1] : "missingpath";

        (string Name, string Value)[] headers = testCase switch
        {
            "missingpath"         => [(":method", "GET"), (":scheme", "https"), (":authority", "localhost:8443")],
            "missingmethod"       => [(":scheme", "https"), (":authority", "localhost:8443"), (":path", "/")],
            "missingscheme"       => [(":method", "GET"), (":authority", "localhost:8443"), (":path", "/")],
            "empty-path"          => [(":method", "GET"), (":scheme", "https"), (":authority", "localhost:8443"), (":path", "")],
            "uppercase"           => [(":method", "GET"), (":scheme", "https"), (":authority", "localhost:8443"), (":path", "/"), ("X-Custom", "v")],
            "connection"          => [(":method", "GET"), (":scheme", "https"), (":authority", "localhost:8443"), (":path", "/"), ("connection", "keep-alive")],
            "te-bad"              => [(":method", "GET"), (":scheme", "https"), (":authority", "localhost:8443"), (":path", "/"), ("te", "gzip")],
            "te-ok"               => [(":method", "GET"), (":scheme", "https"), (":authority", "localhost:8443"), (":path", "/"), ("te", "trailers")],
            "pseudo-after-regular"=> [(":method", "GET"), ("x-foo", "bar"), (":scheme", "https"), (":authority", "localhost:8443"), (":path", "/")],
            "unknown-pseudo"      => [(":method", "GET"), (":scheme", "https"), (":authority", "localhost:8443"), (":path", "/"), (":status", "200")],
            "duplicate-pseudo"    => [(":method", "GET"), (":method", "POST"), (":scheme", "https"), (":authority", "localhost:8443"), (":path", "/")],
            _                     => throw new ArgumentException($"unknown malformed case '{testCase}'")
        };

        var block = encoder.EncodeHeaderBlock(headers);

        await Send(new HTTP2Frame {
            Type = HTTP2FrameType.HEADERS, StreamId = 1,
            Flags = HTTP2FrameFlags.END_HEADERS | HTTP2FrameFlags.END_STREAM,
            Payload = block
        });

        if (testCase == "te-ok")
        {
            var okStatus = await StatusFor(1).Task.WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine($"[attack] '{testCase}' (valid TE) -> :status {okStatus}  {(okStatus == "200" ? "✓ PASS" : "✗ FAIL")}");
            return;
        }

        try
        {
            var code = await RstFor(1).Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pass = code == HTTP2ErrorCode.PROTOCOL_ERROR;
            Console.WriteLine($"[attack] '{testCase}' -> RST_STREAM {code}  {(pass ? "✓" : "✗ unexpected code")}");
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"[attack] '{testCase}' -> ✗ FAIL: no RST_STREAM within 5 s");
            return;
        }

        // Connection must still be usable — malformed request is a STREAM error, not connection-wide.
        await Send(new HTTP2Frame {
            Type = HTTP2FrameType.HEADERS, StreamId = 3,
            Flags = HTTP2FrameFlags.END_HEADERS | HTTP2FrameFlags.END_STREAM, Payload = Req("/")
        });

        try
        {
            var followUpStatus = await StatusFor(3).Task.WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine($"[attack] connection still alive -> :status {followUpStatus}  " +
                              (followUpStatus == "200" ? "✓ PASS" : "✗ FAIL"));
        }
        catch (TimeoutException)
        {
            Console.WriteLine("[attack] ✗ FAIL: connection died after malformed request (should be stream-scoped)");
        }

        return;
    }

    case "trailers":
    {
        var testCase = args.Length > 1 ? args[1] : "ok";

        // HEADERS (no END_STREAM) + DATA (no END_STREAM) — a request with a body,
        // trailers expected next.
        await Send(new HTTP2Frame {
            Type = HTTP2FrameType.HEADERS, StreamId = 1,
            Flags = HTTP2FrameFlags.END_HEADERS, Payload = Req("/echo")
        });
        await Send(RawData(1, Encoding.ASCII.GetBytes("Hello"), endStream: false));

        (string Name, string Value)[] trailerFields = testCase switch
        {
            "ok"           => [("x-checksum", "abc123")],
            "pseudo"       => [(":status", "200")],
            "no-endstream" => [("x-checksum", "abc123")],
            _              => throw new ArgumentException($"unknown trailers case '{testCase}'")
        };

        var trailerBlock = encoder.EncodeHeaderBlock(trailerFields);
        var setEndStream = testCase != "no-endstream";

        await Send(new HTTP2Frame {
            Type = HTTP2FrameType.HEADERS, StreamId = 1,
            Flags = HTTP2FrameFlags.END_HEADERS | (setEndStream ? HTTP2FrameFlags.END_STREAM : HTTP2FrameFlags.NONE),
            Payload = trailerBlock
        });

        if (testCase == "ok")
        {
            var st = await StatusFor(1).Task.WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine($"[attack] trailers '{testCase}' -> :status {st}  {(st == "200" ? "✓ PASS" : "✗ FAIL")}");
            return;
        }

        try
        {
            var code = await RstFor(1).Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pass = code == HTTP2ErrorCode.PROTOCOL_ERROR;
            Console.WriteLine($"[attack] trailers '{testCase}' -> RST_STREAM {code}  {(pass ? "✓" : "✗ unexpected code")}");
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"[attack] trailers '{testCase}' -> ✗ FAIL: no RST_STREAM within 5 s");
            return;
        }

        var followUp = await OpenAndWait(3, "/");
        Console.WriteLine($"[attack] connection still alive -> :status {followUp}  " +
                          (followUp == "200" ? "✓ PASS" : "✗ FAIL"));
        return;
    }

    case "idle":
    {
        var testCase = args.Length > 1 ? args[1] : "even-stream";

        switch (testCase)
        {

            case "even-stream":
            {
                await Send(new HTTP2Frame {
                    Type = HTTP2FrameType.HEADERS, StreamId = 2,
                    Flags = HTTP2FrameFlags.END_HEADERS | HTTP2FrameFlags.END_STREAM, Payload = Req("/")
                });
                break;
            }

            case "idle-data":
            {
                await OpenAndWait(1, "/");
                await Send(RawData(9, Encoding.ASCII.GetBytes("x"), endStream: false));
                break;
            }

            case "idle-windowupdate":
            {
                await OpenAndWait(1, "/");
                await Send(HTTP2Frame.CreateWindowUpdate(9, 100));
                break;
            }

            case "implicit-close-data":
            {
                await OpenAndWait(1, "/");
                await OpenAndWait(7, "/");   // skips 3 and 5 -> implicitly closed
                await Send(RawData(3, Encoding.ASCII.GetBytes("x"), endStream: false));

                var code = await RstFor(3).Task.WaitAsync(TimeSpan.FromSeconds(5));
                var pass = code == HTTP2ErrorCode.STREAM_CLOSED;
                Console.WriteLine($"[attack] idle '{testCase}' -> RST_STREAM {code}  {(pass ? "✓" : "✗ unexpected code")}");

                var followUp = await OpenAndWait(9, "/");
                Console.WriteLine($"[attack] connection still alive -> :status {followUp}  " +
                                  (followUp == "200" ? "✓ PASS" : "✗ FAIL"));
                return;
            }

            case "implicit-close-windowupdate":
            {
                await OpenAndWait(1, "/");
                await OpenAndWait(7, "/");   // skips 3 and 5 -> implicitly closed
                await Send(HTTP2Frame.CreateWindowUpdate(3, 100));

                // Ignored per RFC 9113 §6.9 — no RST_STREAM, no GOAWAY. Prove the
                // connection is still alive with a follow-up request.
                var followUp = await OpenAndWait(9, "/");
                Console.WriteLine($"[attack] idle '{testCase}': WINDOW_UPDATE silently ignored, " +
                                  $"connection alive -> :status {followUp}  " +
                                  (followUp == "200" ? "✓ PASS" : "✗ FAIL"));
                return;
            }

            default:
                throw new ArgumentException($"unknown idle case '{testCase}'");

        }

        break;
    }

    case "rst-cancel":
    {
        // Start a /slow request (server-side 2 s delay), then RST_STREAM it
        // shortly after. The server log's elapsed-time line proves whether the
        // handler actually stopped early (per-stream CancellationToken working)
        // or ran the full 2 s regardless (the bug being fixed).
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Send(new HTTP2Frame {
            Type = HTTP2FrameType.HEADERS, StreamId = 1,
            Flags = HTTP2FrameFlags.END_HEADERS | HTTP2FrameFlags.END_STREAM, Payload = Req("/slow")
        });

        await Task.Delay(300);
        Console.WriteLine($"[attack] sending RST_STREAM at {sw.ElapsedMilliseconds} ms");
        await Send(HTTP2Frame.CreateRstStream(1, HTTP2ErrorCode.CANCEL));

        // No HEADERS/status should ever arrive for stream 1 now.
        var raced = await Task.WhenAny(StatusFor(1).Task, Task.Delay(1500));
        if (raced == StatusFor(1).Task)
            Console.WriteLine("[attack] ✗ FAIL: server sent a response for a stream we RST_STREAM'd");
        else
            Console.WriteLine("[attack] ✓ no response sent for the RST_STREAM'd stream");

        // Connection must still be usable — RST_STREAM is stream-scoped.
        var followUp = await OpenAndWait(3, "/");
        Console.WriteLine($"[attack] connection still alive -> :status {followUp}  " +
                          (followUp == "200" ? "✓ PASS" : "✗ FAIL"));

        return;
    }

    case "rapidreset":
        // CVE-2023-44487: open a stream, then RST_STREAM it before it's ever
        // completed (no END_STREAM on the HEADERS, so nothing is dispatched).
        // Falls through to the generic GOAWAY/ENHANCE_YOUR_CALM wait below.
        for (var i = 0; i < 30; i++)
        {
            var sid = (uint) (2 * i + 1);
            await Send(RawHeaders(sid, Req("/"), endHeaders: true));
            await Send(HTTP2Frame.CreateRstStream(sid, HTTP2ErrorCode.CANCEL));
        }
        break;

    case "streamid-exhaustion":
    {
        // Stream IDs only need to be monotonically increasing, not
        // consecutive — jump straight to the exhaustion margin instead of
        // actually opening ~2^31 streams.
        const uint nearMax = 0x7FFFFFFF - 1000; // exactly at the margin, odd

        var s1 = await OpenAndWait(1, "/");
        Console.WriteLine($"[attack] baseline stream 1 -> :status {s1}");

        var s2 = await OpenAndWait(nearMax, "/");
        Console.WriteLine($"[attack] stream {nearMax} (at the margin) -> :status {s2}  " +
                          (s2 == "200" ? "✓ still accepted" : "✗ unexpectedly refused"));

        // Any FURTHER new stream must now be refused (stream-level).
        await Send(new HTTP2Frame {
            Type = HTTP2FrameType.HEADERS, StreamId = nearMax + 2,
            Flags = HTTP2FrameFlags.END_HEADERS | HTTP2FrameFlags.END_STREAM, Payload = Req("/")
        });

        var rst1 = await RstFor(nearMax + 2).Task.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"[attack] stream {nearMax + 2} (past the margin) -> RST_STREAM {rst1}  " +
                          (rst1 == HTTP2ErrorCode.REFUSED_STREAM ? "✓" : "✗ unexpected code"));

        var goAwayCode = await goaway.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"[attack] proactive GOAWAY received: {goAwayCode}  " +
                          (goAwayCode == HTTP2ErrorCode.NO_ERROR ? "✓" : "✗ unexpected code"));

        // Connection should still be usable for a further (also-refused, but
        // not connection-killing) attempt — proves this isn't a full teardown.
        await Send(new HTTP2Frame {
            Type = HTTP2FrameType.HEADERS, StreamId = nearMax + 4,
            Flags = HTTP2FrameFlags.END_HEADERS | HTTP2FrameFlags.END_STREAM, Payload = Req("/")
        });
        var rst2 = await RstFor(nearMax + 4).Task.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"[attack] connection still alive for a further attempt -> RST_STREAM {rst2}  " +
                          (rst2 == HTTP2ErrorCode.REFUSED_STREAM ? "✓ PASS" : "✗ FAIL"));

        return;
    }

    case "outbound-headerlimit":
    {
        // We advertised MAX_HEADER_LIST_SIZE=150 above. The demo's normal "/"
        // response headers total ~214 uncompressed bytes (over the limit);
        // its 500-fallback error headers total ~96 bytes (comfortably under
        // it) — so we expect a 500, not a 200, and not a dropped connection.
        var headerLimitStatus = await OpenAndWait(1, "/");
        Console.WriteLine($"[attack] oversized response headers -> :status {headerLimitStatus}  " +
                          (headerLimitStatus == "500" ? "✓ PASS (fell back to a response that fits)" : "✗ FAIL"));
        return;
    }
}

var expectedGoAway = mode == "idle" ? HTTP2ErrorCode.PROTOCOL_ERROR : HTTP2ErrorCode.ENHANCE_YOUR_CALM;

try
{
    var code = await goaway.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine($"[attack] server closed with GOAWAY {code}" +
                      (code == expectedGoAway ? "  ✓ PASS" : "  ✗ unexpected code"));
}
catch (TimeoutException)
{
    Console.WriteLine("[attack] ✗ FAIL: no GOAWAY within 5 s (flood not mitigated)");
}
catch (Exception ex)
{
    Console.WriteLine($"[attack] ✗ FAIL: {ex.Message}");
}

async Task Send(HTTP2Frame f)
{
    try { await ssl.WriteAsync(f.Serialize()); await ssl.FlushAsync(); }
    catch { /* server may have closed mid-flood — expected */ }
}
