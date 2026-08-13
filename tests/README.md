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

```bash
# from the repo root, on Linux/macOS/WSL:
tests/run-tests.sh
```

Two runners rather than one because the PowerShell version cannot be made to
work on Linux: it frees and polls the demo's ports with `Get-NetTCPConnection`,
which lives in the Windows-only `NetTCPIP` module, so installing `pwsh` does not
help. The bash runner is the Linux path, the same way `h2spec.sh` and
`autobahn.sh` are for the conformance drivers.

Either runner builds the solution, starts the Demo host on `:8443`, drives the
demo-dependent harnesses against it (one process per scenario), and prints a
pass/fail summary. Flags:

- `-NoBuild` / `--no-build` — skip the build step (assumes a current build).
- `-Filter <substr>` / `--filter <substr>` — only run harnesses whose
  label/project matches.

Current status: **48/48 harness runs pass on Windows** (each self-reports its own
check count — e.g. h2semantics 66/66, plus the h2attack / h2connect / h2priority
raw-frame scenarios). On Linux it is **45–46/48**: two `h2priority` scenarios are
flaky — which of them fails varies per run — and `h2attack trailers no-endstream`
fails reproducibly for a reason not yet diagnosed. That last one is not an
environment artifact: a WSL box and a `debian:13` container on GitHub's runners
produce a byte-identical stack trace. See *Harnesses that differ on Linux* in
[`../CLAUDE.md`](../CLAUDE.md); it is also why CI runs the Linux harness step
without gating on it.

The in-process unit + integration tests — HPACK/Huffman, the stream manager,
1xx interim responses, content coding, the QUERY method, streaming bodies +
trailers, RFC 9111 caching, auth/mTLS, timeout hardening, backpressure, the
client pool/robustness, RFC 6455 WebSocket framing, client interop vs. .NET
Kestrel (TLS `h2` and cleartext `h2c`), and gRPC (all four call types, vs. the
real `Grpc.Net.Client`) — now live as NUnit fixtures in Hermod's
`HermodTests/HTTP2/` (212 tests), so they are no longer harnesses here. What
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
| `h2interop`        | outbound       | our **client** against eight foreign HTTP/2 servers, full certificate validation — the only harness here that points our own stack outwards; see [`../INTEROP.md`](../INTEROP.md) |
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

The `probe` scenario is the diagnostic one: it splits each request at the moment
`StartRequestAsync` returns (our HEADERS on the wire), so our send path can be
told apart from server turnaround without instrumenting the library, and it runs
the same `HttpClient` against **Kestrel** as a control.

That control matters more than it sounds. It is what established that per-request
*latency* here is not anomalous at all — a loopback HTTP/2 round trip costs about
a millisecond in this environment, and our server is consistently faster at it
than Kestrel (0.73–1.14 ms vs 1.15–1.69 ms). An absolute number with nothing to
compare it against is how "slower than expected" gets mistaken for "slow".

What the split *did* find is real and ours: at 64 concurrent requests, "onto the
wire" goes from 0.22 ms to 13.65 ms while "waiting for the response" stays at
0.35 ms. The server multiplexes correctly; the client serialises request starts,
because `requestStartLock` is held across the HEADERS write and not just the
stream-ID allocation it exists to order. That caps one connection at roughly
1 000 req/s regardless of concurrency. The fix is tracked separately.

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
