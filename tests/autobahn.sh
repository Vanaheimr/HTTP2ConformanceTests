#!/usr/bin/env bash
#
# Run the Autobahn TestSuite WebSocket conformance run against our echo server.
# See tests/TestingAgainst_Autobahn.md for the full walkthrough.
#
# Linux, macOS and WSL are the verified paths. Windows (Git Bash + Docker
# Desktop) is supported here -- it is where the PowerShell twin's knowledge
# went when that file was removed -- but nothing runs it regularly: the
# nightly runs Autobahn only on ubuntu-latest, because the suite ships usably
# only as a Docker image. On 2026-10-29 the Windows branch was driven under Git
# Bash with a stand-in for `docker`, on a machine without Docker Desktop.
# Everything up to the container held: the echo server, the D:\ mount paths
# docker is handed, the host.docker.internal URL, the verdict and the 137
# path. One defect turned up, in finding Python (see below). What is still
# unverified is Docker Desktop itself, i.e. that its container reaches an echo
# server bound to the host's 127.0.0.1 through host.docker.internal.
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
#   tests/autobahn.sh --oom-retries 0      # never repeat a run wstest's OOM kill lost
#   tests/autobahn.sh --split-off ''       # all cases in one wstest, 12.4 included
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
# 481 since 2026-09-23, lowered from 517 -- and lowered because the server got
# STRICTER. That is the one direction in which a falling number is the right
# answer, so the reason belongs here rather than in a changelog nobody opens.
#
# The pin used to carry a WebSocketDeflate.ShouldAccept that accepted any offer
# whose text merely contained "permessage-deflate", parameters unread. Faced with
# server_max_window_bits=9 -- the client capping the window this server may
# compress with -- it answered "accepted" and then compressed with the full
# 15-bit window anyway, which a peer that had sized its inflate window to 9 bits
# could not have decoded. Autobahn scored that 517/517, because Python's zlib
# inflates with a large window regardless and so never notices.
#
# Hermod eb7bf410 made it parse the offer and decline what it cannot deliver --
# RFC 7692 Section 7.1.2.1 requires that rather than permitting it -- and the pin
# bump of 2026-09-23 brought the fix here. The 36 cases in sections 13.3 and 13.5
# now report UNIMPLEMENTED instead of OK. Nothing regressed: the missing 36 were
# the price of claiming a capability we did not have, and 517 was never a number
# this server had earned.
#
# The HTTP/1.1 sibling reports the same 481/517 from an implementation written
# independently of this one, which is what two correct readings of one RFC ought
# to look like.
#
# UNIMPLEMENTED is the one non-passing verdict tolerated, because it is not a
# failure: it means the server declined an extension offer it cannot satisfy.
# Everything else -- FAILED, WRONG CODE, UNCLEAN -- fails the run outright no
# matter what the count says, so the floor can never launder a real regression
# into a pass.
#
# Raise it when the number goes up. A floor that is never raised is a ratchet
# that has rusted.
min_pass=481

# How often a run lost to the fuzzing client's own memory runaway is repeated.
#
# On 2026-10-10 and 2026-11-20 the nightly died the same way: early in section
# 12.4, wstest grew by about 1.7 GB/s until the kernel's OOM killer took it.
# The second night's kernel report named the consumer: wstest at 16.6 GiB,
# our echo server at 63 MiB (docs/BUILD_LOG.md, 2026-11-21). That says nothing
# about our server, and a night it fails is a night the gate reports nothing.
#
# So such a run is repeated, as a WHOLE run, and the repeat is judged by the
# same verdict as any other; one that is lost the same way again fails. The
# repeat happens only when the kernel's own report says so: the container
# OOM-killed, its victim wstest, and our echo server alive and at most
# oom_retry_server_max_mib in the kernel's task table. A server that had grown
# is our problem, and is never retried. A repeat is announced in the log, in
# the verdict, and as a GitHub warning, and the lost attempt's evidence is kept
# in attempt-<n>/ beside the report. Linux only, because only there is the
# kernel's report readable (see kernel_log in lib.sh).
oom_retries=1
oom_retry_server_max_mib=1024

