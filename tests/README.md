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

The runner builds the solution, starts the Demo host on `:8443`, drives the
demo-dependent harnesses against it (one process per scenario), and prints a
pass/fail summary. Flags:

- `-NoBuild` — skip the build step (assumes a current build).
- `-Filter <substr>` — only run harnesses whose label/project matches.

Current status: **48/48 harness runs pass** (each self-reports its own check
count — e.g. h2semantics 59/59, plus the h2attack / h2connect / h2priority
raw-frame scenarios).

The in-process unit + integration tests — HPACK/Huffman, the stream manager,
1xx interim responses, content coding, the QUERY method, streaming bodies +
trailers, RFC 9111 caching, auth/mTLS, timeout hardening, backpressure, the
client pool/robustness, RFC 6455 WebSocket framing, client interop vs. .NET
Kestrel (TLS `h2` and cleartext `h2c`), and gRPC (all four call types, vs. the
real `Grpc.Net.Client`) — now live as NUnit fixtures in Hermod's
`HermodTests/HTTP2/` (103 tests), so they are no longer harnesses here. What
remains under `tests/` are the demo-driven raw-frame scenarios
(h2attack/h2connect/h2priority/h2semantics), the external-suite drivers (h2spec,
Autobahn), and the diagnostic tools (h2raw, h2test, autobahn-server).

## The harnesses

| Harness | Kind | Covers |
|---|---|---|
| `h2semantics`      | demo-driven    | RFC 9110 GET/HEAD/OPTIONS, conditional, Range (single + multi `multipart/byteranges`), negotiation (59 checks) |
| `h2attack`         | demo-driven    | flood / malformed / trailers / idle-stream / rapid-reset / exhaustion / header-limit |
| `h2connect`        | demo-driven    | plain + extended CONNECT, WebSocket framing, malformed CONNECT |
| `h2priority`       | demo-driven    | server-side RFC 9218 scheduling: urgency ordering, PRIORITY_UPDATE |
| `autobahn-server`  | server         | RFC 6455 WebSocket echo server (HTTP/1.1 Upgrade) for the Autobahn TestSuite — not a pass/fail harness, see below |
| `h2raw`, `h2test`  | diagnostic     | raw frame loggers / ad-hoc request drivers (not in the pass/fail gate) |

"demo-driven" harnesses talk to the Demo host on `https://localhost:8443`
(started by the runner). The former "self-contained" harnesses — which spun up
their own server(s) on private ports — are now NUnit fixtures in Hermod's
`HermodTests/HTTP2/`.

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
