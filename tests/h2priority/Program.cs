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

// Raw HTTP/2 client exercising RFC 9218 (Extensible Prioritization Scheme for
// HTTP) support:
//   settings           - confirm SETTINGS_NO_RFC7540_PRIORITIES=1 is advertised
//   urgency-header      - two concurrent /large requests, one "priority: u=0"
//                         (most urgent) and one "priority: u=7" (least), opened
//                         back-to-back with no WINDOW_UPDATE ever sent by the
//                         client -> the connection-window-limited initial burst
//                         of DATA frames must, once both streams are
//                         contending, exclusively favor the u=0 stream.
//   priority-update     - two concurrent /large requests at default priority
//                         (u=3), burst until window-blocked, then a
//                         PRIORITY_UPDATE promotes one to u=0 and window is
//                         topped up -> from that point on, the promoted
//                         stream must finish (reach END_STREAM) before the
//                         other one receives any further bytes.
//   priority-update-unknown-stream - PRIORITY_UPDATE for a stream ID that was
//                         never opened -> ignored (RFC 9218 Section 7.1),
//                         connection stays alive for a follow-up request.
//   malformed-priority  - "priority: u=99, i=?1" (urgency out of the 0-7
//                         range) -> the request still succeeds (falls back to
//                         default urgency instead of a protocol error).

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

var serverSettings   = new TaskCompletionSource<HTTP2Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
var goaway           = new TaskCompletionSource<HTTP2ErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
var rstStreams       = new Dictionary<uint, TaskCompletionSource<HTTP2ErrorCode>>();
var statusByStream   = new Dictionary<uint, TaskCompletionSource<string>>();
var endStreamByStream = new Dictionary<uint, TaskCompletionSource<bool>>();

// Ordered log of every DATA frame received: (StreamId, Length, EndStream).
// This ordering is exactly what demonstrates (or refutes) priority-aware
// scheduling — first-come-first-served would interleave both streams roughly
// evenly regardless of priority; the writer loop under test should not.
var dataLog = new List<(uint StreamId, int Length, bool EndStream)>();

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

TaskCompletionSource<bool> EndStreamFor(uint sid)
{
    lock (endStreamByStream)
    {
        if (!endStreamByStream.TryGetValue(sid, out var tcs))
            endStreamByStream[sid] = tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return tcs;
    }
}