# The cases that run in a fuzzing client of their own, after all the others.
#
# Every OOM kill so far fell early in section 12.4: in 12.4.6 on 2026-10-10,
# in 12.4.4 on 2026-11-20, and on 2026-11-21 in 12.4.3 and then, in the repeat
# three minutes later on the same runner, in 12.4.4. Each time wstest had run
# 12.1 first, which leaves it holding about 3 GiB it never gives back
# (docs/BUILD_LOG.md, 2026-11-21). The repeat above runs exactly that sequence
# again, and on 2026-11-21 it died the same way. So 12.4 now starts in a fresh
# wstest, in a container of its own.
#
# It was a hypothesis, and the night of 2026-11-22 refuted it: with 12.4 split
# off, wstest ran away in 12.3.11 and, in the repeat, in 12.3.12, while 12.4
# alone was clean. 12.4 was where the runaway arrived, not its cause. The split
# stays because it is cheap and still brings home 12.4's report on such a
# night; it does not keep the night green. The second container costs about a
# second to start. Each part has
# its own report directory under reports/ and its own repeat. The verdict counts
# the two reports merged, so the floor and the hard failures mean what they
# meant for a single run. --split-off '' runs everything in one wstest. The
# pattern is wstest's: a trailing '*' works, '?' does not.
split_off='12.4.*'

while [ $# -gt 0 ]; do
    case "$1" in
        --port)        port="$2";        shift 2 ;;
        --image)       image="$2";       shift 2 ;;
        --run-timeout) run_timeout="$2"; shift 2 ;;
        --min-pass)    min_pass="$2";    shift 2 ;;
        --oom-retries) oom_retries="$2"; shift 2 ;;
        --split-off)   split_off="$2";   shift 2 ;;
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

# Python counts the report at the end, so find one now rather than after an
# eight-minute run. Each candidate is found by RUNNING it, because being on
# PATH proves nothing on Windows: there, `python3` is usually the App
# Installer's Store stub. It prints "Python was not found" on stderr and exits
# 49, and on 2026-10-29 it ended a run of this script under Git Bash after the
# suite had finished, with no verdict. A python.org install provides `python`
# and `py`, not `python3`. 3.6 is the floor because the counting uses
# f-strings.
python=""
for cand in python3 python "py -3"; do
    # shellcheck disable=SC2086  # "py -3" is a command and its argument, on purpose
    if $cand -c 'import sys; sys.exit(sys.version_info < (3, 6))' >/dev/null 2>&1; then
        python="$cand"; break
    fi
