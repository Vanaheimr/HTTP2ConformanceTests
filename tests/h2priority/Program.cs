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
//                         (most urgent) and one "priority: u=7" (least). Both
//                         are first parked window-blocked (see ProbeWindow),
//                         then released in the same instant -> the u=0 stream
//                         must run to completion before the u=7 stream gets a
//                         single byte, and must not starve it afterwards.
//   priority-update     - the same two streams, both at default priority
//                         (u=3), parked the same way; a PRIORITY_UPDATE then
//                         promotes one to u=0 before either can move -> the
//                         promoted stream must finish (reach END_STREAM)
//                         before the other one receives any further bytes.
//   priority-update-unknown-stream - PRIORITY_UPDATE for a stream ID that was
//                         never opened -> ignored (RFC 9218 Section 7.1),
//                         connection stays alive for a follow-up request.
//   malformed-priority  - "priority: u=99, i=?1" (urgency out of the 0-7
//                         range) -> the request still succeeds (falls back to
//                         default urgency instead of a protocol error).
//
// The two contention scenarios exist to observe one specific decision: which
// stream the server's writer loop picks when several are sendable at once. So
// the hard part is not the assertion, it is establishing that "at once" — a
// scheduler can only choose among the streams that are actually candidates,
// and a stream whose response has not been queued yet is not one. Both
// scenarios used to just open the two streams and inspect the resulting burst
// after a fixed delay, which assumed that race resolved in their favor; on
// Linux it usually did not, and they failed 19 times in 20 while the server
// was behaving perfectly. See ProbeWindow and UnblockBothStreamsAsync for how
// that assumption was removed rather than re-tuned.

var mode = args.Length > 0 ? args[0] : "settings";

// The send window the two contention scenarios advertise up front, so the
// server may send exactly one byte per stream and is then stuck. That single
// byte is the point: receiving it proves the request handler has run, the
// response body is queued, and the stream is now flow-control blocked at 0 —
// the exact state the scenarios need, established by observation instead of
// by hoping a Task.Delay was long enough.
const UInt32 ProbeWindow   = 1;

// What both scenarios raise the windows to afterwards. Comfortably above the
// 128 KiB /large body, so a released stream can run all the way to END_STREAM.
const UInt32 ReleaseWindow = 1_000_000;

var contentionScenario = mode is "urgency-header" or "priority-update";

using var tcp = new TcpClient();
await tcp.ConnectAsync("127.0.0.1", 8443);

var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
    TargetHost           = "localhost",
    ApplicationProtocols = [SslApplicationProtocol.Http2]
});

await ssl.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"));

// The tiny initial window goes out in the connection preface's own SETTINGS,
// before any stream exists — so it is in force for every stream this
// connection will ever open, with no window in which a response could slip
// out at full speed. Only the contention scenarios ask for it; the others
// want an ordinary connection.
await ssl.WriteAsync(
    (contentionScenario
         ? HTTP2Frame.CreateSettings((HTTP2SettingsParameter.INITIAL_WINDOW_SIZE, ProbeWindow))
         : HTTP2Frame.CreateSettings()
    ).Serialize());

await ssl.FlushAsync();

var serverSettings   = new TaskCompletionSource<HTTP2Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
var goaway           = new TaskCompletionSource<HTTP2ErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
var rstStreams       = new Dictionary<uint, TaskCompletionSource<HTTP2ErrorCode>>();
var statusByStream   = new Dictionary<uint, TaskCompletionSource<string>>();
var endStreamByStream = new Dictionary<uint, TaskCompletionSource<bool>>();
var firstDataByStream = new Dictionary<uint, TaskCompletionSource<int>>();

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