_ = Task.Run(async () =>
{
    var hdr     = new byte[9];
    var decoder = new HPACKDecoder();

    try
    {
        while (true)
        {
            await ReadExact(hdr, 9);
            var f = HTTP2Frame.ParseHeader(hdr);
            if (f.Length > 0) { f.Payload = new byte[f.Length]; await ReadExact(f.Payload, (int) f.Length); }

            switch (f.Type)
            {

                case HTTP2FrameType.SETTINGS when f.StreamId == 0 && !f.IsAck:
                    serverSettings.TrySetResult(f);
                    break;

                case HTTP2FrameType.GOAWAY:
                    goaway.TrySetResult((HTTP2ErrorCode) BinaryPrimitives.ReadUInt32BigEndian(f.Payload.AsSpan(4, 4)));
                    break;

                case HTTP2FrameType.RST_STREAM:
                    RstFor(f.StreamId).TrySetResult((HTTP2ErrorCode) BinaryPrimitives.ReadUInt32BigEndian(f.Payload.AsSpan(0, 4)));
                    break;

                case HTTP2FrameType.HEADERS:
                {
                    var hs = decoder.DecodeHeaderBlock(f.Payload);
                    var st = hs.FirstOrDefault(h => h.Name == ":status").Value ?? "?";
                    StatusFor(f.StreamId).TrySetResult(st);
                    break;
                }

                case HTTP2FrameType.DATA:
                {
                    lock (dataLog)
                        dataLog.Add((f.StreamId, (int) f.Length, f.EndStream));

                    if (f.EndStream)
                        EndStreamFor(f.StreamId).TrySetResult(true);

                    break;
                }

            }
        }
    }
    catch { goaway.TrySetException(new Exception("connection closed")); }
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

async Task Send(HTTP2Frame frame) => await ssl.WriteAsync(frame.Serialize());

var encoder = new HPACKEncoder();

byte[] Req(string path, string? priority = null)
{
    var headers = new List<(string Name, string Value)> {
        (":method", "GET"), (":scheme", "https"), (":authority", "localhost:8443"), (":path", path)
    };

    if (priority is not null)
        headers.Add(("priority", priority));

    return encoder.EncodeHeaderBlock(headers);
}

async Task OpenStream(uint sid, string path, string? priority = null)
{
    await Send(new HTTP2Frame {
        Type = HTTP2FrameType.HEADERS, StreamId = sid,
        Flags = HTTP2FrameFlags.END_HEADERS | HTTP2FrameFlags.END_STREAM,
        Payload = Req(path, priority)
    });
}

async Task<string> OpenAndWait(uint sid, string path)
{
    await OpenStream(sid, path);
    return await StatusFor(sid).Task.WaitAsync(TimeSpan.FromSeconds(5));
}

HTTP2Frame PriorityUpdate(uint prioritizedStreamId, string priorityFieldValue)
{
    var valueBytes = Encoding.ASCII.GetBytes(priorityFieldValue);
    var payload    = new byte[4 + valueBytes.Length];
    BinaryPrimitives.WriteUInt32BigEndian(payload, prioritizedStreamId & 0x7FFFFFFFu);
    valueBytes.CopyTo(payload, 4);

    return new HTTP2Frame {
        Type = HTTP2FrameType.PRIORITY_UPDATE, StreamId = 0, Payload = payload
    };
}

int BytesFor(uint sid)
{
    lock (dataLog)
        return dataLog.Where(d => d.StreamId == sid).Sum(d => d.Length);
}

Console.WriteLine($"[priority] mode = {mode}");

switch (mode)
{

    case "settings":
    {
        var settings = await serverSettings.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var noRfc7540Priorities = false;
        var value = 0u;

        for (var i = 0; i < settings.Payload.Length; i += 6)
        {
            var id = (HTTP2SettingsParameter) BinaryPrimitives.ReadUInt16BigEndian(settings.Payload.AsSpan(i, 2));
            if (id == HTTP2SettingsParameter.NO_RFC7540_PRIORITIES)
            {
                noRfc7540Priorities = true;
                value = BinaryPrimitives.ReadUInt32BigEndian(settings.Payload.AsSpan(i + 2, 4));
            }
        }

        Console.WriteLine($"[priority] NO_RFC7540_PRIORITIES present={noRfc7540Priorities} value={value}  " +
                           (noRfc7540Priorities && value == 1 ? "✓ PASS" : "✗ FAIL"));
        return;
    }

    case "urgency-header":
    {
        // Both requests opened back-to-back, with no client WINDOW_UPDATE
        // ever sent: the server's send budget is capped at the default
        // 65535-byte connection window (~4 frames of MAX_FRAME_SIZE), so
        // once it's exhausted the server blocks — deterministically, not
        // just "eventually quiet" — making the burst safe to inspect after
        // a short fixed wait.
        await OpenStream(1, "/large", priority: "u=7");
        await OpenStream(3, "/large", priority: "u=0");

        await Task.Delay(700);

        List<(uint StreamId, int Length, bool EndStream)> snapshot;
        lock (dataLog) snapshot = [.. dataLog];

        Console.WriteLine($"[priority] burst: {snapshot.Count} DATA frames, " +
                           $"stream 1 (u=7) = {BytesFor(1)} bytes, stream 3 (u=0) = {BytesFor(3)} bytes");

        var firstHighUrgencyIndex = snapshot.FindIndex(d => d.StreamId == 3);

        var ok = firstHighUrgencyIndex >= 0 &&
                 snapshot.Skip(firstHighUrgencyIndex).All(d => d.StreamId == 3);

        Console.WriteLine(ok
            ? "[priority] ✓ PASS: once the u=0 stream started, every remaining burst frame was its own"
            : "[priority] ✗ FAIL: a u=7 frame was interleaved after the u=0 stream started");

        return;
    }

    case "priority-update":
    {
        await OpenStream(1, "/large");   // default priority (u=3)
        await OpenStream(3, "/large");   // default priority (u=3)

        await Task.Delay(700);

        int before1, before3;
        lock (dataLog) { before1 = BytesFor(1); before3 = BytesFor(3); }

        Console.WriteLine($"[priority] pre-update burst: stream 1 = {before1} bytes, stream 3 = {before3} bytes");

        var markerIndex = dataLog.Count;

        // Promote stream 1 to the most urgent level, then top up both the
        // connection- and stream-level windows generously so both requests
        // can actually finish.
        await Send(PriorityUpdate(1, "u=0"));
        await Send(HTTP2Frame.CreateWindowUpdate(0, 1_000_000));
        await Send(HTTP2Frame.CreateWindowUpdate(1, 1_000_000));
        await Send(HTTP2Frame.CreateWindowUpdate(3, 1_000_000));

        await EndStreamFor(1).Task.WaitAsync(TimeSpan.FromSeconds(5));
        await EndStreamFor(3).Task.WaitAsync(TimeSpan.FromSeconds(5));

        List<(uint StreamId, int Length, bool EndStream)> afterMarker;
        lock (dataLog) afterMarker = [.. dataLog.Skip(markerIndex)];

        var stream1DoneIndex = afterMarker.FindIndex(d => d.StreamId == 1 && d.EndStream);
        var stream3BeforeDone = afterMarker.Take(stream1DoneIndex + 1).Any(d => d.StreamId == 3);

        Console.WriteLine($"[priority] post-update: stream 1 reached END_STREAM at index {stream1DoneIndex} of {afterMarker.Count}, " +
                           $"stream 3 sent any bytes before that = {stream3BeforeDone}");

        var ok = stream1DoneIndex >= 0 && !stream3BeforeDone;

        Console.WriteLine(ok
            ? "[priority] ✓ PASS: after PRIORITY_UPDATE promoted stream 1, it finished before stream 3 got any more bytes"
            : "[priority] ✗ FAIL: stream 3 made progress after the promotion but before stream 1 finished");

        return;
    }

    case "priority-update-unknown-stream":
    {
        // Stream 99 was never opened — RFC 9218 Section 7.1 says this is
        // just ignored, not a protocol violation.
        await Send(PriorityUpdate(99, "u=0"));

        var status = await OpenAndWait(1, "/");
        Console.WriteLine($"[priority] PRIORITY_UPDATE for an unopened stream ignored, connection alive -> :status {status}  " +
                           (status == "200" ? "✓ PASS" : "✗ FAIL"));
        return;
    }

    case "malformed-priority":
    {
        await OpenStream(1, "/", priority: "u=99, i=?1");
        var status = await StatusFor(1).Task.WaitAsync(TimeSpan.FromSeconds(5));

        Console.WriteLine($"[priority] out-of-range urgency falls back to default -> :status {status}  " +
                           (status == "200" ? "✓ PASS" : "✗ FAIL"));
        return;
    }

    default:
        Console.WriteLine($"[priority] unknown mode '{mode}'");
        return;

}
