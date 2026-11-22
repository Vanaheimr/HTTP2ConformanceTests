# Evidence: the Autobahn OOM kills of 2026-11-21, both attempts

These files come from the nightly of 2026-11-21, at `d2d9aea` (pin
`4b8be28a`, .NET 11.0.0), on the hosted runner image `ubuntu-26.04`,
version `20261116.202.1`. It was the first night with the repeat that
`a67eae8` added to `tests/autobahn.sh`, and both attempts were lost the same
way. GitHub removes failed runs automatically. These are copies taken on
2026-11-22, before the run went. The entry *2026-11-22 — The repeat did not
save the night of 2026-11-21* in [`docs/BUILD_LOG.md`](../../BUILD_LOG.md)
rests on them.

| file | what it is |
|---|---|
| `attempt-1/kernel-oom.log` | The kernel's OOM report for attempt 1, as `tests/autobahn.sh` moved it aside before the repeat. Unchanged. |
| `kernel-oom.log` | The same for attempt 2, three minutes later on the same runner. Unchanged. |
| `autobahn-step.log` | The Autobahn step from the job log, from the kernel-log probe to the step's exit, with GitHub's timestamp on every line. Both attempts, the `RETRY:` line between them and the `Not repeated:` line after the second. Only the step is kept. |
| `echo-server.log` | Our echo server's whole output: its startup banner and nothing else. One server served both attempts. |
| `fuzzingclient.json` | The fuzzing client's configuration, the same for both attempts. |

What they show, in short:

| | attempt 1 | attempt 2 |
|---|---|---|
| died in | 12.4.3, 1.93 s into it | 12.4.4, 2.21 s into it |
| the same case on the green run of `a67eae8` that day | 0.44 s | 0.48 s |
| `wstest`, `rss` / `swapents` | 3 542 956 / 781 998 pages (13.52 / 2.98 GiB) | 3 881 319 / 471 523 pages (14.81 / 1.80 GiB) |
| `dotnet`, our echo server (pid 3017), `rss` / `swapents` | 16 058 / 1 454 pages (63 / 6 MiB) | 14 108 / 1 952 pages (55 / 8 MiB) |
| `oom_score_adj`, wstest / ours | 0 / 500 | 0 / 500 |
| free swap, of 3 GiB | 0 kB | 144 kB |

Up to 12.4 both attempts ran at the pace of the green run, or faster:
sections 12.1, 12.2 and 12.3 took 16.5 / 62.2 / 79.2 s and 16.3 / 62.0 /
80.4 s, against 19.5 / 63.9 / 79.5 s on the green run. The green run of
`a67eae8` at 01:17 that day ran on the same image version.
