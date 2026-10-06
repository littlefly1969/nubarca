# Media navigation PostgreSQL acceptance

The reproducible benchmark is
[`MediaNavigationPerformanceTests`](../tests/NubArca.Api.Tests/Files/MediaNavigationPerformanceTests.cs).
It measures the actual `FileItemService` methods and intercepts their generated,
parameterized SQL. Each statement is then executed through
`EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`, with the original parameter values.
It accepts no external connection string: the database is an ephemeral,
fully migrated PostgreSQL 17 Testcontainers instance.

## Dataset and method

- 50,000 or 100,000 media belonging to the measured owner, plus another owner's
  10,000 or 20,000 media. Every media has a distinct blob and blob-metadata row.
- 20% videos; ten years of dates; varied alphabetical names and accented titles.
  80% have user metadata, 20% have a custom title, with ratings, favourites, tags
  and descriptions. The other names exercise the filename fallback.
- Deleted, vaulted and individually excluded files, plus a folder excluding
  photos. Production folder visibility is enabled. The album contains half of
  the owner's files.
- Three scopes: whole library; favourites with minimum rating 3, metadata search
  and a date range; album. Both acquisition-date and display-name ordering.
- Each scope measures the complete index and a direct middle-bucket window of
  at most 50 items, including DTO projection and previous/next cursor detection.
  Windows use the same ordinary pagination engine as subsequent reads.
- One warmup followed by five measured service reads. Reported medians and
  maxima include EF/Npgsql round trips; EXPLAIN execution is measured separately.
  These are warm-cache synthetic metadata measurements, not network/device
  latency, cold-cache, concurrency or million-item acceptance.

Local runs on 2026-10-07 used PostgreSQL 17.10, `en_US.utf8`, 128 MB shared
buffers and 4 MB work memory, on an Intel i7-8550U development workstation with
7.41 GB RAM available to Docker.
The local client build is Debug; CI repeats the benchmark in Release on its
runner. No originals or derivatives are generated or read.

## Local results

All 24 measured combinations passed. Times below are milliseconds, median /
maximum of five reads. “Matched” is the visible collection after all filters;
other-owner, vault, deleted and excluded content does not enter that count.

| Owner media | Scope | Order | Matched | Index ms | 50-item window ms |
| ---: | --- | --- | ---: | ---: | ---: |
| 50,000 | library | DateTaken | 46,179 | 140 / 148 | 130 / 132 |
| 50,000 | library | Name | 46,179 | 202 / 248 | 528 / 595 |
| 50,000 | filtered | DateTaken | 1,548 | 186 / 211 | 334 / 354 |
| 50,000 | filtered | Name | 1,548 | 207 / 227 | 591 / 616 |
| 50,000 | album | DateTaken | 23,091 | 151 / 160 | 80 / 83 |
| 50,000 | album | Name | 23,091 | 193 / 208 | 402 / 414 |
| 100,000 | library | DateTaken | 92,356 | 485 / 507 | 457 / 485 |
| 100,000 | library | Name | 92,356 | 468 / 542 | 1051 / 1118 |
| 100,000 | filtered | DateTaken | 3,092 | 383 / 402 | 682 / 774 |
| 100,000 | filtered | Name | 3,092 | 422 / 434 | 1157 / 1178 |
| 100,000 | album | DateTaken | 46,179 | 329 / 342 | 159 / 162 |
| 100,000 | album | Name | 46,179 | 412 / 414 | 872 / 896 |

The original unfiltered name query at 100k measured 1,250 ms for the index and
2,605 ms for a complete jump; the final version measured 468 ms and 1,051 ms.
At 50k those medians were 931 / 2,199 ms before and 202 / 528 ms after.
This comparison is observed on one workstation, not a general throughput claim.

The 100k name-index plan changed from two title-index subplans, each executed
92,356 times, to a left join without those per-candidate title lookups. The
original EXPLAIN execution took 1,346 ms, including 704 ms of JIT; the final
plan took 470 ms with no JIT. Temporary writes fell from 1,624 to 450 blocks
with the default 4 MB work memory.

The ordinary name-page seek still scans/sorts display keys; it is not an ordered
scan of the raw filename index. Its measured 100k statement execution fell from
1,834 ms (1,225 ms JIT) to 714 ms with transaction-local JIT disabled. Date-window
medians vary with planning and transaction overhead; no across-the-board window
speedup is claimed. All final cases remain within the declared ceilings.

The aggregate remains O(n) in the filtered collection, and these results do not
establish million-item or concurrent-load performance. Larger requirements
would justify investigating a maintained, indexed display-key projection and
aggregate caching, with the write/invalidation rules tested separately.

## CI gate and reproduction

The PostgreSQL PR job runs both sizes and retains `navigation-50000.json`,
`navigation-100000.json`, `environment.json` and `progress.txt` in the
`media-navigation-performance` Actions artifact. JSON includes every sample,
the median, maximum, acceptance budget, generated SQL and the full plans for
all statements, including loops, buffers, temporary I/O and JIT timing.

The median ceilings are 1.5 s / 3 s for the index and 3 s / 6 s for the complete
50-item window, at 50k / 100k respectively. These deliberately allow runner
variance and serve as regression ceilings, not an advertised responsiveness
target. A database unavailable in this benchmark is a failure, not a skip;
outside this opt-in job it is excluded from ordinary local/fast tests.

```sh
NUBARCA_NAVIGATION_BENCHMARK=1 \
NUBARCA_NAVIGATION_BENCHMARK_OUTPUT=/tmp/nubarca-navigation-benchmark \
dotnet test tests/NubArca.Api.Tests/NubArca.Api.Tests.csproj \
  --filter 'FullyQualifiedName~MediaNavigationPerformanceTests' \
  --logger 'console;verbosity=normal'
```

The fixture is prepared outside the read budget and followed by `ANALYZE`.
Read commands keep the normal 30-second deadline. Navigation uses a read-only
PostgreSQL transaction with `SET LOCAL jit = off`; EXPLAIN uses that same setting.
The setting is restored when the read ends, including cancellation, and existing
caller-owned transactions are left untouched. The PostgreSQL correctness cases
keep one physical connection open and check that the setting and transaction
state are restored after navigation.

This benchmark is independent of the seven PostgreSQL correctness cases
covering both sort directions, bidirectional keyset continuation, cancellation,
and minimum/maximum dates. Npgsql represents the CLR date extrema as PostgreSQL
infinities: the index maps them explicitly to January 0001 / December 9999,
and the last month's window has no exclusive upper bound.
