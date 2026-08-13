# HTTP/2 Conformance Tests & Demo — C# / .NET 10

This repository is the **runnable demo host** plus the **conformance / interop
test drivers** for the from-scratch HTTP/2 stack (built directly on `SslStream`
— no `Kestrel`, no `HttpListener`, no `System.Net.Http` HTTP/2 stack). The stack
itself lives in the Vanaheimr **Hermod** library, pulled in here as a git
submodule under `libs/Hermod/Hermod/HTTP2/` (split by concern into `Core` — the
direction-neutral framing, HPACK, stream layer, settings, HTTP semantics —,
`Server`, `Client`, `WebSocket`, and `Auth`). This repo adds the `Demo/` host,
the `tests/` live-host raw-frame harnesses, the `h2bench` benchmark, and the
h2spec/Autobahn drivers; the
212 NUnit unit + integration tests live with the stack in Hermod
(`HermodTests/HTTP2/`).

This is a learning/reference implementation in the spirit of the Vanaheimr
Hermod protocol stacks (SMTP, IMAP, HTTP/2, DHCP, NTS-KE, TCP, ... hand-rolled
in C#).

This file (CLAUDE.md) holds the **working notes for this repo** plus a
**concern-level map of the stack under test** (the Architecture tables below),
a current-state summary, and the conventions. The stack's own reader-facing
reference (API, RFC-compliance matrix, feature detail) lives next to the code in
`libs/Hermod/Hermod/HTTP2/README.md`; the full chronological **build log** — every
feature, the design decisions, edge cases found, and how each was verified — is
in [`docs/BUILD_LOG.md`](docs/BUILD_LOG.md).

## Build & Run

The HTTP/2 stack lives in the **Hermod** submodule (`libs/Hermod/Hermod/HTTP2/`,
split into `Core`/`Server`/`Client`/`WebSocket`/`Auth`); this repo wraps it with
the runnable `Demo`, the remaining live-host harnesses under `tests/`, and the
root-level solution `HTTP2.slnx` (which also pulls `Hermod`/`Styx` +
`HermodTests`/`StyxTests` as dependencies). Clone **with submodules**
(`git clone --recurse-submodules …`, or `git submodule update --init --recursive`
after the fact) — otherwise `libs/` is empty and nothing builds.

```bash
# from the repository root (not src/ — that was removed when the stack moved
# into the Hermod submodule):
dotnet build HTTP2.slnx
dotnet run --project Demo/HTTP2.Demo.csproj
# then, from another shell:
curl --http2 -k https://localhost:8443/
curl --http2 -k https://localhost:8443/echo -d "Hello HTTP/2!"
curl --http2 -k https://localhost:8443/large   # 128 KiB — exercises flow control
curl --http2 -k https://localhost:8443/slow    # 2 s handler — exercises multiplexing
curl --http2 -k -X QUERY --data 'ap' https://localhost:8443/search  # RFC 10008 QUERY
# cleartext h2c (prior knowledge — no TLS), on :8080:
curl --http2-prior-knowledge http://localhost:8080/
```

Note: the stock Windows curl (Schannel build) has no HTTP/2 support and silently
falls back to HTTP/1.1 — use a curl with nghttp2, or .NET's `HttpClient`.

Target framework is `net10.0`. Uses a self-signed cert generated at startup.

**Tests:** most coverage is the **212 NUnit tests** in
`libs/Hermod/HermodTests/HTTP2/` — run `dotnet test HTTP2.slnx --filter
"FullyQualifiedName~Tests.HTTP2"`. The remaining **48** live-host harness runs
(demo-driven raw-frame scenarios) run via `tests/run-tests.ps1`; conformance via
`tests/h2spec.ps1` (146/146 over h2 + h2c) and Autobahn (517/517). Performance is
measured by `tests/h2bench` (`dotnet run -c Release --project tests/h2bench`) —
not a pass/fail gate, but the baseline any optimisation has to beat. See
[`tests/README.md`](tests/README.md).

## Architecture

The stack lives in the **Hermod** submodule under `libs/Hermod/Hermod/HTTP2/`,
split by concern into `Core` (shared, direction-neutral), `Server`, `Client`,
`WebSocket`, and `Auth`; the runnable **`Demo`** (in this repo, `→ Hermod`,
`Styx`) is the host. Dependency direction is `Core ← Server`, `Core ← Client` —
Core never references the role-specific code. The concern tables below name the
primary file(s) per concern; **their paths are relative to
`libs/Hermod/Hermod/HTTP2/`** (e.g. the `Core/` table → `…/HTTP2/Core/`).

**File layout (updated 2026-07-20):** each public enum / interface / class /
struct / record now lives in its **own file named after the type**; a
one-line delegate stays in the file of the type it seams (placed above it), and
extension/helper static classes likewise. The tables below therefore group the
files by **concern** and name the primary/representative file(s) per concern —
not every single type file (e.g. the frame enums, HPACK's two tables, the
WebSocket value types, and the auth schemes are each their own file).

**`Core/`** — the shared, direction-neutral library:

| Concern (primary file[s]) | Responsibility |
|---|---|
| `HTTP2Frame.cs` (+ `HTTP2FrameType.cs`, `HTTP2FrameFlags.cs`, `HTTP2ErrorCode.cs`, `HTTP2SettingsParameter.cs`, `HTTP2StreamState.cs`, `HTTP2Role.cs`, `HTTP2*Exception.cs`) | 9-byte frame header parse/serialize + frame factories; the frame type/flag/error-code/role enums and the exception types are sibling files |
| `HPACKDecoder.cs` / `HPACKEncoder.cs` (+ `HuffmanDecoder.cs` / `HuffmanEncoder.cs`) | RFC 7541: static + dynamic table, integer/string coding, Huffman decode **and encode**, full-featured encoder (static + per-connection dynamic table + Huffman) |
| `HTTP2Stream.cs` (+ `HTTP2StreamManager.cs`, `HTTP2OutboundQueue.cs`, `HTTP2OutboundItem.cs`, `HTTP2Priority.cs`) | Per-stream state machine (RFC 9113 §5.1), per-stream flow-control windows, RFC 9218 priority + outbound DATA queue, role-parameterized `HTTP2StreamManager` |
| `HTTP2Settings.cs` | The connection-settings bag (advertised vs. peer), used by both roles |
| `HTTP2HeaderList.cs` | RFC 9113 §6.5.2 header-list accounting (name + value + 32 per field, uncompressed) — the one formula both roles measure `MAX_HEADER_LIST_SIZE` against |
| `HTTP2Trailers.cs` | RFC 9113 §8.1 trailer-field validation (no pseudo-headers, lowercase names) — identical whichever direction they travel, so the server's response trailers and the client's request trailers cannot drift apart |
| `HTTP2EarlyData.cs` | RFC 8470: the `Early-Data` field and the safe-method policy behind 425. We terminate no 0-RTT (`SslStream` has no early-data API), so this is about what an *intermediary* forwards to us |
| `HTTP2CipherSuites.cs` | RFC 9113 §9.2.2 / Appendix A: which TLS 1.2 cipher suites may carry HTTP/2 (ephemeral key exchange + AEAD), used by both roles after the handshake |
| `HTTP2EventSource.cs` (+ `HTTP2Diagnostics.cs`) | The observability seam: structured events + counters via `EventSource`, connection/request spans via `ActivitySource` — both BCL, so no logging dependency. The stack writes nothing to the console itself |
| `HTTPAlternativeService.cs` | RFC 7838: the `Alt-Svc` field-value grammar (protocol id, alt-authority, `ma`/`persist`, `clear`) in both directions — carried by the ALTSVC frame or the header field |
| `HTTPRedirect.cs` | RFC 9110 §15.4: `Location` resolution (RFC 3986 §5) plus the per-status method/body rewriting rules, and whether the target is the same origin |
| `HTTPValidators.cs` (+ `HTTPContentRange.cs`) | RFC 9110 §8.8/§13/§14 primitives both roles share: HTTP-date parse *and* format, entity-tag lists, strong/weak comparison, and `Content-Range` parse *and* format — the server evaluates preconditions with them, the client builds them |
| `HTTPContentCoding.cs` | RFC 9110 §8.4 content codings in **both** directions (gzip/br/deflate encode *and* decode, with a decompression-bomb bound) — the server compresses through it, the client decodes through it |
| `HTTPDigest.cs` (+ `HTTPDigestVerification.cs`, `HTTPDigestMismatchException.cs`) | RFC 9530 digest fields: `Content-Digest` (the octets of this message) vs. `Repr-Digest` (the whole representation), the `Want-…` preference grammar, and verification — sha-256/sha-512 only, computed over the *encoded* bytes, so both roles digest what actually crosses the wire |
| `HTTPClientAuthenticator.cs` (+ `HTTPClientCredentials.cs`) | The client half of RFC 9110 §11: parse a `WWW-Authenticate` challenge and compute the `Authorization` credential (Digest/Bearer/Token/Basic) — the mirror of the `Auth/` schemes, which only validate |
| `HTTPAuthority.cs` | Which origins a connection is authoritative for: `:authority` parsing, RFC 6125 name matching, and the certificate- / Origin-Set-derived predicates behind 421 |
| `HTTP2RequestHandler.cs` | The app-logic request-handler delegate (produced by `HTTPSemantics`, consumed by the server) |
| `IHTTP2RequestStream.cs` (+ `HTTP2StreamingHandler` delegate) / `IHTTP2ResponseStream.cs` | The streaming seam (incremental body + trailers + 1xx interim responses, for gRPC-style bidi and 103 Early Hints) |
| `IHTTP2Tunnel.cs` | Transport-agnostic byte-tunnel interface, so `WebSocketConnection.cs` doesn't depend on the server's concrete tunnel |
| `WebSocketConnection.cs` (+ `WebSocketDeflate.cs`, `WebSocketOpcode.cs`, `WebSocketMessage.cs`, `WebSocketRole.cs`, `WebSocketProtocolException.cs`) | RFC 6455 WebSocket framing (masking, opcodes, fragmentation, close handshake) + RFC 7692 permessage-deflate over an `IHTTP2Tunnel`, direction-aware via `WebSocketRole` |
| `HTTPSemantics.cs` (+ `HTTPResource.cs`) | RFC 9110 semantics: GET/HEAD/OPTIONS, conditional requests, Range requests, proactive content negotiation (Accept*/Vary), opt-in on-the-fly content coding (gzip/br/deflate) — version-independent, never touches frames/streams/HPACK |
| `HTTPAuthentication.cs` (+ `HTTPAuthenticator.cs`, `IHTTPAuthenticationScheme.cs`, `{Basic,Bearer,Digest,Token}AuthenticationScheme.cs`, `HTTPAuthenticatedIdentity.cs`, `HTTPAuthParams.cs`) | RFC 9110 §11 authentication framework (401/WWW-Authenticate/Authorization) + Basic (RFC 7617), Bearer (RFC 6750), Digest (RFC 7616) & Token (non-standard) schemes, store-agnostic (app-supplied validators) |
| `HTTPCache.cs` (+ `HTTPCacheControl.cs`, `HTTPStoredResponse.cs`, `HTTPCacheMode.cs`, `HTTPCacheUsability.cs`, `HTTPCacheDecision.cs`) | RFC 9111 caching *logic*: Cache-Control parsing, age/freshness computation, storability, revalidation, Vary keying — store-agnostic, direction-neutral |

**`Server/`** — references `Core`:

| Concern (primary file[s]) | Responsibility |
|---|---|
| `HTTP2Connection.cs` (+ `HTTP2ConnectResult.cs` [+ `HTTP2ConnectHandler` delegate], `HTTP2Tunnel.cs`, `HTTP2Timeouts.cs`) | Connection preface, SETTINGS handshake, the frame dispatch loop, request assembly, CONNECT tunneling (`HTTP2Tunnel` implements `IHTTP2Tunnel`), the priority-aware DATA writer loop (RFC 9218), streaming dispatch + response trailers, Slowloris/idle timeouts |
| `HTTP2RequestStream.cs` / `HTTP2ResponseStream.cs` | Server-side impls of the Core streaming seam over one `HTTP2Stream` |
| `HTTP2Server.cs` (+ `HTTP11FallbackHandler` delegate) | `TcpListener` + `SslStream` with ALPN `h2` negotiation and TLS-handshake timeout; `http/1.1` advertised only when an `HTTP11Fallback` handler is supplied (h2-only otherwise); optional `Cleartext` mode (h2c prior-knowledge, no TLS) |

**`Client/`** — references `Core`:

| Concern (primary file[s]) | Responsibility |
|---|---|
| `HTTP2ClientConnection.cs` (+ `HTTP2Response.cs`, `HTTP2ResponseHead.cs`, `HTTP2RequestHandle.cs`, `HTTP2ClientStream.cs`, `HTTP2ClientTunnel.cs`, `HTTP2ClientOptions.cs`) | Client-role connection: sends the preface, allocates odd request streams, sends requests + assembles `HTTP2Response`s; the response/handle/stream/tunnel/options types are sibling files |
| `HTTP2Client.cs` | Dialer: TCP connect + TLS/ALPN `h2` handshake (or optional `Cleartext` h2c prior-knowledge), the client-side counterpart of `HTTP2Server` |
| `HTTP2CachingClient.cs` | RFC 9111 cache (store + origin wiring) in front of a client connection — serves fresh hits, revalidates stale entries, keys by `Vary` |
| `HTTP2ClientPool.cs` | Single-origin connection pool — keeps N warm connections, routes to the least-loaded, fails over not-processed requests, and self-heals dead connections in the background |

**`Demo/`** — references `Server` + `Client`:

| File | Responsibility |
|---|---|
| `Program.cs` | Demo host (TLS `h2` on :8443 + cleartext `h2c` on :8080) + self-signed cert + example request/connect/resource handlers (the app-logic plug-in point), plus a `ConsoleEventListener` showing the observability seam from the consumer side |

The stack (`Core`/`Server`/`Client`/`WebSocket`/`Auth` in Hermod) and the `Demo`
here all share the `org.GraphDefined.Vanaheimr.Hermod.HTTP2` namespace (the
Vanaheimr/Hermod convention).

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

## Current State

The stack is **HTTP/2 feature-complete** and verified end-to-end. Everything below
the HTTP application semantics is implemented and interop-tested on *both* sides
of the wire (our server ↔ .NET `HttpClient`/curl; our client ↔ .NET Kestrel):

- **Protocol core (RFC 9113 / 7541):** 9-byte framing, HPACK decode **and**
  encode (Huffman both ways, per-connection dynamic table), the role-parameterized
  stream layer + state machine, flow control (batched WINDOW_UPDATE, 1 MiB
  windows, consumption-driven backpressure), and the priority-aware multiplexed
  writer (RFC 9218). Full abuse hardening: Rapid Reset (CVE-2023-44487),
  CONTINUATION-flood (CVE-2024-27316), PING/SETTINGS floods, stream-ID exhaustion,
  in/outbound `MAX_HEADER_LIST_SIZE` on both roles (shared accounting in
  `HTTP2HeaderList`), and Slowloris/idle/handshake/SETTINGS-ACK
  timeouts. Every time source is injectable via the BCL `System.TimeProvider`
  (`HTTP2ClientOptions.TimeProvider` / `HTTP2Timeouts.TimeProvider`, default
  `TimeProvider.System`) for deterministic clock/timeout tests. The §9.2 TLS
  profile is enforced on both roles: renegotiation off, and a TLS 1.2 cipher
  suite from Appendix A answered with `GOAWAY INADEQUATE_SECURITY`.
- **Authoritative origins:** `:authority` is checked against the server
  certificate's identities (or an announced Origin Set) and a request for an
  origin we don't serve gets **421 Misdirected Request** — the counterpart to
  connection coalescing (§9.1.1). The **ORIGIN frame** (RFC 8336) lets the server
  state that set explicitly; the client parses and exposes it. **ALTSVC**
  (RFC 7838) answers the neighbouring question — where else the *same* origin is
  reachable — completing the set of non-deprecated frame types.
- **Server + client, two transports:** mirror connection roles over TLS `h2`
  (ALPN, + optional mTLS) and cleartext `h2c` (prior knowledge). The client adds
  robustness (REFUSED_STREAM auto-retry, MAX_CONCURRENT_STREAMS gating,
  GOAWAY/exhaustion → retry-safe `HTTP2RequestNotProcessedException`, PING
  keepalive) and a single-origin connection pool (`HTTP2ClientPool` — warm
  connections, least-loaded routing, failover, background self-heal).
- **Tunneling / WebSocket / gRPC:** plain + extended CONNECT (RFC 8441), RFC 6455
  framing + RFC 7692 permessage-deflate (both roles), and real gRPC — all four
  call types — over the streaming seam (`HTTP2StreamingHandler` +
  request/response streams + trailers in **both** directions:
  `IHTTP2ResponseStream.CompleteAsync(Trailers)` on the server,
  `HTTP2ClientStream.CompleteRequestAsync(Trailers)` on the client),
  interop-tested against `Grpc.Net.Client` and — for the client's request
  trailers — against Kestrel's `Request.GetTrailer`.
- **HTTP semantics (Core, version-independent, never touches framing):** RFC 9110
  methods / conditional requests / Range (single + multi `multipart/byteranges`) /
  proactive content negotiation / on-the-fly gzip-brotli-deflate; the QUERY method
  (RFC 10008); 1xx interim responses (`Expect: 100-continue`, 103 Early Hints);
  the §11 auth framework (Basic/Bearer/Digest/Token + transport-layer mTLS); and
  RFC 9111 client-side caching (freshness, revalidation, `Vary`, shared/private).
- **Early data (RFC 8470):** we terminate no TLS 1.3 0-RTT — `SslStream` exposes
  no early-data API, verified rather than assumed — so there is no replay window
  of our own. What is implemented is the reachable half: the server judges an
  intermediary's `Early-Data: 1` (safe methods pass, everything else gets **425
  Too Early** with `no-store`; overridable via `AcceptEarlyData`), on both the
  buffered and the streaming dispatch path, and the client repeats a 425 exactly
  once with the field removed.
