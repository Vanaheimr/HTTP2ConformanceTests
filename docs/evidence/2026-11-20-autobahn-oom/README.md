# Evidence: the Autobahn OOM kill of 2026-11-20

The nightly run these files come from, **44727173478** (2026-11-20, at
`3af379d`, pin `4b8be28a`, .NET 11.0.0), no longer exists on GitHub. Since
2026-11-21 it answers 404, as the run of 2026-10-10 did before it. These are
copies taken on 2026-11-21, before it went. The entry *The Autobahn kill of
2026-11-20 names its consumer* in [`docs/BUILD_LOG.md`](../../BUILD_LOG.md)
rests on them.

| file | what it is |
|---|---|
| `kernel-oom.log` | The kernel's OOM report, as `tests/autobahn.sh` captured it from `dmesg`: the trigger, the memory state, the full task table with `rss`, `swapents` and `oom_score_adj` per process, and the victim. Unchanged. |
| `autobahn-step.log` | The Autobahn step from the job log, from the kernel-log probe to the step's exit, with GitHub's timestamp on every line. The timestamps give each case's duration; 12.4.4 began at 11:27:42.82, and the kill was reported at 11:27:50.67. Only the step is kept, not the checkout, build and cleanup around it. |
| `echo-server.log` | Our echo server's whole output: its startup banner and nothing else. |
| `fuzzingclient.json` | The fuzzing client's configuration for that run. |

What they show, in short:

- `wstest`, pid 3130, in the container: `rss` 3 578 436 pages (13.65 GiB),
  `swapents` 769 660 (2.94 GiB), `oom_score_adj` 0.
- `dotnet`, pid 2978, our echo server: `rss` 16 070 pages (63 MiB),
  `swapents` 1 455 (6 MiB), `oom_score_adj` 500.
- `Free swap = 220kB` of 3 GiB.

`kernel_oom_victim` and `kernel_oom_task_rss_kib` in `tests/lib.sh` give
`wstest` and 64 280 KiB for pid 2978 when run on `kernel-oom.log`, under Linux.
