# Interoperability

Proof that the from-scratch HTTP/2 stack works with real, independent implementations — in
**both** directions (our client against foreign servers, foreign clients against our server).

## Repeat at any time

**Client side** — the whole matrix with a single command (fresh connection per target, **full**
certificate chain + hostname validation, no bypass):

```bash
dotnet run --project tests/h2interop
```

```bash
dotnet run --project tests/h2interop -- cloudflare caddy   # a subset
```

**Server side** — start the demo host and point a foreign client at it:

```bash
dotnet run --project Demo/HTTP2.Demo.csproj
```

```bash
curl --http2 -k https://localhost:8443/
```

**Browsers** — one command starts the demo, drives headless Chrome and Edge through the whole
battery, and exits non-zero if anything failed:

```powershell
pwsh tools/browser-interop.ps1
```

**Conformance suites** — h2spec and Autobahn, both wrapped so they need one command each:

```bash
tests/h2spec.sh          # or: pwsh tests/h2spec.ps1
```

```bash
tests/autobahn.sh        # or: pwsh tests/autobahn.ps1   (needs Docker)
```

Both also run nightly on GitHub — see [`.github/workflows/nightly.yml`](.github/workflows/nightly.yml).

> Note: the system `curl` on Windows is a Schannel build **without** HTTP/2 support, and it falls
> back to HTTP/1.1 *silently* — which looks exactly like our server refusing h2. Use an
> nghttp2-backed build, or the `curl` preinstalled in WSL/Debian. Check with `curl -V`: the last
> line must list `nghttp2`. The same `curl --http2` doubles as an oracle for "does target X speak
> HTTP/2 at all?" — see the two negative results at the end of the client section.

## Client interop matrix

As of **2026-08-13** — 8 independent server implementations, every one reached end to end with
full certificate validation:

| Target | Expected stack | `server:` said | MAX_CONCURRENT_STREAMS | Alt-Svc | Result |
|---|---|---|---|---|---|
| `www.nginx.com` | nginx | **volt-adc** | 100 | — | 403, 0 B |
| `www.google.com` | GFE | `gws` | 100 | h3, h3-29 | 200, 85 812 B |
| `www.cloudflare.com` | Cloudflare | `cloudflare` | 100 | h3 | 200, 1 308 814 B |
| `www.haproxy.org` | HAProxy | `Apache` | **30** | h3 | 200, 81 944 B |
| `www.litespeedtech.com` | LiteSpeed | `LiteSpeed` | 100 | h3, h3-29 | 200, 68 801 B |
| `caddyserver.com` | Caddy (Go) | `Caddy` | **250** | h3 | 200, 73 099 B |
| `www.apache.org` | Apache httpd | `Apache` | 100 | — | 200, 51 320 B |
| `github.com` | GitHub | `github.com` | 100 | — | 200, 572 198 B |

A 403 is a regular HTTP response — bot protection forming an opinion about datacentre IPs — and it
proves the same thing a 200 does: TLS + ALPN `h2`, the connection preface, their SETTINGS parsed,
our HEADERS accepted, their HPACK-encoded response decoded, flow control, END_STREAM. What would
*not* count is a timeout, a GOAWAY, a decode failure or an ALPN rejection.

**Why this matrix exists.** The server had been interop-tested against three independent peers
(below); the client had exactly one — Kestrel, which shares our BCL and our .NET lineage. Every
wire-visible decision the client makes had therefore only ever been judged by one implementation
family. These eight are the second opinion.

**Three things it surfaced that a Kestrel-only test could not:**

- **MAX_CONCURRENT_STREAMS is not 100 everywhere.** `www.haproxy.org` advertises **30** and
  `caddyserver.com` **250**. The client's stream-slot gating had only ever seen values at or above
  its own default; 30 exercises the path where the peer is stricter than we are.
- **Nobody sends an ALTSVC frame.** All four `h3` advertisements above arrived as an `alt-svc`
  *header field*. RFC 7838 allows either carrier, but the frame path — the HTTP/2-specific one our
  frame dispatch implements — is exercised only by our own tests, not by anything in the wild.
- **Nobody sends an ORIGIN frame either** (RFC 8336), across all eight. Same conclusion: implemented
  and unit-tested, but not corroborated by a deployed peer.

The `server:` column is deliberately reported rather than assumed, and it earns that immediately:
`www.nginx.com` is not fronted by nginx at all but by a **Volt ADC**, and `www.haproxy.org` answers
with `Apache`. The "expected stack" column is a label; the header is the evidence.

**Two hosts verified NOT to offer HTTP/2**, both dropped from the matrix rather than left failing:

| Host | Our client | `curl --http2` | Reading |
|---|---|---|---|
| `nginx.org` | `AuthenticationException` — no common application protocol | HTTP/1.1, 200 | The server rejects the ALPN outright, a TLS `no_application_protocol` alert |
| `www.akamai.com` | `Server did not negotiate HTTP/2 over ALPN (got '')` | HTTP/1.1, 403 | The server completes the handshake and selects nothing |

Two different server behaviours for one cause — no h2 on that host — and our client reports both
correctly, one via `SslStream`, one via our own check. Neither is a defect on our side: `curl` with
nghttp2 gets HTTP/1.1 from both.

## Server interop

Our demo host is driven by five independent foreign client stacks:

| Client | Stack | Verified |
|---|---|---|
| .NET `HttpClient` | `System.Net.Http` HTTP/2 | The strict reference client — requests, responses, flow control, multiplexing, trailers |
| `curl` | **nghttp2** | `GET /`, `POST /echo`, `GET /large` (128 KiB, flow control), `GET /slow` (multiplexing), `QUERY /search` (RFC 10008), and h2c prior-knowledge on `:8080` |
| `Grpc.Net.Client` | gRPC over HTTP/2 | Real gRPC, **all four call types** — unary, server-streaming, client-streaming, bidirectional — over the streaming seam, with `grpc-status` in trailers |
| Chrome 151 / Edge 151 | **Chromium** + BoringSSL | 4/4 checks below, both browsers, headless |
| Kestrel | ASP.NET Core | The mirror direction: our **client** against a .NET server, including request trailers read back through `Request.GetTrailer` |

These are test-only reference peers and do not count against the BCL-only rule for the stack itself.

### Browser self-test (as of 2026-08-13)

`GET /browser` serves a page whose JS runs the battery and POSTs its verdict to `/report`, which the
demo prints — so the server log is the record and nothing has to scrape a DOM or guess when a
headless run finished. Chrome 151 and Edge 151 both report **4/4**:

| Check | What it exercises | Measured |
|---|---|---|
| Navigation used h2 | `performance.nextHopProtocol` — **the browser's own verdict, not ours** | `h2` |
| `GET /large` | 128 KiB under the browser's receive window | 131 072 B |
| `POST /echo` | a 64 KiB request body from the browser, mirrored back byte for byte | 65 536 B |
| 4× `/slow` | real multiplexing: four handlers that each sleep 2 s, on one connection | **2 011 ms**, not 8 000 |

The first check is the one nothing else here can make. Every other test in this repository is us
asserting that we spoke HTTP/2; `nextHopProtocol` is Chrome saying it. The last is the one that
would fail alone if the connection were quietly serializing — every other check would still pass.

Only the self-signed certificate needs handling, via `--ignore-certificate-errors` on a throwaway
profile. Unlike the sibling HTTP/3 project this needs no SPKI pin, no certificate-hash
authentication and no draft feature flags: plain h2 in a browser is unremarkable, which is rather
the point.

## Conformance suites

Both re-measured by the nightly on **2026-08-13**, not merely remembered:

| Suite | Scope | Result |
|---|---|---|
| [h2spec](https://github.com/summerwind/h2spec) 2.6.0 | RFC 9113 + RFC 7541 | **146/146**, four times over — TLS `h2` *and* cleartext `h2c`, on Windows *and* Debian 13 |
| [Autobahn TestSuite](https://github.com/crossbario/autobahn-testsuite) | RFC 6455 + RFC 7692 | **517/517**, the full suite including `permessage-deflate` |

Autobahn drives [`tests/autobahn-server`](tests/autobahn-server), which exposes the same
`WebSocketConnection` framing production uses over a plain-TCP tunnel behind a minimal HTTP/1.1
Upgrade. Autobahn speaks WebSocket over HTTP/1.1 rather than RFC 8441 over HTTP/2, but the framing
under test is the same code either way — see
[`tests/TestingAgainst_Autobahn.md`](tests/TestingAgainst_Autobahn.md).

## Open (optional)

- **Firefox** — installed on the development machine but untested, and it is the one browser that
  needs real work: it ignores the Chromium flags, so the certificate has to go into its own NSS
  store (or `security.enterprise_roots.enabled` has to be set in a prepared profile). The same
  entry stands open in the HTTP/3 sibling for the same reason.
- **`h2load`** (nghttp2's benchmark client) as a sixth foreign client, which would drive the server
  at concurrency levels the current peers do not reach. Not installed here — it ships in Debian's
  `nghttp2-client` package.
- **Further targets:** the list at the top of [`tests/h2interop/Program.cs`](tests/h2interop/Program.cs).
- A server advertising **ORIGIN** or a frame-borne **ALTSVC** — both implemented here, neither
  corroborated by any of the eight. Worth revisiting if a deployed peer is ever found that sends
  them.
