# Autobahn, nightly of 2026-11-23 — wstest OOM-killed in 12.3.12, twice, still unpinned

The second nightly with 12.4 in a wstest of its own, and the last one
before the PyPy nursery was pinned to 4 MB (commit `0a406dc`, pin
`4b8be28a`, .NET 11.0.0; `c323c98`, which pins it, was pushed on
2026-11-24). Kept here because GitHub removes failed runs.

| | part `main`, attempt 1 | part `main`, attempt 2 | part `12.4` |
|---|---|---|---|
| last case started | 12.3.12 | 12.3.12 | all 18 |
| failure noticed after the case started | 1.82 s | 2.63 s | — |
| wstest resident at the kill | 13 584 MiB | 13 665 MiB | — |
| our echo server (`dotnet`, same process both times) | 106 MiB | 65 MiB | — |
| result | OOM kill, no report | OOM kill, no report | all 18 run, 22.5 s |

Up to the kill the two attempts kept the same pace: 12.3.7 – 12.3.10 took
1.1, 2.4, 5.5 and 11.5 s in attempt 1 and 1.1, 2.4, 5.5 and 11.4 s in
attempt 2.

**The same kind of runner as on 2026-11-22.** The kernel reports name
`Amazon EC2 m8id.xlarge`, kernel `7.0.0-1016-aws`, **no swap** (`Total swap =
0kB`), 4 119 563 pages RAM (15.7 GiB). Runner image `ubuntu-26.04`
`20261116.88`. The `i` in `m8id` is AWS's mark for an Intel processor, so the
`cache size` its `/proc/cpuinfo` reports is an L3 — under PyPy's rule (see
`../2026-11-22-pypy-nursery/`), a nursery well above the 4 MiB of an AMD
host. How large that L3 is, and so the nursery, is not in the reports; this
script did not yet print the host line.

What this says:

- **Two nights in a row on EC2 m8id, two nights of two kills each, all four
  in 12.3.11 or 12.3.12.** The runaway now comes at the same place each time
  on that runner, rather than wherever 12.4 happens to be.
- **12.4 alone was clean again**, as on 2026-11-22.
- **Our server is not the consumer:** 65 – 106 MiB against 13.6 GiB.

The first night with the 4 MB pin will print the host's CPU, the nursery
PyPy would have chosen there, and wstest's peak.

Files:

- `autobahn-step.log` — the Autobahn step's own output, from the echo
  server's start to the verdict.
- `main/attempt-1/kernel-oom.log`, `main/kernel-oom.log` — the kernel's full
  reports of both kills.
- `main/fuzzingclient.json`, `12.4/fuzzingclient.json` — the two parts'
  configurations.
- `echo-server.log` — our server's log.
