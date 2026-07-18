#!/usr/bin/env bash
#
# Run the h2spec HTTP/2 conformance suite against the demo host (Linux/macOS).
#
# h2spec (https://github.com/summerwind/h2spec) is the canonical RFC 9113 +
# RFC 7541 conformance suite. It is an external binary and is NOT vendored in
# this repo -- download a release first (see tests/TestingAgainst_h2spec.md),
# then put it on your PATH or pass --h2spec <path>.
#
# This script does the fiddly bits for you:
#   * builds the solution (unless --no-build),
#   * frees ports 8443 (TLS) and 8080 (cleartext h2c) if a stale demo is bound,
#   * starts the demo host with its output redirected to a file (so h2spec's
#     flood of malformed cases can't fill an undrained console pipe and stall
#     the demo -- the same gotcha the PowerShell runner documents),
#   * runs h2spec against the TLS and/or cleartext listener,
#   * stops the demo again (via an EXIT trap, even on error/Ctrl-C).
#
# Usage:
#   tests/h2spec.sh                        # both transports (TLS + h2c)
#   tests/h2spec.sh --transport tls        # only the TLS listener :8443
#   tests/h2spec.sh --transport cleartext  # only the h2c  listener :8080
#   tests/h2spec.sh --h2spec ~/bin/h2spec  # explicit binary path
#   tests/h2spec.sh --no-build --spec "http2/6.5"   # skip build, one section
#
set -euo pipefail

transport="both"
h2spec="h2spec"
path="/echo"
spec=""
strict=""
nobuild=0

while [ $# -gt 0 ]; do
    case "$1" in
        --transport) transport="$2"; shift 2 ;;
        --h2spec)    h2spec="$2";    shift 2 ;;
        --path)      path="$2";      shift 2 ;;
        --spec)      spec="$2";      shift 2 ;;
        --strict)    strict="-S";    shift ;;
        --no-build)  nobuild=1;      shift ;;
        -h|--help)   grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

case "$transport" in both|tls|cleartext) ;; *)
    echo "--transport must be both|tls|cleartext (got '$transport')" >&2; exit 2 ;;
esac

# Repo layout, relative to this script.
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(dirname "$here")"
sln="$root/src/HTTP2.slnx"
demo="$root/src/Demo/HTTP2.Demo.csproj"

# --- locate h2spec ---------------------------------------------------------
if ! command -v "$h2spec" >/dev/null 2>&1; then
    echo "h2spec not found ('$h2spec'). Download it from" >&2
    echo "  https://github.com/summerwind/h2spec/releases" >&2
    echo "and put it on your PATH, or pass --h2spec <path>." >&2
    echo "See tests/TestingAgainst_h2spec.md." >&2
    exit 127
fi
echo "Using h2spec: $(command -v "$h2spec")"

# --- build -----------------------------------------------------------------
if [ "$nobuild" -eq 0 ]; then
    echo "Building solution..."
    dotnet build "$sln" -v quiet >/dev/null
fi

# --- free the demo ports ---------------------------------------------------
# Kill whatever still listens on 8443/8080 (a stale demo would fault the new
# one's bind). Prefer fuser; fall back to ss (fuser/lsof aren't always
# installed, but ss usually is).
free_ports() {
    if command -v fuser >/dev/null 2>&1; then
        fuser -k 8443/tcp 8080/tcp >/dev/null 2>&1 || true
    elif command -v ss >/dev/null 2>&1; then
        local pids
        # The '|| true' matters: with `set -o pipefail`, grep finding no match
        # (the common "nothing stale is bound" case) would otherwise abort the
        # whole script under `set -e`.
        pids="$(ss -ltnpH 'sport = :8443 or sport = :8080' 2>/dev/null \
                | grep -oE 'pid=[0-9]+' | grep -oE '[0-9]+' | sort -u || true)"
        [ -n "$pids" ] && kill $pids 2>/dev/null && sleep 0.5 || true
    fi
}
free_ports

# --- start the demo, output redirected to a file (see header) --------------
# Run the built DLL directly, NOT `dotnet run`: the latter forks a child that
# `dotnet run` doesn't forward signals to, so killing it would orphan the real
# server (leaving the ports bound for the next run). Running the DLL means
# $demo_pid IS the server, so the cleanup below actually stops it.
demo_dll=""
for cfg in Debug Release; do
    cand="$root/src/Demo/bin/$cfg/net10.0/HTTP2.Demo.dll"
    [ -f "$cand" ] && { demo_dll="$cand"; break; }
done
if [ -z "$demo_dll" ]; then
    echo "Demo host not built (no HTTP2.Demo.dll). Run without --no-build first." >&2
    exit 1
fi
demo_log="$(mktemp -t h2spec-demo.XXXXXX.log)"
dotnet "$demo_dll" >"$demo_log" 2>&1 &
demo_pid=$!

cleanup() {
    kill "$demo_pid" 2>/dev/null || true
    wait "$demo_pid" 2>/dev/null || true
    free_ports
    rm -f "$demo_log"
}
trap cleanup EXIT

# Wait for whichever listener we need (TLS -> 8443, cleartext -> 8080).
# Probe with a bare TCP connect (bash /dev/tcp), NOT an HTTP request: the h2c
# listener speaks only HTTP/2 prior-knowledge and rejects a plain HTTP/1.1
# probe, so "can I open a socket?" is the right readiness signal here.
if [ "$transport" = "tls" ]; then wait_port=8443; else wait_port=8080; fi
ready=0
for _ in $(seq 1 40); do
    if ! kill -0 "$demo_pid" 2>/dev/null; then
        echo "Demo host exited during startup:" >&2; cat "$demo_log" >&2; exit 1
    fi
    if (exec 3<>"/dev/tcp/127.0.0.1/$wait_port") 2>/dev/null; then
        exec 3>&- 3<&-; ready=1; break
    fi
    sleep 0.5
done
[ "$ready" -eq 1 ] || { echo "Demo host did not start listening on port $wait_port" >&2; exit 1; }
echo "Demo host up (pid $demo_pid)"

# Let the freshly-started host JIT-warm before h2spec fires: h2spec's per-test
# timeout defaults to 2 s, and a cold .NET process's first request can trip it.
sleep 1

failed=0
run_h2spec() {  # $1 = extra args describing the transport
    # shellcheck disable=SC2086
    "$h2spec" "$@" -P "$path" $strict $spec || failed=$((failed + 1))
}

if [ "$transport" = "both" ] || [ "$transport" = "tls" ]; then
    echo; echo "=== h2spec over TLS (h2, :8443) ==="
    run_h2spec -t -k -h 127.0.0.1 -p 8443
fi
if [ "$transport" = "both" ] || [ "$transport" = "cleartext" ]; then
    echo; echo "=== h2spec over cleartext (h2c, :8080) ==="
    run_h2spec -h 127.0.0.1 -p 8080
fi

echo
if [ "$failed" -gt 0 ]; then
    echo "h2spec reported failures on $failed transport(s)."
    exit 1
fi
echo "h2spec: all conformance tests passed."
exit 0
