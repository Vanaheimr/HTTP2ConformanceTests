# Autobahn, nightly of 2026-11-22 — wstest OOM-killed in 12.3, with 12.4 split off

The first nightly with 12.4 in a wstest of its own (commit `0a406dc`, pin
`4b8be28a`, .NET 11.0.0). Kept here because GitHub removes failed runs.

| | part `main`, attempt 1 | part `main`, attempt 2 | part `12.4` |
|---|---|---|---|
| last case started | 12.3.11 | 12.3.12 | all 18 |
| killed after the case started | 1.03 s | 1.80 s | — |
| wstest resident at the kill | 13 360 MiB | 13 525 MiB | — |
| our echo server (`dotnet`) | 58 MiB | 75 MiB | — |
| result | OOM kill, no report | OOM kill, no report | 18 × OK in 22.4 s |

Up to the kill both attempts kept the green run's pace (2026-11-21, 01:17):
12.1 in 16.9 s (green 19.5), 12.2 in 66.8 / 67.3 s (green 63.9), and
12.3.7 – 12.3.10, the cases that take 1 – 12 s, within 5 % of green case by
case. 12.3.11 took 1.03 / 0.89 s where the green run needed 0.58 s.

**The runner was different from every earlier kill.** The kernel reports name
`Amazon EC2 m8id.xlarge`, kernel `7.0.0-1016-aws`, **no swap** (`Total swap =
0kB`), 4 119 563 pages RAM (15.7 GiB). The kills of 2026-11-20 and 2026-11-21
ran on `Microsoft Corporation Virtual Machine` (Hyper-V), kernel
`7.0.0-1020-azure`, with 3 GiB of swap. Runner image `ubuntu-26.04`
`20261116.88.1`.

What this says:

- **The split did not help, and 12.4 is not the trigger.** Without 12.4 the
  same runaway came in 12.3; 12.4 alone in a fresh wstest was clean.
- **Both attempts died within one case of each other**, as on 2026-11-21. On
  the nights it happens, it happens to every attempt on that runner.
- **Without swap it came earlier.** The Azure kills had about 3 GiB of swap
  to spend first; whether that alone moves the kill from 12.4 forward into
  12.3 is not shown.

Files:

- `autobahn-step.log` — the Autobahn step, timestamped, from the kernel-log
  check to the verdict (build warnings dropped).
- `main/attempt-1/kernel-oom.log`, `main/kernel-oom.log` — the kernel's full
  reports of both kills.
- `main/fuzzingclient.json`, `12.4/fuzzingclient.json` — the two parts'
  configurations.
- `echo-server.log` — our server's log.
