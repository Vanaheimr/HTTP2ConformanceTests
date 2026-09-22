#!/usr/bin/env bash
#
# Run the Autobahn TestSuite WebSocket conformance run against our echo server.
# See tests/TestingAgainst_Autobahn.md for the full walkthrough.
#
# Linux, macOS and WSL are the verified paths. Windows (Git Bash + Docker
# Desktop) is supported here -- it is where the PowerShell twin's knowledge
# went when that file was removed -- but is NOT exercised by anything: the
# nightly runs Autobahn only on ubuntu-latest, because the suite ships usably
# only as a Docker image. Treat the Windows branch as untested until someone
# with Docker Desktop runs it.
#
# The Autobahn TestSuite (https://github.com/crossbario/autobahn-testsuite) is
# the canonical RFC 6455 WebSocket conformance suite; its "fuzzingclient" drives
# ~500 cases against a WebSocket ECHO server. We run it from the official Docker
# image (the native wstest is legacy Python 2), so the only prerequisite is
# Docker. The echo server under test (tests/autobahn-server) exposes the SAME
# WebSocketConnection framing used in production, over a plain-TCP tunnel behind
# a minimal HTTP/1.1 Upgrade handshake -- Autobahn speaks WebSocket over
# HTTP/1.1, not RFC 8441 over HTTP/2, but the framing under test is
# transport-agnostic (see the doc).
#
# This script builds the echo server, starts it (output drained to a file), runs
# the Autobahn fuzzingclient (Docker, --network host) against it, parses the JSON
# report, and exits non-zero if any case did not pass. The echo server is stopped
# on exit (even on error / Ctrl-C).
#
# Usage:
#   tests/autobahn.sh                 # build + run everything
#   tests/autobahn.sh --no-build
#   tests/autobahn.sh --port 9010 --image crossbario/autobahn-testsuite
#   tests/autobahn.sh --run-timeout 1200   # cap the fuzzingclient (seconds, 0 = off)
#
set -euo pipefail

port=9010
image="crossbario/autobahn-testsuite"
nobuild=0

# A ceiling on the fuzzingclient itself. The whole run takes about eight
# minutes in CI (7:50, 8:08, 8:09 on three consecutive nights), so twenty is
# far above normal and still well inside the workflow's own 45-minute step
# budget -- which is the point. On 2026-08-15 the container hung instead of
# finishing; without a cap of its own it ate the entire step budget, and a step
# that overruns timeout-minutes is treated as a *cancellation*, so the summary
# was never written, the report artifact was never uploaded, and GitHub never
# even flushed the job's log. The hang left literally nothing to look at.
#
# With this, a hang becomes an ordinary non-zero exit inside the script: the
# missing index.json below is reported, the caller's log is intact, and the
# artifact upload still runs.
run_timeout=1200

# The floor this run must not fall below, and the reason it is a floor rather
# than an expected total.
#
# 517 today, and that is deliberately NOT 481.
#
# The pin still carries the old WebSocketDeflate.ShouldAccept, which accepted
# any offer without parsing its parameters -- including server_max_window_bits=9,
# which it then ignored and compressed with 15 bits anyway. Against that code
# Autobahn really does report 517/517, so 517 is the honest floor for what this
# repository currently builds.
#
# The fix is on Hermod master. When the pin advances past it this run will drop
# to 481 and FAIL against this floor -- on purpose. That failure is the prompt to
# lower it to 481 deliberately, with the reason recorded, rather than having the
# number quietly slip. Measured: 481/517 against the fixed library, identical to
# what the HTTP/1.1 sibling reports, the 36 difference being sections 13.3 and
# 13.5 declined rather than falsely accepted.
#
# UNIMPLEMENTED is the one non-passing verdict tolerated, because it is not a
# failure: it means the server declined an extension offer it cannot satisfy,
# which RFC 7692 Section 7.1.2.1 requires rather than permits. Everything else
# -- FAILED, WRONG CODE, UNCLEAN -- fails the run outright no matter what the
# count says, so the floor can never launder a real regression into a pass.
#
# Raise it when the number goes up. A floor that is never raised is a ratchet
# that has rusted.
min_pass=517

