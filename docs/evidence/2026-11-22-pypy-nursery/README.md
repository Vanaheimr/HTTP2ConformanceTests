# wstest's memory against PyPy's nursery size — measured locally, 2026-11-22

Measured in WSL (Debian, AMD Ryzen 7 3700X, 16 threads, 62.8 GiB),
`crossbario/autobahn-testsuite` (PyPy 7.3.20), echo server at pin `4b8be28a`
on .NET 11.0.0. Each arm is one run of `tools/autobahn-repro.sh` in the
nightly's order up to the end of 12.3 (`--cases '*' --exclude
'12.4.*,12.5.*,13.*'`, 355 cases). The scratch copy passed the PyPy setting
to `docker run`. The sampler read wstest's `VmRSS` every 50 ms. The watchdog
froze wstest at 8 GiB, so that it would not take the shared VM down.

| arm | wstest after 12.1 | after 12.2 | at the end | outcome |
|---|---|---|---|---|
| `default` (nursery 4 MiB here) | 2 636 MiB | 2 835 MiB | 2 862 MiB | 355 passed |
| `maxdelta2g` (`PYPY_GC_MAX_DELTA=2GB`, as on 16 GiB) | 2 932 MiB | 3 067 MiB | 3 108 MiB | 355 passed |
| `nursery24m` (`PYPY_GC_NURSERY=24MB`) | 4 670 MiB | 5 055 MiB | 5 375 MiB | 355 passed |
| `nursery64m` (`PYPY_GC_NURSERY=64MB`) | 5 437 MiB | 7 429 MiB | 8 192 MiB in 12.3.18 | watchdog |
| `nursery240m` (`PYPY_GC_NURSERY=240MB`) | 5 571 MiB | — | 8 198 MiB in 12.2.12 | watchdog |

`<arm>.txt` lists wstest's resident memory at the start and peak of every
case that lasted long enough to be sampled.

**How PyPy picks the nursery.** It logs that with
`PYPYLOG=gc-hardware,gc-set-nursery-size`. Here it read `L2cache = 524288`,
`memtotal = 67389702144`, and chose `nursery size: 4194304`. With a
`/proc/cpuinfo` whose `cache size` line was replaced, bind-mounted into the
container, it chose:

| `cache size` in /proc/cpuinfo | nursery |
|---|---|
| 512 KB, 1 024 KB, 2 048 KB | 4 MiB |
| 32 768 KB | 16 MiB |
| 49 152 KB | 24 MiB |
| 107 520 KB | 52.5 MiB |
| 491 520 KB | 240 MiB |

So it is half the reported cache size, at least 4 MiB, and there is no upper
bound. An AMD CPU reports its L2 in that line, an Intel CPU its L3. With
`PYPY_GC_NURSERY` set, PyPy does not read the cache size at all; it logs only
`memtotal` and the nursery.
