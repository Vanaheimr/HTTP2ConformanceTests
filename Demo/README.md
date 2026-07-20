# HTTP/2 Demo host

The runnable demo host for the from-scratch HTTP/2 stack: a small `Program.cs`
that wires example request / connect / resource handlers onto `HTTP2Server`. It
listens on two endpoints:

- `https://localhost:8443` — HTTP/2 over TLS (`h2` via ALPN, self-signed cert
  generated at startup)
- `http://localhost:8080` — cleartext HTTP/2 (`h2c`, prior knowledge, no TLS)

## Run

```bash
# from the repository root (clone with --recurse-submodules first — see the root README):
dotnet run --project Demo/HTTP2.Demo.csproj
```

Then, from another shell:

```bash
curl --http2 -k https://localhost:8443/
curl --http2 -k https://localhost:8443/echo -d "Hello HTTP/2!"
curl --http2 -k https://localhost:8443/large                         # 128 KiB — exercises flow control
curl --http2 -k -X QUERY --data 'ap' https://localhost:8443/search   # RFC 10008 QUERY
curl --http2-prior-knowledge http://localhost:8080/                  # cleartext h2c
```

`-k` skips certificate verification (self-signed). The stock Windows curl has no
HTTP/2 support and silently falls back to HTTP/1.1 — use an nghttp2 build, or
.NET's `HttpClient`.

## Full documentation

Everything else — getting the sources (git submodules), the complete build &
test instructions, the RFC-compliance matrix, the feature-by-feature reference,
the security-hardening summary, and the project layout — lives in the
**[root README](../README.md)**. See also [`../CLAUDE.md`](../CLAUDE.md) for the
architecture and [`../docs/BUILD_LOG.md`](../docs/BUILD_LOG.md) for the full
chronological build history.
