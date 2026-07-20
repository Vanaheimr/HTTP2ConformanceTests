#!/usr/bin/env bash
#
# Run the Autobahn TestSuite WebSocket conformance run against our echo server
# (Linux/macOS). See tests/TestingAgainst_Autobahn.md for the full walkthrough.
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
#
set -euo pipefail

port=9010
image="crossbario/autobahn-testsuite"
nobuild=0

while [ $# -gt 0 ]; do
    case "$1" in
        --port)     port="$2";  shift 2 ;;
        --image)    image="$2"; shift 2 ;;
        --no-build) nobuild=1;  shift ;;
        -h|--help)  grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(dirname "$here")"
sln="$root/src/HTTP2.slnx"
repdir="$root/tests/autobahn/reports"

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
free_port() {
    if command -v fuser >/dev/null 2>&1; then
        fuser -k "${port}/tcp" >/dev/null 2>&1 || true
    elif command -v ss >/dev/null 2>&1; then
        local pids
        pids="$(ss -ltnpH "sport = :${port}" 2>/dev/null | grep -oE 'pid=[0-9]+' | grep -oE '[0-9]+' | sort -u || true)"
        [ -n "$pids" ] && kill $pids 2>/dev/null && sleep 0.5 || true
    fi
}
free_port

# --- start the echo server, output drained to a file -----------------------
# Run the DLL directly (not `dotnet run`, whose forked child a plain kill would
# orphan -- same reasoning as tests/h2spec.sh).
srv_log="$(mktemp -t autobahn-server.XXXXXX.log)"
dotnet "$srv_dll" "$port" >"$srv_log" 2>&1 &
srv_pid=$!

cleanup() {
    kill "$srv_pid" 2>/dev/null || true
    wait "$srv_pid" 2>/dev/null || true
    free_port
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

# --- write a Linux config (host networking -> 127.0.0.1) and run Autobahn ---
rm -rf "$repdir"; mkdir -p "$repdir"
cfg="$repdir/fuzzingclient.json"
cat >"$cfg" <<JSON
{
    "outdir": "/reports",
    "servers": [{ "agent": "Hermod.HTTP2", "url": "ws://127.0.0.1:$port" }],
    "cases": ["*"],
    "exclude-cases": [],
    "exclude-agent-cases": {}
}
JSON

echo "Running Autobahn fuzzingclient (Docker image $image)..."
docker run --rm --network host \
    -v "$repdir:/config" \
    -v "$repdir:/reports" \
    "$image" \
    wstest -m fuzzingclient -s /config/fuzzingclient.json || echo "docker run returned $?"

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
