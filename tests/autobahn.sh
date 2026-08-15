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

while [ $# -gt 0 ]; do
    case "$1" in
        --port)        port="$2";        shift 2 ;;
        --image)       image="$2";       shift 2 ;;
        --run-timeout) run_timeout="$2"; shift 2 ;;
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

cleanup() {
    kill "$srv_pid" 2>/dev/null || true
    wait "$srv_pid" 2>/dev/null || true
    free_ports "$port"
    rm -f "$srv_log"
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
MSYS_NO_PATHCONV=1 $runner docker run --rm --name "$container" $docker_net \
    -v "$mount_src:/config" \
    -v "$mount_src:/reports" \
    "$image" \
    wstest -m fuzzingclient -s /config/fuzzingclient.json || {
        rc=$?
        if [ "$rc" -eq 124 ] || [ "$rc" -eq 137 ]; then
            echo "fuzzingclient did not finish within ${run_timeout}s -- stopping the container." >&2
            docker rm -f "$container" >/dev/null 2>&1 || true
        else
            echo "docker run returned $rc"
        fi
    }

# --- parse the report ------------------------------------------------------
index="$repdir/index.json"
[ -f "$index" ] || { echo "No Autobahn report at $index (did the container reach the server?)" >&2; exit 1; }

# Pass = behavior AND behaviorClose both in {OK, NON-STRICT, INFORMATIONAL}.
# Prefer jq; fall back to python3; last resort a grep heuristic.
bad=1
if command -v jq >/dev/null 2>&1; then
    bad="$(jq '[.. | objects | select(has("behavior")) |
                 select((.behavior      | IN("OK","NON-STRICT","INFORMATIONAL") | not) or
                        (.behaviorClose  | IN("OK","NON-STRICT","INFORMATIONAL") | not))] | length' "$index")"
    total="$(jq '[.. | objects | select(has("behavior"))] | length' "$index")"
    echo; echo "Autobahn: $((total - bad))/$total cases OK"
elif command -v python3 >/dev/null 2>&1; then
    read -r total bad < <(python3 - "$index" <<'PY'
import json, sys
allowed = {"OK", "NON-STRICT", "INFORMATIONAL"}
d = json.load(open(sys.argv[1]))
total = fail = 0
for agent in d.values():
    for cid, r in agent.items():
        total += 1
        if r.get("behavior") not in allowed or r.get("behaviorClose") not in allowed:
            fail += 1
            print(f"  {cid}: behavior={r.get('behavior')} close={r.get('behaviorClose')}", file=sys.stderr)
print(total, fail)
PY
)
    echo; echo "Autobahn: $((total - bad))/$total cases OK"
else
    echo "Neither jq nor python3 found; falling back to a grep heuristic." >&2
    if grep -qE '"(behavior|behaviorClose)": *"(FAILED|WRONG CODE|UNCLEAN)"' "$index"; then
        bad=1
    else
        bad=0
    fi
fi

echo "Full HTML report: $repdir/index.html"
echo
if [ "$bad" -gt 0 ]; then
    echo "Autobahn reported non-passing cases."
    exit 1
fi
echo "Autobahn: all cases passed."
exit 0
