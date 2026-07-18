namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

using System.Threading.Channels;


/// <summary>
/// RFC 9218 (Extensible Prioritization Scheme for HTTP), Section 4: the
/// urgency/incremental pair carried by a "priority" header field or a
/// PRIORITY_UPDATE frame. Urgency ranges 0 (most urgent) to 7 (least),
/// default 3; incremental defaults to false ("send as a single unit").
/// Read by <see cref="HTTP2Connection"/>'s writer loop to decide which
/// stream's queued bytes go on the wire next when several are ready at once.
/// </summary>
public readonly record struct HTTP2Priority(byte Urgency, bool Incremental)
{
    public const byte DefaultUrgency = 3;

    public static readonly HTTP2Priority Default = new(DefaultUrgency, false);

    /// <summary>
    /// Serialize as an RFC 9218 Priority Field Value — the RFC 8941 Structured
    /// Fields Dictionary <c>u=&lt;urgency&gt;</c> plus a bare <c>i</c> when
    /// incremental (a bare key is shorthand for the Boolean true). Used both
    /// for the <c>priority</c> request header and the PRIORITY_UPDATE payload
    /// a client sends; the mirror of <see cref="HTTP2Connection"/>'s parser.
    /// </summary>
    public string ToHeaderValue()
        => Incremental ? $"u={Urgency}, i" : $"u={Urgency}";
}


/// <summary>
/// One item of body/tunnel bytes queued on a stream's <see cref="HTTP2OutboundQueue"/>,
/// waiting for the connection's single writer loop to send it as DATA frame(s).
/// </summary>
internal sealed class HTTP2OutboundItem
{
    public required byte[]               Data        { get; init; }
    public required bool                 EndStream   { get; init; }
    public          int                  Offset;
    public required TaskCompletionSource Completion  { get; init; }

    /// <summary>
    /// Trailer fields to send as a trailing HEADERS block (with END_STREAM)
    /// once this item's DATA has gone out — set only on the final,
    /// end-of-stream item of a response that carries trailers (RFC 9113,
    /// Section 8.1). Null for an ordinary item.
    /// </summary>
    public List<(string Name, string Value)>? Trailers { get; init; }
}


/// <summary>
/// Per-stream FIFO of outbound body bytes, drained by the connection's single
/// priority-aware writer loop (<see cref="HTTP2Connection"/>'s writer loop)
/// rather than written directly by whichever response/tunnel task produced
/// them. That indirection is what makes RFC 9218 prioritization possible:
/// only the writer loop decides whose bytes actually go on the wire next,
/// instead of producers racing each other for the connection's shared send
/// window first-come-first-served.
///
/// Single-consumer (the writer loop) / multi-producer (response and tunnel
/// tasks) — only <see cref="TakeChunk"/> and <see cref="AbandonAll"/> ever
/// remove items, and both only ever run on the writer loop's own thread of
/// execution, so they never race each other.
///
/// Public only because it's exposed as the type of <see cref="HTTP2Stream.OutboundQueue"/>;
/// there's no reason for anything outside this assembly to construct or call
/// it directly.
/// </summary>
public sealed class HTTP2OutboundQueue
{

    private readonly object                   gate  = new();
    private readonly Queue<HTTP2OutboundItem>  items = new();

    /// <summary>True if at least one item is queued (possibly just a zero-length end-of-stream marker).</summary>
    public bool HasPending
    {
        get { lock (gate) return items.Count > 0; }
    }

    /// <summary>
    /// True if the head item still has actual payload bytes left to send.
    /// False for an item that's only carrying a pending End-Stream marker —
    /// an empty final DATA frame needs no flow-control window, so the writer
    /// loop's picker must not treat such a stream as blocked just because its
    /// send window happens to be exhausted.
    /// </summary>
    public bool HeadNeedsWindow
    {
        get
        {
            lock (gate)
            {
                if (items.Count == 0)
                    return false;

                var head = items.Peek();
                return head.Data.Length - head.Offset > 0;
            }
        }
    }

