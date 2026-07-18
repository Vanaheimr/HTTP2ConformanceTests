# HTTP/2 From Scratch (C# / .NET 10)

A from-scratch HTTP/2 stack built directly on `SslStream`, focused on the
**binary framing layer**: frame parsing, HPACK header compression, the stream
state machine, flow control, and TLS + ALPN (`h2`) negotiation. No Kestrel, no
`System.Net.Http` HTTP/2 stack — everything is hand-rolled.

It's three parts: a shared protocol library (`Core` — direction-neutral
framing, HPACK, the stream layer, WebSocket framing, HTTP semantics), an HTTP/2
**server**, and an HTTP/2 **client**, each its own project. Both roles are
interop-verified against .NET (`HttpClient`/curl for the server; a Kestrel
HTTP/2 server for the client). See `CLAUDE.md` for the full status.

> ⚠️ **Reference implementation.** Requests, responses, flow control, real
> stream multiplexing, CONTINUATION-flood/Rapid-Reset/stream-ID-exhaustion
> and Slowloris/timeout hardening, RFC 9113 §8 request validation,
> trailers/implicit stream
> closure, per-stream RST_STREAM cancellation, graceful `GOAWAY` shutdown, a
> table-driven Huffman decoder *and* encoder, a full HPACK encoder (static +
> dynamic table + Huffman), CONNECT + extended CONNECT (RFC 8441) +
> WebSocket (RFC 6455) tunneling, RFC 9218 priority-aware response
> scheduling, streaming request/response bodies with response trailers
> (gRPC-style, verified against .NET `HttpClient` — and a real gRPC service
> interop-tested against `Grpc.Net.Client`), 1xx interim responses
> (`Expect: 100-continue`, 103 Early Hints), an RFC 9110 semantics
> layer (GET/HEAD/OPTIONS, conditional
> requests, Range requests, proactive content negotiation with `Vary`,
> opt-in on-the-fly gzip/brotli/deflate compression), cleartext h2c
> (prior-knowledge, no TLS — server and client),
> authentication (RFC 9110 §11 framework with Basic/Bearer, plus mutual TLS on
> server and client), and an RFC 9111 client-side cache (freshness, conditional
> revalidation, `Vary`, shared/private semantics) all work end-to-end (verified
> against .NET's strict `HttpClient`/Kestrel and raw frame-level attack
> clients). See `CLAUDE.md` for the full status. Still built for learning the
> wire protocol, not for production traffic (single-process demo host, no
> server push, etc.).

## Requirements

- .NET 10 SDK (`net10.0` target)

## Build & run

```bash
cd src
dotnet build HTTP2.slnx
dotnet run --project Demo/HTTP2.Demo.csproj
```

The demo listens on `https://localhost:8443` (HTTP/2 over TLS, self-signed cert
generated at startup) and additionally on `http://localhost:8080` (cleartext
HTTP/2 — "h2c" — with prior knowledge, no TLS).

## Test

The interop + attack harnesses live under [`tests/`](tests/). Run the whole
suite (builds, starts the demo host, drives every harness) with:

```powershell
powershell -ExecutionPolicy Bypass -File tests/run-tests.ps1
```

Current status: **69/69 harness runs pass**, and the stack scores **146/146 on
[h2spec](https://github.com/summerwind/h2spec)** (the canonical HTTP/2
conformance suite) over *both* the TLS and cleartext-h2c listeners. Reproduce
the h2spec run with a single command —

```powershell
pwsh tests/h2spec.ps1   # builds, starts the demo, runs h2spec on both transports
```

— see [`tests/TestingAgainst_h2spec.md`](tests/TestingAgainst_h2spec.md) for the
full h2spec walkthrough, [`tests/README.md`](tests/README.md) for the harness
layout, and [`CLAUDE.md`](CLAUDE.md) for the conformance breakdown.

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

