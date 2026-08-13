#!/usr/bin/env bash
#
# Drive the demo host with h2load, nghttp2's HTTP/2 load generator.
#
# h2load is the one foreign client here that pushes CONCURRENCY rather than
# features: curl and HttpClient issue a handful of requests, Grpc.Net.Client a
# few streams, the browsers four. This offers thousands of streams at once and
# reports how many the server actually completed — which is the number that
# matters, not the rate.
#
# It is an external binary and is NOT vendored: on Debian/Ubuntu it comes from
# the `nghttp2-client` package, on macOS from `brew install nghttp2`.
#
# WSL NOTE: run this INSIDE the Linux side. WSL2's NAT boundary means a client
# here cannot reach a listener on the Windows loopback, and the demo binds
# loopback only (deliberately). Both ends on the same side needs no
# configuration change and no exposed port.
#
# Usage:
#   tools/h2load.sh                      # all three scenarios
#   tools/h2load.sh --no-build
#   tools/h2load.sh --port 9443 --requests 20000
#
set -uo pipefail

# Not 8443: the sibling demos (HTTP1-/HTTP3ConformanceTests) like that port too,
# and this script is not entitled to evict whatever is already on it.
port=9443
portc=9080
requests=20000
nobuild=0

while [ $# -gt 0 ]; do
    case "$1" in
        --port)      port="$2";      portc=$((port - 363)); shift 2 ;;
        --requests)  requests="$2";  shift 2 ;;
        --no-build)  nobuild=1;      shift ;;
        -h|--help)   grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(dirname "$here")"

command -v h2load >/dev/null 2>&1 || {
    echo "h2load not found. Debian/Ubuntu: apt-get install nghttp2-client. macOS: brew install nghttp2." >&2
    exit 127
}

if [ "$nobuild" -eq 0 ]; then
    echo "Building the demo host..."
    dotnet build "$root/Demo/HTTP2.Demo.csproj" -v quiet >/dev/null || exit 1
fi

dll=""
for cfg in Debug Release; do
    cand="$root/Demo/bin/$cfg/net10.0/HTTP2.Demo.dll"
    [ -f "$cand" ] && { dll="$cand"; break; }
done
[ -n "$dll" ] || { echo "Demo host not built (no HTTP2.Demo.dll). Run without --no-build first." >&2; exit 1; }

# The built DLL rather than `dotnet run`, whose forked child a plain kill would
# orphan with the ports still bound — the lesson tests/h2spec.sh records.
log="$(mktemp -t h2load-demo.XXXXXX.log)"
dotnet "$dll" --port "$port" --cleartext-port "$portc" >"$log" 2>&1 &
demo_pid=$!
trap 'kill "$demo_pid" 2>/dev/null; wait "$demo_pid" 2>/dev/null; rm -f "$log"' EXIT

ready=0
for _ in $(seq 1 60); do
    if ! kill -0 "$demo_pid" 2>/dev/null; then echo "Demo exited during startup:" >&2; cat "$log" >&2; exit 1; fi
    if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then exec 3>&- 3<&-; ready=1; break; fi
    sleep 0.5
done
[ "$ready" -eq 1 ] || { echo "Demo did not start listening on :$port" >&2; cat "$log" >&2; exit 1; }
echo "Demo host up (pid $demo_pid) on :$port (h2) and :$portc (h2c)"

failed=0

# $1 = label, rest = h2load arguments. The verdict is "did every request
# complete", not the rate: a load generator that reports 50k req/s while
# quietly dropping a tenth of them has measured nothing.
run() {
    local label="$1"; shift
    echo
    echo "=== $label ==="

    local out
    out="$("$@" 2>&1)"
    echo "$out" | grep -vE "^progress|^spawning|^starting"

    if ! echo "$out" | grep -qE "requests: $requests total, .* $requests succeeded, 0 failed, 0 errored, 0 timeout"; then
        echo "  -> NOT all requests succeeded" >&2
        failed=$((failed + 1))
    fi
}

run "TLS h2 — $requests requests, 10 connections x 32 concurrent streams" \
    h2load -n "$requests" -c 10 -m 32 "https://127.0.0.1:$port/"

run "cleartext h2c — $requests requests, 10 connections x 32 concurrent streams" \
    h2load -n "$requests" -c 10 -m 32 "http://127.0.0.1:$portc/"

# 100 x 100 offers 10 000 streams at once against a server advertising
# MAX_CONCURRENT_STREAMS = 100 per connection, so this exercises the gating as
# much as the throughput.
run "TLS h2 — deep concurrency, 100 connections x 100 streams" \
    h2load -n "$requests" -c 100 -m 100 "https://127.0.0.1:$port/"

echo
if [ "$failed" -gt 0 ]; then
    echo "h2load: $failed scenario(s) did not complete every request."
    exit 1
fi
echo "h2load: every request completed in all scenarios."
exit 0
