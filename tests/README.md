# Tests

Interop and attack harnesses for the hand-rolled HTTP/2 stack. Every
wire-visible feature is checked against a .NET counterpart on the *opposite*
side of the wire (server ↔ `HttpClient`/Kestrel) *and* against hand-rolled
raw-frame clients that exercise the framing layer directly.

## Running everything

```powershell
# from the repo root, Windows PowerShell or pwsh:
powershell -ExecutionPolicy Bypass -File tests/run-tests.ps1
```

The runner builds the solution, runs the self-contained harnesses, then starts
the Demo host on `:8443` and drives the demo-dependent harnesses against it
(one process per scenario), and prints a pass/fail summary. Flags:

- `-NoBuild` — skip the build step (assumes a current build).
- `-Filter <substr>` — only run harnesses whose label/project matches.

Current status: **69/69 harness runs pass** (each self-reports its own check
count — e.g. h2semantics 51/51, h2clienttest 14/14, h2authtest 18/18,
h2cachetest 23/23, h2clientpriority 15/15, h2c 12/12).

## The harnesses

| Harness | Kind | Covers |
|---|---|---|
| `h2hufftest`       | self-contained | HPACK Huffman decode: RFC 7541 Appendix B, 5000-round fuzz, §5.2 padding edge cases |
| `h2hpackenc`       | self-contained | HPACK encoder: static/dynamic-table indexing, Huffman coding, size-update signaling (round-trips via our decoder) |
| `h2interim`        | self-contained | 1xx interim responses: automatic 100-continue + 103 Early Hints, vs. our client + .NET HttpClient (7 checks) |
| `h2c`              | self-contained | cleartext HTTP/2 (prior knowledge, no TLS): our client ↔ our server, .NET HttpClient prior-knowledge, and .NET Kestrel h2c (12 checks) |
| `h2streamtest`     | self-contained | `HTTP2StreamManager` unit tests: pruning, window adjust, ID reuse |
| `h2shutdowntest`   | self-contained | graceful `GOAWAY` shutdown timing (own server + port) |
| `h2timeout`        | self-contained | Slowloris/timeout hardening: handshake, preface, partial header block, withheld payload, SETTINGS-ACK timeouts |
| `h2clienttest`     | self-contained | our client vs. our server *and* vs. .NET Kestrel (14 checks) |
| `h2authtest`       | self-contained | RFC 9110 §11 auth framework + Basic/Bearer/Digest (RFC 7616)/Token (non-standard) + mTLS, vs. our client + .NET `HttpClient` (33 checks) |
| `h2cachetest`      | self-contained | RFC 9111 client cache: freshness, revalidation, Vary, shared/private (23 checks) |
| `h2clientpriority` | self-contained | client-side RFC 9218 emission vs. our server + Kestrel (15 checks) |
| `h2clientrobust`   | self-contained | client robustness vs. a raw mock server: REFUSED_STREAM retry, MCS gating, GOAWAY retry-safety, keepalive (8 checks) |
| `h2streaming`      | self-contained | streaming bodies + response trailers (gRPC-style) vs. our client *and* .NET HttpClient (8 checks) |
| `h2wsclient`       | self-contained | client-side CONNECT tunnel + WebSocket (text/binary/close) + permessage-deflate (RFC 7692) negotiation over the RFC 8441 CONNECT path vs. our server (15 checks) |
| `h2flowbatch`      | self-contained | WINDOW_UPDATE batching + startup connection-window bump on a large upload (4 checks) |
| `h2rfcpolish`      | self-contained | MUST-level details h2spec misses: padded-DATA flow accounting (§6.1), closed-stream DATA connection-window credit (§6.9), cookie crumb reassembly (§8.2.3) (8 checks) |
| `h2backpressure`   | self-contained | consumption-driven flow-control backpressure (window returned on consume, not receipt) + bounded buffered body (`MaxRequestBodySize`) (6 checks) |
| `h2wsconformance`  | self-contained | RFC 6455 WebSocket framing conformance — the critical Autobahn cases (framing, fragmentation, UTF-8 §8.1, close §7.4) + permessage-deflate (RFC 7692) round-trips, driven raw against our `WebSocketConnection` (31 checks) |
| `h2compress`       | self-contained | on-the-fly gzip/brotli/deflate content coding vs. our client + .NET HttpClient (11 checks) |
| `h2query`          | self-contained | RFC 10008 the HTTP QUERY method (safe/idempotent body-carrying read) vs. our client + .NET HttpClient — filtering, Content-Location, ETag/304, Allow, 400/404/405 (12 checks) |
| `grpc`             | self-contained | gRPC over our stack — all four call types (unary, server-/client-streaming, bidi), length-prefix framing, `grpc-status` trailers — vs. our client (incl. the streaming request API) + the real `Grpc.Net.Client` (16 checks) |
| `h2semantics`      | demo-driven    | RFC 9110 GET/HEAD/OPTIONS, conditional, Range, negotiation (51 checks) |
| `h2attack`         | demo-driven    | flood / malformed / trailers / idle-stream / rapid-reset / exhaustion / header-limit |
| `h2connect`        | demo-driven    | plain + extended CONNECT, WebSocket framing, malformed CONNECT |
| `h2priority`       | demo-driven    | server-side RFC 9218 scheduling: urgency ordering, PRIORITY_UPDATE |
| `autobahn-server`  | server         | RFC 6455 WebSocket echo server (HTTP/1.1 Upgrade) for the Autobahn TestSuite — not a pass/fail harness, see below |
| `h2raw`, `h2test`  | diagnostic     | raw frame loggers / ad-hoc request drivers (not in the pass/fail gate) |