while [ $# -gt 0 ]; do
    case "$1" in
        --port)        port="$2";        shift 2 ;;
        --image)       image="$2";       shift 2 ;;
        --run-timeout) run_timeout="$2"; shift 2 ;;
        --min-pass)    min_pass="$2";    shift 2 ;;
        --no-build)    nobuild=1;        shift ;;
        -h|--help)     grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(dirname "$here")"
sln="$root/HTTP2.slnx"
repdir="$root/tests/autobahn/reports"

# shellcheck source=tests/lib.sh
. "$here/lib.sh"

command -v docker >/dev/null 2>&1 || {
    echo "docker not found. Install Docker and retry. See tests/TestingAgainst_Autobahn.md." >&2
    exit 127
}

# --- build -----------------------------------------------------------------
if [ "$nobuild" -eq 0 ]; then
    echo "Building echo server..."
    dotnet build "$sln" -v quiet >/dev/null
fi

# --- locate the built echo server DLL --------------------------------------
srv_dll=""
for cfg in Debug Release; do
    cand="$root/tests/autobahn-server/bin/$cfg/net10.0/autobahn-server.dll"
    [ -f "$cand" ] && { srv_dll="$cand"; break; }
done
[ -n "$srv_dll" ] || { echo "Echo server not built (no autobahn-server.dll). Run without --no-build first." >&2; exit 1; }

# --- free the port (a stale server would fault the new bind) ----------------
# free_ports lives in tests/lib.sh, shared with the other two runners.
free_ports "$port"

# --- start the echo server, output drained to a file -----------------------
# Run the DLL directly (not `dotnet run`, whose forked child a plain kill would
# orphan -- same reasoning as tests/h2spec.sh).
srv_log="$(mktemp -t autobahn-server.XXXXXX.log)"
dotnet "$srv_dll" "$port" >"$srv_log" 2>&1 &
srv_pid=$!

# The echo server's own log is the one view of a failure from OUR side of the
# wire, and it used to be deleted here unconditionally -- which is why three
# hung nights produced nothing but the container's console output. It is now
# kept next to the report, where the workflow's artifact upload picks it up.
cleanup() {
    kill "$srv_pid" 2>/dev/null || true
    wait "$srv_pid" 2>/dev/null || true
    free_ports "$port"
    if [ -s "$srv_log" ] && [ -d "$repdir" ]; then
        cp "$srv_log" "$repdir/echo-server.log" 2>/dev/null || true
    fi
    rm -f "$srv_log"
    # The container is no longer --rm (see the run below), so it is this
    # trap's job to make sure none is left behind, however the script ends.
    if [ -n "${container:-}" ]; then
        docker rm -f "$container" >/dev/null 2>&1 || true
    fi
    return 0
}
trap cleanup EXIT

# Wait for the echo server to accept a TCP connection (bare connect, not HTTP).
ready=0
for _ in $(seq 1 40); do
    if ! kill -0 "$srv_pid" 2>/dev/null; then
        echo "Echo server exited during startup:" >&2; cat "$srv_log" >&2; exit 1
    fi
    if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
        exec 3>&- 3<&-; ready=1; break
    fi
    sleep 0.3
done
[ "$ready" -eq 1 ] || { echo "Echo server did not start listening on port $port" >&2; exit 1; }
echo "Echo server up (pid $srv_pid) on ws://127.0.0.1:$port/"

# --- how the container reaches the echo server -----------------------------
# The one genuine platform difference in this script, inherited from the
# PowerShell runner this replaced.
#
# `--network host` puts the container in the host's own network namespace, so
# 127.0.0.1 inside the container *is* the host. That is a Linux-namespace
# feature: on Docker Desktop it either does not exist or is an opt-in beta, so
# the container would loop back to itself and reach nothing. There the default
# bridge plus the host.docker.internal alias is the documented way in.
#
# The mount paths need the same care. Git Bash reports this repo as
# /d/Coding/..., and MSYS rewrites an argument beginning with a slash into a
# Windows path before docker sees it -- worse, it treats the colon in
# "src:/config" as a path-list separator and mangles both halves.
# MSYS_NO_PATHCONV=1 turns that off, and cygpath hands docker the D:\... form
# Docker Desktop actually wants. Both are inert on Linux.
if is_windows; then
    docker_net=""
    ws_host="host.docker.internal"
    mount_src="$(cygpath -w "$repdir")"
