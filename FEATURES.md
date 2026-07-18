# Supported Features, RFCs & Extensions

A complete reference of everything this from-scratch HTTP/2 stack implements —
every RFC, every extension, and where each is verified. The stack is a
**shared protocol library (`Core`) + an HTTP/2 server + an HTTP/2 client**,
built directly on `SslStream` (no Kestrel, no `HttpListener`, no
`System.Net.Http` HTTP/2 stack), **BCL-only** (no NuGet packages in the core
stack).

For *how* things were built and the change history, see [`CLAUDE.md`](CLAUDE.md);
for usage, see [`README.md`](README.md); for the test layout, see
[`tests/README.md`](tests/README.md).

## At a glance

| Conformance suite | Result | Transports |
|---|---|---|
| [h2spec](https://github.com/summerwind/h2spec) 2.6.0 (RFC 9113 + RFC 7541) | **146 / 146** | TLS `h2` **and** cleartext `h2c`, on Windows **and** Linux |
| [Autobahn TestSuite](https://github.com/crossbario/autobahn-testsuite) (RFC 6455) | **517 / 517** | full suite incl. permessage-deflate (§12/13) |
| In-repo harness suite (`tests/run-tests.ps1`) | **70 / 70** runs | interop vs. `HttpClient`, Kestrel, curl, `Grpc.Net.Client` |

Every wire-visible feature is verified against a .NET counterpart on the
*opposite* side of the wire (server ↔ `HttpClient`/Kestrel/curl; client ↔
Kestrel/`Grpc.Net.Client`) **in addition to** hand-rolled raw-frame test clients.
Those reference peers are test-only and don't count against the BCL-only rule.

## Architecture

| Project | Role | Depends on |
|---|---|---|
| `Core` | Direction-neutral protocol library (framing, HPACK, stream layer, settings, WebSocket framing, HTTP semantics, auth, caching logic) | — |
| `Server` | HTTP/2 server role (`HTTP2Connection`, `HTTP2Server`) | `Core` |
| `Client` | HTTP/2 client role (`HTTP2ClientConnection`, `HTTP2Client`, caching client, connection pool) | `Core` |
| `Demo` | Runnable host wiring example handlers (TLS `h2` on :8443, cleartext `h2c` on :8080) | `Server`, `Client` |

Project references enforce the direction-neutral vs. role-specific boundary
structurally: `Core` references neither `Server` nor `Client`.

---

## RFC compliance matrix

| RFC | Title | Status | Notes |
|---|---|---|---|
| **9113** | HTTP/2 | ✅ Complete | Framing, streams, flow control, settings, GOAWAY. h2spec 146/146. |
| **7541** | HPACK: Header Compression | ✅ Complete | Full decoder **and** encoder (static + dynamic table + Huffman both ways). |
| **7301** | TLS ALPN | ✅ | `h2` negotiation in the TLS handshake. |
| **9218** | Extensible Prioritization Scheme | ✅ | `priority` header, `PRIORITY_UPDATE`, `SETTINGS_NO_RFC7540_PRIORITIES`; priority-aware writer. Both roles emit + the server acts on it. |
| **8441** | Bootstrapping WebSockets with HTTP/2 | ✅ | Extended CONNECT, `:protocol`, `SETTINGS_ENABLE_CONNECT_PROTOCOL`. |
| **6455** | The WebSocket Protocol | ✅ Complete | Framing, masking, fragmentation, close handshake, UTF-8 validation. Autobahn 517/517. Server **and** client roles. |
| **7692** | Compression Extensions for WebSocket (permessage-deflate) | ✅ | No-context-takeover mode, negotiated on both HTTP/1.1-Upgrade and HTTP/2-CONNECT handshakes. |
| **9110** | HTTP Semantics | ✅ | Methods, conditional requests, Range (single + multi), content negotiation, the §11 auth framework. |
| **9111** | HTTP Caching | ✅ | Client-side cache with shared/private semantics. |
| **7617** | Basic Authentication | ✅ | |
| **6750** | Bearer Token Usage | ✅ | |
| **7616** | Digest Access Authentication | ✅ | Challenge-response, SHA-256 (+ MD5 interop), stateless nonce, `qop=auth`. |
| **8297** | An HTTP Status Code for Indicating Hints (103 Early Hints) | ✅ | Handler-driven interim responses. |
| **10008** | The HTTP QUERY Method | ✅ | Safe/idempotent/cacheable body-carrying read (published 2026-06). |
| **5861** | HTTP Cache-Control Extensions for Stale Content | ✅ | `stale-while-revalidate`, `stale-if-error` (part of caching). |
| **8941** | Structured Field Values | ◑ Partial | The Dictionary grammar needed to parse the `priority` header. |
| **4647** | Matching of Language Tags | ◑ Partial | Basic-filtering + lookup-truncation for `Accept-Language`. |
| **1123** | (HTTP-date format) | ✅ | Date parsing/formatting for conditional requests. |
| **2069 / 2617** | (legacy Digest) | ✅ | Accepted for interop: no-`qop` responses and `algorithm=MD5`. |

✅ = implemented · ◑ = the subset this stack needs.

---

## Feature detail

### Connection & framing (RFC 9113)

- 9-byte frame header parse/serialize; all frame types
  (DATA, HEADERS, PRIORITY, RST_STREAM, SETTINGS, PUSH_PROMISE, PING, GOAWAY,
  WINDOW_UPDATE, CONTINUATION, PRIORITY_UPDATE).
- Connection preface + SETTINGS handshake (server-preface-first ordering,
  SETTINGS ACK).
- Decoupled read/write loops with **true multiplexing** — application handlers
  run on their own tasks; the frame read loop never blocks on app logic.
- Reserved-bit masking, padding handling, atomic HEADERS+CONTINUATION sequences.
- GOAWAY (graceful + error), with a bounded inbound drain so the peer actually
  receives it.
- Request validation (§8): pseudo-header ordering/uniqueness, lowercase field
  names, connection-specific header rejection, `te: trailers` only — malformed
  requests are stream errors, not connection errors.
- Trailers (§8.1) and implicit stream closure (§5.1.1).
- `content-length` vs. DATA-length enforcement (§8.1.2.6).
- Cleartext **h2c** (prior knowledge, RFC 9113 §3.3) — server and client. (The
  RFC 7540 `Upgrade: h2c` negotiation was removed in RFC 9113 and is
  deliberately not implemented.)

### HPACK (RFC 7541)

- Full decoder: static + dynamic table, integer/string coding, **Huffman decode
  via a bit-level trie**, dynamic-table-size-update bounds (§4.2 / §6.3),
  truncated-block → `COMPRESSION_ERROR`.
- Full encoder: 61-entry static table, per-connection dynamic table (with a
  volatile-value denylist and *never-indexed* for sensitive fields §7.1.3),
  **Huffman encode**, table-size signaling from the peer's
  `SETTINGS_HEADER_TABLE_SIZE`.
- The 257-entry Huffman table is self-validated at class-init (prefix-collision
  check).

### Flow control

- Per-stream and connection-level windows; signal-based send-window reservation
  (no polling).
- **WINDOW_UPDATE batching** (replenish once per half-window, not per DATA
  frame) + larger default windows (1 MiB stream + connection).
- **Consumption-driven backpressure**: for streaming/tunnel bodies the receive
  window is returned only as the *application* reads, so a slow consumer forces
  the peer to stop — the window *is* the memory bound.
- Bounded buffered request body (`MaxRequestBodySize`, default 16 MiB).
- Padding counted against flow control (§6.1); closed-stream DATA still
  window-accounted (§6.9); cookie-crumb reassembly (§8.2.3).

### Stream management & hardening (RFC 9113 §5)

- **Rapid Reset mitigation (CVE-2023-44487)** — a peer-reset-ratio guard.
- **CONTINUATION-flood mitigation (CVE-2024-27316)** — bounded header-block
  accumulation + a per-block CONTINUATION cap (server **and** client).
- PING/SETTINGS/PRIORITY_UPDATE flood counting.
- Stream-ID exhaustion handling (proactive GOAWAY + `REFUSED_STREAM`).
- Inbound + outbound `MAX_HEADER_LIST_SIZE` enforcement.
- Per-stream `RST_STREAM` cancellation (a `CancellationToken` into the handler).
- Closed-stream pruning; graceful shutdown (GOAWAY to every active connection).

### Slowloris / timeout hardening

- TLS-handshake, preface, SETTINGS-ACK, idle, and in-progress (partial
  frame/header-block) timeouts (`HTTP2Timeouts`) — reclaiming a peer that sends
  *too little*, complementing the flood defenses against *too much*.

### Prioritization (RFC 9218)

- `SETTINGS_NO_RFC7540_PRIORITIES=1` advertised (RFC 7540 priority is
  parsed-and-ignored, per §5.3.1 self-dependency validation only).
- The `priority` request/response header (urgency + incremental) and
  `PRIORITY_UPDATE` frame — parsed leniently (bad hint → default, not an error).
- A **priority-aware multiplexed writer**: a single per-connection writer loop
  schedules DATA by urgency → non-incremental-first → round-robin fairness.
- Client emits the signals too (`Priority` param, `UpdatePriorityAsync`).

### CONNECT & tunneling

- Plain CONNECT (RFC 9113 §8.5) — `:authority` present, `:scheme`/`:path` absent.
- Extended CONNECT (RFC 8441) — `:protocol` + mandatory `:scheme`/`:path`.
- `HTTP2Tunnel` (server) / `HTTP2ClientTunnel` (client): a raw, flow-controlled,
  transport-agnostic byte tunnel behind the `IHTTP2Tunnel` interface.

### WebSocket (RFC 6455 + RFC 7692)

- Full framing: masking (direction-aware — client masks, server doesn't),
  opcodes, fragmentation reassembly, automatic ping→pong, close handshake.
- Strict UTF-8 validation of text (§8.1, incremental across fragments) and
  close-frame validation (§5.5 / §7.4.1).
- **permessage-deflate** (RFC 7692) in no-context-takeover mode, negotiated over
  both the Autobahn HTTP/1.1-Upgrade path and the production HTTP/2 CONNECT path.
- Server **and** client roles (`WebSocketRole`), over `IHTTP2Tunnel` on both
  ends.

### HTTP semantics (RFC 9110)

- **Methods**: GET/HEAD (shared path), OPTIONS (204 + `Allow`), 405 for
  unsupported (with `Allow`).
- **Conditional requests** (§13): `If-Match`/`If-None-Match` (strong/weak),
  `If-Modified-Since`/`If-Unmodified-Since`, `If-Range`, in the §13.2.2
  precedence order → 304 / 412.
- **Range** (§14): single-range → 206 + `Content-Range`; **multi-range →
  `multipart/byteranges`**; unsatisfiable → 416; `Accept-Ranges: bytes`. A
  `MaxRanges` cap guards against range-amplification.
- **Proactive content negotiation** (§12): `Accept`, `Accept-Encoding`,
  `Accept-Language` with `q`-values, `Vary`, and the 406-vs-default policy.
- **On-the-fly content coding**: opt-in gzip / brotli / deflate compression
  (weakens the ETag, updates `Vary`).
- **QUERY** (RFC 10008): a safe/idempotent/cacheable body-carrying read; runs the
  same representation pipeline as GET (ETag/304, negotiation), with
  `Content-Location` and the §4 `Content-Type`-required rule.

### Authentication (RFC 9110 §11)

- A scheme-agnostic framework: reads `Authorization`, dispatches to a registered
  scheme, answers 401 with one `WWW-Authenticate` challenge per scheme. Never
  validates itself — each scheme defers to an app-supplied validator, so `Core`
  carries no credential store.
- **Basic** (RFC 7617), **Bearer** (RFC 6750), **Digest** (RFC 7616 —
  challenge-response, SHA-256 + MD5-interop, stateless HMAC nonce, `qop=auth`,
  constant-time compare), **Token** (non-standard — Rails/GitHub-style, bare +
  parameterized forms).
- **mutual TLS (mTLS)** — a separate transport-layer mechanism: server requires
  + validates a client cert, surfaces the subject to handlers; the client can
  present one.

### Caching (RFC 9111)

- Direction-neutral caching *logic* in `Core` (Cache-Control grammar, age /
  freshness §4.2, storability §3, revalidation, `Vary` keying §4.1,
  private/shared §3.5) + a client-side cache (`HTTP2CachingClient`) that serves
  fresh hits with no round trip, revalidates stale entries conditionally, serves
  stale within `max-stale`/`stale-while-revalidate`, returns 504 for
  `only-if-cached` misses, and invalidates on unsafe methods (§4.4).

### Streaming, trailers & gRPC

- A streaming seam alongside the buffered handler: incremental request-body read
  + response-body write + **response trailers** (RFC 9113 §8.1) — server and
  client (`HTTP2ClientStream`).
- **gRPC** runs over the stack (unary, server-streaming, client-streaming, bidi)
  with `grpc-status` in trailers — verified against the real `Grpc.Net.Client`,
  with **zero gRPC-specific production code**.

### 1xx interim responses

- Automatic **`100 Continue`** (server) for `Expect: 100-continue`.
- Handler-driven **103 Early Hints** (RFC 8297).
- Client surfaces interim responses on `HTTP2Response.InformationalResponses`.

### Client features

- Full client-side multiplexing; flow-control receive replenishment; priority
  signaling.
- **Robustness**: REFUSED_STREAM auto-retry, `MAX_CONCURRENT_STREAMS` gating
  (queue, don't fail), GOAWAY/exhaustion → retry-safe
  `HTTP2RequestNotProcessedException`, PING keepalive / dead-connection
  detection, client-side flood bounds.
- **`HTTP2ClientPool`**: a single-origin pool that keeps N warm connections
  (default 4), routes to the least-loaded, transparently fails over
  not-processed requests, and self-heals dead connections in the background.

### Transports

- TLS `h2` (ALPN, TLS 1.2/1.3), with optional mTLS.
- Cleartext `h2c` (prior knowledge) — server and client.

---

## Non-standard extensions supported

These are widely used but are **not** IETF standards; they're supported because
they're common in the wild:

- **gRPC** — the de-facto RPC protocol on HTTP/2 (length-prefixed messages,
  `application/grpc`, `grpc-status` trailers). Not an RFC.
- **Token authentication** — Rails' `ActionController::HttpAuthentication::Token`
  and GitHub-style `Authorization: token …` (the `draft-hammer-http-token-auth`
  I-D expired).

## Security hardening summary

| Threat | Defense |
|---|---|
| HTTP/2 Rapid Reset (CVE-2023-44487) | Peer-reset-ratio guard → `GOAWAY ENHANCE_YOUR_CALM` |
| CONTINUATION flood (CVE-2024-27316) | Bounded header buffer + per-block CONTINUATION cap (both roles) |
| PING / SETTINGS / PRIORITY_UPDATE floods | Unproductive-frame counting |
| Slowloris (trickle / withhold) | Handshake / preface / idle / in-progress / SETTINGS-ACK timeouts |
| Memory exhaustion by fast producer | Consumption-driven backpressure + bounded buffered body |
| Stream-ID exhaustion | Proactive GOAWAY + `REFUSED_STREAM` |
| Oversized header lists | Inbound + outbound `MAX_HEADER_LIST_SIZE` |
| Range amplification | `MaxRanges` cap on a byte-range set |
| Credential timing oracles | Constant-time compare in Digest (`FixedTimeEquals`) |

## Explicitly out of scope

- **HTTP/3** — a separate transport (QUIC/RFC 9000-9002, QPACK/RFC 9204, H3
  framing/RFC 9114) that shares only the HTTP *semantics* with this stack, not
  the framing/HPACK/flow-control core. Belongs in its own project.
- **Server push** (`PUSH_PROMISE` outbound) — deprecated; we advertise
  `ENABLE_PUSH=0` and reject inbound pushes.
- **RFC 7540 priority** (stream dependencies/weights) — superseded by RFC 9218;
  parsed-and-ignored (only structural self-dependency is validated).
- **RFC 7540 `Upgrade: h2c`** — removed in RFC 9113 §3.1; only prior-knowledge
  h2c is implemented.
- **`Accept-Charset`** — deprecated in RFC 9110 §12.5.2.
- **Multi-origin connection pooling** — the pool is single-origin by design.

---

## Conformance & testing

### h2spec (RFC 9113 + RFC 7541)

**146 / 146** over both the TLS (`h2`, :8443) and cleartext (`h2c`, :8080)
listeners, on Windows and Linux. Reproduce with `pwsh tests/h2spec.ps1` /
`tests/h2spec.sh`; walkthrough in
[`tests/TestingAgainst_h2spec.md`](tests/TestingAgainst_h2spec.md).

### Autobahn (RFC 6455 + RFC 7692)

**517 / 517** — the full WebSocket conformance suite including sections 12/13
(permessage-deflate). Reproduce with `tests/autobahn.{ps1,sh}` (Docker);
walkthrough in
[`tests/TestingAgainst_Autobahn.md`](tests/TestingAgainst_Autobahn.md). The
critical cases are also pinned in the Docker-free `h2wsconformance` harness.

### In-repo harness suite

**70 / 70** harness runs (`tests/run-tests.ps1`), each self-reporting its own
checks — e.g. h2semantics 59/59, h2authtest 33/33, h2cachetest 23/23,
h2clienttest 14/14, grpc 16/16, h2pool 12/12. See
[`tests/README.md`](tests/README.md) for the full harness list.

### Interop reference peers (test-only)

| Peer | Exercises |
|---|---|
| .NET `HttpClient` (strict) | our **server** — semantics, auth, conditional/range, compression, interim, HPACK decode of our encoder |
| .NET **Kestrel** | our **client** — HPACK decode, flow control, h2c |
| **curl** (nghttp2, Linux) | our server over both `h2` and `h2c` |
| **`Grpc.Net.Client`** | our server + streaming seam — all four gRPC call types |

---

## References

- RFC 9113 — HTTP/2
- RFC 7541 — HPACK: Header Compression for HTTP/2
- RFC 7301 — TLS Application-Layer Protocol Negotiation (ALPN)
- RFC 9218 — Extensible Prioritization Scheme for HTTP
- RFC 8441 — Bootstrapping WebSockets with HTTP/2
- RFC 6455 — The WebSocket Protocol
- RFC 7692 — Compression Extensions for WebSocket (permessage-deflate)
- RFC 9110 — HTTP Semantics
- RFC 9111 — HTTP Caching
- RFC 7617 — The 'Basic' HTTP Authentication Scheme
- RFC 6750 — OAuth 2.0 Bearer Token Usage
- RFC 7616 — HTTP Digest Access Authentication
- RFC 8297 — An HTTP Status Code for Indicating Hints (103 Early Hints)
- RFC 10008 — The HTTP QUERY Method
- RFC 5861 — HTTP Cache-Control Extensions for Stale Content
- RFC 8941 — Structured Field Values for HTTP
- RFC 4647 — Matching of Language Tags
