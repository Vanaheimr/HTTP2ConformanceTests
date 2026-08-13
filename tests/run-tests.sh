#!/usr/bin/env bash
#
# Test runner for the hand-rolled HTTP/2 stack (Linux/macOS/WSL).
#
# The counterpart of tests/run-tests.ps1, and not merely a convenience alias:
# the PowerShell runner uses Get-NetTCPConnection, which lives in the Windows-
# only NetTCPIP module, so it cannot run under pwsh on Linux at all. This is
# the Linux path.
#
# The harnesses under tests/ are demo-driven -- raw frame-level clients that
# talk to the running Demo host on https://localhost:8443. They print per-check
# marks but exit 0 regardless, so their output is scanned for the failure mark
# to decide pass/fail. Each is invoked once per scenario (mode [+ sub-case]),
# 48 runs in total.
#
# (The formerly self-contained harnesses now live as NUnit tests in Hermod's
#  HermodTests/HTTP2/ -- see tests/README.md.)
#
# Usage:
#   tests/run-tests.sh                  # build + run everything
#   tests/run-tests.sh --no-build       # skip the build step
#   tests/run-tests.sh --filter attack  # only harnesses matching *attack*
#
set -euo pipefail

nobuild=0
filter=""

while [ $# -gt 0 ]; do
    case "$1" in
        --no-build) nobuild=1;   shift ;;
        --filter)   filter="$2"; shift 2 ;;
        -h|--help)  grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

# Repo layout, relative to this script.
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(dirname "$here")"
sln="$root/HTTP2.slnx"

# The marks the harnesses print. Matched as raw UTF-8 bytes, which is what .NET
# writes on Linux regardless of the shell's locale.
CHECK="✓"
CROSS="✗"

if [ -t 1 ]; then
    C_CYAN=$'\033[36m'; C_GREEN=$'\033[32m'; C_RED=$'\033[31m'
    C_GRAY=$'\033[90m'; C_OFF=$'\033[0m'
else
    C_CYAN=""; C_GREEN=""; C_RED=""; C_GRAY=""; C_OFF=""
fi

total=0
passed=0
fails=()

section() { printf '\n%s=== %s ===%s\n' "$C_CYAN" "$1" "$C_OFF"; }

# --- build -----------------------------------------------------------------
if [ "$nobuild" -eq 0 ]; then
    section "Build"
    dotnet build "$sln" -v quiet >/dev/null
    printf '  %sBuild OK%s\n' "$C_GREEN" "$C_OFF"
fi

# --- free the demo ports ---------------------------------------------------
# Kill whatever still listens on 8443/8080: a stale demo would fault the new
# one's bind, and the demo serves both listeners as one set. Prefer fuser and
# fall back to ss (fuser/lsof aren't always installed, ss usually is).
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

# Locate a built assembly, Debug first (what a plain `dotnet build` produces).
find_dll() {  # $1 = project name, $2 = directory holding the project
    local project="$1" dir="$2" cfg cand
    for cfg in Debug Release; do
        cand="$dir/$project/bin/$cfg/net10.0/$project.dll"
        [ -f "$cand" ] && { printf '%s' "$cand"; return 0; }
    done
    return 1
}

# --- start the demo --------------------------------------------------------
section "Starting Demo host on :8443"

free_ports

# Run the built DLL directly, NOT `dotnet run`: the latter forks a child it does
# not forward signals to, so killing it would orphan the real server and leave
# the ports bound for the next run. Running the DLL means $demo_pid IS the
# server, so the cleanup below actually stops it.
demo_dll=""
for cfg in Debug Release; do
    cand="$root/Demo/bin/$cfg/net10.0/HTTP2.Demo.dll"
    [ -f "$cand" ] && { demo_dll="$cand"; break; }
done
if [ -z "$demo_dll" ]; then
    echo "Demo host not built (no HTTP2.Demo.dll). Run without --no-build first." >&2
    exit 1
fi

# Redirect the demo's output to a file rather than letting it inherit the
# console: the attack harnesses make it log heavily, and an undrained pipe can
# block it mid-run -- which then looks like a crash rather than backpressure.
demo_log="$(mktemp -t h2run-demo.XXXXXX.log)"
dotnet "$demo_dll" >"$demo_log" 2>&1 &
demo_pid=$!

cleanup() {
    section "Stopping Demo host"
    kill "$demo_pid" 2>/dev/null || true
    wait "$demo_pid" 2>/dev/null || true
    free_ports
    rm -f "$demo_log"
}
trap cleanup EXIT

# Wait for the TLS listener to accept (up to ~20 s). Probe with a bare TCP
# connect rather than an HTTP request: these listeners speak HTTP/2 only, so
# "can I open a socket?" is the right readiness signal.
ready=0
for _ in $(seq 1 40); do
    if ! kill -0 "$demo_pid" 2>/dev/null; then
        echo "Demo host exited during startup:" >&2; cat "$demo_log" >&2; exit 1
    fi
    if (exec 3<>"/dev/tcp/127.0.0.1/8443") 2>/dev/null; then
        exec 3>&- 3<&-; ready=1; break
    fi
    sleep 0.5
