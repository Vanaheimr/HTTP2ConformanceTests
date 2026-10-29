#!/usr/bin/env bash
#
# Loop chosen Autobahn cases against ONE long-lived echo server and sample the
# memory of both ends, to find out who takes the memory when a case dies of it.
#
# Why this exists: on 2026-10-10 the nightly's fuzzingclient was OOM-killed
# 8.5 s into case 12.4.6, a case green nights finish in under a second (see
# docs/BUILD_LOG.md, "The Autobahn nightly was a memory kill after all").
# OOMKilled names the process that was killed, not the one that had the
# memory, and there are two candidates: wstest and our echo server.
# tests/autobahn.sh runs every case once and gives a verdict. This runs a few
# cases many times and reports memory.
#
# Linux only, because it reads /proc. Run it as root (in WSL: wsl -u root):
# otherwise the watchdog cannot freeze the container's process, and it falls
# back to docker kill.
#
# Per iteration:
#   - One fuzzingclient container (--network host, as in tests/autobahn.sh)
#     against the SAME echo server. A leak on our side therefore accumulates
#     across iterations the way it would across a full run.
#   - A sampler records the resident memory of both processes and the
#     system's MemAvailable every 50 ms, in memory.csv.
#   - A watchdog: if either process exceeds --limit-mib, it is frozen
#     (SIGSTOP), and its /proc status and smaps_rollup are saved before it is
#     killed. That names the consumer without letting it take the machine
#     down -- which an OOM kill would need, and which matters on a WSL VM
#     other sessions share. (Docker's own --memory is no substitute there: the
#     WSL kernel has no swap accounting, so a limited container swaps instead
#     of dying.)
#   - One line: each case's verdict and duration, the container's exit, and
#     the two peaks.
#
# Usage:
#   tools/autobahn-repro.sh                                  # 12.4.6, 200 times
#   tools/autobahn-repro.sh --cases 12.4.5,12.4.6,12.4.7 --iterations 50
#   tools/autobahn-repro.sh --cases '*' --exclude "$(seq -s, -f '12.4.%g' 7 18),12.5.*,13.*"
#                                                            # the nightly's order, up to 12.4.6
#   tools/autobahn-repro.sh --server-dll /some/copy/autobahn-server.dll
#                                                            # no build; e.g. a Windows-built
#                                                            # tree driven from WSL
#   tools/autobahn-repro.sh --limit-mib 4096 --port 9410
#   tools/autobahn-repro.sh --out /root/autobahn-repro       # output on ext4 (see below)
#   tools/autobahn-repro.sh --keep-all                       # keep every iteration's reports
#                                                            # and log, not only the unclean ones
#
# Case patterns are wstest's, not the shell's: a trailing '*' works, '?' does
# not. On 2026-10-29, '12.4.1?' excluded nothing and 12.4.10-18 ran anyway.
#
# Output goes to tests/autobahn/repro/<timestamp>/ (git-ignored), or under
# --out DIR: memory.csv, echo-server.log, and for every iteration that was not
# clean, its reports and the container's log. In WSL, point --out at the Linux
# file system when the tree is on a Windows drive. Over /mnt/d, reading
# memory.csv while the sampler appends to it failed with "No data available"
# after six iterations; read_retrying below is the second line of defence, not
# the first.
#
set -euo pipefail

cases="12.4.6"
exclude=""
iterations=200
port=9410
limit_mib=6144
iter_timeout=600
image="crossbario/autobahn-testsuite"
server_dll=""
nobuild=0
keep_all=0
out_base=""

while [ $# -gt 0 ]; do
    case "$1" in
        --cases)        cases="$2";        shift 2 ;;
        --exclude)      exclude="$2";      shift 2 ;;
        --iterations)   iterations="$2";   shift 2 ;;
        --port)         port="$2";         shift 2 ;;
        --limit-mib)    limit_mib="$2";    shift 2 ;;
        --iter-timeout) iter_timeout="$2"; shift 2 ;;
        --image)        image="$2";        shift 2 ;;
        --server-dll)   server_dll="$2";   shift 2 ;;
        --no-build)     nobuild=1;         shift ;;
        --keep-all)     keep_all=1;        shift ;;
        --out)          out_base="$2";     shift 2 ;;
        -h|--help)      grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

