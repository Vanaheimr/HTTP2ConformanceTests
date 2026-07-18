# HTTP/2 From Scratch — C# / .NET 10

A from-scratch HTTP/2 stack built directly on `SslStream` — no `Kestrel`, no
`HttpListener`, no `System.Net.Http` HTTP/2 stack. It's **three parts** (Track
E, done): a **shared protocol library** (`Core` — the direction-neutral code:
framing, HPACK, the stream layer, WebSocket framing, HTTP semantics), an
**HTTP/2 server**, and an **HTTP/2 client**, each its own project under
`src/`. The focus is the **binary framing layer** and everything below the HTTP
application semantics: frame parsing/serialization, HPACK, the stream state
machine, flow control, and the TLS+ALPN handshake. HTTP/1.1 is assumed to
already exist elsewhere; the higher-level request semantics are deliberately
thin here.

This is a learning/reference implementation in the spirit of the Vanaheimr
Hermod protocol stacks (SMTP, IMAP, HTTP/2, DHCP, NTS-KE, TCP, ... hand-rolled
in C#).

## Build & Run

The solution (`src/HTTP2.slnx`) has four projects — `Core` (shared library),
`Server`, `Client`, and `Demo` (the runnable host):

```bash
cd src
dotnet build HTTP2.slnx
dotnet run --project Demo/HTTP2.Demo.csproj
# then, from another shell:
curl --http2 -k https://localhost:8443/
curl --http2 -k https://localhost:8443/echo -d "Hello HTTP/2!"
curl --http2 -k https://localhost:8443/large   # 128 KiB — exercises flow control
curl --http2 -k https://localhost:8443/slow    # 2 s handler — exercises multiplexing
# cleartext h2c (prior knowledge — no TLS), on :8080:
curl --http2-prior-knowledge http://localhost:8080/
```

Note: the stock Windows curl (Schannel build) has no HTTP/2 support and silently
falls back to HTTP/1.1 — use a curl with nghttp2, or .NET's `HttpClient`.

Target framework is `net10.0`. Uses a self-signed cert generated at startup.

## Architecture

Four projects under `src/` (solution `HTTP2.slnx`): a shared **`Core`**
library, a **`Server`**, a **`Client`**, and a runnable **`Demo`**. Dependency
direction is `Core ← Server`, `Core ← Client`, and `Server, Client ← Demo` —
Core never references the role-specific projects.

**`Core/`** — the shared, direction-neutral library:

| File | Responsibility |
|---|---|
| `HTTP2Frame.cs` | 9-byte frame header parse/serialize, frame type/flag/error-code/role enums, frame factories, exceptions |
| `HPACK.cs` | RFC 7541: static + dynamic table, integer/string coding, Huffman decode **and encode**, full-featured encoder (static + per-connection dynamic table + Huffman) |
| `HTTP2Stream.cs` | Per-stream state machine (RFC 9113 §5.1), per-stream flow-control windows, RFC 9218 priority + outbound DATA queue, role-parameterized `HTTP2StreamManager` |
| `HTTP2Settings.cs` | The connection-settings bag (advertised vs. peer), used by both roles |
| `HTTP2RequestHandler.cs` | The app-logic request-handler delegate (produced by `HTTPSemantics`, consumed by the server) |
| `HTTP2Streaming.cs` | The streaming seam: `HTTP2StreamingHandler` delegate + `IHTTP2RequestStream`/`IHTTP2ResponseStream` (incremental body + trailers + 1xx interim responses, for gRPC-style bidi and 103 Early Hints) |
| `IHTTP2Tunnel.cs` | Transport-agnostic byte-tunnel interface, so `WebSocket.cs` doesn't depend on the server's concrete tunnel |
| `WebSocket.cs` | RFC 6455 WebSocket framing (masking, opcodes, fragmentation, close handshake) over an `IHTTP2Tunnel`, direction-aware via `WebSocketRole` (server vs. client masking) |
| `HTTPSemantics.cs` | RFC 9110 semantics: GET/HEAD/OPTIONS, conditional requests, Range requests, proactive content negotiation (Accept*/Vary), opt-in on-the-fly content coding (gzip/br/deflate) — version-independent, never touches frames/streams/HPACK |
| `HTTPAuthentication.cs` | RFC 9110 §11 authentication framework (401/WWW-Authenticate/Authorization) + Basic (RFC 7617) & Bearer (RFC 6750) schemes, store-agnostic (app-supplied validators) |
| `HTTPCaching.cs` | RFC 9111 caching *logic*: Cache-Control parsing, age/freshness computation, storability, revalidation, Vary keying — store-agnostic, direction-neutral |

**`Server/`** — references `Core`:

| File | Responsibility |
|---|---|
| `HTTP2Connection.cs` | Connection preface, SETTINGS handshake, the frame dispatch loop, request assembly, CONNECT tunneling (`HTTP2Tunnel` implements `IHTTP2Tunnel`), the priority-aware DATA writer loop (RFC 9218), streaming dispatch + response trailers, Slowloris/idle timeouts |
| `HTTP2StreamAdapters.cs` | `HTTP2RequestStream`/`HTTP2ResponseStream` — server-side impls of the Core streaming seam over one `HTTP2Stream` |
| `HTTP2Server.cs` | `TcpListener` + `SslStream` with ALPN `h2` negotiation, TLS-handshake timeout, HTTP/1.1 fallback stub; optional `Cleartext` mode (h2c prior-knowledge, no TLS) |

**`Client/`** — references `Core`:

| File | Responsibility |
|---|---|
| `HTTP2ClientConnection.cs` | Client-role connection: sends the preface, allocates odd request streams, sends requests + assembles `HTTP2Response`s |
| `HTTP2Client.cs` | Dialer: TCP connect + TLS/ALPN `h2` handshake (or optional `Cleartext` h2c prior-knowledge), the client-side counterpart of `HTTP2Server` |
| `HTTP2CachingClient.cs` | RFC 9111 cache (store + origin wiring) in front of a client connection — serves fresh hits, revalidates stale entries, keys by `Vary` |

**`Demo/`** — references `Server` + `Client`:

| File | Responsibility |
|---|---|
| `Program.cs` | Demo host (TLS `h2` on :8443 + cleartext `h2c` on :8080) + self-signed cert + example request/connect/resource handlers (the app-logic plug-in point) |

All four projects share the `org.GraphDefined.Vanaheimr.Hermod.HTTP2` namespace
(the Vanaheimr/Hermod convention).

The integration seam for real application logic is the `HTTP2RequestHandler`
delegate (in `Core`): it receives decoded headers + body and returns response
headers + body. That is where an existing HTTP/1.1 handler would plug in. The
parallel seam for tunnels (CONNECT, extended CONNECT) is `HTTP2ConnectHandler`
(in `Server`): it decides accept/reject, and — if accepted — runs against an
`HTTP2Tunnel` (a raw bidirectional byte stream over the accepted CONNECT
stream). The client's seam is `HTTP2ClientConnection.SendRequestAsync`.

The `Core`/`Server`/`Client` split (Track E, done 2026-07-18) makes the
direction-neutral vs. role-specific boundary physical rather than conventional:
`Core` holds everything usable by both roles (frames, HPACK, the
role-parameterized stream layer, settings, the request-handler seam, RFC 9110
semantics, WebSocket framing behind `IHTTP2Tunnel`); `Server` and `Client` are
the two mirror connection roles built on top; neither can accidentally depend on
the other, and Core can't depend on either.

## Current State (verified 2026-07-18 with `dotnet build` + live HTTP/2 clients)

**Working:** frame header parse/serialize, reserved-bit masking, SETTINGS
handshake (server preface → client SETTINGS → ACK), HPACK decode incl. dynamic
table, stream state machine, flow control, **decoupled read/write loops with
real multiplexing** (former TODO items 1+2, done 2026-07-16):

- Request handlers + response sending run on their own tasks
  (`HTTP2Connection.StartRequestHandler`); the frame read loop never blocks
  on application logic.
- The DATA send path reserves window space via `ReserveSendWindowAsync` —
  signal-based (a `TaskCompletionSource` pulsed by the WINDOW_UPDATE /
  SETTINGS / RST_STREAM handlers), no polling. Window accounting is guarded
  by `flowLock` since concurrent response tasks share the connection window.
- HEADERS+CONTINUATION sequences are written atomically under the write lock
  (RFC 9113 §4.3: header blocks must be contiguous).
- Peer RST_STREAM wakes a waiting sender, which then abandons the response.

**Flood hardening** (former TODO item 1, done 2026-07-16) — all abort the
connection with `GOAWAY ENHANCE_YOUR_CALM`:

- Header-block accumulation is bounded against the advertised
  `MAX_HEADER_LIST_SIZE` (checked after every HEADERS/CONTINUATION fragment in
  `EnforceHeaderBufferLimit`) — the CONTINUATION-flood / CVE-2024-27316 class.
- The number of CONTINUATION frames per header block is capped
  (`MaxContinuationFrames = 64`), which also stops the *empty*-CONTINUATION
  variant that never grows the buffer.
- PING/SETTINGS floods are counted (`CountUnproductiveFrame`,
  `MaxUnproductiveFrames = 1000`); the counter resets on any HEADERS/DATA, so
  only control frames with no request progress accumulate.
- `SendGoAwayAsync` now drains inbound data briefly (bounded 250 ms / 256 KiB,
  `DrainForCloseAsync`) so the peer actually receives the GOAWAY instead of a
  TCP RST swallowing it when a flood's tail is left unread (RFC 9113 §6.8).

**Request validation** (RFC 9113 §8, done 2026-07-17) —
enforced in `ValidateRequestHeaders`, called right after HPACK-decoding a
completed header block (the HPACK dynamic table is connection-wide state, so
decoding always runs to completion first — even for a request that turns out
malformed — or the peer's encoder and our decoder desync for every later
header block on the connection). Malformed requests are a **stream** error
(`RST_STREAM`/`PROTOCOL_ERROR`), not a connection error — one bad request
doesn't take down other streams:

- Pseudo-headers (`:method`, `:scheme`, `:authority`, `:path`) must precede
  regular headers, must be from the allowed request set, and must not repeat
  (§8.1.1).
- `:method`, `:scheme`, `:path` are mandatory and `:path` must be non-empty.
  (Superseded 2026-07-18 by Track B: plain CONNECT is now the one case where
  `:scheme`/`:path` are *not* mandatory — see the CONNECT entry below.)
- Regular header field names must be lowercase (§8.2.1).
- Connection-specific headers (`connection`, `keep-alive`, `proxy-connection`,
  `transfer-encoding`, `upgrade`) are rejected; `te` is allowed only with the
  value `trailers` (§8.2.2).

**Trailers & implicit stream closure** (RFC 9113 §5.1.1/§8.1, done 2026-07-17):

- A second `HEADERS` block on a stream that's still expecting body/trailer
  data (state `Open`/`HalfClosedLocal`, recognized in `HandleHeaders` by the
  stream object already existing) is now treated as trailers instead of
  crashing at `stream.Open()`. Trailers must set `END_STREAM` and must not
  contain pseudo-header fields (§8.1); decoded into the new `HTTP2Stream.Trailers`
  property (kept separate from `RequestHeaders`, not yet plumbed into
  `HTTP2RequestHandler` — no consumer needs it yet). Field-level rules
  (lowercase names, no connection-specific headers, `te: trailers` only) are
  shared with request-header validation via `ValidateRegularHeaderField`.
- Clients must use odd-numbered stream IDs (§5.1.1); a HEADERS frame opening
  an even stream ID is now a connection error instead of being silently
  accepted by `GetOrCreateStream`.
- `HTTP2StreamManager.IsIdle` distinguishes a genuinely idle stream (never
  touched, ID higher than any the peer has opened) from one that's
  *implicitly closed* (ID lower than `LastPeerStreamId` but never itself
  opened, skipped over by a later HEADERS). DATA/WINDOW_UPDATE/RST_STREAM on a
  genuinely idle stream is now a connection `PROTOCOL_ERROR` (§5.1: idle
  streams only accept HEADERS/PRIORITY); the same frames on an
  implicitly-closed stream keep their existing closed-stream semantics
  (`STREAM_CLOSED` for DATA, silently ignored for WINDOW_UPDATE/RST_STREAM per
  §6.9 — unchanged from before, now just reached via the correct branch).

**Closed-stream pruning** (done 2026-07-17): `HTTP2StreamManager.PruneClosedStreams()`
existed but was never called, so the per-connection stream dictionary grew
unboundedly over a long-lived connection. Now called from `HandleHeaders`
whenever a genuinely new (non-trailers) stream is opened — the streams
dictionary is only ever touched from the frame read loop (response tasks run
in the background via `StartRequestHandler` and only mutate a stream's own
`State`/window fields under `stateLock`/`flowLock`, never the dictionary), so
calling it there needs no extra locking. Tying the sweep to "a new stream just
arrived" keeps dictionary size proportional to live + in-flight streams
instead of growing with total requests ever served, with no arbitrary
frame-count threshold.

Verified end-to-end against **both** .NET's `HttpClient` (strict) and a raw
frame-level test client: `GET /`, `POST /echo`, `/large` (128 KiB — the old
deadlock case), `/slow` + `/` + `/large` concurrently on one connection (fast
streams complete in ~15 ms while `/slow` pends), and 4× `/large` concurrently
(connection-window sharing). A raw attack client confirms all four flood
mitigations fire (empty-CONTINUATION flood, oversized header block, PING flood,
SETTINGS flood) while a legit request on a fresh connection still returns 200.
A second raw client drove 11 malformed-request cases (missing/empty/duplicate
pseudo-headers, uppercase header name, `connection`/bad-`te` headers, pseudo
header after a regular header, unknown pseudo-header) — each got
`RST_STREAM`/`PROTOCOL_ERROR` while the connection stayed usable for a
follow-up request; a `te: trailers` request (the one legal `te` value) still
returned 200. A third raw client drove trailers (valid trailers -> 200;
pseudo-header in trailers and missing `END_STREAM` -> `RST_STREAM`/
`PROTOCOL_ERROR`) and implicit-closure cases (even stream ID and a
genuinely-idle stream referenced by DATA/WINDOW_UPDATE -> connection-wide
`GOAWAY`/`PROTOCOL_ERROR`; the same frames on an implicitly-closed stream ->
`STREAM_CLOSED`/silently-ignored respectively, connection still usable
afterward) — all matched expectations. An isolated unit test against
`HTTP2StreamManager` (no network) confirmed `PruneClosedStreams` itself:
closed/reset streams removed, open ones kept, `LastPeerStreamId` and the
"reject a re-used stream ID" check unaffected by pruning, and
`AdjustAllStreamWindows` only touching the survivors. Live: 200 sequential
requests on one connection (stream IDs 1..399) all returned 200 with no
errors, and multiplexed concurrent requests (`/`, `/large`, `/slow` together;
4× `/large`) were unaffected by pruning running alongside in-flight streams.

**Certificate loading API** (done 2026-07-17): `Program.cs`'s
`CreateSelfSignedCertificate` now loads the PFX round-trip via
`X509CertificateLoader.LoadPkcs12(...)` instead of the obsolete
`new X509Certificate2(byte[], ...)` constructor (`SYSLIB0057`). The originally
planned `X509KeyStorageFlags.EphemeralKeySet` turned out to be a dead end on
Windows: `SslStream.AuthenticateAsServerAsync` failed every handshake with
*"Authentication failed because the platform does not support ephemeral
keys"* — SChannel-backed server auth needs the private key in a CAPI/CNG key
container, not just in memory (Linux's OpenSSL-backed implementation doesn't
have this restriction, which is presumably why the original TODO recommended
it). Switched to `X509KeyStorageFlags.UserKeySet` instead: it persists to the
current user's key store, so it needs no elevated rights (unlike the
`MachineKeySet` it replaced) and isn't shared machine-wide, while working
unchanged on Linux (key-storage flags are largely no-ops there). Verified live
against a strict client (.NET `HttpClient`, which rejected the ephemeral-key
cert outright before the fix) — `GET /`, `POST /echo`, multiplexing, 50
sequential requests, and trailers all still pass.