    /// <summary>
    /// Queue Data (and, if EndStream, a pending End-Stream marker) for the
    /// writer loop to send. Returns a task that completes once this exact
    /// item has been fully handed to the wire, or abandoned (see
    /// <see cref="AbandonAll"/>) — callers that want write-completion
    /// backpressure (e.g. a slow tunnel peer) should await it.
    /// </summary>
    public Task EnqueueAsync(byte[] Data, bool EndStream, List<(string Name, string Value)>? Trailers = null)
    {

        var item = new HTTP2OutboundItem {
            Data       = Data,
            EndStream  = EndStream,
            Trailers   = Trailers,
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        lock (gate)
            items.Enqueue(item);

        return item.Completion.Task;

    }

    /// <summary>
    /// Called only by the writer loop. Takes up to MaxBytes from the head
    /// item; if that exhausts the item, it is dequeued and its Completion is
    /// signalled. Returns null if nothing is queued.
    /// </summary>
    public (byte[] Chunk, bool EndStream, List<(string Name, string Value)>? Trailers)? TakeChunk(int MaxBytes)
    {

        lock (gate)
        {

            if (items.Count == 0)
                return null;

            var item      = items.Peek();
            var remaining = item.Data.Length - item.Offset;
            var take      = Math.Max(0, Math.Min(remaining, MaxBytes));

            var chunk = take > 0
                            ? item.Data.AsSpan(item.Offset, take).ToArray()
                            : [];

            item.Offset += take;

            if (item.Offset < item.Data.Length)
                return (chunk, false, null);

            items.Dequeue();
            item.Completion.TrySetResult();

            return (chunk, item.EndStream, item.Trailers);

        }

    }

    /// <summary>
    /// Drop everything still queued and unblock any producer awaiting
    /// <see cref="EnqueueAsync"/> — called when the stream is reset, so a
    /// producer isn't left waiting forever for bytes that will now never be
    /// sent.
    /// </summary>
    public void AbandonAll()
    {
        lock (gate)
        {
            while (items.Count > 0)
                items.Dequeue().Completion.TrySetResult();
        }
    }

}


/// <summary>
/// Represents a single HTTP/2 stream with its state machine (RFC 9113, Section 5.1)
/// and per-stream flow control window.
/// </summary>
public sealed class HTTP2Stream
{

    public UInt32             StreamId        { get; }
    public HTTP2StreamState   State           { get; private set; } = HTTP2StreamState.Idle;

    /// <summary>
    /// Outbound flow control window (how many bytes WE can still send to the peer).
    /// Initialized to the peer's INITIAL_WINDOW_SIZE setting.
    /// </summary>
    public Int64              SendWindow      { get; set; }

    /// <summary>
    /// Inbound flow control window (how many bytes the peer can still send to us).
    /// Initialized to our INITIAL_WINDOW_SIZE setting.
    /// </summary>
    public Int64              RecvWindow      { get; set; }

    /// <summary>
    /// Bytes consumed on this stream since the last stream-level WINDOW_UPDATE we
    /// sent — accumulated so we can replenish in batches (once it crosses a
    /// fraction of the window) instead of one WINDOW_UPDATE per DATA frame.
    /// </summary>
    public Int64              PendingRecvUpdate { get; set; }

    /// <summary>
    /// Tracks whether END_STREAM was set on the HEADERS frame
    /// while waiting for CONTINUATION frames to complete the header block.
    /// </summary>
    public bool               EndStreamPending  { get; set; }

    /// <summary>
    /// Accumulates HEADERS/CONTINUATION fragments until END_HEADERS is received.
    /// </summary>
    public MemoryStream?      HeaderBuffer    { get; set; }

    /// <summary>
    /// The decoded request headers once the full header block is received.
    /// </summary>
    public List<(string Name, string Value)>?  RequestHeaders  { get; set; }

