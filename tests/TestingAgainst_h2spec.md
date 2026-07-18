# Testing against h2spec

[h2spec](https://github.com/summerwind/h2spec) is the canonical conformance
suite for **HTTP/2 (RFC 9113)** and **HPACK (RFC 7541)** — the same tool used to
vet Kestrel, nghttp2, Go's `net/http`, etc. This stack passes **all 146 tests,
over both transports**:

| Transport | Listener | h2spec invocation | Result |
|---|---|---|---|
| TLS (`h2`, ALPN) | `https://localhost:8443` | `h2spec -t -k -h 127.0.0.1 -p 8443 -P /echo` | **146 / 146** |
| cleartext (`h2c`, prior knowledge) | `http://localhost:8080` | `h2spec -h 127.0.0.1 -p 8080 -P /echo` | **146 / 146** |

Verified with **h2spec 2.6.0**. h2spec is an external binary and is **not
vendored** in this repo — download it once (below).

---

## 1. Install h2spec

Grab a release binary for your platform from
<https://github.com/summerwind/h2spec/releases> (v2.6.0 or newer) and unzip it.
Either put `h2spec` (`h2spec.exe` on Windows) on your `PATH`, or note its full
path for the `-H2spec` argument below.

```powershell
# quick check
h2spec --version        # -> Version: 2.6.0 (...)
```

## 2. The easy way — the wrapper script

[`tests/h2spec.ps1`](h2spec.ps1) does the whole dance for you: build the
solution, free the demo ports, start the demo host **with its output redirected
to a file** (see the gotcha below — this is why a naive `dotnet run` in a hidden
window can appear to hang), run h2spec against both listeners, and stop the demo
again.

```powershell
# both transports (TLS + h2c), h2spec found on PATH:
pwsh tests/h2spec.ps1

# if h2spec isn't on PATH, point at the binary:
pwsh tests/h2spec.ps1 -H2spec C:\tools\h2spec\h2spec.exe

# just one transport:
pwsh tests/h2spec.ps1 -Transport tls
pwsh tests/h2spec.ps1 -Transport cleartext

# skip the build (assumes a current one), and/or limit to one section:
pwsh tests/h2spec.ps1 -NoBuild -Spec http2/6.5
```

It exits `0` iff every selected transport reports `0 failed`, so it drops
straight into a CI gate. Options: `-Transport both|tls|cleartext` (default
`both`), `-H2spec <path>`, `-Path <endpoint>` (default `/echo`), `-Spec <ids...>`
(one or more section IDs, e.g. `http2/6.5 hpack`), `-Strict` (h2spec's `-S`,
includes the strict cases), `-NoBuild`.

## 3. The manual way

Two shells. First, start the demo host (it serves **both** the TLS listener on
`:8443` and the cleartext h2c listener on `:8080`):

```powershell
dotnet run --project src/Demo/HTTP2.Demo.csproj
```

Then, in a second shell, point h2spec at whichever listener you want:

```powershell
# TLS ("h2"): -t connects over TLS, -k accepts the self-signed cert
h2spec -t -k -h 127.0.0.1 -p 8443 -P /echo

# cleartext ("h2c", prior knowledge): no -t, no cert
h2spec -h 127.0.0.1 -p 8080 -P /echo
```

Both should print `146 tests, 146 passed, 0 skipped, 0 failed`.

### Useful flags

| Flag | Meaning |
|---|---|
| `-P /echo` | Target path. Use a **POST-capable** endpoint — several tests send a request body, and `/echo` accepts one. |
| `-S` | Also run the *strict* test cases. |
| `-v` | Verbose: dump the frames exchanged (invaluable when a test fails). |
| `--dryrun` | List every test case's title without running anything. |
| `-o <sec>` | Per-test timeout (default 2 s). |
| `http2/6.5` | A positional *spec selector* — run only that section (here §6.5 SETTINGS). Also `generic`, `hpack`, `http2/5.1`, … |

```powershell
# run just the HPACK section, verbosely:
h2spec -t -k -h 127.0.0.1 -p 8443 -P /echo -v hpack
```

## 4. Two gotchas (both learned the hard way)

1. **Use `127.0.0.1`, not `localhost`.** h2spec resolving `localhost` may try
   `::1` (IPv6) first; if anything about that path is slow, tests time out. The
   demo does listen on both loopbacks, but pinning `127.0.0.1` removes the
   variable.

2. **Drain the demo's console output.** h2spec fires *hundreds* of malformed
   inputs; the demo logs each one. If the demo's `stdout`/`stderr` isn't being
   read (e.g. `Start-Process -WindowStyle Hidden` with no redirect), the OS
   console pipe buffer fills, the demo **blocks in `Console.WriteLine`**, and it
   stops accepting connections — which looks exactly like a server crash
   (subsequent connections, especially IPv6, time out). Redirect the demo's
   output to a file (which is what [`h2spec.ps1`](h2spec.ps1) does), or just run
   it in a normal visible console. This is a *test-harness* artifact, not a bug
   in the server.

## 5. Conformance history

The very first h2spec run scored **136 / 146**. All 10 failures were closed in
the h2spec-conformance track (six categories: PRIORITY-frame envelope validation
§6.3, HPACK dynamic-table-size-update bounds §4.2/§6.3, content-length vs. DATA
length §8.1.2.6, HEADERS-on-closed-stream §5.1, inbound PUSH_PROMISE §8.2, and
RFC 7540 self-dependency §5.3.1). The full per-category write-up is in
[`../CLAUDE.md`](../CLAUDE.md) under the **h2spec conformance track** entry.

The HPACK fixes live in `Core/HPACK.cs`, so they harden the **client's** decoder
too, not just the server. A later cleartext-h2c run additionally surfaced (and
fixed) a truncated-header-block `IndexOutOfRangeException` that is now a proper
`COMPRESSION_ERROR` — see the **h2c** entry in `CLAUDE.md`.
