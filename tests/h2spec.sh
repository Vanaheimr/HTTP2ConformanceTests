#!/usr/bin/env bash
#
# Run the h2spec HTTP/2 conformance suite against the demo host. Linux, macOS,
# WSL -- and Windows, under the Git Bash that ships with Git for Windows (this
# replaced a PowerShell twin; see the header of tests/run-tests.sh for why
# there is only one of each runner now).
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
#     the demo -- which then looks like a conformance failure, not backpressure),
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

# How many tests a full run (no --spec) must at least report, per transport --
# a floor in the sense of autobahn.sh's min_pass. It exists because h2spec's
# exit status cannot carry it: a --spec that matches nothing prints "No matched
# tests found." and exits 0, and before this check the script then announced
# "all conformance tests passed" (the dead-target audit, 2026-10-29). A run
# with --spec must still report at least one test. Raise this when h2spec
# grows; 146 is what 2.6.0 runs.
min_tests=146

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
sln="$root/HTTP2.slnx"
demo="$root/Demo/HTTP2.Demo.csproj"

# shellcheck source=tests/lib.sh
. "$here/lib.sh"

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
# Both listeners come up as one set, so both are freed together. free_ports
# itself lives in tests/lib.sh, shared with the other two runners.
demo_ports="8443 8080"
free_ports $demo_ports || {
    echo "Not starting the demo beside someone else's listener: h2spec would not know which server it reached." >&2
    exit 1
}

# --- start the demo, output redirected to a file (see header) --------------
# Run the built DLL directly, NOT `dotnet run`: the latter forks a child that
# `dotnet run` doesn't forward signals to, so killing it would orphan the real
# server (leaving the ports bound for the next run). Running the DLL means
# $demo_pid IS the server, so the cleanup below actually stops it.
demo_dll=""
for cfg in Debug Release; do
    cand="$root/Demo/bin/$cfg/net10.0/HTTP2.Demo.dll"
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
    free_ports $demo_ports || true
    rm -f "$demo_log" "${h2spec_out:-}"
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
h2spec_out="$(mktemp -t h2spec-out.XXXXXX.log)"
run_h2spec() {  # $1 = extra args describing the transport
    local rc summary total passed

    # Teed rather than captured, so the run still streams; set +e because
    # under pipefail a failing h2spec would otherwise end the script right here.
    set +e
    # shellcheck disable=SC2086
    "$h2spec" "$@" -P "$path" $strict $spec 2>&1 | tee "$h2spec_out"
    rc=${PIPESTATUS[0]}
    set -e

    # The exit status says whether a test failed, not whether any ran -- so the
    # verdict also needs h2spec's own last line, "N tests, P passed, S
    # skipped, F failed": tests ran, and every one of them passed (a skip is
    # not a pass).
    summary="$(grep -E '^[0-9]+ tests, [0-9]+ passed, ' "$h2spec_out" | tail -n 1 || true)"
    total="${summary%% tests,*}"
    passed="$(printf '%s' "$summary" | sed -E 's/^[0-9]+ tests, ([0-9]+) passed.*/\1/')"

    if [ "$rc" -ne 0 ]; then
        failed=$((failed + 1))
    elif [ -z "$summary" ]; then
        echo "h2spec exited 0 but printed no test summary -- nothing was tested." >&2
        failed=$((failed + 1))
    elif [ "$passed" -ne "$total" ]; then
        echo "h2spec: only $passed of $total tests passed (the rest were skipped)." >&2
        failed=$((failed + 1))
    elif [ -z "$spec" ] && [ "$total" -lt "$min_tests" ]; then
        echo "h2spec ran $total tests; a full run must report at least $min_tests." >&2
        failed=$((failed + 1))
    elif [ "$total" -lt 1 ]; then
        echo "h2spec ran no tests." >&2
        failed=$((failed + 1))
    fi
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
