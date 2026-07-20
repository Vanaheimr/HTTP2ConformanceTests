# HTTP/2 Conformance Tests & Demo — C# / .NET 10

This repository is the **runnable demo host** plus the **conformance / interop
test drivers** for the from-scratch HTTP/2 stack (built directly on `SslStream`
— no `Kestrel`, no `HttpListener`, no `System.Net.Http` HTTP/2 stack). The stack
itself lives in the Vanaheimr **Hermod** library, pulled in here as a git
submodule under `libs/Hermod/Hermod/HTTP2/` (split by concern into `Core` — the
direction-neutral framing, HPACK, stream layer, settings, HTTP semantics —,
`Server`, `Client`, `WebSocket`, and `Auth`). This repo adds the `Demo/` host,
the `tests/` live-host raw-frame harnesses, and the h2spec/Autobahn drivers; the
102 NUnit unit + integration tests live with the stack in Hermod
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

**Tests:** most coverage is the **102 NUnit tests** in
`libs/Hermod/HermodTests/HTTP2/` — run `dotnet test HTTP2.slnx --filter
"FullyQualifiedName~Tests.HTTP2"`. The remaining **48** live-host harness runs
(demo-driven raw-frame scenarios) run via `tests/run-tests.ps1`; conformance via
`tests/h2spec.ps1` (146/146 over h2 + h2c) and Autobahn (517/517). See
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
| `HTTP2Server.cs` | `TcpListener` + `SslStream` with ALPN `h2` negotiation, TLS-handshake timeout, HTTP/1.1 fallback stub; optional `Cleartext` mode (h2c prior-knowledge, no TLS) |

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
| `Program.cs` | Demo host (TLS `h2` on :8443 + cleartext `h2c` on :8080) + self-signed cert + example request/connect/resource handlers (the app-logic plug-in point) |

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
  in/outbound `MAX_HEADER_LIST_SIZE`, and Slowloris/idle/handshake/SETTINGS-ACK
  timeouts.
- **Server + client, two transports:** mirror connection roles over TLS `h2`
  (ALPN, + optional mTLS) and cleartext `h2c` (prior knowledge). The client adds
  robustness (REFUSED_STREAM auto-retry, MAX_CONCURRENT_STREAMS gating,
  GOAWAY/exhaustion → retry-safe `HTTP2RequestNotProcessedException`, PING
  keepalive) and a single-origin connection pool (`HTTP2ClientPool` — warm
  connections, least-loaded routing, failover, background self-heal).
- **Tunneling / WebSocket / gRPC:** plain + extended CONNECT (RFC 8441), RFC 6455
  framing + RFC 7692 permessage-deflate (both roles), and real gRPC — all four
  call types — over the streaming seam (`HTTP2StreamingHandler` +
  request/response streams + response trailers), interop-tested against
  `Grpc.Net.Client`.
- **HTTP semantics (Core, version-independent, never touches framing):** RFC 9110
  methods / conditional requests / Range (single + multi `multipart/byteranges`) /
  proactive content negotiation / on-the-fly gzip-brotli-deflate; the QUERY method
  (RFC 10008); 1xx interim responses (`Expect: 100-continue`, 103 Early Hints);
  the §11 auth framework (Basic/Bearer/Digest/Token + transport-layer mTLS); and
  RFC 9111 client-side caching (freshness, revalidation, `Vary`, shared/private).

**Verification:** `tests/run-tests.ps1` → **48/48** harness runs; **h2spec
146/146** over both transports (Windows + Linux); **Autobahn 517/517** (full RFC
6455 + permessage-deflate). Reference peers (test-only, don't count against the
BCL-only rule): .NET `HttpClient`, Kestrel, curl (nghttp2), `Grpc.Net.Client`.
The pure in-memory Core unit tests (Huffman, HPACK encoder, `HTTP2StreamManager`)
live as NUnit fixtures in Hermod's `HermodTests/HTTP2/`, not as harnesses here.

All originally-planned roadmap tracks (A–E) plus every follow-up extension are
**done**; nothing is open. The full history — feature by feature, with the design
rationale, the bugs caught, and the exact verification for each — is in
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