"demo-driven" harnesses talk to the Demo host on `https://localhost:8443`.
"self-contained" harnesses spin up their own server(s) on private ports
(9443–9469).

## h2spec conformance

[h2spec](https://github.com/summerwind/h2spec) is the canonical HTTP/2
conformance suite (RFC 9113 + RFC 7541). This stack passes **146 / 146** over
*both* the TLS (`h2`, :8443) and cleartext (`h2c`, :8080) listeners, on Windows
*and* Linux (WSL/Debian). The easiest way to reproduce it — a wrapper for each
platform that builds, starts the demo, runs h2spec on both transports, and stops
the demo again:

```bash
tests/h2spec.sh          # Linux / macOS
pwsh tests/h2spec.ps1    # Windows
```

For the full walkthrough — installing h2spec, running individual sections,
interpreting output, and the two gotchas (`127.0.0.1` not `localhost`; drain the
demo's console output) — see **[TestingAgainst_h2spec.md](TestingAgainst_h2spec.md)**.
The conformance history (the initial 136/146 and the six categories that closed
the 10 failures) is in [`../CLAUDE.md`](../CLAUDE.md) under the h2spec entry.

## Autobahn WebSocket conformance

[Autobahn|TestSuite](https://github.com/crossbario/autobahn-testsuite) is the
canonical RFC 6455 WebSocket conformance suite. This stack passes **517 / 517**
cases — the full suite, including sections 12/13 (`permessage-deflate`, RFC 7692,
negotiated in no-context-takeover mode). It drives the
`autobahn-server` echo host, which runs the same `WebSocketConnection` framing
used in production over a plain-TCP tunnel behind an HTTP/1.1 Upgrade handshake
(Autobahn speaks WebSocket over HTTP/1.1, not RFC 8441 over HTTP/2 — but the
framing under test is transport-agnostic). Run from the official Docker image:

```bash
tests/autobahn.sh          # Linux / macOS
pwsh tests/autobahn.ps1    # Windows (Docker Desktop)
```

The critical cases (framing, fragmentation, UTF-8 §8.1, close §7.4) are also in
the committed `h2wsconformance` harness, which runs in the gate above with no
Docker needed. For the walkthrough (installing Docker, reading the report, the
HTTP/1.1-handshake rationale, and the UTF-8/close conformance history) see
**[TestingAgainst_Autobahn.md](TestingAgainst_Autobahn.md)**.
