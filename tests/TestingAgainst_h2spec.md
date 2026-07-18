# Testing against h2spec

[h2spec](https://github.com/summerwind/h2spec) is the canonical conformance
suite for **HTTP/2 (RFC 9113)** and **HPACK (RFC 7541)** — the same tool used to
vet Kestrel, nghttp2, Go's `net/http`, etc. This stack passes **all 146 tests,
over both transports**:

| Transport | Listener | h2spec invocation | Result |
|---|---|---|---|
| TLS (`h2`, ALPN) | `https://localhost:8443` | `h2spec -t -k -h 127.0.0.1 -p 8443 -P /echo` | **146 / 146** |
| cleartext (`h2c`, prior knowledge) | `http://localhost:8080` | `h2spec -h 127.0.0.1 -p 8080 -P /echo` | **146 / 146** |

Verified with **h2spec 2.6.0** on both Windows and Linux (WSL/Debian). h2spec is
an external binary and is **not vendored** in this repo — download it once
(below). The h2spec invocation is identical on every OS; only the wrapper
script and the download differ.

---

## 1. Install h2spec

Grab a release for your platform from
<https://github.com/summerwind/h2spec/releases> (v2.6.0 or newer).

**Linux / WSL:**

```bash
curl -L https://github.com/summerwind/h2spec/releases/download/v2.6.0/h2spec_linux_amd64.tar.gz \
  | tar -xz            # -> ./h2spec
sudo mv h2spec /usr/local/bin/    # optional: put it on PATH
h2spec --version                  # -> Version: 2.6.0 (...)
```

**macOS:** the `h2spec_darwin_amd64.tar.gz` asset (same steps), or `brew install h2spec`.

**Windows:** download `h2spec_windows_amd64.zip`, unzip, and either put
`h2spec.exe` on your `PATH` or note its full path.

## 2. The easy way — the wrapper script

Both wrappers do the whole dance for you: build the solution, free the demo
ports, start the demo host **with its output redirected to a file** (see the
gotcha below — this is why a naive backgrounded `dotnet run` can appear to
hang), run h2spec against both listeners, and stop the demo again (even on
error / Ctrl-C). Each exits `0` iff every selected transport reports `0 failed`,
so it drops straight into a CI gate.

**Linux / macOS — [`tests/h2spec.sh`](h2spec.sh):**

```bash
tests/h2spec.sh                          # both transports (TLS + h2c)
tests/h2spec.sh --h2spec ~/bin/h2spec    # if h2spec isn't on PATH
tests/h2spec.sh --transport tls          # just one transport
tests/h2spec.sh --transport cleartext
tests/h2spec.sh --no-build --spec "http2/6.5"   # skip build, one section
```

Options: `--transport both|tls|cleartext` (default `both`), `--h2spec <path>`,
`--path <endpoint>` (default `/echo`), `--spec "<ids>"` (e.g. `"http2/6.5 hpack"`),
`--strict` (h2spec's `-S`), `--no-build`.

**Windows — [`tests/h2spec.ps1`](h2spec.ps1):**

```powershell
pwsh tests/h2spec.ps1                                # both transports
pwsh tests/h2spec.ps1 -H2spec C:\tools\h2spec.exe    # if not on PATH
pwsh tests/h2spec.ps1 -Transport tls                 # just one transport
pwsh tests/h2spec.ps1 -NoBuild -Spec http2/6.5       # skip build, one section
```

Options mirror the bash script: `-Transport`, `-H2spec`, `-Path`, `-Spec`,
`-Strict`, `-NoBuild`.

## 3. The manual way

Two shells. First, start the demo host (it serves **both** the TLS listener on
`:8443` and the cleartext h2c listener on `:8080`) — same command everywhere:

```bash
dotnet run --project src/Demo/HTTP2.Demo.csproj
```

Then, in a second shell, point h2spec at whichever listener you want (identical
flags on every OS):

```bash
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

```bash
# run just the HPACK section, verbosely:
h2spec -t -k -h 127.0.0.1 -p 8443 -P /echo -v hpack
```

### Bonus: curl as a quick smoke test (Linux/macOS)

A curl built against **nghttp2** (Linux/macOS distro curl usually is;
`curl --version` should list `nghttp2`) can hit both listeners directly — a fast
sanity check before the full h2spec run:

```bash
curl --http2 -k  https://localhost:8443/            # TLS h2
curl --http2-prior-knowledge http://localhost:8080/ # cleartext h2c
curl --http2-prior-knowledge http://localhost:8080/echo -d "Hello h2c!"
```

(The curl bundled with Windows is a Schannel build with **no** HTTP/2 support and
silently falls back to HTTP/1.1 — use WSL, or .NET's `HttpClient`, there.)

## 4. Two gotchas (both learned the hard way)

1. **Use `127.0.0.1`, not `localhost`.** h2spec resolving `localhost` may try
   `::1` (IPv6) first; if anything about that path is slow, tests time out. The
   demo does listen on both loopbacks, but pinning `127.0.0.1` removes the
   variable.

2. **Drain the demo's console output.** h2spec fires *hundreds* of malformed
   inputs; the demo logs each one. If the demo's `stdout`/`stderr` isn't being
   read — a hidden-window `Start-Process` on Windows, or a backgrounded
   `dotnet run &` on Linux, both with no redirect — the OS pipe buffer fills, the
   demo **blocks in `Console.WriteLine`**, and it stops accepting connections,
   which looks exactly like a server crash (subsequent connections, especially
   IPv6, time out). Redirect the demo's output to a file (which both wrapper
   scripts do), or just run it in a normal visible console. This is a
   *test-harness* artifact, not a bug in the server.

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