done
[ "$ready" -eq 1 ] || { echo "Demo host did not start listening on :8443" >&2; exit 1; }
printf '  %sDemo host up (pid %s)%s\n' "$C_GREEN" "$demo_pid" "$C_OFF"

# Let the freshly-started host JIT-warm before the harnesses fire; several of
# them have their own short per-check timeouts.
sleep 1

# --- harness runner --------------------------------------------------------
# $1 = label, $2 = project, $3 = mode, rest = the harness's own arguments.
#   exitcode : pass when the process exits 0 (self-verifying harnesses)
#   nocross  : pass when it exits 0 and printed no failure mark
run_harness() {
    local label="$1" project="$2" mode="$3"
    shift 3

    if [ -n "$filter" ] && [[ "$label" != *"$filter"* && "$project" != *"$filter"* ]]; then
        return 0
    fi

    total=$((total + 1))

    local dll out rc=0 ok=0 summary
    if ! dll="$(find_dll "$project" "$here")"; then
        fails+=("$label")
        printf '  %sFAIL  %s%s\n' "$C_RED" "$label" "$C_OFF"
        printf '        %s(no built assembly for %s)%s\n' "$C_GRAY" "$project" "$C_OFF"
        return 0
    fi

    out="$(dotnet "$dll" "$@" 2>&1)" || rc=$?

    if [ "$rc" -eq 0 ]; then
        if [ "$mode" = "exitcode" ]; then
            ok=1
        elif ! printf '%s' "$out" | grep -qF "$CROSS"; then
            ok=1
        fi
    fi

    if [ "$ok" -eq 1 ]; then
        passed=$((passed + 1))
        # Echo the harness's own last verdict line (a self-reported count, or the
        # last per-check tick) so the runner says something meaningful per run.
        summary="$(printf '%s\n' "$out" \
                   | grep -E "checks passed|$CHECK" \
                   | tail -1 \
                   | sed 's/^[[:space:]]*//; s/[[:space:]]*$//' || true)"
        printf '  %sPASS  %-34s %s%s\n' "$C_GREEN" "$label" "$summary" "$C_OFF"
    else
        fails+=("$label")
        printf '  %sFAIL  %s%s\n' "$C_RED" "$label" "$C_OFF"
        if printf '%s' "$out" | grep -qF "$CROSS"; then
            printf '%s\n' "$out" | grep -F "$CROSS" | while IFS= read -r line; do
                printf '        %s%s%s\n' "$C_RED" "$(printf '%s' "$line" | sed 's/^[[:space:]]*//')" "$C_OFF"
            done
        else
            printf '        %s(exit %s)%s\n%s\n' "$C_GRAY" "$rc" "$C_OFF" "$out"
        fi
    fi
}

# --- the scenarios ---------------------------------------------------------
section "RFC 9110 semantics"
run_harness "h2semantics" "h2semantics" exitcode

section "Attack / hardening scenarios (h2attack)"
for m in contcount contbytes ping settings rapidreset streamid-exhaustion outbound-headerlimit; do
    run_harness "h2attack $m" "h2attack" nocross "$m"
done
for c in missingpath missingmethod missingscheme empty-path uppercase connection \
         te-bad te-ok pseudo-after-regular unknown-pseudo duplicate-pseudo; do
    run_harness "h2attack malformed $c" "h2attack" nocross malformed "$c"
done
for c in ok pseudo no-endstream; do
    run_harness "h2attack trailers $c" "h2attack" nocross trailers "$c"
done
for c in even-stream idle-data idle-windowupdate implicit-close-data implicit-close-windowupdate; do
    run_harness "h2attack idle $c" "h2attack" nocross idle "$c"
done
run_harness "h2attack rst-cancel" "h2attack" nocross rst-cancel

section "CONNECT / WebSocket scenarios (h2connect)"
for m in settings loopback ws-echo ws-fragmented ws-ping ws-close reject cancel multiplex; do
    run_harness "h2connect $m" "h2connect" nocross "$m"
done
for c in scheme-on-connect path-on-connect missing-authority \
         missing-scheme-extended missing-path-extended protocol-on-get; do
    run_harness "h2connect malformed $c" "h2connect" nocross malformed "$c"
done

section "RFC 9218 priority scenarios (h2priority)"
for m in settings urgency-header priority-update priority-update-unknown-stream malformed-priority; do
    run_harness "h2priority $m" "h2priority" nocross "$m"
done

# --- summary ---------------------------------------------------------------
section "Summary"
if [ "${#fails[@]}" -eq 0 ]; then
    printf '  %s%s/%s harness runs passed%s\n' "$C_GREEN" "$passed" "$total" "$C_OFF"
    exit 0
fi
printf '  %s%s/%s harness runs passed%s\n' "$C_RED" "$passed" "$total" "$C_OFF"
printf '  %sFailures:%s\n' "$C_RED" "$C_OFF"
for f in "${fails[@]}"; do
    printf '    %s- %s%s\n' "$C_RED" "$f" "$C_OFF"
done
exit 1
