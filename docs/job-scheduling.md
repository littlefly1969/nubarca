# Background job scheduling

NubArca runs background work (imports, media derivatives, metadata
extraction, storage reconcile) through a single durable queue
(`background_jobs`) drained by an in-process worker (`JobWorker`, off by
default — see `Jobs:WorkerEnabled`) or out-of-band via the `jobs run-once` /
`jobs worker` CLI commands. Both paths share one engine (`JobProcessor`), so
they schedule and slice identically.

This document describes the **cooperative, priority-aware scheduler** ("v2").

## The problem it solves

The worker is effectively single-lane. Previously every job was enqueued at the
same priority and each job ran to completion in one execution, so a long
`media.derivatives.backfill` could monopolize the worker for hours while a
newly-queued, user-facing `admin.import` (or staging-to-library import) sat
waiting.

The fix is **cooperative scheduling**: long jobs voluntarily checkpoint and
yield at safe boundaries, and the worker then re-selects the highest-priority
eligible job. There is **no hard preemption** — threads are never killed and no
job is interrupted mid-transaction or between acquiring a blob reference and
committing its owner row.

> **The guarantee:** the maximum a foreground import waits behind a running
> maintenance job is bounded by **one slice budget**, not the whole backfill.

## Priority classes

`Priority` is an integer column (lower = higher priority). `JobScheduling` names
the bands and maps each job type to a default; the class *label* shown on the
admin dashboard is derived from the number (no stored column).

| Class         | Priority | Job types                                                        |
|---------------|----------|------------------------------------------------------------------|
| `foreground`  | 10       | `admin.import`, staging-to-library import                        |
| `normal`      | 50       | (reserved)                                                       |
| `maintenance` | 100      | `media.derivatives.backfill`, `metadata.embedded.backfill`, `storage.reconcile` |
| `cleanup`     | 150      | (reserved; the hosted janitor/sweepers run independently)        |
| `compute`     | 200      | (reserved for future AI: tagging, embeddings, OCR, …)            |

`JobQueue.EnqueueAsync` stamps the registry default when a caller doesn't pin a
priority, so imports land in `foreground` and backfills in `maintenance`
automatically.

## Cooperative slicing

A long-running job is **one logical job = one row**, executed over multiple
*slices*. Per slice, a sliceable handler:

1. resumes from the job's `CheckpointJson` (internal, versioned resume state —
   never exposed by any API);
2. processes a bounded amount of work, polling `JobContext.ShouldYield(processedThisSlice)`
   at **safe per-item boundaries** (after a unit of work is fully committed or
   safely failed);
3. when `ShouldYield` trips with work remaining, calls
   `JobContext.RequestContinuation(reason, checkpoint)`.

`ShouldYield` returns true when any of these hold:

- cancellation has been requested;
- the slice's **item budget** (`MaintenanceSliceItemBudget`) is reached;
- the slice's **wall-clock budget** (`MaintenanceSliceSeconds`) has elapsed;
- a **higher-priority job is waiting** (a best-effort heartbeat probe; the
  budgets are the hard guarantee).

On continuation the processor re-queues the **same row**: `Status → queued`,
`AvailableAt = now + ContinuationDelaySeconds`, `SliceNumber++`, the new
checkpoint is persisted, and `Attempts` is reset to 0 (a checkpoint is forward
progress, not a retry — each slice gets a fresh retry budget). Because the row
re-queues with `AvailableAt = now`, a waiting equal-priority peer is selected
first — this gives same-priority jobs fair turns (a natural round-robin).

Foreground jobs are **not** sliced here: admin/staging import already self-pace
via their own `MaxRunMinutes` (they pause and re-enqueue a resume job), and the
scheduler never preempts them.

### Today's sliceable workloads

Both backfills share the same cooperative shape: **keyset-paged** candidate
selection (never load the full set), a successfully-processed item drops out of
the candidate query on its own, and only items that *fail to resolve* are
recorded in the checkpoint and skipped on later slices — so a permanent failure
can never block forward progress. Failures are counted and surfaced; a fresh
enqueue (no checkpoint) re-attempts everything still outstanding.

- **`media.derivatives.backfill`** — a processed item gains its `FileThumbnail`
  row(s) and leaves the "missing derivatives" query. The safe yield point is
  after each file's derivatives are fully persisted or safely failed (never
  between blob-ref acquisition and the row commit, which already releases the
  ref on cancel).