done
[ -n "$python" ] || {
    echo "No working Python 3.6+ found (tried python3, python, py -3). It counts the report." >&2
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
    cand="$root/tests/autobahn-server/bin/$cfg/net11.0/autobahn-server.dll"
    [ -f "$cand" ] && { srv_dll="$cand"; break; }
done
[ -n "$srv_dll" ] || { echo "Echo server not built (no autobahn-server.dll). Run without --no-build first." >&2; exit 1; }

# --- free the port (a stale server would fault the new bind) ----------------
# free_ports lives in tests/lib.sh, shared with the other two runners.
free_ports "$port" || {
    echo "Not starting the echo server beside someone else's listener on :$port." >&2
    exit 1
}

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
    free_ports "$port" || true
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

# --name, so the timeout below has something to stop: killing the `docker run`
# client detaches from the container, it does not end it, and a container left
# running would hold the report directory and the port.
container="autobahn-wstest-$port"

# `timeout` is GNU coreutils and is present on Linux, macOS (as gtimeout under
# a different name) and Git Bash alike; if it is missing, or the cap is 0, run
# uncapped rather than refusing to run at all.
runner=""
if [ "$run_timeout" -gt 0 ] && command -v timeout >/dev/null 2>&1; then
    runner="timeout --signal=TERM --kill-after=30s ${run_timeout}s"
fi

# The kernel's own account of an OOM kill. On 2026-10-10 the container died of
# the OOM killer -- `docker inspect` below said so -- and that was all there
# was to learn, because OOMKilled names the process that was KILLED, not the
# one that HAD the memory. The kernel chooses its victim by a score (resident
# memory, adjusted by oom_score_adj), and the candidates were wstest and our
# own echo server. Its report of the kill lists every task's resident memory
# and oom_score_adj, so the failure path below reads it.
#
# The last report already in the log is noted now, so that one left over from
# an earlier run is not taken for this run's. And the line is printed now, on
# every run, because that is what tells a green night whether the kernel log
# is readable here at all -- rather than the next killed night finding out.
kernel_readable=0
oom_before=""
if klog="$(kernel_log)"; then
    kernel_readable=1
    oom_before="$(printf '%s\n' "$klog" | kernel_oom_last | head -n 1 || true)"
    echo "Kernel log readable: an OOM kill would be reported with the kernel's task table."
else
    echo "Kernel log not readable here: an OOM kill would be reported by docker inspect alone."
fi
unset klog

# One part of the suite (see split_off above), in a wstest of its own, with its
# own repeat. Its report goes to reports/<name>/. Sets attempt.
#   run_part NAME CASES EXCLUDE   (CASES and EXCLUDE: JSON list bodies)
run_part() {
local name="$1" cases="$2" exclude="$3"
local dir="$repdir/$name"
mkdir -p "$dir"
cat >"$dir/fuzzingclient.json" <<JSON
{
    "outdir": "/reports/$name",
    "servers": [{ "agent": "Hermod.HTTP2", "url": "ws://$ws_host:$port" }],
    "cases": [$cases],
    "exclude-cases": [$exclude],
    "exclude-agent-cases": {}
}
JSON
attempt=1
while :; do
client_oom=0
# Before EVERY attempt: a container that finished cleanly is still there (it
# is not --rm, see below), and the next part's run would refuse its name.
# The first real split run, on 2026-11-22, ended that way, with rc 125.
docker rm -f "$container" >/dev/null 2>&1 || true
echo "Running Autobahn fuzzingclient (Docker image $image, ws://$ws_host:$port), part $name [$cases]${exclude:+ minus [$exclude]}, attempt $attempt..."
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
    wstest -m fuzzingclient -s "/config/$name/fuzzingclient.json" || {
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
            # ss prints its header line even when nothing matches. On
            # 2026-10-10 that header was the whole output, and the fallback
            # below never fired, because a header alone is not empty.
            [ "$(printf '%s\n' "$sockets" | grep -c .)" -gt 1 ] || sockets=""
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

        # And who had the memory, which only the kernel knows (see above the
        # run). Looked for on every failure, not only when OOMKilled says
        # true: had the kernel picked the echo server instead, the container's
        # state would say false, and this report would be the only place the
        # kill showed. The full report goes next to the Autobahn report, where
        # the workflow's artifact upload picks it up.
        if [ "$kernel_readable" -eq 1 ]; then
            oom="$(kernel_log | kernel_oom_last || true)"
            if [ -n "$oom" ] && [ "$(printf '%s\n' "$oom" | head -n 1)" != "$oom_before" ]; then
                printf '%s\n' "$oom" >"$dir/kernel-oom.log"
                echo "--- the kernel's OOM report (in full: $name/kernel-oom.log beside the report) ---" >&2
                printf '%s\n' "$oom" | kernel_oom_summary >&2 || true

                # Lost to wstest's own runaway? See oom_retries above. Every
                # condition must hold; a pid missing from the task table
                # leaves srv_kib empty, and then there is no retry.
                victim="$(printf '%s\n' "$oom" | kernel_oom_victim || true)"
                srv_kib="$(printf '%s\n' "$oom" | kernel_oom_task_rss_kib "$srv_pid" || true)"
                if [ "$rc" -eq 137 ] && [[ "$state" == *"oom-killed=true"* ]] \
                   && [ "$victim" = wstest ] && kill -0 "$srv_pid" 2>/dev/null \
                   && [ -n "$srv_kib" ] && [ "$srv_kib" -le $(( oom_retry_server_max_mib * 1024 )) ]; then
                    client_oom=1
                elif [ "$victim" = wstest ]; then
                    echo "Not repeated: the kill took wstest, but our echo server held ${srv_kib:-an unknown number of} KiB (a repeat allows ${oom_retry_server_max_mib} MiB) or had exited." >&2
                fi
                # The next attempt must not take this report for its own.
                oom_before="$(printf '%s\n' "$oom" | head -n 1)"
            else
                echo "no OOM report in the kernel log since the run started." >&2
            fi
        fi

        docker rm -f "$container" >/dev/null 2>&1 || true
    }

if [ "$client_oom" -eq 1 ] && [ "$attempt" -le "$oom_retries" ]; then
    # The lost attempt's evidence goes aside, out of the next one's way:
    # wstest writes its per-case files flat into the report directory.
    keep="$dir/attempt-$attempt"
    mkdir -p "$keep"
    for f in "$dir"/*; do
        case "${f##*/}" in
            fuzzingclient.json|attempt-*) ;;
            *) mv "$f" "$keep/" ;;
        esac
    done
    lost="part $name, attempt $attempt, was lost to the fuzzing client: the kernel OOM-killed wstest while our echo server held $(( srv_kib / 1024 )) MiB"
    echo "RETRY: $lost. Running this part once more, whole; the lost attempt's evidence is in $keep." >&2
    if [ -n "${GITHUB_ACTIONS:-}" ]; then
        echo "::warning title=Autobahn run repeated::$lost (see $name/attempt-$attempt/ in the autobahn-report artifact)"
    fi
    attempt=$(( attempt + 1 ))
    continue