    /// <summary>
    /// The decoded trailer fields, if the request sent a second HEADERS block
    /// after DATA (RFC 9113, Section 8.1). Null unless trailers were sent.
    /// Set once <see cref="RequestHeaders"/> is already populated — that's how
    /// a second header block on the same stream is recognized as trailers.
    /// </summary>
    public List<(string Name, string Value)>?  Trailers        { get; set; }

    /// <summary>
    /// Accumulates DATA frame payloads for the request body.
    /// </summary>
    public MemoryStream?      RequestBody     { get; set; }

    /// <summary>
    /// The value of the request's <c>content-length</c> header field, if it
    /// declared one. Used to enforce RFC 9113, Section 8.1.2.6: the declared
    /// length MUST equal the sum of the DATA frame payload lengths, else the
    /// request is malformed. Null when no (valid) content-length was sent.
    /// </summary>
    public long?              ExpectedContentLength { get; set; }

    /// <summary>
    /// True once this stream has been recognized as an accepted CONNECT
    /// tunnel (RFC 9113, Section 8.5; RFC 8441 extended CONNECT). Once set,
    /// DATA frames are routed to <see cref="TunnelInbound"/> instead of
    /// buffering into <see cref="RequestBody"/>, and the stream is dispatched
    /// to the connect handler rather than the ordinary request handler — a
    /// CONNECT tunnel has no "complete body, single response" request/response
    /// cycle, just a bidirectional byte stream for as long as it's open.
    /// </summary>
    public bool                IsConnectTunnel { get; set; }

    /// <summary>
    /// Inbound side of an accepted CONNECT tunnel: DATA frame payloads the
    /// peer sends are written here by the frame read loop as they arrive
    /// (HandleDataAsync) and read by the application's tunnel handler
    /// (HTTP2Tunnel.ReadAsync). The channel is unbounded, but it is bounded in
    /// practice by flow control: the receive window for these bytes is returned
    /// only as the consumer reads them (consumption-driven backpressure — see
    /// HandleDataAsync / ReplenishConsumedAsync), so the peer can never have
    /// more than a window's worth in flight, and a slow consumer simply leaves
    /// the peer's window depleted rather than growing this queue without bound.
    /// </summary>
    public Channel<byte[]>?    TunnelInbound   { get; set; }

    /// <summary>
    /// True once this stream is being handled by a streaming request handler
    /// (<see cref="HTTP2StreamingHandler"/>). Like a CONNECT tunnel, its DATA
    /// frames are routed to <see cref="RequestBodyChannel"/> as they arrive
    /// (rather than buffered into <see cref="RequestBody"/>), and the handler is
    /// dispatched at HEADERS-complete rather than at END_STREAM — but unlike a
    /// tunnel it keeps ordinary request/response + trailer semantics.
    /// </summary>
    public bool                IsStreamingRequest { get; set; }

    /// <summary>
    /// Inbound request-body chunks for a streaming request, written by the frame
    /// read loop as DATA arrives and read by the handler via
    /// <see cref="IHTTP2RequestStream.ReadAsync"/>. Completed at END_STREAM.
    /// </summary>
    public Channel<byte[]>?    RequestBodyChannel { get; set; }

    /// <summary>
    /// Running total of DATA payload bytes received on this stream — used to
    /// validate <see cref="ExpectedContentLength"/> (RFC 9113, Section 8.1.2.6)
    /// on the streaming path, where there is no buffered <see cref="RequestBody"/>
    /// whose length could be checked instead.
    /// </summary>
    public long                ReceivedBodyLength { get; set; }

    /// <summary>
    /// RFC 9218 priority (urgency + incremental). Set from the request's
    /// "priority" header field if present (default otherwise), and
    /// updatable afterwards via a PRIORITY_UPDATE frame or a "priority"
    /// response header — read by the connection's writer loop to decide send
    /// order among concurrent streams.
    /// </summary>
    public HTTP2Priority       Priority        { get; set; } = HTTP2Priority.Default;

    /// <summary>
    /// Queued response/tunnel body bytes not yet handed to the wire — see
    /// <see cref="HTTP2OutboundQueue"/> and the connection's writer loop.
    /// </summary>
    public HTTP2OutboundQueue  OutboundQueue   { get; } = new();

