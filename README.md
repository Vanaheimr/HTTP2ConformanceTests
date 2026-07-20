# HTTP/2 Conformance Tests & Demo (C# / .NET 10)

The runnable **demo host** and the **conformance / interop test drivers** for the
from-scratch HTTP/2 stack that lives in the Vanaheimr **Hermod** library (built
directly on `SslStream` — no Kestrel, no `System.Net.Http` HTTP/2 stack,
everything hand-rolled). Hermod is pulled in here as a git submodule under
`libs/`; this repository adds:

- a runnable **demo host** (`Demo/`) on TLS `h2` (`:8443`) and cleartext `h2c` (`:8080`);
- **demo-driven raw-frame harnesses** (`tests/`) — abuse/hardening, CONNECT/
  WebSocket, RFC 9218 priority scheduling, RFC 9110 semantics — driven against
  the live demo host;
- **external-conformance drivers** — [h2spec](https://github.com/summerwind/h2spec)
  (**146/146** over `h2` + `h2c`) and the
  [Autobahn TestSuite](https://github.com/crossbario/autobahn-testsuite) (**517/517**).

The bulk of the coverage — **102 NUnit unit + integration tests** — lives with
the stack in Hermod (`HermodTests/HTTP2/`); see the [Test](#test) section.

📖 **The stack's own reference** — the API, the RFC-compliance matrix, the
feature-by-feature breakdown, the security-hardening summary, and what's out of
scope — lives next to the code in **`libs/Hermod/Hermod/HTTP2/README.md`**
(GitHub: [Vanaheimr/Hermod](https://github.com/Vanaheimr/Hermod) →
`Hermod/HTTP2/README.md`). This repo's [`docs/BUILD_LOG.md`](docs/BUILD_LOG.md)
holds the full chronological build history.

> ⚠️ **Reference implementation** — built for learning the wire protocol, not
> for production traffic (single-process demo host, no server push, etc.).

## Requirements

- .NET 10 SDK (`net10.0` target)

## Get the sources

The hand-rolled HTTP/2 stack lives in the Vanaheimr **Hermod** library, which —
together with **Styx** — is pulled in as a git submodule under `libs/`. Clone
**with submodules**, otherwise `libs/Hermod` and `libs/Styx` are empty and
nothing builds:

```bash
git clone --recurse-submodules https://github.com/Vanaheimr/HTTP2ConformanceTests.git

# already cloned without --recurse-submodules?
git submodule update --init --recursive
```

## Build & run

```bash
# from the repository root — the solution lives here, not under src/:
dotnet build HTTP2.slnx
dotnet run --project Demo/HTTP2.Demo.csproj
```

The demo listens on `https://localhost:8443` (HTTP/2 over TLS, self-signed cert
generated at startup) and additionally on `http://localhost:8080` (cleartext
HTTP/2 — "h2c" — with prior knowledge, no TLS).

## Test

Most of the coverage is **102 NUnit tests** under
[`libs/Hermod/HermodTests/HTTP2/`](libs/Hermod/HermodTests/HTTP2) — the
HPACK/Huffman codec and stream state machine, plus the full in-process
integration matrix (streaming bodies + trailers, RFC 9111 caching, auth/mTLS,
timeout hardening, backpressure, the client pool/robustness, WebSocket framing,
and client interop vs. .NET Kestrel and the real gRPC client). Run them from
this solution:

```bash
dotnet test HTTP2.slnx --filter "FullyQualifiedName~Tests.HTTP2"
```

On top of that, the [`tests/`](tests/) folder holds the **demo-driven**
raw-frame scenarios (abuse/hardening, CONNECT/WebSocket, RFC 9218 priority
scheduling, RFC 9110 semantics) that run against a live demo host. Run the whole
harness suite (builds, starts the demo host, drives every scenario) with:

```powershell
powershell -ExecutionPolicy Bypass -File tests/run-tests.ps1
```

Current status: **48/48 harness runs pass**, and the stack scores **146/146 on
[h2spec](https://github.com/summerwind/h2spec)** (the canonical HTTP/2
conformance suite) over *both* the TLS and cleartext-h2c listeners. Reproduce
the h2spec run with a single command —

```powershell
pwsh tests/h2spec.ps1   # builds, starts the demo, runs h2spec on both transports
```

— see [`tests/TestingAgainst_h2spec.md`](tests/TestingAgainst_h2spec.md) for the
full h2spec walkthrough, [`tests/README.md`](tests/README.md) for the harness
layout, and [`docs/BUILD_LOG.md`](docs/BUILD_LOG.md) for the conformance breakdown.

The WebSocket framing (RFC 6455) likewise passes **517/517** cases of the
canonical [Autobahn TestSuite](https://github.com/crossbario/autobahn-testsuite)
— the full suite, including `permessage-deflate` (RFC 7692) compression —
`pwsh tests/autobahn.ps1` / `tests/autobahn.sh` (Docker), with the critical
cases also pinned in the committed `h2wsconformance` harness (no Docker needed);
see [`tests/TestingAgainst_Autobahn.md`](tests/TestingAgainst_Autobahn.md).

Ad-hoc `curl` checks against the demo host:

```bash
curl --http2 -k https://localhost:8443/
curl --http2 -k https://localhost:8443/echo -d "Hello HTTP/2!"
curl --http2 -k https://localhost:8443/large   # 128 KiB — exercises flow control
curl --http2 -k https://localhost:8443/slow    # 2 s handler — exercises multiplexing

# RFC 9110 core mechanics — GET/HEAD/OPTIONS, conditional requests, Range:
curl --http2 -k -I https://localhost:8443/files/resource.txt          # HEAD
curl --http2 -k -X OPTIONS https://localhost:8443/files/resource.txt  # -> 204 + Allow
curl --http2 -k -H 'Range: bytes=0-9' https://localhost:8443/files/resource.txt
curl --http2 -k -H 'If-None-Match: "<etag from a prior response>"' https://localhost:8443/files/resource.txt

# RFC 10008 — the HTTP QUERY method (a safe, body-carrying read). /search:
curl --http2 -k https://localhost:8443/search                 # GET -> whole corpus
curl --http2 -k -X QUERY --data 'ap' https://localhost:8443/search   # QUERY -> filtered (note Content-Location)

# RFC 9110 content negotiation — /files/greeting has en/de text + en JSON variants:
curl --http2 -k https://localhost:8443/files/greeting                        # server default (en text)
curl --http2 -k -H 'Accept-Language: de' https://localhost:8443/files/greeting   # -> German
curl --http2 -k -H 'Accept: application/json' https://localhost:8443/files/greeting  # -> JSON (note the Vary header)

# RFC 9110 §11 auth — /secret needs Basic alice:secret or Bearer valid-token-123:
curl --http2 -k -i https://localhost:8443/secret                             # -> 401 + WWW-Authenticate
curl --http2 -k -u alice:secret https://localhost:8443/secret                # -> 200
curl --http2 -k -H 'authorization: Bearer valid-token-123' https://localhost:8443/secret  # -> 200

# cleartext h2c (prior knowledge — no TLS), on :8080:
curl --http2-prior-knowledge http://localhost:8080/
curl --http2-prior-knowledge http://localhost:8080/echo -d "Hello h2c!"
```

`-k` skips certificate verification (self-signed). `--http2` forces HTTP/2 over
TLS via ALPN; `--http2-prior-knowledge` speaks cleartext HTTP/2 directly (no
Upgrade, no TLS). Note: the curl bundled with Windows has no HTTP/2 support and
silently falls back to HTTP/1.1.

## Project layout

Each public enum / interface / class / struct / record lives in its **own file
named after the type**. The HTTP/2 stack itself lives inside the **Hermod**
submodule; this repository wraps it with a runnable demo, the remaining
live-host harnesses, and the solution:

```
HTTP2ConformanceTests/               solution HTTP2.slnx (at the repo root)
├── libs/
│   ├── Hermod/                      ← git submodule (Vanaheimr Hermod)
│   │   ├── Hermod/HTTP2/            the hand-rolled HTTP/2 stack:
│   │   │   ├── Core/                direction-neutral — framing, HPACK (+ Huffman), the stream
│   │   │   │                        state machine + flow control, settings, the request/streaming
│   │   │   │                        seams, RFC 9110 semantics, RFC 9111 cache logic
│   │   │   ├── Server/              HTTP2Connection + HTTP2Server (TLS/ALPN, mTLS, cleartext h2c)
│   │   │   ├── Client/              HTTP2ClientConnection + HTTP2Client + caching client + pool
│   │   │   ├── WebSocket/           RFC 6455 + RFC 7692 framing over IHTTP2Tunnel
│   │   │   └── Auth/                RFC 9110 §11 framework + Basic/Bearer/Digest/Token schemes
│   │   └── HermodTests/HTTP2/       the 102 NUnit tests + shared fixtures (H2, TestH2Server,
│   │                                H2Raw, MockH2Server, KestrelH2Server)
│   └── Styx/                        ← git submodule (Vanaheimr Styx — Hermod's dependency)
├── Demo/                            runnable demo host (→ Hermod, Styx) + example handlers
├── tests/                          demo-driven raw-frame harnesses + h2spec/Autobahn drivers + tools
└── docs/BUILD_LOG.md                full chronological build history
```

For the file-by-file breakdown of the stack, see the Architecture tables in
[`CLAUDE.md`](CLAUDE.md) and `libs/Hermod/Hermod/HTTP2/README.md`.

## The HTTP/2 stack

The hand-rolled stack itself — framing, HPACK, the stream state machine, flow
control, the server and client roles, WebSocket framing, HTTP semantics, auth,
and caching — lives in the **Hermod** submodule under `libs/Hermod/Hermod/HTTP2/`.

Its **complete reference** — the public API and where application logic plugs in,
the RFC-compliance matrix, the feature-by-feature breakdown, the non-standard
extensions, the security-hardening summary, what's explicitly out of scope, the
interop reference peers, and the RFC list — is documented next to the code in
**[`libs/Hermod/Hermod/HTTP2/README.md`](libs/Hermod/Hermod/HTTP2/README.md)**
(on GitHub: [Vanaheimr/Hermod](https://github.com/Vanaheimr/Hermod) →
`Hermod/HTTP2/README.md`). For the file-by-file architecture see
[`CLAUDE.md`](CLAUDE.md).

## Status & roadmap

The stack is HTTP/2 feature-complete and verified end-to-end — **102 NUnit
tests** + **48 live-host harness runs** + **h2spec 146/146** + **Autobahn
517/517**. The stack's own reference lives in
[`libs/Hermod/Hermod/HTTP2/README.md`](libs/Hermod/Hermod/HTTP2/README.md);
[`docs/BUILD_LOG.md`](docs/BUILD_LOG.md) is the full chronological build log
(every feature, why it was built that way, and how it was verified); and
[`CLAUDE.md`](./CLAUDE.md) holds the architecture, conventions, and a
current-state summary (the agent/working-notes file).

## License

Apache License 2.0 — © 2010-2026 GraphDefined GmbH. The full text is in
[`LICENSE`](LICENSE), and every source file carries the standard Apache-2.0
header. The Hermod and Styx submodules are likewise Apache-2.0.