else
    docker_net="--network host"
    ws_host="127.0.0.1"
    mount_src="$repdir"
fi

rm -rf "$repdir"; mkdir -p "$repdir"
cfg="$repdir/fuzzingclient.json"
cat >"$cfg" <<JSON
{
    "outdir": "/reports",
    "servers": [{ "agent": "Hermod.HTTP2", "url": "ws://$ws_host:$port" }],
    "cases": ["*"],
    "exclude-cases": [],
    "exclude-agent-cases": {}
}
JSON

# --name, so the timeout below has something to stop: killing the `docker run`
# client detaches from the container, it does not end it, and a container left
# running would hold the report directory and the port.
container="autobahn-wstest-$port"
docker rm -f "$container" >/dev/null 2>&1 || true

# `timeout` is GNU coreutils and is present on Linux, macOS (as gtimeout under
# a different name) and Git Bash alike; if it is missing, or the cap is 0, run
# uncapped rather than refusing to run at all.
runner=""
if [ "$run_timeout" -gt 0 ] && command -v timeout >/dev/null 2>&1; then
    runner="timeout --signal=TERM --kill-after=30s ${run_timeout}s"
fi

echo "Running Autobahn fuzzingclient (Docker image $image, ws://$ws_host:$port)..."
# shellcheck disable=SC2086  # $runner and $docker_net are fixed literals or empty, on purpose
# Deliberately NOT --rm: an auto-removed container takes its exit status with
# it, and "why did this die" is exactly the question. `docker inspect` below
# distinguishes a kernel OOM kill from every other cause of the 137 seen on
# 2026-09-18 -- which the exit code alone cannot. It is removed by hand
# instead, on the failure path and in the EXIT trap.
# PYTHONUNBUFFERED, because without it wstest's "Running test case ID X"
# lines arrive in blocks and the last one printed is NOT the case it is
# working on. That cost a wrong diagnosis once: three CI logs ended at
# 12.3.3 and it looked like a case that reliably hangs, when a *passing*
# local run showed 12.3.3 sitting as the last flushed line for four
# minutes while the suite ran on through 13.3. Unbuffered, the last line
# is the truth, and a hang names itself.
MSYS_NO_PATHCONV=1 $runner docker run --name "$container" -e PYTHONUNBUFFERED=1 $docker_net \
    -v "$mount_src:/config" \
    -v "$mount_src:/reports" \
    "$image" \
    wstest -m fuzzingclient -s /config/fuzzingclient.json || {
        rc=$?

        # 124 is `timeout` firing; 137 is the container's own process dying on
        # SIGKILL, which is NOT the same thing and must not be reported as if
        # it were. On 2026-09-18 this branch claimed "did not finish within
        # 1200s" after eight and a half minutes, because it lumped the two
        # together -- the cap had not fired at all.
        case "$rc" in
            124) echo "fuzzingclient exceeded the ${run_timeout}s cap -- stopping the container." >&2 ;;
            137) echo "fuzzingclient was killed (SIGKILL, exit 137) before the ${run_timeout}s cap." >&2 ;;
            *)   echo "docker run returned $rc" >&2 ;;
        esac

        # Whatever ended it, the interesting question is what the two ends were
        # doing. These only run on failure, so they cost nothing normally.
        #
        # First: is our side even still there? A dead echo server would leave
        # the fuzzingclient waiting forever on a case that never answers, which
        # is exactly what the hung nights look like from outside -- and the
        # server prints only a startup banner, so a crash stack in
        # echo-server.log (kept by the cleanup above) would be the whole answer.
        if kill -0 "$srv_pid" 2>/dev/null; then
            echo "echo server (pid $srv_pid) was still alive at this point." >&2
        else
            echo "echo server (pid $srv_pid) had ALREADY EXITED -- see echo-server.log." >&2
        fi

        # Second: what was on the wire. Bytes stuck in both send queues is a
        # flow-control deadlock; empty queues on a live connection is not.
        if command -v ss >/dev/null 2>&1; then
            echo "--- sockets on :$port at the time of failure ---" >&2
            # Captured, not redirected in place: `2>/dev/null >&2` applies left
            # to right, so it would point stdout at /dev/null and silently throw
            # away the very output being collected.
            sockets="$(ss -tnpi "sport = :$port or dport = :$port" 2>/dev/null || true)"
            echo "${sockets:-  (no sockets on this port)}" >&2
        fi

        # The corpse still knows how it died. OOMKilled separates "the kernel
        # took it" from "something else sent the signal", and the two call for
        # completely different fixes.
        state="$(docker inspect \
                   --format 'exit={{.State.ExitCode}} oom-killed={{.State.OOMKilled}} error="{{.State.Error}}"' \
                   "$container" 2>/dev/null || true)"
        if [ -n "$state" ]; then
            echo "container state: $state" >&2
        else
            echo "container already gone; no state to inspect." >&2
        fi

        docker rm -f "$container" >/dev/null 2>&1 || true
    }