- **Content integrity (RFC 9530):** opt-in `Content-Digest` / `Repr-Digest` on
  both roles. The server digests every content-bearing response (plus the
  representation digest a 206 needs) and validates an inbound digest on QUERY;
  the client asks, verifies *before* decoding, and — the payoff — verifies a
  resumed `DownloadAsync` against the `Repr-Digest` no single range response
  could have proven. `HTTPDigestVerification` keeps "nothing was checked" and
  "it matched" apart; a mismatch throws.
- **Client-side semantics (in progress):** the client now decodes
  `content-encoding` (opt-in `AutomaticDecompression`, bomb-bounded) and answers
  a **401** from a `WWW-Authenticate` challenge (Digest > Bearer > Token >
  Basic, one retry, never preemptive). `DownloadAsync` resumes an interrupted
  transfer with `Range` + `If-Range` (strong validators only; 200 means restart,
  and the stale prefix is truncated), conditional GETs round-trip to 304, and
  `MaxRedirects` follows `Location` with the §15.4 rewriting rules — but only
  within this connection's own origin, since pooling is single-origin by design.
  A cookie jar remains open — see the task list.

**Verification:** **212/212** NUnit tests and `tests/run-tests.ps1` → **48/48**
harness runs, both current. **h2spec 146/146** over both transports (Windows +
Linux) and **Autobahn 517/517** (full RFC 6455 + permessage-deflate) as last run
— both need an external binary that is not vendored here, so they were *not*
re-run for the §9.2 / 421 / ORIGIN work. Reference peers (test-only, don't count
against the BCL-only rule): .NET `HttpClient`, Kestrel, curl (nghttp2),
`Grpc.Net.Client`. The pure in-memory Core unit tests (Huffman, HPACK encoder,
`HTTP2StreamManager`) live as NUnit fixtures in Hermod's `HermodTests/HTTP2/`,
not as harnesses here.

