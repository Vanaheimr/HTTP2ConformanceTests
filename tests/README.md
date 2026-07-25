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
`HermodTests/HTTP2/` (166 tests), so they are no longer harnesses here. What
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
| `h2bench`          | benchmark      | throughput, latency distribution and allocation — not a pass/fail harness, see below |
| `h2raw`, `h2test`  | diagnostic     | raw frame loggers / ad-hoc request drivers (not in the pass/fail gate) |

"demo-driven" harnesses talk to the Demo host on `https://localhost:8443`
(started by the runner). The former "self-contained" harnesses — which spun up
their own server(s) on private ports — are now NUnit fixtures in Hermod's
`HermodTests/HTTP2/`.

## Benchmarks (h2bench)

Everything else in this repository has a number behind it — 166 unit tests, 48
harness runs, h2spec 146/146, Autobahn 517/517. Performance had none, which made
"readable rather than fast" an assumption rather than a finding. `h2bench` exists
to turn it into a finding, and to give any future optimisation a baseline to beat.

```bash
dotnet run -c Release --project tests/h2bench                 # everything
dotnet run -c Release --project tests/h2bench -- hpack        # one scenario
dotnet run -c Release --project tests/h2bench -- --mib 256    # bigger transfers
dotnet run -c Release --project tests/h2bench -- --cleartext  # h2c instead of TLS
```

Scenarios: `frames` and `hpack` (pure in-memory, low noise), `requests` (small
GET at 1/8/64 concurrent, with latency percentiles), `throughput` and `upload`
(large body). Each reports operations/s, bytes allocated per operation, and the
GC collections provoked.

**What these numbers are not.** Client and server run in the *same process* over
loopback, so every figure covers the whole round trip — both roles, TLS and the
loopback stack, sharing one machine. They track this stack against itself over
time; they are not a comparison with nginx, and quoting them as one is quoting
them wrong. Allocation is process-wide (`GC.GetTotalAllocatedBytes`), which is
the honest answer to "what does one request cost this stack" precisely because
it includes both roles.

Indicative figures from the first run (Windows 11, 16 cores, .NET 10, Release):

| | |
|---|---|
| small GET | ~1 000 req/s, p50 0.85 ms, ~9.1 KiB allocated per request |
| 64 MiB download / upload | ~180 / ~205 MiB/s, ~6.2× the payload allocated |
| HPACK encode+decode | ~145 k blocks/s, 2.2 KiB per block (270 B on the wire cold, 99 B warm) |
| frame serialize+parse | ~2 M frames/s, 1 104 B per 1 KiB frame |

The first run also found something the numbers alone do not explain: request
throughput is flat at ~1 000/s for *every* concurrency while latency grows
linearly with it — the signature of a ~1 ms serialized stage per request. Nagle
(ruled out: `NoDelay` changed nothing) and TLS (ruled out: `--cleartext` is
identical) have both been eliminated; the investigation is tracked separately.

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
