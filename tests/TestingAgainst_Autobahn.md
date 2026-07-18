# Testing against the Autobahn TestSuite

[Autobahn|TestSuite](https://github.com/crossbario/autobahn-testsuite) is the
canonical conformance suite for **WebSocket (RFC 6455)** — the same tool used to
vet nearly every WebSocket implementation in the wild. Its *fuzzingclient* mode
connects to a WebSocket **echo server** and drives ~500 cases across framing,
fragmentation, ping/pong, UTF-8 handling, and the close handshake, then writes an
HTML/JSON report on exactly how the server behaved on each one.

This is the WebSocket counterpart of [h2spec](TestingAgainst_h2spec.md) for
HTTP/2. It is run the same way: an external tool (here, a Docker image), not
vendored, driven by a wrapper script per platform, with a committed
self-contained harness (`h2wsconformance`) covering the critical cases in the
normal pass/fail gate so the conformance behavior is verified even without Docker.

**Result: 517 / 517 cases pass** (verified with Autobahn under WSL/Debian) —
every case, including sections **12 & 13 (permessage-deflate, RFC 7692)**, the
optional per-message compression extension, which the echo server negotiates in
no-context-takeover mode (see [§ permessage-deflate](#permessage-deflate) below).

---

## What is actually under test (and the one wrinkle)

The suite tests the **WebSocket framing layer** — in this project,
[`Core/WebSocket.cs`](../src/Core/WebSocket.cs) (`WebSocketConnection`): masking,
opcodes, fragmentation reassembly, ping→pong, the close handshake, and RFC 6455
§8.1 UTF-8 validation.

The wrinkle: Autobahn's client speaks WebSocket over the **classic HTTP/1.1
`Upgrade` handshake**. This project's WebSockets run in production over **RFC 8441
extended CONNECT on HTTP/2** — Autobahn can't drive that directly. But the
framing layer under test is deliberately **transport-agnostic**: `WebSocketConnection`
sits on top of the byte-in/byte-out [`IHTTP2Tunnel`](../src/Core/IHTTP2Tunnel.cs)
seam and knows nothing about HTTP/2. So the echo server used here
([`tests/autobahn-server`](autobahn-server/Program.cs)) runs the **exact same
`WebSocketConnection` code** over a plain-TCP tunnel behind a minimal HTTP/1.1
Upgrade handshake. The handshake is test-only glue; **not one line of the framing
under test is test-specific** — this is precisely the reuse the `IHTTP2Tunnel`
seam exists for (the same argument by which `WebSocket.cs` would serve a
WebSocket-over-HTTP/3 endpoint unchanged).

---

## 1. Install Docker

The native `wstest` is legacy **Python 2** and painful to install; the official
Docker image is the maintained, cross-platform way to run the suite:

- **Windows / macOS:** install [Docker Desktop](https://www.docker.com/products/docker-desktop/).
- **Linux:** `sudo apt-get install docker.io` (or your distro's package), then
  ensure the daemon is running (`sudo systemctl start docker`) and your user can
  reach it.

```bash
docker pull crossbario/autobahn-testsuite   # optional; the wrappers pull on first run
```

## 2. The easy way — the wrapper script

Both wrappers build the echo server, start it (**output drained to a file** — same
gotcha as h2spec: an undrained console pipe can stall a busy server), run the
Autobahn fuzzingclient against it via Docker, parse the JSON report, and stop the
echo server again — exiting `0` iff every case passed.

**Windows — [`tests/autobahn.ps1`](autobahn.ps1):**

```powershell
pwsh tests/autobahn.ps1
pwsh tests/autobahn.ps1 -NoBuild
pwsh tests/autobahn.ps1 -Port 9010
```

On Docker Desktop the container reaches the host via `host.docker.internal`
(already in [`tests/autobahn/fuzzingclient.json`](autobahn/fuzzingclient.json)),
so the default bridge network just works.

**Linux / macOS — [`tests/autobahn.sh`](autobahn.sh):**

```bash
tests/autobahn.sh
tests/autobahn.sh --no-build
tests/autobahn.sh --port 9010
```

On Linux the wrapper runs the container with `--network host` and generates a
config pointing at `ws://127.0.0.1:<port>` (Linux has no `host.docker.internal`
by default). Report parsing prefers `jq`, then `python3`, then a `grep` heuristic.

Both exit non-zero and list the offending case IDs if any case does not pass. The
full human-readable report is written to `tests/autobahn/reports/index.html`.

## 3. The manual way

Two shells. First, build and start the echo server:

```bash
dotnet build src/HTTP2.slnx
dotnet tests/autobahn-server/bin/Debug/net10.0/autobahn-server.dll 9010
# -> [autobahn-server] WebSocket echo server listening on ws://127.0.0.1:9010/
```

Then run the fuzzingclient from the Docker image against it:

```bash
# Linux (host networking; config uses 127.0.0.1):
docker run --rm --network host \
  -v "$PWD/tests/autobahn:/config" \
  -v "$PWD/tests/autobahn/reports:/reports" \
  crossbario/autobahn-testsuite \
  wstest -m fuzzingclient -s /config/fuzzingclient.json

# Docker Desktop (Windows/macOS; config uses host.docker.internal):
docker run --rm \
  -v "${PWD}/tests/autobahn:/config" \
  -v "${PWD}/tests/autobahn/reports:/reports" \
  crossbario/autobahn-testsuite \
  wstest -m fuzzingclient -s /config/fuzzingclient.json
```

Open `tests/autobahn/reports/index.html` to browse the per-case results.

### Reading the report

Each case reports a **`behavior`** (and a **`behaviorClose`** for the closing
handshake), one of:

| Value | Meaning | Counts as |
|---|---|---|
| `OK` | fully correct | pass |
| `NON-STRICT` | correct outcome, reached in a spec-permitted looser way | pass |
| `INFORMATIONAL` | timing/performance case, nothing to fail | pass |
| `FAILED` / `WRONG CODE` | wrong behavior / wrong close code | **fail** |
| `UNCLEAN` (close) | the connection wasn't closed cleanly | **fail** |

The wrappers treat `OK`/`NON-STRICT`/`INFORMATIONAL` as passing.

## 4. The committed self-contained harness (`h2wsconformance`)

Because Autobahn needs Docker, the **critical** conformance cases are also
encoded in [`tests/h2wsconformance`](h2wsconformance/Program.cs), which runs in
the normal `tests/run-tests.ps1` gate (no Docker needed). It spins up the same
echo server in-process and plays a **raw WebSocket client** — including
deliberately malformed frames a well-behaved client would never send — asserting
the framing-level response:

- **§1/2/5** — text/binary echo, ping→pong, fragmented-text reassembly.
- **§3/4** — a set reserved bit and a reserved opcode each fail with Close `1002`;
  an orphan continuation frame fails with `1002`.
- **§6** — valid multi-byte/astral UTF-8 echoes; a code point **split across two
  fragments** is valid; invalid UTF-8 (single frame, in a later fragment, or a
  truncated trailing sequence) fails with Close `1007`.
- **§7** — valid close (`1000`, with/without reason, and app-range `3000`/`4999`)
  is echoed; a 1-byte close payload and reserved/invalid codes
  (`999`/`1004`/`1005`/`1006`/`1016`/`2000`/`65535`) fail with `1002`; invalid
  UTF-8 in a close reason fails with `1007`.
- **§12/13 (permessage-deflate)** — the extension is negotiated when offered;
  compressed text/binary/fragmented messages round-trip; an uncompressed message
  on a deflate-negotiated connection still works.

This is to Autobahn what [`h2rfcpolish`](h2rfcpolish/Program.cs) is to h2spec: the
deep external suite for the full sweep, plus a committed harness that pins the
important cases in CI.

## permessage-deflate

Sections **12 and 13** test **`permessage-deflate` (RFC 7692)** — the optional
WebSocket per-message compression extension, negotiated via
`Sec-WebSocket-Extensions` at the opening handshake. This stack **implements it**
(all 216 of these cases pass), so [`fuzzingclient.json`](autobahn/fuzzingclient.json)
runs the full `["*"]` set with nothing excluded.

The framing lives in [`Core/WebSocket.cs`](../src/Core/WebSocket.cs): a message's
first frame carries the RSV1 bit when its payload is DEFLATE-compressed; the
codec is raw DEFLATE (`System.IO.Compression.DeflateStream`) with the RFC 7692
§7.2 `00 00 FF FF` tail handling. The connection runs in **no-context-takeover**
mode — each message is compressed independently, the LZ77 window reset per
message — which is what lets a fixed-window codec like `DeflateStream` handle
each message on its own without carrying deflate state across messages. The
handshake layer advertises this: the echo server, when the client offers
`permessage-deflate`, responds with
`permessage-deflate; server_no_context_takeover; client_no_context_takeover`.
(An earlier revision, before the extension existed, excluded 12/13 and scored
301/301 on the RFC 6455 core alone.)

## 5. Conformance history

The initial WebSocket framing handled masking, opcodes, fragmentation, ping/pong,
reserved bits, and the basic close handshake — but **did not validate UTF-8**
(RFC 6455 §8.1) and **did not validate close frames** (§5.5/§7.4: it echoed any
close payload back verbatim, including reserved/invalid codes and a malformed
1-byte payload). Autobahn §6 (UTF-8) and §7 (close handling) would have reported
failures. Both were closed in the Autobahn-conformance work:

- **UTF-8 (§8.1):** text messages are validated with a strict UTF-8 codec —
  incrementally across fragments (a `Decoder` retains a code point split across
  frame boundaries; the final fragment is flushed to catch a truncated tail) —
  failing the connection with Close `1007` on any invalid sequence.
- **Close frames (§5.5/§7.4.1):** a 1-byte payload is a protocol error (`1002`); a
  reserved/undefined status code (`1004`, `1005`, `1006`, `1015`, `<1000`,
  `1012–2999`, `>4999`) is `1002`; an invalid-UTF-8 reason is `1007`; only a
  well-formed (or empty) close is echoed back.

These fixes live in `Core/WebSocket.cs`, so they harden the WebSocket **client**
(role-parameterized, same framing) as well as the server.
