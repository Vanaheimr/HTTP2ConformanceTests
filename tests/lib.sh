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