    /// <summary>
    /// Set by the writer loop each time this stream is chosen to send;
    /// breaks priority ties round-robin-fairly (least-recently-served goes
    /// first) instead of always favoring whichever stream happens to be
    /// enumerated first.
    /// </summary>
    public long                LastServedSequence  { get; set; } = -1;

    /// <summary>
    /// Cancelled when the stream is forcibly closed (<see cref="Reset"/>), so a
    /// running <c>HTTP2RequestHandler</c> invocation for this stream can be
    /// told to stop instead of running to completion for a peer that already
    /// walked away. Never disposed — its lifetime is tied to this stream
    /// object, it holds no timer/unmanaged resources, and disposing it would
    /// risk an ObjectDisposedException if a handler read the token concurrently.
    /// </summary>
    private readonly CancellationTokenSource  requestCancellation  = new();

    /// <summary>Signaled when this stream is reset — see <see cref="requestCancellation"/>.</summary>
    public CancellationToken  CancellationToken  => requestCancellation.Token;


    public HTTP2Stream(UInt32 StreamId, Int64 InitialSendWindow, Int64 InitialRecvWindow)
    {
        this.StreamId   = StreamId;
        this.SendWindow = InitialSendWindow;
        this.RecvWindow = InitialRecvWindow;
    }


    #region State transitions (RFC 9113, Section 5.1)

    /// <summary>
    /// Makes state transitions atomic — the connection's read loop and the
    /// per-stream response task may transition concurrently.
    /// </summary>
    private readonly object stateLock = new();

    /// <summary>
    /// Transition to Open state (receiving HEADERS on an idle stream).
    /// </summary>
    public void Open()
    {

        lock (stateLock)
        {

            if (State != HTTP2StreamState.Idle)
                throw new HTTP2StreamException(HTTP2ErrorCode.PROTOCOL_ERROR, StreamId,
                                               $"Cannot open stream {StreamId} in state {State}");

            State = HTTP2StreamState.Open;

        }

    }

    /// <summary>
    /// Transition when the remote peer sends END_STREAM.
    /// </summary>
    public void CloseRemote()
    {

        lock (stateLock)
        {
            State = State switch {
                HTTP2StreamState.Open            => HTTP2StreamState.HalfClosedRemote,
                HTTP2StreamState.HalfClosedLocal => HTTP2StreamState.Closed,
                _                                => throw new HTTP2StreamException(
                                                        HTTP2ErrorCode.STREAM_CLOSED, StreamId,
                                                        $"Cannot close remote on stream {StreamId} in state {State}")
            };
        }

    }

    /// <summary>
    /// Transition when we send END_STREAM.
    /// </summary>
    public void CloseLocal()
    {

        lock (stateLock)
        {
            State = State switch {
                HTTP2StreamState.Open             => HTTP2StreamState.HalfClosedLocal,
                HTTP2StreamState.HalfClosedRemote => HTTP2StreamState.Closed,
                _                                 => throw new HTTP2StreamException(
                                                         HTTP2ErrorCode.STREAM_CLOSED, StreamId,
                                                         $"Cannot close local on stream {StreamId} in state {State}")
            };
        }

    }

    /// <summary>
    /// True once this stream was closed by an RST_STREAM (sent or received),
    /// as opposed to a clean END_STREAM close. RFC 9113, Section 5.1 treats a
    /// later frame differently in the two cases: after RST_STREAM it's a stream
    /// error, after END_STREAM it's a connection error.
    /// </summary>
    public bool WasReset { get; private set; }