**Per-stream RST_STREAM cancellation** (done 2026-07-17): `HTTP2RequestHandler`
now takes a `CancellationToken` (4th parameter — a breaking signature change,
`Program.cs`'s handler updated accordingly). `HTTP2Stream` owns a
`CancellationTokenSource`, cancelled from `Reset()` — which already ran on
both "peer sent RST_STREAM" and "we're resetting due to our own error," so no
new call site was needed, just wiring the token through. `DispatchRequestAsync`
passes `Stream.CancellationToken` into the handler call; a new
`catch (OperationCanceledException) { throw; }` ahead of the existing
catch-all stops it from being swallowed into a wasted "send a 500" attempt on
a stream the peer already walked away from. Deliberately scoped to
RST_STREAM only — connection-level teardown (`connectionCts`) still does not
cancel in-flight handlers, which is intentional: graceful shutdown (see next
item) needs in-flight streams to keep running, not be aborted. The demo's
`/slow` handler now passes the token to its `Task.Delay` and logs elapsed time
on cancellation, which doubles as a live demonstration.

**Graceful shutdown** (done 2026-07-17): `HTTP2Server.Stop()` is now
`async Task StopAsync()`. It sends each active connection a best-effort GOAWAY
(`NO_ERROR`, current `LastPeerStreamId`) via the new
`HTTP2Connection.InitiateGracefulShutdownAsync()`, waits up to 2 s for those
writes to land, then cancels as before. `HTTP2Server` tracks live connections
in a `ConcurrentDictionary` (added/removed around `HTTP2Connection.RunAsync()`
in `HandleConnectionAsync`) so `StopAsync` has something to notify.
`InitiateGracefulShutdownAsync` deliberately does *not* reuse the existing
internal `SendGoAwayAsync` (which also drains the socket): that drain reads
from the same `SslStream` the connection's own frame-read loop may still be
blocked reading from, and `SslStream` doesn't support two concurrent reads —
reusing it would race. This is a single best-effort GOAWAY, not RFC 9113
§6.8's full two-phase sequence (initial "stop opening streams" notice, one RTT
wait, then a final GOAWAY) — a client could in principle still race a new
stream into the brief window before `StopAsync`'s subsequent cancellation
takes effect. `Program.cs` now wires `Console.CancelKeyPress` to `StopAsync()`
on both listeners, so the existing "Press Ctrl+C to stop" banner is now true —
previously nothing called `Stop()` at all.

Verified with a standalone harness driving `HTTP2Server` directly (own
self-signed cert, its own port): a baseline request completes normally, then
`StopAsync()` — a GOAWAY (`NO_ERROR`, correct `lastStreamId`) arrived in 7 ms,
`StopAsync()` itself completed in 14 ms, and `RunAsync()` returned (listener
actually stopped) well within the 3 s check window. Per-stream cancellation
was verified against the live demo server: a raw client opened `/slow`, sent
`RST_STREAM(CANCEL)` at the 307 ms mark, and the server log confirms the
handler unwound at 293 ms elapsed (not the full 2000 ms) with no response sent
for that stream, while a follow-up request on a fresh stream over the same
connection still returned 200 (RST_STREAM stayed stream-scoped, exactly as
`HTTP2StreamException`-driven resets already did before this change).