/// The first DATA frame seen on a stream, which under ProbeWindow is the one
/// byte the server is allowed to send before it blocks — awaiting it is how
/// the contention scenarios wait for a *state* rather than for a duration.
TaskCompletionSource<int> FirstDataFor(uint sid)
{
    lock (firstDataByStream)
    {
        if (!firstDataByStream.TryGetValue(sid, out var tcs))
            firstDataByStream[sid] = tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
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

                    FirstDataFor(f.StreamId).TrySetResult((int) f.Length);

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

/// Open both /large streams and wait until each has spent its ProbeWindow
/// byte, i.e. until both are queued, contending and blocked. Returns the
/// index into dataLog just past the two probe frames — everything after it
/// is a scheduling decision made with both streams on the table.
async Task<int> ParkBothStreamsAsync(string? priority1, string? priority3)
{

    await OpenStream(1, "/large", priority1);
    await OpenStream(3, "/large", priority3);

    // Deliberately no ordering assumption between the two: whichever handler
    // finishes first sends its one byte first, and both must have sent one
    // before the scenario continues.
    var probe1 = await FirstDataFor(1).Task.WaitAsync(TimeSpan.FromSeconds(10));
    var probe3 = await FirstDataFor(3).Task.WaitAsync(TimeSpan.FromSeconds(10));

    Console.WriteLine($"[priority] parked: stream 1 sent {probe1} byte(s), stream 3 sent {probe3} byte(s) — both now window-blocked");

    lock (dataLog)
        return dataLog.Count;

}

/// Release both streams in a single event. This is a SETTINGS frame rather
/// than one WINDOW_UPDATE per stream on purpose: RFC 9113 Section 6.9.2 makes
/// a changed INITIAL_WINDOW_SIZE adjust *every* open stream's send window by
/// the same delta, which the server applies under one lock before waking its
/// writer once — so both streams become sendable in the same instant and the
/// writer's next pick is a real choice.
///
/// Per-stream WINDOW_UPDATEs cannot express that. They are separate frames
/// processed one after another, so the first one named gets a head start, and
/// a stream still blocked in that gap is skipped entirely legitimately. That
/// is not a hypothetical: priority-update used to top the connection window
/// up before the promoted stream's own, which left exactly one moment in
/// which the promoted stream was the only one that could not send — and the
/// server, correctly, served the other one. One run in twenty on Linux.
///
/// The connection-level WINDOW_UPDATE goes first and on its own releases
/// nothing, since every stream window is still sitting at 0.
async Task UnblockBothStreamsAsync()
{
    await Send(HTTP2Frame.CreateWindowUpdate(0, ReleaseWindow));
    await Send(HTTP2Frame.CreateSettings((HTTP2SettingsParameter.INITIAL_WINDOW_SIZE, ReleaseWindow)));
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

        // Stream 1 is the least urgent of the two and is opened first, so
        // first-come-first-served and RFC 9218 ordering disagree about who
        // should be served — which is what makes the observation meaningful.
        var marker = await ParkBothStreamsAsync(priority1: "u=7", priority3: "u=0");

        await UnblockBothStreamsAsync();

        // The u=0 stream running to completion is the claim; waiting for its
        // END_STREAM is therefore the natural end of the observation window,
        // and it needs no timeout guess about how long the transfer takes.
        await EndStreamFor(3).Task.WaitAsync(TimeSpan.FromSeconds(30));

        List<(uint StreamId, int Length, bool EndStream)> contended;
        lock (dataLog) contended = [.. dataLog.Skip(marker)];

        var stream3DoneIndex = contended.FindIndex(d => d.StreamId == 3 && d.EndStream);
        var stream1Bytes     = contended.Take(stream3DoneIndex + 1).Sum(d => d.StreamId == 1 ? d.Length : 0);

        Console.WriteLine($"[priority] contended: {contended.Count} DATA frames, stream 3 (u=0) reached END_STREAM at index {stream3DoneIndex}, " +
                           $"stream 1 (u=7) got {stream1Bytes} bytes before that");

        // Deferring the u=7 stream is the point; starving it is a different
        // bug, and would otherwise pass this scenario silently. It must still
        // finish once the urgent one is out of the way.
        await EndStreamFor(1).Task.WaitAsync(TimeSpan.FromSeconds(30));

        var ok = stream3DoneIndex >= 0 && stream1Bytes == 0;

        Console.WriteLine(ok
            ? "[priority] ✓ PASS: with both streams contending, the u=0 stream ran to completion before the u=7 stream got a single byte — which then finished too"
            : "[priority] ✗ FAIL: the u=7 stream was served while the u=0 stream still had both data and window");

        return;
    }

    case "priority-update":
    {

        // Both at default priority (u=3), so nothing distinguishes them until
        // the PRIORITY_UPDATE below does.
        var marker = await ParkBothStreamsAsync(priority1: null, priority3: null);

        // Sent while both streams are still parked, so the promotion is in
        // force before either can move again. PRIORITY_UPDATE travels on
        // stream 0 ahead of the releasing frames and the server's frame loop
        // reads them in order, so this needs no timing assumption either.
        await Send(PriorityUpdate(1, "u=0"));

        await UnblockBothStreamsAsync();

        await EndStreamFor(1).Task.WaitAsync(TimeSpan.FromSeconds(30));

        List<(uint StreamId, int Length, bool EndStream)> afterMarker;
        lock (dataLog) afterMarker = [.. dataLog.Skip(marker)];

        var stream1DoneIndex = afterMarker.FindIndex(d => d.StreamId == 1 && d.EndStream);
        var stream3Bytes     = afterMarker.Take(stream1DoneIndex + 1).Sum(d => d.StreamId == 3 ? d.Length : 0);

        Console.WriteLine($"[priority] post-update: stream 1 reached END_STREAM at index {stream1DoneIndex} of {afterMarker.Count}, " +
                           $"stream 3 got {stream3Bytes} bytes before that");

        await EndStreamFor(3).Task.WaitAsync(TimeSpan.FromSeconds(30));

        var ok = stream1DoneIndex >= 0 && stream3Bytes == 0;

        Console.WriteLine(ok
            ? "[priority] ✓ PASS: after PRIORITY_UPDATE promoted stream 1, it finished before stream 3 got any more bytes — which then finished too"
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