**Performance** is measured by `tests/h2bench`: ~9.1 KiB allocated per trivial
request (both roles), a large transfer allocating ~6.2× its payload, ~180/~205
MiB/s down/up, ~145 k HPACK blocks/s. Per-request *latency* is not anomalous —
against a Kestrel control on the same machine our server is the faster of the two
(0.73–1.14 ms vs 1.15–1.69 ms per loopback round trip). What is ours is a
**throughput cap of ~1 000 req/s per connection at any concurrency**: splitting
each request shows server turnaround flat under load (0.34 → 0.36 ms) while the
client's send path goes from 0.22 ms to 13.65 ms at 64 concurrent, because
`requestStartLock` is held across the HEADERS write rather than just the
stream-ID allocation it exists to order. The fix is open (see the task list).

All originally-planned roadmap tracks (A–E) plus every follow-up extension are
**done**. Two things are genuinely open (see the task list): the per-request
serialization described above, and a decision on a client cookie jar.

### Optional — parked, not planned

Real candidates, deliberately not scheduled. Each is worth doing *if wanted*;
none is a gap, and nothing depends on any of them. Listed so the reasoning
survives and they stop being re-proposed as "what comes next":

| Item | Why it is parked |
|---|---|
| **Server-side shared cache** (RFC 9111 reverse-proxy mode) | Would reuse the direction-neutral `HTTPCache` and exercise what is dead today (`s-maxage`, `must-revalidate`, shared-vs-private storability, origin-side `Vary`). The strongest argument for it is architectural — a second consumer proving `Core`'s direction-neutrality — rather than a missing capability. |
| **RFC 9421** — HTTP Message Signatures | The most expensive of the batch, and the cost is not the crypto: it needs a full RFC 9651 structured-fields implementation, because the signature base must be byte-identical on both ends and the `sf` parameter demands strict re-serialization. We only ever built the slices we needed. Also, `Ed25519` is absent from the BCL on .NET 10 (verified), so the algorithm the RFC's own examples use is unreachable without breaking the no-NuGet rule. Composes well with the RFC 9530 digests already in place — signatures cover `Content-Digest`, not the body. |
| **RFC 9298** — CONNECT-UDP | Mostly reuse (extended CONNECT + `IHTTP2Tunnel` exist; add `:protocol = connect-udp` and RFC 9297 capsules), but over HTTP/2 it is the *degraded* path. MASQUE's point is HTTP/3 datagrams, so the natural home is the sibling project, not here. |

**HTTP/3 is not on either list, and never was.** It is a different transport —
QUIC + QPACK + H3 framing — sharing only the version-independent HTTP semantics
with this stack, which is exactly why `Core` was cut the way it was. It is driven
from the sibling repository **`HTTP3ConformanceTests`** (same parent directory),
built the same way this one is: the stack itself lives in Hermod
(`Hermod/HTTP3` + `Hermod/QUIC`, alongside `Hermod/HTTP2`), and the sibling repo
adds the host, the harnesses, and the interop drivers. Not a future track here.
Anything that says otherwise is stale.

The full history — feature by feature, with the design rationale, the bugs
caught, and the exact verification for each — is in
[`docs/BUILD_LOG.md`](docs/BUILD_LOG.md).

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
- RFC 6455 — The WebSocket Protocol
- RFC 7692 — Compression Extensions for WebSocket (permessage-deflate)
- RFC 7616 — HTTP Digest Access Authentication
- RFC 10008 — The HTTP QUERY Method
- RFC 9530 — Digest Fields
- RFC 8470 — Using Early Data in HTTP