[ "$(uname -s)" = Linux ] || { echo "Linux only: the sampler reads /proc." >&2; exit 2; }
for tool in docker python3 dotnet; do
    command -v "$tool" >/dev/null 2>&1 || { echo "$tool not found." >&2; exit 127; }
done

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(dirname "$here")"
# shellcheck source=tests/lib.sh
. "$root/tests/lib.sh"
out_base="${out_base:-$root/tests/autobahn/repro}"

# Run a reader of memory.csv while the sampler appends to it, retrying a
# transient read error (see --out above). Prints nothing and fails if every
# try did.
read_retrying() {
    for _ in 1 2 3 4 5; do
        "$@" 2>/dev/null && return 0
        sleep 0.2
    done
    return 1
}

# --- the echo server ---------------------------------------------------------
if [ -z "$server_dll" ]; then
    if [ "$nobuild" -eq 0 ]; then
        echo "Building echo server..."
        dotnet build "$root/tests/autobahn-server/autobahn-server.csproj" -v quiet >/dev/null
    fi
    for cfg in Debug Release; do
        cand="$root/tests/autobahn-server/bin/$cfg/net10.0/autobahn-server.dll"
        [ -f "$cand" ] && { server_dll="$cand"; break; }
    done
fi
[ -f "$server_dll" ] || { echo "Echo server not found (${server_dll:-no autobahn-server.dll})." >&2; exit 1; }

out="$out_base/$(date +%Y%m%d-%H%M%S)"
cfgdir="$out/config"
repdir="$out/reports"
mkdir -p "$cfgdir" "$repdir"
csv="$out/memory.csv"
current="$out/current"     # "<iteration> <wstest pid>", for the sampler
trip="$out/tripped"        # written by the watchdog
echo "0 0" >"$current"

free_ports "$port" || {
    echo "Not starting the echo server beside someone else's listener on :$port." >&2
    exit 1
}

dotnet "$server_dll" "$port" >"$out/echo-server.log" 2>&1 &
srv_pid=$!

# Not kill -0: a dead child this shell has not reaped yet is a zombie, and a
# zombie still answers kill -0.
alive() {
    local s
    s="$(awk '/^State:/ { print $2 }' "/proc/$1/status" 2>/dev/null)"
    [ -n "$s" ] && [ "$s" != Z ]
}
container="autobahn-repro-$port"
sampler_pid=""

cleanup() {
    [ -n "$sampler_pid" ] && kill "$sampler_pid" 2>/dev/null || true
    docker rm -f "$container" >/dev/null 2>&1 || true
    kill -CONT "$srv_pid" 2>/dev/null || true
    kill "$srv_pid" 2>/dev/null || true
    wait "$srv_pid" 2>/dev/null || true
    return 0
}
trap cleanup EXIT

ready=0
for _ in $(seq 1 40); do
    alive "$srv_pid" || { echo "Echo server exited during startup:" >&2; cat "$out/echo-server.log" >&2; exit 1; }
    if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then ready=1; break; fi
    sleep 0.3
done
[ "$ready" -eq 1 ] || { echo "Echo server did not start listening on :$port" >&2; exit 1; }

# --- the fuzzingclient's config -------------------------------------------------
json_list() {   # "a,b" -> "a", "b"
    [ -n "$1" ] || return 0
    printf '%s' "$1" | tr ',' '\n' | sed 's/.*/"&"/' | paste -sd, - | sed 's/,/, /g'
}
cat >"$cfgdir/fuzzingclient.json" <<JSON
{
    "outdir": "/reports",
    "servers": [{ "agent": "Hermod.HTTP2", "url": "ws://127.0.0.1:$port" }],
    "cases": [$(json_list "$cases")],
    "exclude-cases": [$(json_list "$exclude")],
    "exclude-agent-cases": {}
}
JSON

# --- the sampler and watchdog ---------------------------------------------------
# Resident memory, not virtual: .NET and PyPy both reserve far more address
# space than they ever touch, and only what is resident can exhaust a machine.
cat >"$out/sampler.py" <<'PY'
import os, signal, sys, time