**Huffman decoder** (done 2026-07-18): `HuffmanDecoder.Decode` in `HPACK.cs`
was O(n×257) — for every symbol it linearly scanned all 257 table entries,
retried at shrinking bit-lengths. Replaced with a bit-level trie
(`TrieNode` with `Zero`/`One` children, built once into a `static readonly
Root` from the existing 257-entry `HuffmanTable`): each decode step is one
array-lookup per *bit* instead of one 257-entry scan per *symbol*. Using
1-bit steps (rather than byte- or nibble-sized table chunks) is deliberate:
HPACK's shortest codeword is 5 bits, so a single bit can never complete two
codewords at once, meaning each step resolves at most one symbol — this
sidesteps the "multiple symbols per table entry" bookkeeping that byte-at-a-
time Huffman table designs (e.g. Go's `hpack` package) need. Measured 8.1×
faster on realistic (printable-ASCII) input; the theoretical worst case
(all rare/control-byte symbols, 20-30 bit codes) is still meaningfully
faster since it's O(bits) per symbol instead of O(257) regardless of length.

`BuildTrie` doubles as a self-check of the hardcoded 257-entry `HuffmanTable`:
inserting a symbol whose code is a prefix of (or has as a prefix) an
already-inserted symbol's code now throws `InvalidOperationException`
immediately at class-init time. **This caught real, pre-existing bugs in the
literal table**, latent since whenever this file was first written — four
symbols had the wrong hex code:

- Symbol 123 (`'{'`): was `0x7fffe` (19 bits), correct is `0x7ffe` (15 bits)
  — this one produced a hard *prefix collision* with 16 other codes and
  crashed `BuildTrie` outright the moment it ran.
- Symbols 228, 232, 233: transposed by one in the low hex digit relative to
  the correct values — these didn't collide structurally, so they'd have
  silently mis-decoded specific rare byte values forever without ever
  throwing. (An earlier attempted fix for these three, based on a bulk
  WebFetch-summarized copy of the RFC table, turned out to be wrong in the
  *opposite* direction — LLM-summarized transcription of a long repetitive
  table is not reliable. What actually nailed it down: RFC 7541's Huffman
  code is a canonical Huffman code, meaning the exact codes are fully
  determined by the bit-*lengths* alone via the standard DEFLATE-style
  assignment algorithm (RFC 1951 §3.2.2). Bit-lengths are far shorter/less
  error-prone to transcribe than 6-8 hex-digit codes, so three independent
  small verbatim-quote fetches of length-only data — internally consistent
  at every chunk boundary — were used to reconstruct all 257 codes
  mathematically and diff them against the file. That reconstruction now
  matches the file exactly for all 257 entries.)

Verified: the RFC 7541 Appendix B "www.example.com" worked example decodes
correctly; 5000-round fuzz test round-trips random strings (full byte range,
using a from-scratch reference Huffman *encoder* built for the test) through
`Decode` with zero mismatches; explicit RFC §5.2 padding-edge-case tests
(valid all-1s padding, corrupted non-1s padding → `COMPRESSION_ERROR`,
>7 leftover bits → `COMPRESSION_ERROR`, explicit EOS codeword →
`COMPRESSION_ERROR`, empty input → empty string) all pass. A differential
comparison against the *old* algorithm (restricted to printable-ASCII input)
also agreed in all 2000 cases — but the old algorithm turned out to have its
*own* separate latent bug outside that restricted range: its `uint` bit
buffer accumulates via unbounded left-shifts, silently losing high bits once
enough consecutive long (20-30 bit) codes pile up past 32 bits before
resolving — caught by fuzzing with the full byte range rather than
printable-ASCII-only input, and not something the replacement (which has no
fixed-width buffer at all) is susceptible to.

Fixed the padding-*validation* gap noted in the original TODO wording along
the way: the old code only checked the leftover bit *count* (`> 7` bits ⇒
error) but never checked the bit *value* — RFC 7541 §5.2 requires padding to
be all 1s specifically (a valid prefix of the EOS codeword), and a
crafted input with non-1s trailing bits was silently accepted before. Also
added the explicit-EOS-codeword rejection required by the same section,
which the old code only enforced by accident (its symbol-matching loop
happened to exclude symbol 256 from consideration).

**Stream-management hardening** (Roadmap Track A, done 2026-07-18) — three
independent fixes, RFC 9113 §5:

- **HTTP/2 Rapid Reset mitigation (CVE-2023-44487).** A peer that opens a
  stream and immediately `RST_STREAM`s it, over and over, never triggers the
  existing CONTINUATION/PING/SETTINGS flood counters (HEADERS resets
  `unproductiveFrames`, since it *is* real request progress each cycle) and
  never counts against `MAX_CONCURRENT_STREAMS` (a `Closed` stream doesn't
  count in `GetOrCreateStream`'s `openCount`) — yet each cycle still costs a
  stream slot, HPACK decode work, and a dispatched handler task. New
  `CheckRapidReset` tracks `streamsOpenedByPeer` vs. `peerResetStreams`; once
  at least 20 streams have been opened (`MinStreamsForResetRatioCheck` — too
  small a sample makes a ratio meaningless), a peer-reset ratio over 50%
  (`MaxPeerResetRatio`) aborts the connection with `GOAWAY ENHANCE_YOUR_CALM`.
  Deliberately a connection-lifetime ratio, not a sliding time window — simple
  and effective for this server's threat model, but a very long-lived
  connection with a naturally high organic cancellation rate could in
  principle still trip it eventually.
- **Stream-ID exhaustion (§5.1.1).** Stream IDs are a 31-bit field
  (`HTTP2StreamManager.MaxStreamId = 0x7FFFFFFF`); once
  `LastPeerStreamId` is within 1000 of that ceiling
  (`IsNearStreamIdExhaustion`), `HandleHeaders` now proactively sends a
  best-effort `GOAWAY NO_ERROR` exactly once (reusing
  `InitiateGracefulShutdownAsync` from the graceful-shutdown work) and
  refuses every further new stream with a stream-level `RST_STREAM
  REFUSED_STREAM` — the connection keeps serving already-open streams instead
  of running to the hard wall, where the next stream ID would otherwise fail
  the existing "must be greater than the last one" check as an abrupt
  connection-level `PROTOCOL_ERROR`.
- **Outbound `MAX_HEADER_LIST_SIZE` (§6.5.2, audit item).** Inbound
  enforcement already existed (`EnforceHeaderBufferLimit`, part of the
  CONTINUATION-flood hardening); `SendResponseAsync` only ever split by the
  peer's `MAX_FRAME_SIZE`, never checked its advertised header-list limit at
  all. New `EnforceOutboundHeaderListSize` computes the uncompressed size
  (name + value bytes + 32 per field — the same accounting HPACK's dynamic
  table uses) and throws if it exceeds what the peer said it'll accept; the
  throw is caught by `DispatchRequestAsync`'s existing catch-all, which falls
  back to the (much smaller, safely-under-any-sane-limit) 500 response instead
  of spending a round trip on headers the peer already said it would reject.

Verified with a raw attack client: `rapidreset` (30× open+immediate-reset,
never completing a request) → `GOAWAY ENHANCE_YOUR_CALM` after exactly the
20th reset (log: `"Too many streams reset by peer (20/20)"`), confirming the
ratio check fires as soon as the minimum sample is reached rather than
waiting for all 30. `streamid-exhaustion` (stream IDs only need to be
monotonically increasing, not consecutive, so the test jumps straight to
`0x7FFFFFFF - 1000` instead of opening ~2^31 streams): a stream exactly at the
margin still succeeds (200), the next one past it is refused
(`RST_STREAM REFUSED_STREAM`) alongside a proactive `GOAWAY NO_ERROR`, and a
third attempt afterward is *also* just refused rather than killing the
connection outright — confirming this stays a per-stream refusal, not a
teardown. `outbound-headerlimit` (client advertises `MAX_HEADER_LIST_SIZE=150`
in its own SETTINGS): the demo's normal `/` response (213 uncompressed bytes,
logged exactly) gets swapped for the 500 fallback (96 bytes, under the limit)
instead of being sent anyway. Regression: normal `HttpClient` requests,
multiplexing, and 100 sequential non-reset requests on one connection (which
must *not* trip the new reset-ratio check) all still pass, as do all the
existing flood/validation/trailers/idle-stream/RST_STREAM-cancellation attack
scenarios from earlier work.

**CONNECT + extended CONNECT + WebSocket** (Roadmap Track B, done 2026-07-18)
— the full three-RFC stack in one pass:

- **RFC 9113 §8.5 — plain CONNECT.** `ValidateRequestHeaders` now branches on
  `:method == CONNECT`: `:scheme`/`:path` MUST be *absent* and `:authority`
  (the tunnel target) MUST be present — the opposite of every other request.
  A new `HTTP2ConnectHandler` delegate (parallel to `HTTP2RequestHandler`)
  decides accept/reject *before* anything is sent, so it can inspect
  `:authority` first; `HTTP2Connection`'s constructor takes an optional one,
  threaded through `HTTP2Server`. No handler registered → a well-formed
  CONNECT still gets a proper `501`, not a framing-level error (that's a "we
  don't implement this" business decision, not a protocol violation).
- **RFC 8441 — extended CONNECT.** Adds the `:protocol` pseudo-header (now in
  `RequestPseudoHeaders`, but only legal on a CONNECT request, and only if a
  connect handler is registered — checked as `PROTOCOL_ERROR` otherwise, since
  using a pseudo-header we never advertised support for is a genuine framing
  violation, unlike plain CONNECT's "unsupported method" case). Unlike plain
  CONNECT, extended CONNECT *requires* `:scheme` and `:path` (RFC 8441 §4
  explicitly differs from §8.5 here — easy to get backwards). We advertise
  `SETTINGS_ENABLE_CONNECT_PROTOCOL = 1` (added to `HTTP2SettingsParameter`,
  value `0x08`) in the server preface, but only when a connect handler is
  actually registered.
- **Accepted-tunnel plumbing.** `HTTP2Stream.IsConnectTunnel` +
  `TunnelInbound` (an unbounded `Channel<byte[]>`) replace the normal
  "buffer a body, dispatch once END_STREAM" flow for these streams:
  `HandleDataAsync` routes DATA payloads straight into the channel as they
  arrive instead of `RequestBody`, since a tunnel (especially a WebSocket) has
  no defined end until the peer says so. `HTTP2Tunnel` (a small public class)
  wraps that channel for reads and reuses `ReserveSendWindowAsync` for writes
  — a tunnel write is flow-controlled exactly like an ordinary response body,
  not privileged. `SendResponseAsync`'s HEADERS(+CONTINUATION)-splitting logic
  was factored out into `SendHeaderBlockAsync` so the new CONNECT response
  path (`SendConnectResponseAsync`) reuses it instead of duplicating it.
  Tunnels run on their own background task (`StartConnectHandler`, mirroring
  `StartRequestHandler`) so a long-lived one (a WebSocket can stay open
  indefinitely) never blocks the frame read loop from servicing other streams.
- **RFC 6455 — WebSocket framing**, in the new `WebSocket.cs`, layered on top
  of `HTTP2Tunnel` with no separate HTTP/1.1-style Upgrade handshake needed
  (RFC 8441's `:protocol` already established the tunnel). `WebSocketConnection`
  handles masking (server frames unmasked out, client frames MUST be masked
  in — enforced, not just assumed), fragmentation reassembly, automatic
  ping→pong, and the close handshake (echo the peer's close frame back per
  §5.5.1). A small internal buffered reader (`ReadExactAsync`) exists because
  WebSocket frame boundaries have no relationship to HTTP/2 DATA frame
  boundaries — a single tunnel chunk can hold a fraction of one WS frame or
  several at once.
- **Demo** (`Program.cs`): `HandleConnect` routes extended CONNECT with
  `:protocol=websocket` at `/ws-echo` to a WebSocket echo loop, and plain
  CONNECT to a raw byte-loopback echo (proving the tunnel framing itself
  works, independent of any protocol on top — a real proxy would open a TCP
  connection to `:authority` and splice the two instead). Anything else gets
  `404`.

Verified with a new raw client (`h2connect`) covering the full stack: our
SETTINGS correctly advertises `ENABLE_CONNECT_PROTOCOL=1`; a plain-CONNECT
loopback tunnel echoes three round trips (including a UTF-8/emoji payload)
byte-for-byte; an extended-CONNECT WebSocket session round-trips a text
message, a binary message, a 3-frame fragmented text message (reassembled
correctly), an unsolicited ping (answered with a matching pong), and a
client-initiated close (echoed back, tunnel ends via `END_STREAM` shortly
after); an unknown `:protocol`/`:path` gets `404` with the connection staying
alive for a follow-up request (an app-level refusal, not a framing error);
`RST_STREAM` mid-tunnel leaves the connection usable for a follow-up request,
same as the existing per-stream cancellation behavior. Six malformed-header
cases (`:scheme`/`:path` present on plain CONNECT, missing `:authority`,
missing `:scheme`/`:path` on extended CONNECT, `:protocol` on a non-CONNECT
request) each get `RST_STREAM`/`PROTOCOL_ERROR` with the connection staying
alive. Multiplexing: a WebSocket tunnel left open on stream 1 does not delay
ordinary requests on streams 3/5 on the same connection (both completed in
single-digit milliseconds), and the tunnel is still fully responsive
afterward. Full regression (HttpClient GET/echo/multiplexing incl. `/large`,
50 sequential requests, all flood/validation/trailers/idle-stream/RST_STREAM-
cancellation/rapid-reset/stream-exhaustion/header-limit attack scenarios from
earlier work) still passes.

**Test-client gotcha found during verification, not a server bug:** an early
version of the multiplexing test used `/large` (128 KiB) alongside the open
WebSocket tunnel and hung indefinitely. Root cause was the raw test client
itself — unlike `HttpClient`, it never sends `WINDOW_UPDATE`, so once `/large`
exhausted the default 64 KiB *connection-level* send window, the server
correctly blocked in `ReserveSendWindowAsync` waiting for window space that
would never come — and since the tunnel write shares that same connection
window, it silently stalled too. Confirmed via the server log (the WebSocket
handler's "echoing" line was logged — meaning the message was received and
the echo attempted — but the bytes never made it out). Fixed by using a small
endpoint in that test instead; the real flow-control path for large responses
is already covered separately by `HttpClient`'s multiplexing test.

Builds cleanly: 0 errors (at the time, 1 warning — the unused field
`prefaceReceived`, a write-only dead-code leftover; removed 2026-07-18, so the
solution now builds with 0 warnings). Fixed on first build: three `CS1739`
named-argument casing errors, and a protocol bug where the SETTINGS **ACK**
was sent before our own SETTINGS frame — RFC 9113 §3.4 requires the server
preface (a non-ACK SETTINGS) to be the *first* frame the server sends; strict
clients (e.g. .NET `HttpClient`) terminated the connection with
`PROTOCOL_ERROR` on that. curl/nghttp2 tolerated it, which is why `GET /`
appeared to work before.

**Gotcha found during verification:** the demo host originally listened on
127.0.0.1 only; clients resolving `localhost` try `::1` first and paid a ~2 s
connect-timeout penalty per connection before falling back to IPv4 — which
masqueraded as a multiplexing failure. The demo now listens on both loopbacks.

**RFC 9218 prioritization + priority-aware multiplexed writer** (Roadmap
Track C, done 2026-07-18) — both halves the roadmap note called for: the RFC
9218 signaling itself, and the "multiplexed-writer rework" needed to actually
act on it (before this, DATA was sent first-come-first-served straight from
whichever response/tunnel task got there first, under one write lock — no
signal could have changed send order even if it existed):

- **Signaling.** `HTTP2SettingsParameter.NO_RFC7540_PRIORITIES` (`0x09`) is
  advertised unconditionally in the server preface — accurate unconditionally,
  since RFC 7540 priority (the `PRIORITY` frame, and `HEADERS`' `PRIORITY`
  flag) was already parsed-and-ignored before this track and still is.
  `HTTP2FrameType.PRIORITY_UPDATE` (`0x10`) is a new connection-level frame
  (RFC 9218 §7.1: its own Stream Identifier is always 0; the *target* stream
  is a "Prioritized Stream ID" inside the payload) — handled by
  `HandlePriorityUpdate`, which silently ignores a target stream that's
  unknown or already closed (RFC 9218 explicitly allows this: reordering
  between a `PRIORITY_UPDATE` and its target's own `HEADERS`, or one arriving
  after its target already finished, are both expected outcomes of ordinary
  network reordering, not protocol violations) and otherwise counts it via
  `CountUnproductiveFrame()` — same flood-defense treatment as PING/SETTINGS,
  since a `PRIORITY_UPDATE` flood costs a lookup+parse without any request
  progress. The `priority` **request** header field (a Structured Fields
  Dictionary, RFC 8941) is parsed in `CompleteHeaders` right after
  `RequestHeaders` is populated; a **response** header of the same name is
  also honored (RFC 9218 §5 lets the origin server reprioritize relative to
  what the client asked for) via `ApplyResponsePriorityOverride` in
  `SendResponseAsync` — applied locally *and* still sent to the client
  unmodified. Both paths, plus `PRIORITY_UPDATE`, share one parser
  (`ParsePriority`) for the `u=<0-7>, i` grammar. Deliberately lenient per RFC
  9218 §4: an unknown key, a malformed value, or an out-of-range urgency
  falls back to that parameter's default (`u=3`, `i=false`) rather than
  raising a stream/connection error — a bad priority hint is a hint gone
  wrong, not a protocol violation.
- **The writer loop.** `HTTP2Stream` gained an `OutboundQueue`
  (`HTTP2OutboundQueue`, `HTTP2Stream.cs`) — a per-stream FIFO of body/tunnel
  bytes. `SendResponseAsync` and `SendTunnelDataAsync` no longer reserve
  window and call `SendFrameAsync` themselves; they just
  `EnqueueOutboundAsync` the whole payload (awaiting completion, so
  write-completion backpressure — e.g. for a slow WebSocket peer — is
  preserved) and return. One new background task per connection,
  `DataWriterLoopAsync` (started in `RunAsync` alongside `FrameLoopAsync`, so
  a slow writer never blocks the read loop or vice versa), is now the *sole*
  place that reserves flow-control window and calls `SendFrameAsync` for
  DATA — it scans `HTTP2StreamManager.GetSendableStreams()` each turn and
  picks via `ComparePriority`: lower urgency number first; within the same
  urgency, non-incremental preferred over incremental (RFC 9218's "send as a
  single unit" vs. "fine to interleave"); ties beyond that broken
  round-robin-fairly by `HTTP2Stream.LastServedSequence` (least-recently-
  served first). This is a deliberate simplification of the RFC's
  non-incremental guidance — a strict reading favors draining one
  non-incremental stream to completion before starting the next at the same
  urgency, whereas same-urgency non-incremental streams here still round-robin
  fairly rather than head-of-line-blocking one behind another; reasonable for
  a learning implementation, and arguably a fairer outcome for concurrent
  equal-urgency responses either way.
- **A starvation bug caught and fixed before it ever shipped:** the first
  draft of `PickNextStreamToSend` picked the single best-priority candidate
  first and only *then* checked whether it had window — discarding the whole
  turn if it didn't, even when a lower-priority candidate needing no window at
  all (e.g. a tunnel's zero-length closing marker, which needs no
  flow-control budget) was sitting right there ready to go. Since the
  discarded high-priority pick would be re-selected as "best" on every
  subsequent turn too, this could starve the low-priority window-free
  candidate indefinitely, not just delay it. Fixed by folding the
  window-availability check into candidacy itself (`PickNextStreamToSend`
  now takes the current connection send window and excludes window-blocked
  streams from consideration *before* comparing priority), so a lower-priority
  but immediately-sendable stream is correctly chosen instead.
- **Locking.** `HTTP2StreamManager`'s `streams` dictionary previously had an
  informal single-writer invariant (only ever touched from the connection's
  frame read loop). `DataWriterLoopAsync` breaks that by enumerating streams
  from a second, concurrently-running task, so the dictionary is now guarded
  by its own `dictLock`, separate from the connection's existing `flowLock`
  (send-window accounting) — consistent nesting order (`flowLock` outer,
  `dictLock` inner, only in `ApplyRemoteSettings`'s `AdjustAllStreamWindows`
  call) rules out deadlock. `GetSendableStreams()` returns a snapshot rather
  than live enumeration specifically so the priority comparison itself runs
  outside `dictLock` entirely.
- **Backpressure/cancellation on teardown.** `HTTP2OutboundQueue.EnqueueAsync`
  returns a `Task` completed when the writer loop actually drains that item
  (or `AbandonAll()` runs). Two teardown paths, complementary: an individual
  `RST_STREAM`'d stream (connection otherwise alive) is handled by
  `HTTP2Stream.Reset()` now also calling `OutboundQueue.AbandonAll()`, so a
  producer awaiting a chunk that will now never be sent isn't left hanging;
  a whole-connection teardown (no per-stream `Reset()` involved) is handled by
  `EnqueueOutboundAsync` wrapping its wait in `.WaitAsync(cancellationToken)`
  against the connection's own token, the same pattern the old
  `ReserveSendWindowAsync` already used. `ReserveSendWindowAsync` itself is
  now dead code (both former call sites route through the queue) and was
  deleted rather than left unused. `SignalWindowChange` was renamed
  `SignalWriterWakeup` — its purpose outgrew "a send window changed" once it
  also became the wakeup for "new data was enqueued" and "a priority changed".

Verified with a new raw client (`h2priority`) plus the full existing
regression suite: `settings` confirms `NO_RFC7540_PRIORITIES=1` is advertised;
`urgency-header` opens two concurrent `/large` requests (one `priority: u=7`,
one `priority: u=0`) with the client deliberately never sending
`WINDOW_UPDATE` (so the initial ~64 KiB connection-window burst is a
deterministic, not just timing-based, cutoff) — once the `u=0` stream's first
frame appears, every remaining frame in that burst is exclusively its own
(zero interleaving from the `u=7` stream once both are contending);
`priority-update` opens two default-priority (`u=3`) requests, lets them
contend to a window-blocked stall, then sends `PRIORITY_UPDATE` promoting one
to `u=0` plus a window top-up — the promoted stream reaches `END_STREAM`
before the other receives one further byte; `priority-update-unknown-stream`
confirms a `PRIORITY_UPDATE` for a never-opened stream is silently ignored,
connection still usable; `malformed-priority` confirms `priority: u=99, i=?1`
(out-of-range urgency) still returns 200 instead of a protocol error. Full
regression: `HttpClient` GET/echo/multiplexing (incl. `/large` and 4×
concurrent `/large` sharing the connection window, and 100 sequential
requests on one connection), all `h2attack` scenarios (flood/malformed-
request/trailers/idle-stream/RST_STREAM-cancellation/rapid-reset/stream-
exhaustion/outbound-header-limit), all `h2connect` scenarios (plain CONNECT,
extended CONNECT WebSocket incl. fragmentation/ping/close, 6 malformed-CONNECT
cases, multiplexing alongside an open tunnel), and the standalone graceful-
shutdown harness (confirms `DataWriterLoopAsync` being awaited in `RunAsync`'s
`finally` doesn't delay shutdown: GOAWAY in 5 ms, `StopAsync()` in 6 ms) all
still pass with zero regressions.

**RFC 9110 "core mechanics" + content negotiation** (Roadmap Track D, done
2026-07-18; content negotiation added later the same day) — the app-semantics
layer above framing, in a new `HTTPSemantics.cs` that deliberately never
touches `HTTP2Connection.cs`, frames, streams, or HPACK: RFC 9110 is
version-independent, so this file (and the demo routes wired to it) is proof
that the layering the roadmap note described actually holds, not just an
assertion. Scoped to the parts of RFC 9110 with a single, directly
wire-testable correct behavior; a generic auth challenge framework and RFC
9111 caching semantics remain deliberately out (see the Track D roadmap note
below for why):

- **The seam.** `HTTPResourceHandler` (`Task<HTTPResource?> (path, headers,
  cancellationToken)`) is the one thing a single-representation app
  implements — "what is this resource's current representation, or null for
  404." `HTTPSemantics.Wrap` turns that into an ordinary
  `HTTP2RequestHandler`, so it plugs into `HTTP2Connection`/`HTTP2Server` (or
  a hypothetical future H1/H3 sibling) exactly like a raw handler would.
  `HTTPResource` carries `Body`, `ContentType`, optional
  `ContentEncoding`/`Language` (the negotiation axes, see below), and optional
  `ETag`/`LastModified` — if `ETag` is omitted, one is derived from a SHA-256
  hash of the body (stable for as long as the body doesn't change, which is
  all a strong validator promises; conveniently this also gives two negotiated
  variants distinct ETags for free, keeping conditional requests correct
  across them). A parallel `HTTPVariantHandler` returns *several* `HTTPResource`s
  for content negotiation; the single-resource `Wrap` overload is just a thin
  adapter over the multi-variant one (null → empty list, resource →
  one-element list).
- **Method semantics (RFC 9110 §9).** GET and HEAD share one code path (HEAD
  runs the same resource lookup + conditional/precondition logic, then omits
  the body while still reporting the real `Content-Length` — RFC 9110 §9.3.2).
  OPTIONS returns `204` + `Allow: GET, HEAD, OPTIONS` without invoking the
  resource handler at all. Anything else (POST/PUT/DELETE/PATCH/...) is
  `405 Method Not Allowed` with `Allow` (§15.5.6 requires the header on a
  405) — this "core mechanics" slice takes no position on resource
  creation/mutation semantics.
- **Proactive content negotiation (§12).** A front stage that runs *before*
  the conditional/Range pipeline and just picks which variant feeds it — so
  everything downstream is byte-identical to the single-representation case
  (that's why adding it disturbed none of the earlier checks). Three
  independent axes: media type (`Accept`, §12.5.1, with
  exact > `type/*` > `*/*` specificity ranking), content coding
  (`Accept-Encoding`, §12.5.3, with the `identity`-acceptable-by-default
  special case), and language (`Accept-Language`, §12.5.4). Language matching
  is prefix-in-*either*-direction on a subtag boundary — `de` matches a
  `de-DE` variant (basic filtering) *and* a `de-AT` request matches a plain
  `de` variant (lookup-style truncation, RFC 4647 §3.4 — a friendlier choice
  than strict basic filtering, which §12.5.4 explicitly permits). All three
  share one weighted-list parser (`q` defaults to 1, `q=0` = explicit
  rejection). The **406-vs-default policy** (§12.1 leaves this to the server)
  is the subtle part: a variant explicitly forbidden by a `q=0` on any axis is
  never served, but a variant that merely fails to positively match (client
  wanted a language we don't have, without forbidding ours) is still eligible
  as a last-resort default — i.e. we *disregard* an unsatisfiable `Accept`
  rather than answer 406 (the friendlier of the two RFC-sanctioned behaviors),
  and only fall through to a genuine `406 Not Acceptable` when *every* variant
  is hard-forbidden. **`Vary` (§12.5.5)** is emitted on every negotiated
  response — including 304 and 406 — listing exactly the axes the variants
  actually differ on (so a cache keys correctly), and `Content-Language` /
  `Content-Encoding` describe the chosen representation on the 200/206 that
  carry it. Media-range parameters beyond `q` (e.g. `text/html;level=1`) are
  the documented simplification. (On-the-fly compression — negotiating a
  content coding and gzip/brotli-ing an identity body on demand — was later
  added as an opt-in; see the "On-the-fly content coding" entry below.)
  `Accept-Charset` is deliberately unimplemented (deprecated in §12.5.2).
- **Conditional requests (§13).** `If-Match`/`If-Unmodified-Since` (strong
  comparison, §13.1.1/§13.1.3) and `If-None-Match`/`If-Modified-Since` (weak
  comparison, §13.1.2/§13.1.4) are evaluated in the exact precedence order
  §13.2.2 mandates — If-Match short-circuits Unmodified-Since, and
  If-None-Match short-circuits Modified-Since — short-circuiting to `412
  Precondition Failed` or (for GET/HEAD specifically) `304 Not Modified`.
  `*` in If-Match/If-None-Match means "any current representation" (trivially
  true/false once a resource is known to exist). HTTP-dates are compared at
  1-second granularity (HTTP-date has no sub-second precision) via a small
  parser built on .NET's `"r"` (RFC 1123) format specifier plus a lenient
  fallback.
- **Range requests (§14).** Single-range `Range: bytes=first-last` /
  `bytes=first-` / `bytes=-suffixLength` → `206 Partial Content` +
  `Content-Range`; a syntactically valid but unsatisfiable range (start at or
  past the resource's length) → `416 Range Not Satisfiable` +
  `Content-Range: bytes */length`; anything this wrapper doesn't parse
  (multi-range, garbage) is simply ignored, falling back to an ordinary `200`
  — both are explicit, distinct RFC 9110 §14.2 outcomes, not the same
  "give up" path. `If-Range` (§13.1.5) gates whether Range is honored at all:
  an entity-tag uses strong comparison (a weak validator can never safely
  resume a partial download), a date uses exact 1-second-granularity
  equality; a mismatch silently ignores Range and returns the full `200`.
  Every successful GET/HEAD/206 response advertises `Accept-Ranges: bytes`.
  Multi-range (`multipart/byteranges`) responses are the one explicitly
  documented gap in Range support — real but rarely used, and meaningfully
  more complex than the single-range case.
- **The demo.** `Program.cs` wires two routes: a single static resource at
  `/files/resource.txt` (via the `HTTPResourceHandler` overload — routing by
  `/files/` *prefix* means `/files/missing.txt` exercises `HTTPSemantics`'
  *own* 404, distinct from `HandleRequest`'s unrelated default-case 404), and
  a content-negotiated `/files/greeting` (via the `HTTPVariantHandler`
  overload) offering the same greeting as three variants — English and German
  `text/plain`, plus an English `application/json` — so `Accept` /
  `Accept-Language` actually select a representation and the response carries a
  matching `Vary`.

**A pre-existing silent-failure gap found and fixed along the way, unrelated
to this feature but caught while chasing down what first looked like a
Track-D bug:** `RunAsync`'s `finally` block (added in Track C to await
`DataWriterLoopAsync`) wrapped that await in a bare `catch { }` — any
unexpected exception from the writer loop would vanish with no log line at
all, unlike every other error path in this class. Now logs via
`Console.Error.WriteLine` before swallowing, same as everywhere else. (The
actual Track-D symptom that led here — a fresh `HttpClient` connection
getting reset in `h2semantics`, the new raw test client, with the server log
showing an ALPN mismatch — turned out to be a bug in the *test client*, not
the server: `HttpRequestMessage.SendAsync` doesn't inherit `HttpClient`'s
`DefaultRequestVersion`/`DefaultVersionPolicy` the way `GetAsync`/`PostAsync`
do, so unset `Version`/`VersionPolicy` on manually-constructed request
messages silently fell back to HTTP/1.1's ALPN offer. Fixed by setting both
explicitly on every constructed request.)

Verified with a new `HttpClient`-based test client (`h2semantics`;
strict-client verification is the natural fit here since RFC 9110
conditional/range/negotiation logic operates purely on headers, not
framing-level edge cases): 51 checks, all passing — baseline GET (status,
ETag, Last-Modified, Accept-Ranges, body), HEAD (matching Content-Length,
empty body), OPTIONS (204 + Allow), POST (405 + Allow), 404 via
`HTTPSemantics` itself for an unmatched path under `/files/`, If-None-Match
(matching ETag → 304 with no body; mismatched → 200; `*` → 304),
If-Match (matching → 200; mismatched → 412), If-Modified-Since (future →
304; past → 200), If-Unmodified-Since (past → 412; future → 200), Range
(`bytes=0-9` → 206 with correct 10-byte slice and Content-Range; suffix
`bytes=-10` → 206 with correct tail; out-of-bounds → 416 with `Content-Range:
bytes */length`), Range + If-Range (matching ETag → 206; mismatched →
full 200 body), and — against `/files/greeting` — content negotiation:
no-Accept baseline picks the server-default (en `text/plain`) with
`Vary: accept, accept-language`; `Accept-Language: de` selects the German body
with `Content-Language: de`; `de;q=0.3, en;q=0.9` picks English on q;
`de-AT` matches the `de` variant (lookup truncation); `Accept: application/json`
selects the JSON variant, `text/*` a text one, and `text/*;q=0.3,
application/json;q=0.9` picks JSON on q; `text/plain;q=0, application/json`
falls to JSON rather than 406 (disregard policy for a non-forbidden miss)
while `*/*;q=0` correctly yields a 406 that still carries `Vary`;
`Accept-Language: fr` (unsatisfiable, not forbidden) falls back to the default
rather than 406; and the en/de variants carry distinct ETags. (One check
initially failed — `de-AT` expecting the `de` variant — which surfaced that
the first draft did *strict* RFC 4647 basic filtering, where a range must be a
prefix of the tag; fixed to prefix-in-either-direction so lookup-style
truncation works, which is the friendlier §12.5.4-permitted behavior.) Full
regression — `HttpClient` GET/echo/multiplexing (incl.
`/large`, 4× concurrent `/large`, 100 sequential requests), a representative
sweep of `h2attack` (flood/malformed/trailers/idle-stream/RST_STREAM-
cancellation/rapid-reset/stream-exhaustion/outbound-header-limit),
`h2connect` (SETTINGS advertisement, plain CONNECT loopback, WebSocket echo,
multiplexing alongside an open tunnel), and `h2priority` (SETTINGS
advertisement, urgency-header ordering, PRIORITY_UPDATE mid-flight
promotion) — all still pass with zero regressions, confirming the new
HTTP2Connection.cs change (the exception-logging fix) altered no behavior.

**HTTP/2 client + stream-manager generalization** (Roadmap Track E, phases
1–3 done 2026-07-18; the physical project split — phase 4 — is the remaining
piece) — the project's first move from server-only toward its stated
shared-lib + server + client shape. Two sub-parts:

- **Generalized the stream layer out of its server-role assumptions (in
  place, zero server behavior change).** `HTTP2StreamManager` had two
  hardcoded server facts — "peer-initiated streams are odd" and "we never
  initiate/push (so every even ID is idle)". Both are now driven by a new
  `HTTP2Role` (Server/Client, defaulting to Server so every existing call site
  is untouched): `PeerInitiatedParity`/`LocalInitiatedParity` replace the
  literal `% 2 == 1` checks in `GetOrCreateStream` and `IsIdle`, and a new
  `CreateLocalStream()` allocates the next locally-initiated odd stream ID
  (the client's request streams) with its own concurrency + 31-bit-exhaustion
  guards. For the server (local parity = even, push disabled)
  `LastLocalStreamId` stays 0, so `IsIdle` is byte-identical to before —
  verified by re-running the idle-stream / stream-exhaustion attack cases
  (unchanged: even-stream/idle-data/idle-windowupdate → connection
  `PROTOCOL_ERROR`; implicit-close cases → `STREAM_CLOSED`/silently-ignored;
  exhaustion → proactive `GOAWAY` + `REFUSED_STREAM`).
- **Built the client** — `HTTP2ClientConnection.cs` (the client-role mirror of
  `HTTP2Connection`) + `HTTP2Client.cs` (the dialing counterpart of
  `HTTP2Server`, doing the TCP connect + TLS/ALPN `h2` handshake). The client
  *sends* the connection preface (magic + its SETTINGS) first, then reads the
  server's SETTINGS (the inverse of the server's read-then-send order),
  allocates its own odd stream IDs via `CreateLocalStream`, sends requests
  (HEADERS [+ flow-controlled DATA]) and assembles responses (HEADERS [+ DATA],
  into an `HTTP2Response`). Real client-side multiplexing: `SendRequestAsync`
  is thread-safe with concurrent in-flight requests, serializing only the
  atomic "allocate ID + HPACK-encode + write HEADERS" start (RFC 9113 §5.1.1
  wants opens in increasing-ID order, and the HPACK encoder's dynamic table is
  stateful). Reuses the shared, direction-neutral code unchanged — frames,
  HPACK (both directions), the stream state machine, flow control (the client
  replenishes its receive window per DATA frame exactly as the server does, so
  large responses don't stall — the mirror of the flow-control gotcha noted in
  the CONNECT work). Handles SETTINGS/WINDOW_UPDATE/PING(→ACK)/RST_STREAM/GOAWAY
  and rejects an unsolicited PUSH_PROMISE (we advertise `ENABLE_PUSH=0`).

Verified with a new interop test (`h2clienttest`) — **14 checks, all
passing** — that runs the client against *both* sides of the interop rule:
against **our own server** in-process (GET → 200 + exact body; POST /echo
byte-exact incl. a UTF-8/emoji payload; GET /large → 128 KiB, exercising send
flow control; and multiplexing — `/`, `/large`, `/slow` fired together, the
fast ones completing in single-digit ms while `/slow` pends ~1.5 s), and
against a **real .NET Kestrel HTTP/2 server** (HTTP/2-only listener, TLS,
self-signed cert): GET → 200 with the body and `content-type` correctly
HPACK/Huffman-decoded from Kestrel's *real* encoder (a strong test of our
decoder against a production encoder), POST /echo byte-exact, GET /big → 64 KiB
under flow control against Kestrel's own windowing, 3 concurrent requests all
200, and an unknown path → 404. The full server regression (h2semantics 51/51,
h2priority, h2connect, h2attack) still passes unchanged — the generalization
touched only server-identical code paths.

**Physical project split** (Track E phase 4, done 2026-07-18): the flat `src/`
project is now four — `Core` (shared library), `Server`, `Client`, `Demo`
(solution `HTTP2.slnx`; see the Architecture section). As anticipated it was
*not* a pure file move: `HTTP2Settings` and the `HTTP2RequestHandler` delegate
were extracted from `HTTP2Connection.cs` into `Core` (the latter because
`HTTPSemantics`, also in `Core`, produces it), and `WebSocket.cs` moved to
`Core` behind a new transport-agnostic `IHTTP2Tunnel` interface so it no longer
depends on the server's concrete `HTTP2Tunnel` (which now implements the
interface). Project references enforce the dependency direction structurally —
`Core` compiles with no reference to `Server`/`Client`, proving the boundary is
real. The raw-frame test harnesses switched from `<Compile Include>` of
individual files to a `<ProjectReference>` on `Core` (or `Server`/`Client`) —
cleaner and robust to file moves. The namespace was then renamed from the
provisional `HTTP2Server` to the Vanaheimr/Hermod-convention
`org.GraphDefined.Vanaheimr.Hermod.HTTP2` across all four assemblies and every
test harness (a mechanical `namespace`/`using` sweep; it also dissolved the old
`HTTP2Server` namespace-vs-class name collision that had forced `using`-alias
workarounds in two harnesses). Verified: the full solution builds clean (0
errors), the demo runs from `Demo/`, and the entire regression re-run against
the split + renamed build passes unchanged — h2semantics 51/51, h2attack
(flood/malformed/idle), h2priority, h2connect (WebSocket + multiplex),
h2shutdowntest, and the client interop 14/14 (our server + Kestrel).

**Authentication: RFC 9110 §11 framework + Basic + Bearer + mTLS** (Track D
auth slice, done 2026-07-18) — two independent layers, deliberately kept
separate:

- **The HTTP-layer framework** (`HTTPAuthentication.cs`, in `Core` next to
  `HTTPSemantics`): the generic RFC 9110 §11 challenge/credential plumbing.
  `HTTPAuthenticator` reads the `Authorization` header, dispatches to a matching
  registered `IHTTPAuthenticationScheme` (case-insensitive scheme match), and —
  when there are no credentials, the scheme is unsupported, or validation fails
  — answers `401` with one `WWW-Authenticate` challenge per registered scheme
  (§11.6.1 multiple-challenges). It never validates anything itself: each scheme
  decodes its credential form and defers the actual "valid?" decision to an
  app-supplied validator delegate, so `Core` stays BCL-only and free of any
  credential store (no password DB, no JWT library). Two concrete schemes ship:
  **Basic** (RFC 7617 — `base64(user:password)`, split on the *first* colon,
  `charset="UTF-8"` in the challenge) and **Bearer** (RFC 6750 — opaque token
  handed straight to the validator). Two schemes rather than one deliberately —
  it proves the abstraction is genuinely scheme-agnostic, not Basic-hardwired.
  `HTTPAuthentication.RequireAuthentication(authenticator, innerHandler)` wraps
  an identity-aware handler into an ordinary `HTTP2RequestHandler` (401 on
  failure, otherwise pass through with the `HTTPAuthenticatedIdentity`), so it
  composes in front of the server or an `HTTPSemantics`-wrapped handler
  unchanged. Demo: a `/secret` route accepting Basic `alice:secret` or Bearer
  `valid-token-123`.
- **mTLS (mutual TLS) — transport layer**, orthogonal to the above. `HTTP2Server`
  gained optional `RequireClientCertificate` + a `ValidateClientCertificate`
  callback (wired into `SslServerAuthenticationOptions.ClientCertificateRequired`
  / `RemoteCertificateValidationCallback`); a client presenting no/invalid cert
  is rejected at the TLS handshake, before any HTTP/2 frame. The validated
  client certificate is surfaced to request handlers as a synthetic,
  server-injected `x-client-cert-subject` header (like a reverse proxy's
  `X-Forwarded-*` — not something the peer sent). `HTTP2Client.ConnectAsync`
  gained an optional `ClientCertificate` to present, so the client is a
  first-class mTLS peer too. mTLS is *not* part of the §11 framework — it's a
  separate authentication mechanism at a lower layer — but the two combine
  (require a client cert *and* a bearer token).

Verified (`h2authtest`, 18/18): the auth framework driven by both .NET
`HttpClient` and our own client — no credentials → 401 advertising Basic +
Bearer; Basic `alice:secret` → 200 with the identity surfaced; wrong/malformed
Basic → 401; Bearer valid → 200, invalid → 401; an unsupported scheme (Digest)
→ 401. mTLS: `HttpClient`/our-client *with* a client cert → 200 with the cert
subject (`CN=test-client`) surfaced, *without* a cert → TLS handshake rejected
(both clients). (curl on this box is a Schannel build with no HTTP/2 — the
documented limitation — so the strict-client legs stand in for it; an
nghttp2-backed curl would additionally cover `curl -u`.) Full regression
(h2semantics 51/51, h2clienttest 14/14) still green — the mTLS additions are
opt-in and the client-cert header injection only fires on an mTLS connection,
so non-mTLS behavior is unchanged.

**RFC 9111 HTTP caching — client-side cache + shared-cache semantics** (Track D
caching slice, done 2026-07-18) — the last unimplemented Track-D piece, and the
client/consumer counterpart of the server-side conditional-request handling in
`HTTPSemantics`: a cache is precisely what *generates* the
If-None-Match/If-Modified-Since revalidations that `HTTPSemantics` answers.
Split the same way as the earlier RFC 9110 work — direction-neutral *logic* in
`Core`, wiring in the role:

- **`HTTPCaching.cs` (Core):** the reusable caching brain, no store or transport.
  `HTTPCacheControl.Parse` handles the full `Cache-Control` grammar (request +
  response directives: no-store/no-cache/private/public/max-age/s-maxage/
  must-revalidate/immutable/only-if-cached/max-stale/min-fresh, plus RFC 5861
  stale-while-revalidate/stale-if-error). `HTTPCache` computes current age (the
  §4.2.3 apparent-age/corrected-age arithmetic), freshness lifetime
  (s-maxage→max-age→Expires→heuristic 10%-of-Last-Modified, §4.2.1/4.2.2),
  storability (§3, including the cacheable-status set and the shared-cache
  `private`/authenticated-request rules of §3.5), the fresh/stale/revalidate
  decision (`Evaluate`, weaving request+response directives together),
  conditional revalidation headers + the §3.2 304-merge, and `Vary` keying
  (§4.1). An `HTTPCacheMode` (Private/Shared) drives the mode-specific behavior.
- **`HTTP2CachingClient.cs` (Client):** an RFC 9111 cache in front of a
  single-origin `HTTP2ClientConnection` — the store (keyed by `:path`, with
  per-path `Vary`-selected variants) plus the "when do I go to the origin?"
  flow. Serves fresh hits with no round trip (stamping the computed `Age`),
  revalidates stale entries with a conditional request (304 → refresh + serve
  cached; 200 → replace), serves stale within `max-stale`/stale-while-revalidate
  (the latter fires a background refresh), returns `504` for `only-if-cached`
  misses (§5.2.1.7), and invalidates a path on a successful unsafe method
  (§4.4). Exposes `Hits`/`Misses`/`Revalidations` counters.

Verified (`h2cachetest`, 23/23) against an origin server that counts per-path
fetches (so a cache hit is *provably* a no-origin-round-trip) and answers
conditionals with 304: `max-age` (MISS then HIT, second served fresh with an
`Age` header); `max-age=0` revalidation (origin contacted with a conditional,
304, body served from cache); `no-store` (fetched every time); `Vary` (en/de
variants cached and served independently); heuristic freshness from
`Last-Modified`; `only-if-cached` with nothing stored → 504; invalidation
(cached, then a POST re-opens the origin); stale-while-revalidate (stale served
immediately, origin refreshed in the background); and the shared/private split —
a `private` response stored by a Private but not a Shared cache, `s-maxage`
honored by Shared but ignored by Private, and an authenticated response not
stored by a Shared cache (§3.5). Full regression (h2semantics 51/51,
h2clienttest 14/14, h2authtest 18/18) still green — the caching code is purely
additive (new Core + Client files, no changes to existing code).

**Client-side RFC 9218 priority signaling** (post-roadmap polish, done
2026-07-18) — the mirror of the Track C server-side work: the server already
*acts on* priority (its writer loop schedules response DATA); this lets the
**client** *emit* the signals. Three pieces:

- **The `priority` request header, typed.** `SendRequestAsync` (and the new
  `StartRequestAsync`) take an optional `HTTP2Priority`, encoded onto the
  request via `HTTP2Priority.ToHeaderValue()` (the RFC 9218 §4 / RFC 8941
  `u=<n>, i` grammar — the exact string the server's `ParsePriority` reads).
  Skipped if the caller already put a `priority` in `ExtraHeaders`.
- **`PRIORITY_UPDATE`, for mid-flight reprioritization.** New
  `HTTP2Frame.CreatePriorityUpdate(streamId, value)` factory (type `0x10`,
  stream 0, payload = 31-bit target stream ID + ASCII field value) and
  `HTTP2ClientConnection.UpdatePriorityAsync(streamId, priority)`. To get the
  stream ID of an in-flight request, `SendRequestAsync` was refactored to
  return via a new `StartRequestAsync` that hands back an `HTTP2RequestHandle`
  (`StreamId` + the `Response` task) the moment HEADERS are on the wire —
  `SendRequestAsync` is now the thin `await (await StartRequestAsync(...)).Response`
  wrapper. So a caller can start a request, grab its stream ID, and promote it
  while awaiting the response.
- **`SETTINGS_NO_RFC7540_PRIORITIES=1`** now advertised in the client preface
  too (the server already did), accurate since the client only uses the modern
  scheme.

Verified (`h2clientpriority`, 15/15): the `CreatePriorityUpdate` factory
round-trips (type/stream-0/target-ID/value bytes) and `ToHeaderValue()` encodes
`u=0, i` / `u=5`; against our own server the typed `Priority` lands as the exact
`priority` header value the server receives (`u=0, i`, `u=5`, and absent when
unset), `StartRequestAsync` exposes the odd stream ID, and a mid-flight
`UpdatePriorityAsync` completes the request 200 with the connection staying
healthy afterward (a malformed PRIORITY_UPDATE would have drawn a GOAWAY);
against **.NET Kestrel**, a priority-hinted request and a client
`PRIORITY_UPDATE` are both accepted (200, connection healthy) — the signals
interoperate with a production server. The server *acting on* these signals
(reordering) stays covered by `h2priority`; this test covers correct *emission*.
Full regression (h2semantics 51/51, h2clienttest 14/14, h2authtest 18/18,
h2cachetest 23/23, h2priority) still green after the `SendRequestAsync` refactor.

**Test suite moved into the repo + h2spec conformance baseline** (done
2026-07-18): every harness that had lived only in the session scratchpad is now
committed under `tests/` (13 projects, added to `HTTP2.slnx`, `ProjectReference`
paths relativized), plus a `tests/run-tests.ps1` runner and `tests/README.md`.
The runner builds the solution, runs the self-contained harnesses (which spin up
their own server/Kestrel on private ports 9443–9449), then starts the Demo host
on `:8443` once and drives the demo-dependent raw-frame harnesses against it
(one process per scenario, scanning stdout for the harness's own ✗ marks), and
prints a pass/fail summary. Baseline: **55/55 harness runs pass** (h2semantics
51/51, h2clienttest 14/14, h2authtest 18/18, h2cachetest 23/23,
h2clientpriority 15/15, plus the h2attack/h2connect/h2priority scenario
matrices and the huffman/stream/shutdown unit harnesses). Two PS-5.1 gotchas
fixed in the runner: the script needs a UTF-8 BOM (else 5.1 mis-decodes the
non-ASCII ✓/✗ literals as Windows-1252 and fails to parse), and
`$ErrorActionPreference` must be `Continue` not `Stop` (the harnesses log to
stderr on normal shutdown — a GOAWAY notice — which 5.1 otherwise promotes to a
terminating `NativeCommandError`).

Then ran [h2spec](https://github.com/summerwind/h2spec) 2.6.0 (the canonical
RFC 9113 + RFC 7541 conformance suite — an external binary, kept in the
scratchpad, not vendored) against the Demo host over TLS
(`h2spec -t -k -h localhost -p 8443 -P /echo`): initial score **136 / 146**.

**h2spec conformance track — 146/146** (done 2026-07-18): closed all 10 initial
failures, in six groups. All server-side (`HTTP2Connection.cs`) except the HPACK
ones, which are in `Core/HPACK.cs` and so benefit the client's decoder too:

1. **PRIORITY-frame envelope validation (§6.3) — 2 tests.** We still don't *act*
   on deprecated RFC 7540 priority, but the frame is no longer blindly ignored:
   `HandlePriority` now rejects a PRIORITY frame on stream 0 (connection
   `PROTOCOL_ERROR`) and one whose length ≠ 5 octets (stream `FRAME_SIZE_ERROR`),
   and counts a well-formed one against the unproductive-frame flood budget.
2. **HPACK dynamic-table-size-update bounds (§4.2 / §6.3) — 2 tests.** The
   decoder now tracks whether a header field has been emitted and rejects a size
   update that appears anywhere but the *start* of a header block (§4.2), and
   rejects one exceeding the advertised `SETTINGS_HEADER_TABLE_SIZE` (new
   `HPACKDecoder.HeaderTableSizeLimit`, default 4096) — both `COMPRESSION_ERROR`.
3. **content-length vs. DATA length (§8.1.2.6) — 2 tests.** `CompleteHeaders`
   parses `content-length` into `HTTP2Stream.ExpectedContentLength` (a malformed
   or self-conflicting value is itself a stream `PROTOCOL_ERROR`); the summed
   DATA payload length is checked against it at END_STREAM (and the no-body case
   rejects a non-zero declared length). CONNECT tunnels are exempt (no body
   semantics).
4. **HEADERS on a closed stream (§5.1) — 1 test.** The nuance the note flagged:
   a HEADERS after the peer's clean END_STREAM is now a *connection*
   `STREAM_CLOSED`, but one after an RST_STREAM close stays a *stream* error (a
   peer racing frames past a reset it hasn't seen). Distinguished by a new
   `HTTP2Stream.WasReset` flag (set in `Reset()`), so both the RFC's cases are
   honored rather than collapsing to one.
5. **Inbound PUSH_PROMISE from a client (§8.2) — 1 test.** A dispatch-loop case
   now rejects it with a connection `PROTOCOL_ERROR` (only servers push, and we
   advertise `ENABLE_PUSH=0`) instead of falling through to the ignore-unknown
   default and timing out.
6. **RFC 7540 self-dependency (§5.3.1) — 2 tests.** Originally called
   out-of-scope, but closed anyway since it's cheap and structurally correct: a
   HEADERS-with-PRIORITY or a PRIORITY frame whose stream dependency equals its
   own stream ID is now a stream `PROTOCOL_ERROR`. This validates the *frame*
   (a stream genuinely can't depend on itself) without walking back the
   `NO_RFC7540_PRIORITIES=1` stance — we still ignore the priority *semantics*,
   we just reject a structurally-invalid frame, same spirit as the §6.3 envelope
   checks.

Verified: h2spec re-run → **146 tests, 146 passed, 0 skipped, 0 failed.** Full
regression (`tests/run-tests.ps1`) still **55/55** — the content-length check
doesn't disturb `HttpClient`'s well-formed POST /echo (declared length matches
body), and the closed-stream/PRIORITY changes leave every existing
attack/trailers/idle scenario green.

**Full HPACK encoder — static + dynamic table + Huffman** (done 2026-07-18):
the encoder was the last half-implemented piece of RFC 7541 (the decoder has
long been complete). Previously it knew only 15 of the 61 static-table entries,
sent every literal with incremental indexing while *never* referencing the
dynamic table it thereby built (pure overhead for both peers), and never
Huffman-coded a string. All three are now real, entirely in `Core/HPACK.cs`
(both roles benefit — server responses and client requests share the encoder):

- **Full static table.** The 61-entry table is no longer duplicated: the
  decoder's `StaticTable` is now `internal`, and the encoder builds its
  exact-`(name,value)` and name-only reverse indices from it — so common fields
  (`content-type`, `date`, `etag`, `server`, …) get a proper static reference
  instead of a full literal.
- **Per-connection dynamic table.** The encoder keeps its own dynamic table,
  mirroring `HPACKDecoder.AddToDynamicTable` byte-for-byte (same 32-byte
  per-entry overhead, same eviction), so a repeated header field collapses to a
  one-byte index on reuse (measured: a repeated `x-custom-header` went 28 B → 1
  B). A small denylist keeps volatile values (`:path`, `date`, `etag`,
  `content-length`, …) out of the table (indexing them just churns it) via
  "literal without indexing", and sensitive fields (`authorization`, `cookie`,
  …) use "literal never indexed" (RFC 7541 §7.1.3) so their values never enter
  a compression-based side channel.
- **Huffman encoding.** New `HuffmanEncoder` (mirror of the existing
  `HuffmanDecoder`, encoding from the same now-`internal` canonical 257-entry
  table — MSB-first, final byte padded with 1-bits per §5.2); string literals
  use it whenever it's strictly shorter than raw, chosen per-string via
  `EncodedByteLength` without double-encoding.
- **Table-size signaling.** `SetMaxDynamicTableSize` (wired from the peer's
  `SETTINGS_HEADER_TABLE_SIZE` on both roles) bounds our table to what the
  peer's decoder will keep, evicting and queuing a dynamic table size update
  (emitted at the next block start, §6.3) — so a peer advertising a smaller (or
  zero) table never desyncs us.

**The ordering constraint this introduced, and the fix:** a stateful encoder
means the order header blocks are *encoded* MUST equal the order they hit the
wire (the decoder replays that order against its shared dynamic table). The
server previously HPACK-encoded a response *outside* the write lock (fine for a
stateless encoder) while multiple response tasks run concurrently — two could
encode in one order and write in another, desyncing the peer. Fixed by folding
the encode into the write-locked section: the former `SendHeaderBlockAsync`
(byte-block in) was split into a pure `BuildHeaderFrames` helper and a new
`SendHeaderListAsync` that takes the write lock, *then* encodes and writes as
one unit. The client was already correct here — it encodes + writes HEADERS
atomically under its existing `requestStartLock`.

Verified: a new `h2hpackenc` unit harness (21/21) — 5000-round Huffman
encode→decode round trip, exact static index (`:method GET` → the single byte
`0x82`), dynamic-table reuse collapsing to one byte, multi-block encoder/decoder
sync across a connection, the never-index/no-index policy (authorization and
content-length stay literal on repeat), size-update signaling on shrink-to-0
and grow-back, and a realistic 8-field request compressing 174 B → 69 B — all
round-trip through our own decoder. The real interop proof is the existing
suite, now exercising the new encoder against *production* decoders: h2clienttest
(our client's Huffman/dynamic-table requests decoded by **Kestrel**, 14/14),
h2semantics + h2authtest (our server's responses decoded by **.NET
`HttpClient`**, 51/51 + 18/18). Full regression **56/56** (h2hpackenc added) and
h2spec still **146/146** — the changed response encoding decodes cleanly across
the whole conformance suite.

**Slowloris / timeout hardening** (done 2026-07-18): the flood defenses stop a
peer sending *too much*; these stop a peer sending *too little* — trickling or
withholding bytes to tie up a connection cheaply, the Slowloris class. All
driven by a new `HTTP2Timeouts` record (`Handshake`/`Preface`/`SettingsAck`/
`Idle`/`InProgress`, generous defaults 10/10/10/120/10 s), injectable via the
`HTTP2Server` and `HTTP2Connection` constructors so tests can use short values:

- **TLS handshake timeout** (`HTTP2Server`). A client that opens the TCP
  connection but stalls the TLS handshake is dropped after `Handshake` — the
  handshake at the very edge, before any HTTP/2 exists, was previously
  unbounded.
- **Read-based frame timeouts** (`HTTP2Connection`). `ReadExactAsync` now takes
  a *whole-operation* deadline (a single `CancelAfter` spanning the entire
  read, so a byte-at-a-time trickle can't reset it) plus the error code to
  raise. Two tiers: the **idle** timeout (generous `Idle`, `NO_ERROR` — a clean
  close of a genuinely abandoned but reused-eligible connection) applies while
  waiting for the *next* frame to begin; the tight **in-progress** timeout
  (`InProgress`, `ENHANCE_YOUR_CALM`) applies to a frame's payload once its
  header has arrived, and to the wait for the *next* frame while a header block
  is mid-flight (CONTINUATION pending) — i.e. a HEADERS-without-END_HEADERS
  followed by silence, or a frame header whose payload never arrives, both now
  abort promptly. The `Preface` timeout bounds the magic string + first
  SETTINGS after TLS.
- **SETTINGS ACK timeout** (RFC 9113 §6.5.3). A background
  `EnforceSettingsAckTimeoutAsync` races the peer's ACK of our SETTINGS against
  `SettingsAck`; on timeout it sends `GOAWAY SETTINGS_TIMEOUT` and cancels. It's
  a deliberately **write-only** GOAWAY (unlike `SendGoAwayAsync`, which also
  drains inbound): the frame read loop is still the sole reader, and `SslStream`
  forbids two concurrent reads, so draining here would race it — the same
  reasoning `InitiateGracefulShutdownAsync` already documents. This is a "MAY"
  in the spec (hence the generous 10 s default): our own raw attack harnesses
  legitimately never ACK, and the 10 s window is well clear of every test's
  runtime.

Verified with a new `h2timeout` harness (7/7) driving a server configured with
short (1–2 s) timeouts: a stalled preface, a HEADERS-without-END_HEADERS then
silence, a frame header whose 100-byte payload never arrives, and a raw TCP
connection that never starts TLS are each reclaimed within their respective
window (with the expected `ENHANCE_YOUR_CALM` where applicable); withholding the
SETTINGS ACK draws a `GOAWAY SETTINGS_TIMEOUT`; and a normal GET returns 200
both before and after the abuse (short timeouts don't harm legitimate traffic).
Full regression **57/57** — notably every `h2attack` scenario still passes
despite those harnesses never ACKing our SETTINGS (they all finish well inside
the 10 s default) — and h2spec still **146/146** (all its timeouts are shorter
than our 10 s windows, so the hardening never interferes). The client
(`HTTP2ClientConnection`) is deliberately unchanged — Slowloris is a server-side
concern; client-side "server went silent" timeouts belong with the separate
client-robustness work.

**Client robustness** (done 2026-07-18): the client was functional but naive —
it now recovers from the failure modes a real client meets. All driven by a new
`HTTP2ClientOptions` record threaded through `HTTP2Client.ConnectAsync`, plus a
shared-library `HTTP2RequestNotProcessedException` (in `Core`) marking a request
the peer provably never processed (safe to retry verbatim):

- **REFUSED_STREAM auto-retry (RFC 9113 §8.1).** A `RST_STREAM`/`REFUSED_STREAM`
  guarantees the server didn't process the request, so the client now re-issues
  it — verbatim, on a fresh stream of the *same* connection — up to
  `MaxRefusedStreamRetries` (default 2) before giving up with
  `HTTP2RequestNotProcessedException`. This required splitting the request-issue
  path: `StartRequestAsync` builds a `ClientExchange` that carries the full
  request definition (headers/body/token/retry count) and reuses one
  `Completion` TCS across attempts; the reusable `IssueOnNewStreamAsync` does the
  actual "allocate stream + send HEADERS (+ spawn body send)" and is called again
  by `RetryExchangeAsync` from the RST_STREAM handler. The handle's `StreamId`
  reflects the first attempt (a stale ID after a retry only affects a moot
  PRIORITY_UPDATE) — the priority feature's `StartRequestAsync`/`UpdatePriorityAsync`
  seam is otherwise unchanged.
- **GOAWAY retry-safety (§6.8).** Streams above a GOAWAY's Last-Stream-ID were
  definitely not processed; they now fail with the same
  `HTTP2RequestNotProcessedException` (was a generic `HTTP2ConnectionException`),
  so a caller or a future connection pool can re-issue them on a new connection
  without risking duplicate side effects.
- **MAX_CONCURRENT_STREAMS gating.** `CreateLocalStream` still *throws*
  `REFUSED_STREAM` at the limit as a backstop, but the client no longer reaches
  it: `IssueOnNewStreamAsync` first `WaitForStreamSlotAsync`es until fewer than
  the server's advertised limit are in flight (tracked as `exchanges.Count`,
  woken by a `streamSlotFreed` signal pulsed on every exchange removal). So
  firing more concurrent requests than the server allows now *queues* them
  instead of failing — verified with a server advertising
  `MAX_CONCURRENT_STREAMS=1` and three concurrent requests (the client never
  opened a second stream, all three completed).
- **PING keepalive / liveness.** Opt-in (`KeepAliveInterval`, default
  `Zero` = off): after that much inbound silence the client sends a PING with
  random opaque data and, if no matching ACK returns within `KeepAliveTimeout`,
  tears the connection down (failing in-flight requests) instead of hanging on a
  silently-dead socket. The read loop stamps `lastActivityTicks` on every frame;
  `HandlePingAsync` now also correlates an inbound PING ACK against the pending
  probe.
- **Client-side flood bounds.** The CONTINUATION-flood / oversized-header-block
  defenses (CVE-2024-27316 class) were server-only; the client now mirrors them
  against a hostile/buggy *server* — a per-block CONTINUATION cap
  (`MaxContinuationFrames`, default 64) and a running header-buffer bound against
  our advertised `MAX_HEADER_LIST_SIZE`.

Deliberately scoped to a *single* connection: auto-retry and gating act within
the existing connection; GOAWAY/exhaustion across connections is surfaced (via
the retry-safe exception) for a connection pool to act on, but no pool is built
here. Verified with a new `h2clientrobust` harness (8/8) driving the client
against a purpose-built raw HTTP/2 *mock server* that misbehaves on cue: it
refuses stream 1 and the client transparently succeeds on the retried stream 3;
persistent refusal and a GOAWAY each surface `HTTP2RequestNotProcessedException`;
`MAX_CONCURRENT_STREAMS=1` holds the client to one open stream across three
concurrent requests; and a silent server is detected by keepalive in ~1 s rather
than hanging. Full regression **58/58** — critically, the request-path
restructure left `h2clienttest` (14/14, our server *and* Kestrel),
`h2clientpriority` (15/15), and `h2cachetest` (23/23, the caching wrapper over
the client) all green — and h2spec still **146/146** (the server is untouched
beyond a new `HTTP2Frame.CreatePing` factory).

**Streaming bodies + response trailers** (done 2026-07-18): the biggest
architectural gap closed — the `HTTP2RequestHandler` seam was fully buffered
(whole request body in, whole response body out), which rules out
server-streaming, SSE, large up/downloads without O(body) memory, and — the
headline — **bidirectional streaming (gRPC)**, whose `grpc-status` lives in
response *trailers* the buffered seam couldn't emit. A new streaming seam sits
alongside the buffered one (the buffered path is untouched and still the default):

- **The seam (Core, `HTTP2Streaming.cs`).** `HTTP2StreamingHandler` receives an
  `IHTTP2RequestStream` (request headers; `ReadAsync` pulls body chunks as DATA
  arrives, returning null at END_STREAM; then `Trailers`) and an
  `IHTTP2ResponseStream` (`WriteHeadersAsync` once, then any number of
  `WriteAsync` body chunks, then `CompleteAsync(trailers?)`). Both directions
  flow concurrently — the handler is dispatched at **HEADERS-complete**, not at
  END_STREAM, so it can read request chunks and write response chunks at the
  same time (true bidi). Returning without completing auto-completes; throwing
  before headers falls back to 500, otherwise resets.
- **Server plumbing.** `HTTP2Connection`/`HTTP2Server` take an optional
  `HTTP2StreamingHandler`; when registered, non-CONNECT requests take the
  streaming path — `CompleteHeaders` sets up an unbounded `RequestBodyChannel`
  and dispatches immediately (`StartStreamingHandler`), and `HandleDataAsync`
  routes DATA into that channel (tracking `ReceivedBodyLength` for the §8.1.2.6
  content-length check) instead of buffering into `RequestBody`. This
  generalizes the CONNECT tunnel's inbound-channel pattern to ordinary requests.
  The impls (`HTTP2RequestStream`/`HTTP2ResponseStream`, `HTTP2StreamAdapters.cs`)
  reuse the existing HEADERS/DATA machinery, so a *streamed* response is
  byte-identical on the wire to a buffered one — flow-controlled and prioritized
  through the same writer loop.
- **Response trailers (RFC 9113 §8.1).** The per-stream `HTTP2OutboundQueue`
  now carries optional trailers on its end-of-stream item; the writer loop, on
  reaching it, sends the last DATA frame *without* END_STREAM and then the
  trailing HEADERS block *with* END_STREAM (HPACK-encoded under the write lock,
  so encode-order == wire-order holds). Trailers are validated (`ValidateOutboundTrailers`:
  no pseudo-headers, lowercase names). Inbound request trailers, already parsed
  into `HTTP2Stream.Trailers`, are now finally consumed — surfaced to the
  streaming handler via `IHTTP2RequestStream.Trailers` (the buffered seam still
  can't expose them, by design).

Deliberately additive: with no streaming handler registered the connection is
byte-for-byte the old buffered server, so every existing test and the whole
h2spec run are unaffected. The demo stays buffered; streaming is exercised in
its own harness. Verified (`h2streaming`, 8/8) against **both** roles: our own
client (server-streaming response assembles correctly; a streamed request body
is echoed; response trailers `grpc-status=0`/`x-checksum` are exposed via
`HTTP2Response.Trailers`; a 200 KB streamed request body is counted correctly,
exercising flow control on the streaming *input* path) and — the real interop
proof — **.NET `HttpClient`** (reads a server-streamed response incrementally;
exposes the response trailers via `HttpResponseMessage.TrailingHeaders`, i.e.
gRPC-style trailers work against a production client; and a chunked,
unknown-length streamed request body is echoed). Full regression **59/59** (the
buffered demo path — h2semantics 51/51, all attack/connect/priority scenarios —
untouched) and h2spec still **146/146**.

**WebSocket / CONNECT-capable client** (done 2026-07-18) — the mirror of the
server's Track B tunneling, so both ends of a CONNECT tunnel (and a WebSocket
over it) are now hand-rolled. Two parts:

- **`WebSocketConnection` is now direction-aware** (`WebSocketRole`, default
  `Server` so the existing server usage is untouched). A **client** masks every
  frame it sends with a random 4-byte key (RFC 6455 §5.3) and requires the
  frames it receives to be *unmasked*; a **server** does the exact opposite
  (§5.1). Masking direction is the only thing that differs — opcodes,
  fragmentation reassembly, ping→pong, and the close handshake are shared. The
  `SendFrameAsync`/`ReadRawFrameAsync` paths branch on the role.
- **Client-side CONNECT** (`HTTP2ClientConnection`): `OpenTunnelAsync` sends a
  CONNECT (plain: `:method`+`:authority`; extended per RFC 8441:
  `+:protocol`+`:scheme`+`:path`) as a HEADERS block *without* END_STREAM —
  keeping the request side open — and awaits the response `:status`; a 2xx
  marks the stream a tunnel (`IsConnectTunnel`, reusing the shared
  `HTTP2Stream.TunnelInbound` channel the server already had) and returns an
  `HTTP2ClientTunnel : IHTTP2Tunnel` (read the inbound DATA channel; write
  flow-controlled DATA that never sets END_STREAM; `CloseAsync` sends the
  zero-length END_STREAM). The read loop routes tunnel-stream DATA into the
  channel (`HandleDataAsync`) exactly as the server does. `OpenWebSocketAsync`
  is the thin convenience: extended CONNECT with `:protocol=websocket`,
  returning a client-role `WebSocketConnection` over the tunnel. A tunnel
  exchange is never auto-retried and doesn't use the buffered-response
  `Completion` — a `ClientExchange.IsTunnel`/`TunnelStatus` pair carries the
  accept/reject signal instead.

Reuses the shared, transport-agnostic seam unchanged: `IHTTP2Tunnel` +
`WebSocketConnection` (Core) work over the client's `HTTP2ClientTunnel` with no
duplication of the framing layer — exactly what the `IHTTP2Tunnel` note
anticipated ("a client-side tunnel could implement the same interface and reuse
the framing unchanged"). Verified (`h2wsclient`, 10/10) against **our own
server** (which implements the server side of CONNECT / extended CONNECT / RFC
6455): plain-CONNECT byte loopback (three round trips incl. a UTF-8/emoji
payload); a full WebSocket session — text, binary, a 1000-byte message
(exercising the 16-bit length path *and* client masking), and a client-initiated
close handshake (`ReceiveAsync` → null after the server echoes the close); and a
rejected extended CONNECT to an unknown path surfacing as an exception with the
connection still usable for an ordinary request afterward. Full regression
**60/60** — the server-side WebSocket scenarios (`h2connect` ws-echo/
fragmented/ping/close) still pass with the role parameter defaulting to
`Server` (byte-identical behavior) — and h2spec still **146/146**.

**WINDOW_UPDATE batching + larger flow-control windows** (done 2026-07-18) — a
throughput/efficiency pass on the receive side, symmetric on both roles. Two
changes:

- **Batched replenish.** Both `HandleDataAsync` paths used to emit *two*
  WINDOW_UPDATEs (stream + connection) for *every* DATA frame, immediately
  returning what was consumed. Now a new `ReplenishReceiveWindowsAsync`
  accumulates consumed bytes per-stream (`HTTP2Stream.PendingRecvUpdate`) and
  connection-wide (`connectionPendingRecvUpdate`) and emits a WINDOW_UPDATE only
  once the accumulated amount crosses **half** the respective window — so a
  transfer smaller than half the window sends *none at all*, and a large one
  sends O(size / halfWindow) instead of O(DATA-frames). The server still
  decrements + enforces its receive window (a peer exceeding it is a
  `FLOW_CONTROL_ERROR`); the client, which never tracked a receive window, just
  batches the emission.
- **Larger windows.** `HTTP2Settings.InitialWindowSize` is now **1 MiB** (was
  the RFC-default 65535), advertised in both roles' SETTINGS, with the stream
  manager's `LocalInitialWindowSize` kept in sync so new streams' receive
  windows match. Since `SETTINGS_INITIAL_WINDOW_SIZE` only governs *stream*
  windows, each role also raises its *connection* receive window to 1 MiB at
  startup with a single initial `WINDOW_UPDATE(0, …)` right after its SETTINGS
  (the connection window otherwise starts at the fixed 65535). Combined with
  batching, a large multiplexed transfer now flows with only a handful of
  flow-control frames instead of one per DATA frame.

Deliberately receive-side only: this changes how *we* return window to the peer,
not how we schedule our own sends (that's the RFC 9218 writer loop, untouched) —
so the send-side behavior the priority tests rely on (the ~64 KiB burst against
a raw client that advertises the default window) is unaffected. Verified with a
new `h2flowbatch` harness (4/4): a raw client uploads 800 KiB as 50×16 KiB DATA
frames and the server answers with just **2** WINDOW_UPDATEs (vs. ~100 under the
old two-per-frame strategy) while the upload still succeeds (200), and the
server's startup connection-window bump (`+983041` = 1 MiB − 65535) is observed.
Full regression **61/61** — the large-transfer paths (`h2semantics` /large,
`h2clienttest` /large + 4× concurrent /large, `h2streaming`'s 200 KB streamed
body) all still pass, confirming the deeper windows and batched replenish don't
stall or miscount — and h2spec still **146/146** (its flow-control §6.9 tests
pass with the larger windows and the initial connection-window WINDOW_UPDATE).

**On-the-fly content coding (gzip / brotli / deflate)** (done 2026-07-18) —
closes the "we negotiate among pre-provided variants but don't compress on
demand" gap the Track-D content-negotiation note documented. Entirely in
`Core/HTTPSemantics.cs` (BCL-only, `System.IO.Compression`); the framing layer
is untouched, as ever:

- **Opt-in** via a new `CompressResponses` flag on both `HTTPSemantics.Wrap`
  overloads (default off — it's a transport optimization, and it never fires
  unless the client positively lists a compression coding). Applied as a pure
  **post-processing step** after the negotiate → conditional → Range pipeline,
  so that pipeline is entirely unaffected: it always runs on the identity
  representation, and only the final full-200 body is (maybe) compressed.
- **Coding selection** (§12.5.3): among the codings we can produce
  (`br > gzip > deflate`, server preference), the highest positive-q one the
  request's `Accept-Encoding` accepts wins; absent/none → identity (never
  compress a client that didn't ask). Skipped for a non-200, a HEAD/304/206/
  bodiless response, a body under 256 B, a non-compressible media type (only
  `text/*` and text-like structured types like `application/json`), an
  already-`content-encoding`'d response (an app-provided pre-encoded variant —
  don't double-encode), or when compression didn't actually shrink it.
- **Correctness details.** The `ETag` is **weakened** (`"abc"` → `W/"abc"`,
  §8.8.1) when we compress — the bytes now differ from the identity
  representation the strong tag named, but they're semantically equivalent,
  which is exactly what a weak validator means and keeps conditional
  revalidation (weak comparison, already implemented) correct across the
  identity and compressed variants of the same URL. `content-encoding` is set,
  `content-length` updated to the compressed size, and `accept-encoding` merged
  into `Vary` so a cache keys on it.

Verified (`h2compress`, 11/11) against **both** roles: our own client — with
exact control over `Accept-Encoding` — confirms gzip (630 B → 74 B, decodes
back to the original), brotli, the `br>gzip>deflate` preference (deflate+gzip
offered → gzip), identity fallback when `Accept-Encoding` is absent or forbids
our codings (`gzip;q=0`), the tiny-body skip, the weak `ETag`, `Vary:
accept-encoding`, and a conditional revalidation against the weak ETag → 304;
and **.NET `HttpClient`** with `AutomaticDecompression` transparently sends
`Accept-Encoding`, receives the compressed response, and decompresses it back to
the original body — the production-client interop proof. Deliberately opt-in and
not enabled on the demo, so full regression **62/62** (h2semantics 51/51
unaffected) and h2spec **146/146** are untouched.

**1xx interim responses — `Expect: 100-continue` + 103 Early Hints** (done
2026-07-18): a stream can now carry one or more interim (1xx) HEADERS blocks
before the final response (RFC 9110 §15.2 / RFC 8297), on both roles:

- **Automatic `100 Continue` (server, RFC 9110 §10.1.1).** A client that sends a
  body but wants to be told to proceed first sets `Expect: 100-continue` and
  waits before sending DATA. `CompleteHeaders` now sends an interim `:status 100`
  (a HEADERS block without END_STREAM) as soon as the initial headers of a
  body-bearing request with that expectation arrive; the final response follows
  normally after the body. We always accept, so an unsupported expectation is
  just ignored and the request processed (the §10.1.1 "MAY 417" is declined).
  This required threading `async` through `HandleHeaders`/`HandleContinuation`/
  `CompleteHeaders` (previously sync) so the interim send can be awaited from the
  read loop — a mechanical change verified by the whole attack/idle/trailers
  suite still passing unchanged.
- **103 Early Hints (server, handler-driven, RFC 8297).** A new
  `IHTTP2ResponseStream.WriteInterimResponseAsync(status, headers)` on the
  streaming seam lets a handler emit any number of 1xx responses (e.g. a 103
  with `Link: rel=preload` hints) before `WriteHeadersAsync` — each a HEADERS
  block that doesn't end the stream. Validates the 1xx range and that the final
  headers haven't gone out yet; reuses the same write-locked `SendHeaderListAsync`
  as every other header block, so encode-order == wire-order holds across the
  interim + final blocks. (The buffered seam can't express interim responses by
  design — it's the streaming seam's job, same as trailers.)
- **Client (RFC 9110 §15.2).** `CompleteHeaderBlock` now recognizes a 1xx
  response as *interim*: it records it and keeps waiting for the final response,
  instead of mistaking the first HEADERS for the final one (which would then
  have treated the real final response as trailers). Collected interim responses
  are surfaced on `HTTP2Response.InformationalResponses` (status + headers, in
  order), so a caller can read a 103's `Link` hints or confirm a 100.

Verified (`h2interim`, 7/7): our own client — a POST with `expect: 100-continue`
gets an interim 100 surfaced in `InformationalResponses` and the body echoed
(and a request *without* the header gets no 100); a streaming handler's 103
Early Hints (two `Link` preload headers) arrives before the final 200 and is
surfaced with its hints intact. And **.NET `HttpClient`** with
`ExpectContinue = true` completes the 100-continue handshake and round-trips a
POST — the production-client interop proof. Full regression **63/63** (the async
`HandleHeaders`/`CompleteHeaders` refactor left every existing request/attack
path unchanged) and h2spec still **146/146**.

**h2c — cleartext HTTP/2 with prior knowledge** (done 2026-07-18): HTTP/2 over
plain TCP with no TLS and no ALPN — the client is expected to have prior
knowledge and sends the connection preface directly (RFC 9113 §3.3). The RFC
7540 `Upgrade: h2c` negotiation was **removed** in RFC 9113 §3.1 and is
deliberately not implemented — only prior-knowledge. Chiefly useful behind a
TLS-terminating proxy, on a trusted internal hop, or for local
tooling/testing. The whole change hangs off one observation: `HTTP2Connection`
and `HTTP2ClientConnection` only ever use the `Stream` base API
(`ReadAsync`/`WriteAsync`/`FlushAsync`), so nothing below the transport had to
change:

- **Transport generalization.** Both connections' transport field was retyped
  `SslStream` → `Stream` (renamed `sslStream` → `transportStream`) and their
  constructors now take a `Stream`. TLS passes an `SslStream` exactly as
  before; h2c passes the raw `NetworkStream`. No other connection code changed
  — framing, HPACK, flow control, the writer loop, streaming, tunnels all run
  identically over either transport.
- **Server.** `HTTP2Server` gained a `Cleartext` flag (and its `Certificate`
  parameter became nullable, guarded: a non-cleartext server still requires a
  cert). When set, `HandleConnectionAsync` skips the `SslStream`/ALPN branch
  entirely and runs the connection straight over the `NetworkStream` (mTLS is
  unavailable — there's no TLS layer). The TLS and cleartext paths share a new
  `RunConnectionAsync` helper (connection construction + `activeConnections`
  tracking for graceful shutdown), so both get GOAWAY-on-stop for free.
- **Client.** `HTTP2Client.ConnectAsync` gained a `Cleartext` flag: it opens a
  plain TCP connection and hands `tcp.GetStream()` straight to
  `HTTP2ClientConnection` (the TLS-only `ValidateServerCertificate` /
  `ClientCertificate` params are ignored in this mode). Cleartext requests use
  `:scheme = http`.
- **Demo.** Now also serves cleartext h2c on port 8080 (both loopbacks,
  `Certificate: null, Cleartext: true`), alongside the TLS listener on 8443 —
  so the demo itself demonstrates h2c and tools like curl
  (`--http2-prior-knowledge`) and h2spec can drive it without TLS.

**A pre-existing HPACK robustness bug found and fixed along the way** (in
`Core/HPACK.cs`, unrelated to h2c but surfaced by running h2spec against the
new cleartext listener with the demo's log actually drained): a truncated
header block — one that ends exactly where an integer prefix byte or a string
literal's length byte is expected — made `DecodeInteger`/`DecodeString`
dereference `Data[Offset]` one past the end and throw an unhandled
`IndexOutOfRangeException` (caught only by the connection's catch-all, so it
was logged as an "Unexpected error" and closed the connection with the wrong
signal). Both now bounds-check first and raise a proper connection
`COMPRESSION_ERROR`, the RFC-correct response to a truncated block. Being in
`Core`, the fix hardens the client's decoder too. (This was latent behind the
same class of malformed input h2spec exercises; TLS h2spec had been hitting it
all along, swallowed by the catch-all — it never failed a conformance test,
just produced the wrong error type and log line.)

**Test-run gotcha, not a bug:** driving h2spec against the demo while its
stdout/stderr is *not* being drained (e.g. `Start-Process -WindowStyle Hidden`
with no redirect) can deadlock the demo — h2spec's flood of malformed cases
makes the demo log thousands of lines, the OS console pipe buffer fills with no
reader, and the demo blocks in `Console.WriteLine` and stops accepting, which
looks exactly like a server crash (later connections time out, especially the
IPv6 `::1` ones). Redirect the demo's output to a file when running h2spec.

Verified with a new self-contained `h2c` harness (12/12) covering all three
interop legs, mirroring the TLS paths: our client → our server (both cleartext:
GET, POST /echo byte-exact incl. a UTF-8/emoji payload, GET /large — 128 KiB
under flow control with no TLS, and 3-way multiplexing); **.NET `HttpClient`**
in prior-knowledge mode (`http://` + exact HTTP/2) → our server (GET/POST, the
production-client interop proof for cleartext); and our client →
**.NET Kestrel** configured for cleartext h2c (`HttpProtocols.Http2` with no
`UseHttps`): GET (Kestrel's real HPACK/Huffman decoded), POST /echo byte-exact,
concurrent requests. h2spec was additionally run over the demo's cleartext
listener: **146/146** (equal to the TLS run), with zero `IndexOutOfRange`
occurrences after the HPACK fix. Full regression **64/64** (h2c added) and
h2spec over TLS still **146/146** — the transport generalization left the TLS
path byte-identical.

**Cross-platform verification (Linux/WSL, done 2026-07-18):** the whole thing
was re-verified under WSL/Debian with .NET 10 — the solution builds clean
(0/0), the `h2c` harness passes 12/12 (incl. the Kestrel-h2c leg), and — the
Linux-native bonus the Windows side can't do — the distro `curl` (built against
**nghttp2**) drives both listeners directly: `curl --http2-prior-knowledge
http://localhost:8080/` and `curl --http2 -k https://localhost:8443/` both
return HTTP/2 200 (Windows' Schannel curl has no HTTP/2). h2spec 2.6.0 scores
**146/146 over both transports** on Linux too. For running h2spec there,
`tests/h2spec.sh` is the bash mirror of `tests/h2spec.ps1` (build → start demo
with drained output → h2spec both transports → stop); the h2spec walkthrough in
`tests/TestingAgainst_h2spec.md` is cross-platform. Two bash-specific gotchas
worth remembering, both fixed in the script: run the built **DLL directly**
rather than `dotnet run` (whose forked child a plain `kill` would orphan,
leaving the port bound for the next run — the equivalent of the PowerShell
runner's kill-by-port-owner), and probe readiness with a **bare TCP connect**
(bash `/dev/tcp`), not an HTTP request — a plain HTTP/1.1 curl to the h2c port
is rejected (prior-knowledge only), so it's a false negative for "is it up?".

The original hand-off TODO is fully cleared (everything above under Current
State is done + verified). What follows is a forward-looking roadmap —
**analyzed 2026-07-18, nothing here is started yet.**

## Roadmap — candidate next tracks (planning only)

Ordered by value-per-effort. Tracks A–C stay in the "from scratch on
`SslStream`" spirit of the project; D is the app layer we've deliberately kept
thin; E was the structural split into shared-lib + server + client (now done,
with an HTTP/2 client as the new build). HTTP/3 is intentionally out of scope here —
it's essentially a separate transport (QUIC + QPACK + H3 framing) that shares
only the HTTP *semantics* with this stack, not the framing/HPACK/flow-control
core, so it lives in its own project rather than extending this one.

### Track A — Stream-management hardening (RFC 9113 §5) — done 2026-07-18
Rapid Reset mitigation (CVE-2023-44487), stream-ID exhaustion handling, and
outbound `MAX_HEADER_LIST_SIZE` enforcement — see the "Stream-management
hardening" entry under Current State above for the full writeup.

Server push and RFC 7540 priority stay deliberately unimplemented (push is
deprecated; 7540 priority is superseded — see Track C).

### Track B — CONNECT + WebSocket over HTTP/2 — done 2026-07-18
RFC 9113 §8.5 plain CONNECT, RFC 8441 extended CONNECT (`:protocol` +
`SETTINGS_ENABLE_CONNECT_PROTOCOL`), and RFC 6455 WebSocket framing on top —
see the "CONNECT + extended CONNECT + WebSocket" entry under Current State
above for the full writeup. RFC 9220 (WebSocket over HTTP/3) would reuse
`WebSocket.cs`'s framing layer unchanged if this project ever grew an H3
sibling — it only depends on the transport-agnostic `HTTP2Tunnel` shape
(byte-in, byte-out), not on anything HTTP/2-specific.

### Track C — Modern prioritization (RFC 9218) — done 2026-07-18
RFC 9113 deprecated RFC 7540's stream-dependency/weight priority (which we
already just ignored, and still do). The modern replacement, **RFC 9218
(Extensible Prioritization Scheme for HTTP)** — the `priority` header field,
`PRIORITY_UPDATE`, and `SETTINGS_NO_RFC7540_PRIORITIES` — plus the
priority-aware multiplexed writer rework needed to actually act on it, is
implemented; see the "RFC 9218 prioritization + priority-aware multiplexed
writer" entry under Current State above for the full writeup. The mirror
client-side *emission* of these signals (the `priority` header,
`PRIORITY_UPDATE`) was added later as polish — see "Client-side RFC 9218
priority signaling" under Current State.

### Track D — HTTP semantics layer (RFC 9110 + RFC 9111) — done 2026-07-18
The `HTTP2RequestHandler` seam is intentionally thin. Making this a *real* HTTP
server (methods, status semantics, conditional requests, ranges, content
negotiation, auth, caching) is **RFC 9110 (HTTP Semantics)** + **RFC 9111 (HTTP
Caching)** — version-independent, shared with H1/H3. This is "the application
layer above the framing" and arguably out of scope for a framing-focused
learning project, but it's where a production server's bulk lives — which is
why, unlike Tracks A–C, this one was scoped incrementally rather than taken in
full at once. All of it is now done: GET/HEAD/OPTIONS method semantics,
conditional requests (If-Match/If-None-Match/If-Modified-Since/If-Unmodified-Since),
single-range Range requests (Range/If-Range/Content-Range/Accept-Ranges),
proactive content negotiation (Accept/Accept-Encoding/Accept-Language, `Vary`,
the 406-vs-default policy), the **RFC 9110 §11 authentication framework** (Basic
+ Bearer + transport-layer mTLS), and **RFC 9111 caching** (a client-side cache
with shared-cache semantics — Cache-Control, age/freshness, conditional
revalidation, Vary keying, invalidation, stale-while-revalidate) — see the
"RFC 9110 'core mechanics' + content negotiation", "Authentication", and
"RFC 9111 HTTP caching" entries under Current State above. Nothing left open in
Track D.

### Track E — HTTP/2 client + shared-library split — done 2026-07-18
The stated target — three parts instead of one: a **shared HTTP library**
(`Core`), an **HTTP/2 server**, and an **HTTP/2 client** — is fully realized.

- **Phases 1–3** (generalize the stream layer via `HTTP2Role`; build
  `HTTP2ClientConnection` + `HTTP2Client`; interop-verify the client against
  both our server and .NET Kestrel, 14/14) — see the "HTTP/2 client +
  stream-manager generalization" entry under Current State.
- **Phase 4** (the physical split into `Core`/`Server`/`Client`/`Demo`, with
  `HTTP2Settings`/`HTTP2RequestHandler` extracted to Core and `WebSocket.cs`
  behind `IHTTP2Tunnel`) — see the "Physical project split" entry under Current
  State.

This satisfies the Conventions interop rule on both sides of the wire (server
vs. `HttpClient`/curl; client vs. Kestrel). The namespace has been renamed to
`org.GraphDefined.Vanaheimr.Hermod.HTTP2` (Vanaheimr/Hermod convention). The
WebSocket *client* + client-side CONNECT (reusing `WebSocket.cs` over a
client-side `IHTTP2Tunnel`) that were noted here as natural extensions are now
done — see "WebSocket / CONNECT-capable client" under Current State.

### HTTP/3 — out of scope (its own project)
Not tracked here. It's a separate transport (QUIC, RFC 9000/9001/9002 over UDP)
with QPACK (RFC 9204, not HPACK — HPACK's ordered dynamic table would
reintroduce head-of-line blocking over QUIC's out-of-order streams) and its own
H3 framing (RFC 9114). It shares only the HTTP *semantics* (RFC 9110) with this
stack; the framing/HPACK/flow-control core here isn't reused. The one seam that
*could* be shared is a version-agnostic `HTTP2RequestHandler` (see Track D). It
belongs in a dedicated project, built on `System.Net.Quic` (MsQuic) the way
this one builds on `SslStream`.

### Suggested order
A, B, C, D, and E are all done, plus client-side RFC 9218 priority signaling,
the full h2spec-conformance track (146/146), the full HPACK encoder,
Slowloris/timeout hardening, client robustness, streaming bodies + response
trailers, a WebSocket/CONNECT-capable client, WINDOW_UPDATE batching +
larger flow-control windows, on-the-fly content coding (gzip/brotli/
deflate), 1xx interim responses (`Expect: 100-continue`, 103 Early Hints), and
h2c (cleartext prior-knowledge — see the h2c entry under Current State). Every
Tier-1 and Tier-2 item from the original weighted analysis is now done. The
only remaining candidate is a neutral home for the demo — as interest dictates.

## Conventions
- English for code, identifiers, comments, and commit messages.
- Style follows the surrounding Vanaheimr/Hermod code: aligned member
  declarations, region blocks per concern, RFC section references in comments.
- Keep it dependency-free (BCL only). No NuGet packages for the core stack.
- **Structure: shared library + server + client** (realized — `Core`/`Server`/
  `Client`/`Demo`, Track E). Direction-neutral protocol code (frame
  (de)serialization, HPACK, the stream layer, settings, WebSocket framing, RFC
  9110 semantics) lives in `Core` and must not take a dependency on
  role-specific types — the project references enforce this (Core references
  neither Server nor Client). The server (`HTTP2Connection`/`HTTP2Server`) and
  the client (`HTTP2ClientConnection`/`HTTP2Client`) are mirror roles in their
  own projects; keep new shared code in `Core`, not duplicated across the two.
- **Interop testing is part of "verified", not optional.** Every wire-visible
  feature is validated against a .NET counterpart on the *opposite* side of the
  wire, in addition to the hand-rolled raw-frame test clients that already
  exist:
    - the **server** against .NET's `HttpClient` (strict client) *and* against
      **curl** (`--http2`; use an nghttp2-backed build — the stock
      Windows/Schannel curl has no HTTP/2 and silently falls back to 1.1);
    - the **client**, once it exists, against a .NET HTTP/2 **server**
      (Kestrel) — the mirror of how the server is tested against `HttpClient`.
  These reference peers (`HttpClient`, Kestrel, curl) are **test-only** and do
  not count against the BCL-only rule for the core stack.

## References
- RFC 9113 — HTTP/2
- RFC 7541 — HPACK
- RFC 7301 — ALPN