elif [ "$client_oom" -eq 1 ]; then
    echo "Not repeated: wstest's OOM kill lost part $name's attempt $attempt too, and $oom_retries repeat(s) is the limit." >&2
fi
break
done
}

# --- run the parts ---------------------------------------------------------
# A part that brings back no report does not stop the next one: the night's
# evidence should cover both, and the verdict below fails either way.
if [ -n "$split_off" ]; then
    split_name="$(printf '%s' "$split_off" | sed 's/[^A-Za-z0-9.]//g; s/\.*$//')"
    parts=("main|\"*\"|\"$split_off\"" "${split_name:-split}|\"$split_off\"|")
else
    parts=("main|\"*\"|")
fi
reports=()
missing=()
repeated=()
for p in "${parts[@]}"; do
    IFS='|' read -r pname pcases pexclude <<<"$p"
    run_part "$pname" "$pcases" "$pexclude"
    if [ -f "$repdir/$pname/index.json" ]; then
        reports+=("$repdir/$pname/index.json")
    else
        missing+=("$pname")
    fi
    [ "$attempt" -gt 1 ] && repeated+=("$pname (attempt $attempt)")
done

# --- parse the report ------------------------------------------------------
[ "${#missing[@]}" -eq 0 ] || {
    echo "No Autobahn report for part(s): ${missing[*]} (see above; did the container reach the server?)" >&2
    exit 1
}

# The parts' reports merged into one, which the counting below reads. The
# parts are disjoint by construction; a case found in two would be counted
# twice, so that ends the run rather than the count. Each case's report file
# is named relative to its part's directory, so the merged copy prefixes it.
INDEXFILE="$repdir/index.json"
# shellcheck disable=SC2086  # $python may be "py -3", see above
$python - "$INDEXFILE" "${reports[@]}" <<'PYEOF'
import json, os, sys
out = {}
for path in sys.argv[2:]:
    part = os.path.basename(os.path.dirname(path))
    for agent, cases in json.load(open(path, encoding="utf-8")).items():
        dst = out.setdefault(agent, {})
        for cid, r in cases.items():
            if cid in dst:
                sys.exit(f"case {cid} ran in two parts; the split is not disjoint")
            if "reportfile" in r:
                r["reportfile"] = f"{part}/{r['reportfile']}"
            dst[cid] = r
json.dump(out, open(sys.argv[1], "w", encoding="utf-8"), indent=1)
PYEOF

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
# only answer yes/no, which is useless once the answer is a number. A Windows
# Python ends its lines with CRLF, so the CR is stripped before `read` takes the
# last field.
# shellcheck disable=SC2086  # $python may be "py -3", see above
read -r passing declined hard total < <(
$python - "$INDEXFILE" <<'PYEOF' | tr -d '\r'
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
for p in "${parts[@]}"; do
    echo "Full HTML report: $repdir/${p%%|*}/index.html"
done
if [ "${#repeated[@]}" -gt 0 ]; then
    echo "NOTE: repeated after wstest's own OOM kill: ${repeated[*]}. This verdict counts"
    echo "      the last attempt of each part (see the RETRY lines above and"
    echo "      <part>/attempt-*/ beside the report)."
fi
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