    /// <summary>
    /// Forcibly close (RST_STREAM received or sent).
    /// </summary>
    public void Reset()
    {
        lock (stateLock)
        {
            State    = HTTP2StreamState.Closed;
            WasReset = true;
        }

        // Outside the lock: Cancel() runs registered callbacks synchronously,
        // and a handler's callback re-entering this stream while stateLock is
        // held would deadlock. Safe to call repeatedly (e.g. RST_STREAM sent
        // by us and then also received from the peer) — Cancel() is idempotent.
        requestCancellation.Cancel();

        // Unblock a tunnel handler possibly waiting in HTTP2Tunnel.ReadAsync —
        // without this, a reset mid-tunnel would leave it awaiting forever
        // (the cancellation token covers *sending*, not this channel read).
        TunnelInbound?.Writer.TryComplete();

        // Unblock a producer possibly awaiting HTTP2OutboundQueue.EnqueueAsync
        // for bytes that will now never be sent — the writer loop's picker
        // skips Closed streams, so without this the data would sit queued
        // forever and the producer would never come back.
        OutboundQueue.AbandonAll();
    }

    #endregion

}


/// <summary>
/// Manages all streams for a single HTTP/2 connection.
/// Handles stream creation, lookup, and connection-level flow control.
/// </summary>
public sealed class HTTP2StreamManager
{

    private readonly Dictionary<UInt32, HTTP2Stream> streams = [];

    /// <summary>
    /// Guards <see cref="streams"/> itself (adds/removes/enumeration) —
    /// previously unnecessary, since only the connection's frame read loop
    /// ever touched the dictionary. That invariant no longer holds once the
    /// priority-aware writer loop (a separate, concurrently-running task)
    /// needs to enumerate live streams via <see cref="GetSendableStreams"/>
    /// on every send decision. Per-stream state (State/SendWindow/etc.) is
    /// still guarded separately (stateLock / the connection's flowLock) —
    /// this lock is only about the dictionary's own shape.
    /// </summary>
    private readonly object dictLock = new();

    /// <summary>
    /// Whether this endpoint is the server or the client (RFC 9113, Section
    /// 5.1.1). Defaults to Server so every existing server call site
    /// (`new HTTP2StreamManager()`) keeps its exact behavior; the Track E
    /// client passes Client.
    /// </summary>
    public HTTP2Role Role { get; }

    /// <summary>
    /// The low bit of stream IDs the *peer* initiates: a server's peer (the
    /// client) uses odd IDs (1); a client's peer (the server) would use even
    /// ones (0, server push — which this stack disables, so in practice a
    /// client sees none). Streams with this parity are validated/tracked as
    /// peer-initiated in <see cref="GetOrCreateStream"/> and <see cref="IsIdle"/>.
    /// </summary>
    private uint PeerInitiatedParity => Role == HTTP2Role.Server ? 1u : 0u;

    /// <summary>The low bit of stream IDs *we* initiate — the mirror of <see cref="PeerInitiatedParity"/>.</summary>
    private uint LocalInitiatedParity => Role == HTTP2Role.Server ? 0u : 1u;

    public HTTP2StreamManager(HTTP2Role Role = HTTP2Role.Server)
    {
        this.Role = Role;
    }

    /// <summary>
    /// The highest peer-initiated stream ID seen so far (for a server, the
    /// highest odd stream the client opened).
    /// </summary>
    public UInt32  LastPeerStreamId  { get; private set; }

    /// <summary>
    /// The highest stream ID *we* have locally initiated (client role: the last
    /// odd request stream we allocated via <see cref="CreateLocalStream"/>).
    /// Stays 0 for a server, which never initiates streams here (no push).
    /// </summary>
    public UInt32  LastLocalStreamId { get; private set; }

    /// <summary>
    /// Connection-level send flow control window (how many DATA bytes we can send).
    /// </summary>
    public Int64   ConnectionSendWindow  { get; set; } = 65535;

    /// <summary>
    /// Connection-level receive flow control window (how many DATA bytes the peer can send).
    /// </summary>
    public Int64   ConnectionRecvWindow  { get; set; } = 65535;

    /// <summary>
    /// The peer's INITIAL_WINDOW_SIZE setting (applied to new streams).
    /// </summary>
    public Int64   PeerInitialWindowSize  { get; set; } = 65535;