echo_pid, current, csv, trip = int(sys.argv[1]), sys.argv[2], sys.argv[3], sys.argv[4]
limit_kib = int(sys.argv[5]) * 1024
outdir = os.path.dirname(csv)

def field(path, name):
    try:
        with open(path) as f:
            for line in f:
                if line.startswith(name):
                    return int(line.split()[1])
    except OSError:
        pass
    return 0

def save(who, pid):
    for part in ("status", "smaps_rollup"):
        try:
            with open(f"/proc/{pid}/{part}") as src, open(f"{outdir}/{who}.{part}", "w") as dst:
                dst.write(src.read())
        except OSError as e:
            with open(f"{outdir}/{who}.{part}", "w") as dst:
                dst.write(f"unreadable: {e}\n")

with open(csv, "a", buffering=1) as out:
    out.write("epoch,iteration,echo_rss_kib,wstest_rss_kib,memavailable_kib\n")
    while os.path.exists(f"/proc/{echo_pid}"):
        try:
            iteration, wpid = (int(x) for x in open(current).read().split())
        except (OSError, ValueError):
            iteration, wpid = 0, 0
        echo = field(f"/proc/{echo_pid}/status", "VmRSS:")
        wst  = field(f"/proc/{wpid}/status", "VmRSS:") if wpid else 0
        out.write(f"{time.time():.2f},{iteration},{echo},{wst},{field('/proc/meminfo', 'MemAvailable:')}\n")
        for who, pid, rss in (("echo-server", echo_pid, echo), ("wstest", wpid, wst)):
            if pid and rss > limit_kib:
                try:
                    os.kill(pid, signal.SIGSTOP)
                    frozen = "frozen"
                except OSError as e:
                    frozen = f"not frozen ({e.strerror})"
                save(who, pid)
                with open(trip, "w") as t:
                    t.write(f"{who} pid {pid} at {rss // 1024} MiB in iteration {iteration}, {frozen}\n")
                sys.exit(3)
        time.sleep(0.05)
PY
python3 "$out/sampler.py" "$srv_pid" "$current" "$csv" "$trip" "$limit_mib" &
sampler_pid=$!

echo "Echo server pid $srv_pid on ws://127.0.0.1:$port/, cases [$cases]${exclude:+ minus [$exclude]}, $iterations iterations"
echo "Watchdog at $limit_mib MiB per process; output in $out"

# --- the loop ---------------------------------------------------------------------
summary="$out/iterations.txt"
anomalies=0
for i in $(seq 1 "$iterations"); do
    rm -rf "$repdir"; mkdir -p "$repdir"
    docker rm -f "$container" >/dev/null 2>&1 || true
    docker run -d --name "$container" -e PYTHONUNBUFFERED=1 --network host \
        -v "$cfgdir:/config:ro" -v "$repdir:/reports" \
        "$image" wstest -m fuzzingclient -s /config/fuzzingclient.json >/dev/null
    wpid="$(docker inspect -f '{{.State.Pid}}' "$container" 2>/dev/null || echo 0)"
    echo "$i $wpid" >"$current"

    started=$SECONDS
    timed_out=0
    while [ "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null)" = true ]; do
        [ -e "$trip" ] && break
        [ $(( SECONDS - started )) -ge "$iter_timeout" ] && { timed_out=1; break; }
        sleep 0.2
    done
    echo "$i 0" >"$current"

    state="$(docker inspect -f '{{.State.ExitCode}} {{.State.OOMKilled}}' "$container" 2>/dev/null || echo '? ?')"
    exit_code="${state% *}"; oom="${state#* }"
    # Every case that did not pass, and the last three that ran: in a run that
    # ends at 12.4.6, those are the ones whose durations matter.
    verdicts="$(python3 - "$repdir/index.json" <<'PY' 2>/dev/null || echo "NO-REPORT"
import json, sys
PASS = {"OK", "NON-STRICT", "INFORMATIONAL"}
d = json.load(open(sys.argv[1], encoding="utf-8"))
runs = sorted(((cid, r) for cs in d.values() for cid, r in cs.items()),
              key=lambda kv: [int(x) for x in kv[0].split(".")])
bad = [f"{cid} {r.get('behavior')}/{r.get('behaviorClose')}" for cid, r in runs
       if not (r.get("behavior") in PASS and r.get("behaviorClose") in PASS)]