# --- parse the report ------------------------------------------------------
INDEXFILE="$repdir/index.json"
[ -f "$INDEXFILE" ] || { echo "No Autobahn report at $INDEXFILE (did the container reach the server?)" >&2; exit 1; }

# Three buckets rather than two, because "not passing" is not one thing here.
#
#   passing  -- OK, NON-STRICT, INFORMATIONAL
#   declined -- UNIMPLEMENTED: the server refused an extension offer it cannot
#               satisfy. RFC 7692 Section 7.1.2.1 requires that refusal, so it
#               is correct behaviour, not a defect. Tolerated, but counted.
#   hard     -- anything else: FAILED, WRONG CODE, UNCLEAN. Always fatal.
#
# The verdict is then: no hard failures at all, and passing >= $min_pass. A
# floor cannot hide a regression into a *failure*, only a change in how many
# offers we decline -- and that number moving down is what the floor catches.
#
# Python does the counting; jq is not assumed, and the old grep fallback could
# only answer yes/no, which is useless once the answer is a number.
read -r passing declined hard total < <(
python3 - "$INDEXFILE" <<'PYEOF'
import json, sys, collections
PASS     = {"OK", "NON-STRICT", "INFORMATIONAL"}
DECLINED = {"UNIMPLEMENTED"}
d = json.load(open(sys.argv[1], encoding="utf-8"))
passing = declined = hard = total = 0
worst = collections.Counter()
for agent, cases in d.items():
    for cid, r in cases.items():
        total += 1
        b, bc = r.get("behavior"), r.get("behaviorClose")
        if b in PASS and bc in PASS:
            passing += 1
        elif b in DECLINED or bc in DECLINED:
            declined += 1
        else:
            hard += 1
            worst[f"  {cid}: behavior={b} close={bc}"] += 1
for line in sorted(worst):
    print(line, file=sys.stderr)
print(passing, declined, hard, total)
PYEOF
)

echo
echo "Autobahn: $passing/$total passing, $declined declined (UNIMPLEMENTED), $hard hard failures"
echo "Full HTML report: $repdir/index.html"
echo

if [ "$hard" -gt 0 ]; then
    echo "FAIL: $hard case(s) failed outright — see the lines above and the HTML report." >&2
    exit 1
fi

if [ "$passing" -lt "$min_pass" ]; then
    echo "FAIL: $passing passing is below the floor of $min_pass." >&2
    echo "      Either a regression, or the floor needs revisiting — deliberately, not silently." >&2
    exit 1
fi

if [ "$passing" -gt "$min_pass" ]; then
    echo "NOTE: $passing passing is ABOVE the floor of $min_pass. Raise min_pass in this script"
    echo "      so the improvement is held rather than merely enjoyed."
fi

echo "Autobahn: at or above the floor ($passing >= $min_pass), no hard failures."
exit 0
