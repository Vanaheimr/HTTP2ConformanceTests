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

**Result: 481 / 517 pass, 36 declined, 0 failures** (measured 2026-09-23 under
WSL/Debian, and re-measured nightly in CI). The 36 are sections 13.3 and 13.5,
where the client offers `server_max_window_bits=9`; this server cannot honor that
and therefore must refuse it — see
[§ the 36 declines](#the-36-declines-server_max_window_bits9) below. Everything
else passes, including the rest of sections **12 & 13 (permessage-deflate, RFC
7692)**, the optional per-message compression extension, which the echo server
negotiates in no-context-takeover mode.

This read **517 / 517** until 2026-09-23. That number was not earned, and how it
was lost is worth more than the number itself — the section below has it.

---

## What is actually under test (and the one wrinkle)

The suite tests the **WebSocket framing layer** — in this project,
[`WebSocketConnection.cs`](../libs/Hermod/Hermod/HTTP2/WebSocket/WebSocketConnection.cs): masking,
opcodes, fragmentation reassembly, ping→pong, the close handshake, and RFC 6455
§8.1 UTF-8 validation.

The wrinkle: Autobahn's client speaks WebSocket over the **classic HTTP/1.1
`Upgrade` handshake**. This project's WebSockets run in production over **RFC 8441
extended CONNECT on HTTP/2** — Autobahn can't drive that directly. But the
framing layer under test is deliberately **transport-agnostic**: `WebSocketConnection`
sits on top of the byte-in/byte-out [`IHTTP2Tunnel`](../libs/Hermod/Hermod/HTTP2/Core/IHTTP2Tunnel.cs)
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

[`tests/autobahn.sh`](autobahn.sh) builds the echo server, starts it (**output
drained to a file** — same gotcha as h2spec: an undrained console pipe can stall
a busy server), runs the Autobahn fuzzingclient against it via Docker, parses
the JSON report, and stops the echo server again — exiting `0` iff every case
passed.

```bash
tests/autobahn.sh
tests/autobahn.sh --no-build
tests/autobahn.sh --port 9010
```

**How the container reaches the echo server** is the one place this script has
to know which platform it is on, and it is a real difference rather than a
stylistic one:

- **Linux / macOS:** `--network host` puts the container in the host's own
  network namespace, so the generated config points at `ws://127.0.0.1:<port>`.
- **Windows (Docker Desktop):** `--network host` is a Linux-namespace feature
  and is either absent or an opt-in beta there, so the script falls back to the
  default bridge and points the config at `ws://host.docker.internal:<port>`.
  The `-v` mounts also need `MSYS_NO_PATHCONV=1` and a `cygpath -w` path, since
  Git Bash would otherwise rewrite `src:/config` as a path list.

This replaced an `autobahn.ps1` that did the Windows half (see
[`README.md`](README.md) for why the PowerShell runners went away). **The
Windows branch is unverified**: the nightly runs Autobahn only on
`ubuntu-latest`, because the suite ships usably only as a Docker image.

Report parsing prefers `jq`, then `python3`, then a `grep` heuristic — the last
of which only answers yes/no, so install one of the first two if you want a
count. The script exits non-zero and lists the offending case IDs if any case
does not pass. The full human-readable report is written to
`tests/autobahn/reports/index.html`.

## 3. The manual way

Two shells. First, build and start the echo server:

```bash
dotnet build HTTP2.slnx
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
the normal `tests/run-tests.sh` gate (no Docker needed). It spins up the same
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
(180 of these 216 cases pass and 36 are declined, see below), so
[`fuzzingclient.json`](autobahn/fuzzingclient.json) runs the full `["*"]` set with
nothing excluded.

The framing lives in [`WebSocketConnection.cs`](../libs/Hermod/Hermod/HTTP2/WebSocket/WebSocketConnection.cs): a message's
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

### The 36 declines: `server_max_window_bits=9`

Read off the wire, from the `httpRequest` recorded in each case report:

| Case | What the client offers | Result |
|---|---|---|
| 13.1, 13.2 | no `server_max_window_bits` | OK |
| 13.4, 13.6 | `server_max_window_bits=15` | OK |
| **13.3, 13.5** | **`server_max_window_bits=9`** | **UNIMPLEMENTED** (18 + 18) |
| 13.7 | a list containing 9 *and* an offer without it | OK — the server takes the other one |

`server_max_window_bits=N` is the client telling the **server** to cap its own
compression window. `DeflateStream` exposes no control over `windowBits`, so 15
is the only value this stack can promise, and RFC 7692 §7.1.2.1 says a server
that cannot satisfy an offer must **decline** it and fall back to no compression.
`UNIMPLEMENTED` is Autobahn's accurate word for "the server declined the
extension for this offer" — it is not `FAILED`, and the run carries zero
`FAILED` / `WRONG CODE` / `UNCLEAN`.

**Why this used to read 517/517, and why that was worse.** Until Hermod
`eb7bf410`, `WebSocketDeflate.ShouldAccept` returned true for any
`Sec-WebSocket-Extensions` value whose text merely contained
`permessage-deflate`. It never parsed the parameters. Offered
`server_max_window_bits=9` it answered "accepted" and then compressed with the
full 15-bit window — which a client that had sized its inflate window to 9 bits
could not have decoded. Autobahn scored it OK because Python's zlib inflates with
a large window regardless and so never notices. The 36 were bought by claiming a
capability this server did not have.

The pin bump of 2026-09-23 brought the fix here, the score fell to 481, and the
floor in [`autobahn.sh`](autobahn.sh) was lowered to match — deliberately, with
the reason recorded in the script, rather than quietly. The HTTP/1.1 sibling,
whose `WebSocketPerMessageDeflate.TryNegotiateAsServer` was written independently
and always parsed the offer, reports the same 481/517 with an identical verdict
breakdown: 476 OK, 3 INFORMATIONAL, 2 NON-STRICT, 36 UNIMPLEMENTED. Two correct
readings of one RFC converging on the same number is a better result than either
of them scoring 517.

The negotiation itself is now pinned by
`HermodTests/HTTP2/WebSocketDeflateNegotiationTests` — 21 offers asserted against
both the HTTP/2 and the HTTP/3 copy of `WebSocketDeflate`, so this no longer
depends on a nightly Docker run to stay true.

### The floor, not a target

[`autobahn.sh`](autobahn.sh) gates on `min_pass=481` and sorts the verdicts into
three buckets rather than two:

| Bucket | Verdicts | Effect |
|---|---|---|
| passing | `OK`, `NON-STRICT`, `INFORMATIONAL` | must stay at or above `min_pass` |
| declined | `UNIMPLEMENTED` | tolerated and counted — the RFC-required refusal |
| hard | `FAILED`, `WRONG CODE`, `UNCLEAN` | **always fatal**, whatever the count |

So the floor can only ever absorb a change in how many extension offers we
decline. It cannot launder a real failure into a pass, which is what made 481
gateable at all. The alternative — excluding 13.3 and 13.5 to buy a green badge —
hides the cases; a floor keeps counting them, and says so out loud when the
number goes up so the floor can be raised.

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
