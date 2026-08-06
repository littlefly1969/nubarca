namespace NubArca.Api.Jobs;

public class JobsOptions
{
    public const string SectionName = "Jobs";

    // The in-process hosted worker is OFF by default. Operators opt in via
    // Jobs:WorkerEnabled=true, or run a worker out-of-band with the
    // `jobs worker` / `jobs run-once` CLI commands. NubArca never processes
    // jobs automatically unless this is set.
    public bool WorkerEnabled { get; set; } = false;

    // How often the hosted worker polls for available jobs.
    public int PollIntervalSeconds { get; set; } = 10;

    // Maximum jobs a single poll / run-once pass will process.
    public int BatchSize { get; set; } = 10;

    // Default max attempts applied to jobs that don't specify their own.
    public int DefaultMaxAttempts { get; set; } = 3;

    // Delay before a failed-but-retryable job becomes available again.
    public int RetryDelaySeconds { get; set; } = 60;

    // Slice 89: explicit lease length. When a worker claims a job it owns it
    // until now + LeaseSeconds; a running job becomes reclaimable once its
    // lease expires. The worker heartbeats well inside this window to keep a
    // healthy long-running job alive, so this is the bounded delay before a
    // CRASHED worker's job is freed — keep it comfortably larger than
    // HeartbeatSeconds. Default 120s.
    public int LeaseSeconds { get; set; } = 120;

    // How often the running handler's heartbeat extends the lease (and flushes
    // the latest progress + reads the cancellation flag). Must be well under
    // LeaseSeconds. Default 30s.
    public int HeartbeatSeconds { get; set; } = 30;

    // Conservative cap on independent polling/processing slots in one worker.
    // Default 1 preserves strictly sequential operation. Values are clamped to
    // 1..8; every slot creates its own DI scope/AppDbContext and claims jobs via
    // the existing atomic status predicate, so no scoped service is shared.
    public int MaxConcurrentJobs { get; set; } = 1;

    // ---- scheduler v2: cooperative slicing --------------------------------
    // Maintenance-class jobs (e.g. media.derivatives.backfill) run in slices:
    // each slice processes at most this many items OR this much wall-clock,
    // then checkpoints and yields so a queued higher-priority foreground import
    // can run next. The maximum a foreground job waits behind a maintenance job
    // is therefore bounded by ONE slice budget, not the whole backfill.
    // Foreground jobs are NOT sliced here (admin/staging import self-pace via
    // their own MaxRunMinutes).
    public int MaintenanceSliceSeconds { get; set; } = 30;
    public int MaintenanceSliceItemBudget { get; set; } = 200;

    // Delay before a yielded job's continuation becomes available again.
    // 0 = immediately (it re-queues with AvailableAt = now, so a waiting
    // equal-priority peer still goes first — natural round-robin).
    public int ContinuationDelaySeconds { get; set; } = 0;

    // Anti-starvation: a lower-priority job that has waited at least this long
    // while higher-priority work kept being selected is promoted for exactly
    // ONE slice, then re-queues (its wait resets). This guarantees maintenance/
    // cleanup work eventually runs under continuous foreground load WITHOUT
    // letting it consistently outrank freshly-queued foreground imports.
    public int StarvationGraceSeconds { get; set; } = 300;

    // Safety cap on the number of continuation slices a single logical job may
    // run before it is force-completed (remaining work is left for a future
    // enqueue). 0 = unlimited. A backstop against a livelooping handler, not a
    // normal limit.
    public int MaxSlicesPerJob { get; set; } = 0;
}