    /// <summary>
    /// Our INITIAL_WINDOW_SIZE setting (applied to new streams).
    /// </summary>
    public Int64   LocalInitialWindowSize { get; set; } = 65535;

    /// <summary>
    /// Maximum number of concurrent streams allowed (from our SETTINGS).
    /// </summary>
    public UInt32  MaxConcurrentStreams { get; set; } = 100;

    /// <summary>
    /// The highest valid client-initiated stream ID: stream identifiers are a
    /// 31-bit field (RFC 9113, Section 4.1 — the top bit is reserved and
    /// always masked off during parsing), so this is the last one a client
    /// can ever legally send on a given connection.
    /// </summary>
    public const UInt32  MaxStreamId  = 0x7FFFFFFF;

    /// <summary>
    /// How far below MaxStreamId to start warning (RFC 9113, Section 5.1.1)
    /// instead of running the connection all the way to the hard boundary.
    /// </summary>
    private const UInt32 StreamIdExhaustionMargin = 1000;

    /// <summary>
    /// True once LastPeerStreamId is close enough to MaxStreamId that new
    /// streams should be refused and the peer told to migrate to a fresh
    /// connection, rather than let it run to the point where the next stream
    /// ID would fail the "must be greater than the last one" check as an
    /// abrupt connection-level error.
    /// </summary>
    public bool IsNearStreamIdExhaustion
        => LastPeerStreamId >= MaxStreamId - StreamIdExhaustionMargin;


    /// <summary>
    /// Get or create a stream the *peer* initiated (a server's client-opened
    /// odd stream). Validates proper ordering and concurrency limits for
    /// peer-parity IDs. A client never receives peer-initiated streams here
    /// (push is disabled), so for the client role the parity branch is simply
    /// never taken.
    /// </summary>
    public HTTP2Stream GetOrCreateStream(UInt32 StreamId)
    {

        lock (dictLock)
        {

            if (streams.TryGetValue(StreamId, out var existing))
                return existing;

            // Peer-initiated streams must carry the peer's parity and be
            // monotonically increasing.
            if (StreamId % 2 == PeerInitiatedParity)
            {
                if (StreamId <= LastPeerStreamId)
                    throw new HTTP2ConnectionException(HTTP2ErrorCode.PROTOCOL_ERROR,
                        $"Stream ID {StreamId} is not greater than last peer stream ID {LastPeerStreamId}");

                var openCount = 0;
                foreach (var s in streams.Values)
                {
                    if (s.State is HTTP2StreamState.Open
                                or HTTP2StreamState.HalfClosedLocal
                                or HTTP2StreamState.HalfClosedRemote)
                        openCount++;
                }

                if (openCount >= MaxConcurrentStreams)
                    throw new HTTP2StreamException(HTTP2ErrorCode.REFUSED_STREAM, StreamId,
                        $"Maximum concurrent streams ({MaxConcurrentStreams}) exceeded");

                LastPeerStreamId = StreamId;
            }

            var stream = new HTTP2Stream(StreamId, PeerInitialWindowSize, LocalInitialWindowSize);
            streams[StreamId] = stream;

            return stream;

        }

    }

    /// <summary>
    /// Allocate and register the next locally-initiated stream (client role:
    /// the next odd request stream). Stream IDs are assigned sequentially in
    /// increments of 2 starting from the local parity, monotonically for the
    /// life of the connection (RFC 9113, Section 5.1.1). Enforces
    /// <see cref="MaxConcurrentStreams"/> (for a client, that field carries the
    /// peer server's advertised limit) and refuses once the 31-bit ID space is
    /// exhausted.
    /// </summary>
    public HTTP2Stream CreateLocalStream()
    {

        lock (dictLock)
        {

            var next = LastLocalStreamId == 0
                           ? (LocalInitiatedParity == 1 ? 1u : 2u)
                           : LastLocalStreamId + 2;

            if (next > MaxStreamId)
                throw new HTTP2ConnectionException(HTTP2ErrorCode.PROTOCOL_ERROR,
                    "Local stream IDs exhausted; open a new connection");

            var openCount = 0;
            foreach (var s in streams.Values)
            {
                if (s.State is HTTP2StreamState.Open
                            or HTTP2StreamState.HalfClosedLocal
                            or HTTP2StreamState.HalfClosedRemote)
                    openCount++;
            }

            if (openCount >= MaxConcurrentStreams)
                throw new HTTP2StreamException(HTTP2ErrorCode.REFUSED_STREAM, next,
                    $"Maximum concurrent streams ({MaxConcurrentStreams}) exceeded");

            var stream = new HTTP2Stream(next, PeerInitialWindowSize, LocalInitialWindowSize);
            streams[next]     = stream;
            LastLocalStreamId = next;

            return stream;

        }

    }