```
src/  (solution HTTP2.slnx)
├── Core/                Shared, direction-neutral library
│   ├── HTTP2Frame.cs        Frame header + frame factories, enums, exceptions
│   ├── HPACK.cs             RFC 7541 header compression (full encode + decode: static/dynamic table + Huffman)
│   ├── HTTP2Stream.cs       Stream state machine + flow control + RFC 9218 priority + role-parameterized manager
│   ├── HTTP2Settings.cs     Connection settings bag
│   ├── HTTP2RequestHandler.cs  App-logic request-handler delegate
│   ├── HTTP2Streaming.cs       Streaming seam (incremental body + trailers, gRPC-style bidi)
│   ├── IHTTP2Tunnel.cs      Transport-agnostic byte-tunnel interface
│   ├── WebSocket.cs         RFC 6455 WebSocket framing over an IHTTP2Tunnel
│   ├── HTTPSemantics.cs     RFC 9110 semantics (GET/HEAD/OPTIONS, conditional + Range, content negotiation)
│   ├── HTTPAuthentication.cs  RFC 9110 §11 auth framework + Basic/Bearer schemes
│   └── HTTPCaching.cs       RFC 9111 caching logic (Cache-Control, freshness, revalidation, Vary)
├── Server/              (→ Core)
│   ├── HTTP2Connection.cs   Preface, SETTINGS, frame dispatch, responses, CONNECT tunneling, priority-aware writer
│   └── HTTP2Server.cs       TLS listener + ALPN negotiation (+ optional mTLS)
├── Client/              (→ Core)
│   ├── HTTP2ClientConnection.cs  Client-role connection (sends requests, assembles responses)
│   ├── HTTP2Client.cs       Client dialer (TCP connect + TLS/ALPN h2)
│   └── HTTP2CachingClient.cs  RFC 9111 cache in front of a client connection
└── Demo/                (→ Server, Client)
    └── Program.cs           Demo host + example request/connect/resource handlers
```

## Where application logic plugs in

The `HTTP2RequestHandler` delegate (see `HTTP2Connection.cs`) receives decoded
request headers + body and returns response headers + body. That is the seam
where an existing HTTP/1.1 handler would attach. The parallel seam for
tunnels — CONNECT and extended CONNECT (RFC 8441), e.g. to bootstrap a
WebSocket — is `HTTP2ConnectHandler`: it decides accept/reject up front, and
if accepted, runs against an `HTTP2Tunnel` (a raw bidirectional byte stream
over the accepted stream). A third, narrower seam sits one level above the
first: `HTTPResourceHandler` (see `HTTPSemantics.cs`) just answers "what is
this resource's current representation, or null for 404" — `HTTPSemantics.Wrap`
turns that into an ordinary `HTTP2RequestHandler`, adding RFC 9110
GET/HEAD/OPTIONS method semantics, conditional requests, and Range requests
on top, entirely without touching HTTP/2 framing. Its `HTTPVariantHandler`
sibling returns *several* representations of a resource, and `Wrap` picks
among them by the client's `Accept` / `Accept-Encoding` / `Accept-Language`
(proactive content negotiation, emitting the appropriate `Vary`). Passing
`CompressResponses: true` to `Wrap` additionally compresses a compressible
identity body on the fly (brotli/gzip/deflate, per the request's
`Accept-Encoding`), weakening the `ETag` and adding `Vary: accept-encoding`.

For streaming — server-streaming, SSE, large transfers without buffering, or
full bidirectional streaming (gRPC) — register an `HTTP2StreamingHandler` on
`HTTP2Server` instead (`StreamingHandler:`). It receives an
`IHTTP2RequestStream` (pull request-body chunks with `ReadAsync` as DATA
arrives; read request `Trailers` once the body ends) and an
`IHTTP2ResponseStream` (optional `WriteInterimResponseAsync` for 1xx — e.g. a
103 Early Hints with `Link` preload headers — then `WriteHeadersAsync` once,
then `WriteAsync` body chunks, then `CompleteAsync(trailers)` — e.g. gRPC's
`grpc-status`). The handler is invoked as soon as the request headers arrive, so
both directions flow at once. `Expect: 100-continue` is handled automatically by
the server. This seam is enough to serve real **gRPC**: the
[`grpc`](tests/grpc/Program.cs) harness runs a Greeter service (unary +
server-streaming, length-prefixed messages, `grpc-status` in trailers) over the
stack and interop-tests it against the real `Grpc.Net.Client`.

For authentication, `HTTPAuthentication.RequireAuthentication` wraps a handler
with the RFC 9110 §11 challenge/response flow (401 + `WWW-Authenticate` when
unauthenticated), backed by pluggable schemes — `BasicAuthenticationScheme`
(RFC 7617) and `BearerAuthenticationScheme` (RFC 6750), each taking an
app-supplied validator so no credential store is baked in. Mutual TLS is a
separate, transport-layer option on `HTTP2Server` (`RequireClientCertificate`)
and `HTTP2Client` (`ClientCertificate`).

## Using the client

`HTTP2Client` dials a server, negotiates TLS + ALPN `h2`, and returns a
connection you can send concurrent requests on:

```csharp
var conn = await HTTP2Client.ConnectAsync("localhost", 8443,
    ValidateServerCertificate: (_, _, _, _) => true);   // accept the demo's self-signed cert

var response = await conn.SendRequestAsync("GET", "https", "localhost:8443", "/");
Console.WriteLine($"{response.Status}: {Encoding.UTF8.GetString(response.Body)}");

await conn.CloseAsync();
```

It reuses the same framing/HPACK/flow-control code as the server, and is
interop-tested against both this server and a .NET Kestrel HTTP/2 server. Pass
`HTTP2ClientOptions` to `ConnectAsync` for robustness knobs — automatic retry of
server-refused streams (`REFUSED_STREAM` is guaranteed unprocessed, so retrying
is side-effect-safe), and an opt-in PING keepalive that drops a silently-dead
connection instead of hanging:

```csharp
var conn = await HTTP2Client.ConnectAsync("localhost", 8443,
    ValidateServerCertificate: (_, _, _, _) => true,
    Options: new HTTP2ClientOptions {
        MaxRefusedStreamRetries = 2,
        KeepAliveInterval       = TimeSpan.FromSeconds(30),   // 0 = disabled
    });
```

Concurrent requests beyond the server's `MAX_CONCURRENT_STREAMS` queue (rather
than fail), and a request the server provably never processed (a
`REFUSED_STREAM` past the retry budget, or a stream above a `GOAWAY`'s
last-stream-id) surfaces as `HTTP2RequestNotProcessedException` — a signal it's
safe to retry on a fresh connection.

The client can also open CONNECT tunnels and WebSockets (RFC 9113 §8.5 / RFC
8441 / RFC 6455), the mirror of the server's tunneling — both ends of the wire
hand-rolled:

```csharp
// plain CONNECT — a raw bidirectional byte tunnel
var tunnel = await conn.OpenTunnelAsync("proxy.target:443");
await tunnel.WriteAsync(bytes);
var reply = await tunnel.ReadAsync(CancellationToken.None);

// extended CONNECT — a WebSocket (client masks its frames per RFC 6455)
var ws = await conn.OpenWebSocketAsync("localhost", "https", "/ws-echo");
await ws.SendTextAsync("hello", CancellationToken.None);
var msg = await ws.ReceiveAsync(CancellationToken.None);

// opt into permessage-deflate (RFC 7692) — offered on the CONNECT handshake,
// only actually used if the server echoes acceptance back
var wsz = await conn.OpenWebSocketAsync("localhost", "https", "/ws-echo", PerMessageDeflate: true);
```
Requests can carry an RFC 9218 priority hint, and an in-flight request can be
reprioritized (both honored by the priority-aware server):

```csharp
var r = await conn.SendRequestAsync("GET", "https", "localhost:8443", "/big",
    Priority: new HTTP2Priority(Urgency: 0, Incremental: false));   // most urgent

var h = await conn.StartRequestAsync("GET", "https", "localhost:8443", "/slow");
await conn.UpdatePriorityAsync(h.StreamId, new HTTP2Priority(0, false));   // PRIORITY_UPDATE
var slow = await h.Response;
```

For full-duplex request/response streaming — the enabler for client-streaming and
bidirectional gRPC — `StartStreamingRequestAsync` returns a handle whose request
body is written incrementally while the response is read incrementally, both at
once:

```csharp
var s = await conn.StartStreamingRequestAsync("POST", "https", "localhost:8443", "/svc.Greeter/Bidi",
    ExtraHeaders: [("content-type", "application/grpc"), ("te", "trailers")]);
var head = await s.GetResponseAsync();                 // status + headers
await s.WriteAsync(frame);                              // send a request-body chunk (DATA)
byte[]? chunk = await s.ReadAsync();                    // read a response-body chunk (null at end)
await s.CompleteRequestAsync();                         // half-close the request side
var trailers = await s.GetTrailersAsync();              // e.g. grpc-status
```

`HTTP2CachingClient` wraps a connection with an RFC 9111 cache — it serves fresh
responses without a round trip, revalidates stale ones with conditional
requests, keys variants by `Vary`, and honors `Cache-Control` (with private vs.
shared-cache semantics):

```csharp
var cache = new HTTP2CachingClient(conn, "https", "localhost:8443", HTTPCacheMode.Private);
var a = await cache.GetAsync("/files/resource.txt");   // MISS — fetched from origin
var b = await cache.GetAsync("/files/resource.txt");   // HIT  — served from cache
```

## Status & roadmap

See [`CLAUDE.md`](./CLAUDE.md) for the full current-state writeup.

## License

TBD.