- **`metadata.embedded.backfill`** — re-extracts embedded EXIF/IPTC/XMP/GPS per
  blob via the existing idempotent `ReExtractEmbeddedMetadataAsync`. A blob that
  reaches the current extractor version with a non-failed status leaves the
  candidate query; the safe yield point is after each blob's extraction is
  committed. The checkpoint holds cumulative counts + the failed-blob id set
  (versioned, internal). A blob already at the current version is skipped, so a
  re-run is a no-op; a blob whose extraction fails stays `Failed` and is
  retried by a fresh enqueue or `--failed-only`. The per-slice result
  distinguishes completed / skipped / failed.

`storage.reconcile` and the hosted cleanup sweepers are not sliced yet — see
*Extending* below.

## Selection and anti-starvation

At claim time the processor fetches two small candidate pools — top-K by
`(Priority, AvailableAt)` and top-K by `(AvailableAt)` — unions them, and picks:

- normally, the lowest base `Priority` (**foreground always wins by default**);
- **but** if a lower-priority job has waited at least `StarvationGraceSeconds`
  while higher-priority work kept being chosen, it is promoted for **one** slice.

After a promoted job runs its slice it re-queues with `AvailableAt = now`, so
its wait resets and it cannot *consistently* outrank fresh foreground — it earns
at most one slice per grace window. This bounds a foreground job's worst-case
added latency to one maintenance slice per `StarvationGraceSeconds`, while
guaranteeing maintenance/cleanup work eventually runs under continuous
foreground load.

## Cancellation, retries, failure

- **Cancellation** stays cooperative: it is observed at a safe yield point; the
  job lands in a terminal `cancelled` state and is **not** turned into a
  continuation. Derivative generation releases any acquired blob reference on
  cancel, so there is no refcount leak or orphaned temp file.
- **Retries** preserve the checkpoint: a slice that throws is re-queued with its
  checkpoint intact and resumes from there (idempotent — existing rows are
  skipped). A clean checkpoint resets the per-slice retry budget.
- **`MaxSlicesPerJob`** (0 = unlimited) is a backstop: a job that keeps
  requesting continuation past the cap is force-completed (remaining work is
  left for a future enqueue) and marked with yield reason `max-slices`.

## Observability

The admin jobs dashboard (`/api/admin/jobs`) surfaces safe scheduler fields:
the derived `priorityClass`, `sliceNumber`, and `yieldReason`
(`slice-budget` | `higher-priority` | `max-slices`), alongside the existing
status/attempts/progress. `CheckpointJson` is internal and never returned.

## Configuration (`Jobs:` section)

| Key                          | Default | Meaning                                                        |
|------------------------------|---------|----------------------------------------------------------------|
| `MaintenanceSliceSeconds`    | 30      | wall-clock budget per maintenance slice                        |
| `MaintenanceSliceItemBudget` | 200     | item budget per maintenance slice                              |
| `ContinuationDelaySeconds`   | 0       | delay before a yielded job's next slice becomes available      |
| `StarvationGraceSeconds`     | 300     | a lower-priority job waiting this long earns one slice         |
| `MaxSlicesPerJob`            | 0       | 0 = unlimited; backstop against a livelooping handler          |

(See also the existing `WorkerEnabled`, `PollIntervalSeconds`, `BatchSize`,
`LeaseSeconds`, `HeartbeatSeconds`, `RetryDelaySeconds`, `DefaultMaxAttempts`.)
Defaults favour correctness and foreground responsiveness over raw throughput.

## Extending (future work)

The design leaves clear seams, intentionally **not** built yet:

- **More sliceable jobs.** Any handler can become sliceable by reading
  `JobContext.Checkpoint`, polling `ShouldYield`, and calling
  `RequestContinuation` (as `media.derivatives.backfill` and
  `metadata.embedded.backfill` already do) — `storage.reconcile` is the next
  candidate.
- **Resource classes & per-resource concurrency.** `JobScheduling` documents a
  future `ResourceClass` (`cpu`, `cpu-gpu`, `external`, …) so CPU/GPU-heavy AI
  jobs (tagging, embeddings, OCR, transcription, semantic indexing) can declare
  their resource and be capped or routed to dedicated worker pools — without
  reworking the core selection/slicing logic.
- **Folding hosted sweepers** (`BlobJanitor`, `FileItemSweeper`,
  `StagingCleanup`) into the queue under the `cleanup` class.
