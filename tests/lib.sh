#!/usr/bin/env bash
#
# Helpers shared by the runners under tests/ (run-tests.sh, h2spec.sh,
# autobahn.sh). Sourced, never executed.
#
# These runners are the only ones: the PowerShell counterparts they used to
# have were removed once the bash ones were shown to work under Git Bash on
# Windows. Two implementations of the same runner produced two numbers that
# looked like agreement and were not -- see the $Args episode in
# docs/BUILD_LOG.md -- so anything genuinely shared belongs here rather than
# copied three times.

# --- is_windows ------------------------------------------------------------
# True under Git Bash / MSYS2 / Cygwin, i.e. bash on Windows rather than bash
# on a Unix kernel. WSL deliberately reports Linux and is treated as Linux,
# which is correct: it has a real Linux kernel, its own network namespace and
# the usual tooling.
is_windows() {
    case "$(uname -s)" in
        MINGW*|MSYS*|CYGWIN*) return 0 ;;
        *)                    return 1 ;;
    esac
}

# --- free_ports <port>... --------------------------------------------------
# Kill whatever still listens on the given TCP ports. A stale demo left bound
# would fault the next run's bind, and the resulting error reads like a test
# failure rather than the leftover process it is.
#
# Never fails: "nothing was bound" is the common case and must not abort a
# caller running under `set -e`.
free_ports() {

    [ $# -gt 0 ] || return 0

    if is_windows; then
        _free_ports_windows "$@"
    else
        _free_ports_posix "$@"
    fi

    return 0

}

_free_ports_posix() {

    local p spec pids

    # Prefer fuser, fall back to ss: fuser and lsof are not always installed,
    # ss usually is.
    if command -v fuser >/dev/null 2>&1; then

        for p in "$@"; do
            fuser -k "${p}/tcp" >/dev/null 2>&1 || true
        done

    elif command -v ss >/dev/null 2>&1; then

        spec=""
        for p in "$@"; do
            if [ -n "$spec" ]; then spec="$spec or "; fi
            spec="${spec}sport = :${p}"
        done

        # The '|| true' matters: with `set -o pipefail`, grep finding no match
        # (the common "nothing stale is bound" case) would otherwise abort the
        # whole script under `set -e`.
        pids="$(ss -ltnpH "$spec" 2>/dev/null \
                | grep -oE 'pid=[0-9]+' | grep -oE '[0-9]+' | sort -u || true)"

        if [ -n "$pids" ]; then
            # shellcheck disable=SC2086
            kill $pids 2>/dev/null || true
            sleep 0.5
        fi

    fi

    return 0

}

_free_ports_windows() {

    local list pids pid

    # Neither fuser nor ss exists under Git Bash, which is why this branch has
    # to exist at all -- without it the POSIX one above falls straight through
    # and silently frees nothing.
    #
    # netstat is present but *localized*: a German Windows prints "ABHÖREN"
    # where an English one prints "LISTENING", so keying on its state column
    # would work on the author's machine and nowhere else.
    # Get-NetTCPConnection returns a typed State instead, which is why the
    # query goes through powershell.exe. That is the Windows counterpart of
    # `ss` -- a system query, not a second runner implementation.
    list="$(printf '%s,' "$@" | sed 's/,$//')"

    pids="$(powershell.exe -NoProfile -NonInteractive -Command \
              "Get-NetTCPConnection -LocalPort $list -State Listen -ErrorAction SilentlyContinue |
               Select-Object -ExpandProperty OwningProcess" 2>/dev/null \
            | tr -d '\r' | grep -E '^[0-9]+$' | sort -u || true)"

    if [ -n "$pids" ]; then
        for pid in $pids; do
            # //PID and //F, not /PID and /F: MSYS rewrites an argument that
            # starts with a single slash into a Windows path before taskkill
            # ever sees it, and the doubled slash is the documented escape.
            taskkill //PID "$pid" //F >/dev/null 2>&1 || true
        done
        sleep 0.5
    fi

    return 0

}

# --- kernel_log ------------------------------------------------------------
# Print the kernel's ring buffer, or fail if it cannot be read here.
#
# For the kernel's own report of an OOM kill (see kernel_oom_last), which names
# the process that HAD the memory: `docker inspect` names only the one that was
# killed for it, and the kernel picks that one by a score, not by who grew.
#
# Linux only. Under Git Bash the containers run in Docker Desktop's VM, and on
# macOS likewise, so the kernel that would write the report is not one this
# host can read.
#
# Reading it needs root wherever kernel.dmesg_restrict is 1, which includes
# Ubuntu and so ubuntu-latest, whose runner user has passwordless sudo. Hence
# the fallback, and hence -n: a sudo that wants a password and has no tty to
# ask on hangs instead of failing.
kernel_log() {

    [ "$(uname -s)" = Linux ]          || return 1
    command -v dmesg >/dev/null 2>&1   || return 1

    dmesg 2>/dev/null || sudo -n dmesg 2>/dev/null

}

# --- kernel_oom_last -------------------------------------------------------
# From a kernel log on stdin, print the last OOM report in it -- from its
# "invoked oom-killer" line to the end of the log -- or nothing if there is
# none. Its first line carries the kernel's timestamp, so a caller can tell a
# new report from one it saw before.
kernel_oom_last() {

    awk '/invoked oom-killer/ { out = ""; found = 1 }
         found                { out = out $0 "\n" }
         END                  { printf "%s", out }'

}

# --- kernel_oom_summary ----------------------------------------------------
# From one OOM report on stdin (kernel_oom_last), print the lines that answer
# "who had the memory": who triggered the kill, the constraint
# (CONSTRAINT_NONE: the whole machine ran out; CONSTRAINT_MEMCG: a cgroup
# limit), the victim, and the five largest tasks of the kernel's task table by
# resident memory.
#
# The table's columns are found by name in its header row rather than by
# position, because kernels differ in them: newer ones split rss into
# rss_anon / rss_file / rss_shmem after the total. Its values are pages.
kernel_oom_summary() {

    local report page_kib

    report="$(_kernel_strip_timestamps)"
    page_kib=$(( $(getconf PAGESIZE 2>/dev/null || echo 4096) / 1024 ))

    printf '%s\n' "$report" \
        | grep -E 'invoked oom-killer|^oom-kill:|Killed process' \
        | sed 's/^/  /' || true

    echo "  largest tasks by resident memory:"
    printf '%s\n' "$report" \
        | awk '/^\[ *pid *\]/ && /total_vm/ {
                   hdr = $0; sub(/^\[ *pid *\] */, "", hdr)
                   n = split(hdr, h, / +/)
                   for (i = 1; i <= n; i++) {
                       if (h[i] == "rss")           ri = i
                       if (h[i] == "oom_score_adj") ai = i
                   }
                   next
               }
               /^oom-kill:/ { ri = 0 }
               ri && ai && /^\[ *[0-9]+\]/ {
                   pid = $0; sub(/^\[ */, "", pid); sub(/\].*/, "", pid)
                   row = $0; sub(/^\[ *[0-9]+\] */, "", row)
                   m = split(row, f, / +/)
                   name = f[ai + 1]
                   for (i = ai + 2; i <= m; i++) name = name " " f[i]
                   print f[ri], pid, f[ai], name
               }' \
        | sort -rn | head -n 5 \
        | awk -v kib="$page_kib" '{
              rss = $1; pid = $2; adj = $3
              $1 = $2 = $3 = ""; sub(/^ +/, "")
              printf "  %8.0f MiB  pid %-7s oom_score_adj %5s  %s\n", rss * kib / 1024, pid, adj, $0
          }' || true

}

# dmesg prefixes every line with "[seconds.micros] ", and with a caller id
# such as "[ T1234]" behind it on kernels built with CONFIG_PRINTK_CALLER.
_kernel_strip_timestamps() {

    sed -E 's/^\[ *[0-9]+\.[0-9]+\](\[ *[A-Z][0-9]+\])? ?//'

}
