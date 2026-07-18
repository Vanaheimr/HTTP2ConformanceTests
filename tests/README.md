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

Current status: **55/55 harness runs pass** (each self-reports its own check
count — e.g. h2semantics 51/51, h2clienttest 14/14, h2authtest 18/18,
h2cachetest 23/23, h2clientpriority 15/15).

## The harnesses

| Harness | Kind | Covers |
|---|---|---|
| `h2hufftest`       | self-contained | HPACK Huffman decode: RFC 7541 Appendix B, 5000-round fuzz, §5.2 padding edge cases |
| `h2hpackenc`       | self-contained | HPACK encoder: static/dynamic-table indexing, Huffman coding, size-update signaling (round-trips via our decoder) |
| `h2interim`        | self-contained | 1xx interim responses: automatic 100-continue + 103 Early Hints, vs. our client + .NET HttpClient (7 checks) |
| `h2streamtest`     | self-contained | `HTTP2StreamManager` unit tests: pruning, window adjust, ID reuse |
| `h2shutdowntest`   | self-contained | graceful `GOAWAY` shutdown timing (own server + port) |
| `h2timeout`        | self-contained | Slowloris/timeout hardening: handshake, preface, partial header block, withheld payload, SETTINGS-ACK timeouts |
| `h2clienttest`     | self-contained | our client vs. our server *and* vs. .NET Kestrel (14 checks) |
| `h2authtest`       | self-contained | RFC 9110 §11 auth framework + Basic/Bearer + mTLS (18 checks) |
| `h2cachetest`      | self-contained | RFC 9111 client cache: freshness, revalidation, Vary, shared/private (23 checks) |
| `h2clientpriority` | self-contained | client-side RFC 9218 emission vs. our server + Kestrel (15 checks) |
| `h2clientrobust`   | self-contained | client robustness vs. a raw mock server: REFUSED_STREAM retry, MCS gating, GOAWAY retry-safety, keepalive (8 checks) |
| `h2streaming`      | self-contained | streaming bodies + response trailers (gRPC-style) vs. our client *and* .NET HttpClient (8 checks) |
| `h2wsclient`       | self-contained | client-side CONNECT tunnel + WebSocket (text/binary/close) vs. our server (10 checks) |
| `h2flowbatch`      | self-contained | WINDOW_UPDATE batching + startup connection-window bump on a large upload (4 checks) |
| `h2compress`       | self-contained | on-the-fly gzip/brotli/deflate content coding vs. our client + .NET HttpClient (11 checks) |
| `h2semantics`      | demo-driven    | RFC 9110 GET/HEAD/OPTIONS, conditional, Range, negotiation (51 checks) |
| `h2attack`         | demo-driven    | flood / malformed / trailers / idle-stream / rapid-reset / exhaustion / header-limit |
| `h2connect`        | demo-driven    | plain + extended CONNECT, WebSocket framing, malformed CONNECT |
| `h2priority`       | demo-driven    | server-side RFC 9218 scheduling: urgency ordering, PRIORITY_UPDATE |
| `h2raw`, `h2test`  | diagnostic     | raw frame loggers / ad-hoc request drivers (not in the pass/fail gate) |

"demo-driven" harnesses talk to the Demo host on `https://localhost:8443`.
"self-contained" harnesses spin up their own server(s) on private ports
(9443–9449).

## h2spec conformance

[h2spec](https://github.com/summerwind/h2spec) is the canonical HTTP/2
conformance suite (RFC 9113 + RFC 7541). It is an external binary and is *not*
vendored here — download the release and point it at the running Demo host:

```powershell
# 1. start the demo host
dotnet run --project src/Demo/HTTP2.Demo.csproj
# 2. in another shell, against the TLS listener (self-signed -> -k):
h2spec -t -k -h localhost -p 8443 -P /echo
```

Result (h2spec 2.6.0): **146 / 146 pass, 0 skipped, 0 failed.** The initial run
scored 136/146; all 10 failures were closed in the h2spec-conformance track —
see [`../CLAUDE.md`](../CLAUDE.md) under the h2spec entry for the per-category
breakdown.