last = "; ".join(f"{cid} {r.get('behavior')} {r.get('duration')} ms" for cid, r in runs[-3:])
more = f" (of {len(runs)} cases)" if len(runs) > 3 else ""
print(("ALL-PASS" if not bad else "NOT-PASSING " + ", ".join(bad[:5])) + " | " + last + more)
PY
)"
    peaks="$(read_retrying awk -F, -v it="$i" '$2 == it { if ($3 > e) e = $3; if ($4 > w) w = $4; if (a == 0 || $5 < a) a = $5 }
                                END { printf "wstest %d MiB, echo-server %d MiB, MemAvailable min %.1f GiB", w/1024, e/1024, a/1048576 }' "$csv" || echo 'memory.csv unreadable')"
    line="$(printf '[%4d/%d] %s | exit %s oom-killed %s | %s' "$i" "$iterations" "$verdicts" "$exit_code" "$oom" "$peaks")"

    clean=1
    case "$verdicts" in ALL-PASS*) ;; *) clean=0 ;; esac
    [ "$exit_code" = 0 ] || clean=0
    [ "$timed_out" -eq 0 ] || { clean=0; line="$line | TIMED OUT after ${iter_timeout}s"; }
    [ -e "$trip" ] && { clean=0; line="$line | WATCHDOG: $(cat "$trip")"; }
    alive "$srv_pid" || { clean=0; line="$line | ECHO SERVER GONE"; }

    echo "$line" | tee -a "$summary"
    [ "$clean" -eq 1 ] || anomalies=$((anomalies + 1))
    if [ "$clean" -eq 0 ] || [ "$keep_all" -eq 1 ]; then
        keep="$out/iteration-$i"
        mkdir -p "$keep"
        cp -r "$repdir"/. "$keep"/ 2>/dev/null || true
        # Timestamped, so that each "Running test case ID" line can be matched
        # against memory.csv: the memory each case started with.
        docker logs --timestamps "$container" >"$keep/container.log" 2>&1 || true
        echo "           kept in $keep (last case started: $(grep 'Running test case ID' "$keep/container.log" | tail -n 1 | sed 's/.*Running test case ID //' || true))"
    fi
    docker rm -f "$container" >/dev/null 2>&1 || true

    # A frozen or dead process ends the experiment: the next iteration would
    # measure a different situation, and the evidence is already saved.
    if [ -e "$trip" ] || ! alive "$srv_pid"; then
        break
    fi
done

# --- the summary --------------------------------------------------------------------
echo
echo "$i iteration(s), $anomalies not clean. Per-iteration lines: $summary"
# "<iteration> <wstest peak MiB> <echo-server peak MiB>"; sorted with sort,
# because mawk (Debian's default awk) has no asort.
per_iter="$(read_retrying awk -F, 'NR > 1 && $2 > 0 { if ($4 > w[$2]) w[$2] = $4; if ($3 > e[$2]) e[$2] = $3; if ($2 > n) n = $2 }
                     END { for (k = 1; k <= n; k++) if (k in w) printf "%d %d %d\n", k, w[k]/1024, e[k]/1024 }' "$csv" || true)"
if [ -n "$per_iter" ]; then
    m="$(printf '%s\n' "$per_iter" | wc -l)"
    ws="$(printf '%s\n' "$per_iter" | cut -d' ' -f2 | sort -n)"
    printf 'wstest peak per iteration: min %s, median %s, max %s MiB\n' \
        "$(printf '%s\n' "$ws" | head -n 1)" "$(printf '%s\n' "$ws" | sed -n "$(( (m + 1) / 2 ))p")" "$(printf '%s\n' "$ws" | tail -n 1)"
    printf 'echo-server peak: %s MiB in the first iteration, %s MiB in the last\n' \
        "$(printf '%s\n' "$per_iter" | head -n 1 | cut -d' ' -f3)" "$(printf '%s\n' "$per_iter" | tail -n 1 | cut -d' ' -f3)"
fi
[ -e "$trip" ] && { echo "WATCHDOG: $(cat "$trip")"; echo "Its /proc status and smaps_rollup are in $out."; }
[ "$anomalies" -eq 0 ]