    /// <summary>
    /// Try to get an existing stream (returns null if not found).
    /// </summary>
    public HTTP2Stream? TryGetStream(UInt32 StreamId)
    {
        lock (dictLock)
            return streams.TryGetValue(StreamId, out var stream) ? stream : null;
    }

    /// <summary>
    /// Snapshot of every stream that hasn't reached the Closed state, for the
    /// connection's priority-aware writer loop to scan on each send decision.
    /// A snapshot (rather than exposing live enumeration) keeps dictLock's
    /// critical section short and lets the writer loop's priority comparison
    /// run outside of it entirely.
    /// </summary>
    public List<HTTP2Stream> GetSendableStreams()
    {
        lock (dictLock)
            return [.. streams.Values.Where(static s => s.State != HTTP2StreamState.Closed)];
    }

    /// <summary>
    /// True if StreamId has never been touched and is not implicitly closed by
    /// a later stream (RFC 9113, Section 5.1.1) — i.e. it is genuinely "idle".
    /// Only HEADERS and PRIORITY are legal on such a stream; any other frame
    /// referencing it is a connection error, unlike a stream that is merely
    /// closed (where most frame types are tolerated as stragglers).
    /// </summary>
    public bool IsIdle(UInt32 StreamId)
    {

        lock (dictLock)
        {

            if (streams.ContainsKey(StreamId))
                return false;

            // Peer-initiated streams below the highest one the peer ever opened
            // were implicitly closed by that later HEADERS, not idle.
            if (StreamId % 2 == PeerInitiatedParity)
                return StreamId > LastPeerStreamId;

            // Locally-initiated parity: idle until we've allocated up to it. For
            // a server (local parity = even, push disabled) LastLocalStreamId
            // stays 0, so every even ID is idle for the whole connection —
            // identical to the previous hardcoded behavior.
            return StreamId > LastLocalStreamId;

        }

    }

    /// <summary>
    /// Remove closed streams to avoid unbounded memory growth.
    /// Call periodically or after processing.
    /// </summary>
    public void PruneClosedStreams()
    {

        lock (dictLock)
        {

            var toRemove = new List<UInt32>();

            foreach (var (id, stream) in streams)
            {
                if (stream.State == HTTP2StreamState.Closed)
                    toRemove.Add(id);
            }

            foreach (var id in toRemove)
                streams.Remove(id);

        }

    }

    /// <summary>
    /// When the peer changes INITIAL_WINDOW_SIZE via SETTINGS, we must adjust
    /// the send window of all open/half-closed streams by the delta.
    /// </summary>
    public void AdjustAllStreamWindows(Int64 Delta)
    {

        lock (dictLock)
        {

            foreach (var stream in streams.Values)
            {
                if (stream.State is HTTP2StreamState.Open
                                 or HTTP2StreamState.HalfClosedLocal
                                 or HTTP2StreamState.HalfClosedRemote)
                {
                    stream.SendWindow += Delta;

                    if (stream.SendWindow > Int32.MaxValue)
                        throw new HTTP2ConnectionException(HTTP2ErrorCode.FLOW_CONTROL_ERROR,
                            $"Flow control window overflow on stream {stream.StreamId}");
                }
            }

        }

    }

}
