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
# Stop OUR stale listener on the given TCP ports, and nothing else. A demo host
# or Autobahn echo server left bound by an earlier run would fault the next
# run's bind, and the resulting error reads like a test failure rather than the
# leftover process it is.
#
# It used to kill whatever listened there, and these ports are not ours alone:
# the HTTP/1.1 sibling's demo uses 8443 and 8080 too. On 2026-10-29 another
# session had it running, on Windows and inside WSL. Killing it ends somebody
# else's run. Starting ours beside it is no better, because then a harness may
# be talking to THEIR server: the dead-target audit got "no application
# protocol" from it in 38 of 49 runs before the cause was found. So a listener
# whose command line does not name one of ours is reported and left alone.
#
# Ours: a process whose command line names HTTP2.Demo or autobahn-server
# (the DLL run through `dotnet`, or the apphost).
#
# Returns 0 when every port is free (or held only by ours, which is then
# stopped) and 1 when something else holds one. A caller about to start a
# server stops there; a caller cleaning up ignores it.
free_ports() {

    [ $# -gt 0 ] || return 0

    local port entry pid cmd ours="" foreign=0

    for port in "$@"; do
        while IFS= read -r entry; do
            [ -n "$entry" ] || continue
            pid="${entry%%|*}"
            cmd="${entry#*|}"
            case "$cmd" in
                *HTTP2.Demo*|*autobahn-server*)
                    ours="$ours $pid" ;;
                *)
                    echo "port $port is held by pid $pid (${cmd:-command line not visible}), which is not one of ours -- leaving it alone." >&2
                    foreign=1 ;;
            esac
        done <<EOF
$(if is_windows; then _listeners_windows "$port"; else _listeners_posix "$port"; fi)
EOF
    done

    if [ -n "$ours" ]; then
        for pid in $ours; do
            if is_windows; then
                # //PID and //F, not /PID and /F: MSYS rewrites an argument that
                # starts with a single slash into a Windows path before taskkill
                # ever sees it, and the doubled slash is the documented escape.
                taskkill //PID "$pid" //F >/dev/null 2>&1 || true
            else
                kill "$pid" 2>/dev/null || true
            fi
        done
        sleep 0.5
    fi

    [ "$foreign" -eq 0 ]

}

# Print one "pid|command line" line per process listening on TCP port $1.
#
# On Linux straight from /proc: the CI container (debian:13) installs neither
# fuser nor ss, so a version built on those found nothing there and freed
# nothing. /proc/net/tcp{,6} gives the listening sockets' inodes (state 0A,
# port in hex), and the process that holds one has a socket:[inode] link under
# its /proc/<pid>/fd. Another user's fds are unreadable without root, so such
# a listener comes back with pid "?" -- foreign either way. macOS has no /proc
# and goes through lsof instead.
_listeners_posix() {

    local port="$1" hex inode pid

    if [ -r /proc/net/tcp ]; then

        hex="$(printf '%04X' "$port")"
        for inode in $(awk -v p=":$hex" '$4 == "0A" && substr($2, length($2) - 4) == p { print $10 }' \
                           /proc/net/tcp /proc/net/tcp6 2>/dev/null | sort -u); do
            pid="$(find /proc/[0-9]*/fd -maxdepth 1 -lname "socket:\[$inode\]" 2>/dev/null \
                   | head -n 1 | cut -d/ -f3 || true)"
            if [ -n "$pid" ]; then
                printf '%s|%s\n' "$pid" "$(tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null || true)"
            else
                printf '?|\n'
            fi
        done

    elif command -v lsof >/dev/null 2>&1; then

        for pid in $(lsof -nP -iTCP:"$port" -sTCP:LISTEN -t 2>/dev/null | sort -u || true); do
            printf '%s|%s\n' "$pid" "$(ps -o command= -p "$pid" 2>/dev/null || true)"
        done

    fi

    return 0

}

# The same, on Windows. Neither /proc nor ss exists under Git Bash, which is
# why this branch has to exist at all.
#
# netstat is present but *localized*: a German Windows prints "ABHÖREN"
# where an English one prints "LISTENING", so keying on its state column
# would work on the author's machine and nowhere else.
# Get-NetTCPConnection returns typed fields instead, and Win32_Process the
# command line, which is why the query goes through powershell.exe. That is
# the Windows counterpart of /proc -- a system query, not a second runner
# implementation. Written for Windows PowerShell 5.1, which is what
# powershell.exe is.
#
# Not by its State, though. On Windows 11 10.0.26300 a .NET TcpListener is
# reported with State 0 rather than Listen, so `-State Listen` matched nothing,
# and the version of this function that filtered on it never freed anything
# there (found 2026-10-29). A listening socket is the one without a remote end.
_listeners_windows() {

    powershell.exe -NoProfile -NonInteractive -Command \
        "Get-NetTCPConnection -LocalPort $1 -ErrorAction SilentlyContinue |
         Where-Object { \$_.RemotePort -eq 0 } |
         Select-Object -ExpandProperty OwningProcess -Unique |
         ForEach-Object { \$p = Get-CimInstance Win32_Process -Filter \"ProcessId=\$_\"; \"\$_|\$(\$p.CommandLine)\" }" \
        2>/dev/null | tr -d '\r' | grep -E '^[0-9]+\|' || true

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
