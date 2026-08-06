namespace NubArca.Api.Jobs;

// Slice 89 / scheduler v2: the per-execution context handed to a job handler.
// It exposes the job id, the (flag-only) payload, a safe log sink, a
// cooperative-cancellation signal, a throttled progress helper, and — new in
// scheduler v2 — the cooperative SLICING contract (checkpoint in, yield check,
// continuation request). Handlers receive this instead of loose parameters so
// new capabilities can be added without changing the IJobHandler signature.
//
// All persistence callbacks are supplied by JobProcessor, which owns the DB
// writes (lease/heartbeat/progress/continuation) and the lease-ownership guard.
// Handlers never touch the background_jobs row directly.
public sealed class JobContext
{
    private readonly Action<string> _log;
    private readonly Func<int?, int?, string?, CancellationToken, Task> _reportProgress;
    private readonly TimeProvider _clock;
    // Wall-clock instant at/after which the current slice should stop. Null =
    // unbounded (foreground jobs are never preempted by the scheduler).
    private readonly DateTimeOffset? _sliceDeadline;
    // Max items this slice should process before yielding. Null = unbounded.
    private readonly int? _sliceItemBudget;

    private volatile bool _cancellationRequested;
    private volatile bool _higherPriorityWaiting;

    internal JobContext(
        Guid jobId,
        string payloadJson,
        Action<string> log,
        CancellationToken cancellationToken,
        Func<int?, int?, string?, CancellationToken, Task> reportProgress,
        TimeProvider clock,
        int priority,
        string? checkpoint,
        int sliceNumber,
        DateTimeOffset? sliceDeadline,
        int? sliceItemBudget)
    {
        JobId = jobId;
        PayloadJson = payloadJson;
        _log = log;
        CancellationToken = cancellationToken;
        _reportProgress = reportProgress;
        _clock = clock;
        Priority = priority;
        Checkpoint = checkpoint;
        SliceNumber = sliceNumber;
        _sliceDeadline = sliceDeadline;
        _sliceItemBudget = sliceItemBudget;
    }

    public Guid JobId { get; }

    // Persisted scheduler priority of the current job. A bounded follow-up job
    // may inherit it (for example targeted face detection -> embeddings).
    public int Priority { get; }

    // The small, flag-only payload JSON for this job (never sensitive data).
    public string PayloadJson { get; }

    // Cancelled when EITHER the worker is shutting down OR an operator has
    // requested cooperative cancellation of this job. Handlers should pass this
    // to downstream async work and/or poll IsCancellationRequested.
    public CancellationToken CancellationToken { get; }

    // ---- cooperative slicing ----------------------------------------------

    // Versioned, internal checkpoint persisted by the previous slice (null on
    // the first slice). A sliceable handler parses this to resume; it should
    // tolerate an unknown/older version by starting fresh rather than failing.
    public string? Checkpoint { get; }

    // 0 on the first run, incremented by the processor on each continuation.
    public int SliceNumber { get; }

    // True once cooperative cancellation has been requested for this job (the
    // flag is refreshed by the worker heartbeat) or the token is cancelled.
    public bool IsCancellationRequested
        => _cancellationRequested || CancellationToken.IsCancellationRequested;

    // True once the heartbeat observed a higher-priority job waiting. A handler
    // can use this to pick the right yield reason for observability.
    public bool HigherPriorityWaiting => _higherPriorityWaiting;

    // The cooperative yield check, polled by sliceable handlers at SAFE
    // boundaries (after a unit of work is fully committed or safely failed —
    // never mid-transaction, never between blob-ref acquisition and owner-row
    // commit). Returns true when the handler should checkpoint and stop:
    //   * cancellation requested;
    //   * a higher-priority job is waiting (best-effort, set by the heartbeat);
    //   * this slice's item budget is reached;
    //   * this slice's wall-clock deadline has passed.
    // Cheap (no DB hit) so it is safe to call in a tight loop.
    public bool ShouldYield(long processedThisSlice = 0)
        => IsCancellationRequested
            || _higherPriorityWaiting
            || (_sliceItemBudget is int budget && processedThisSlice >= budget)
            || (_sliceDeadline is DateTimeOffset deadline && _clock.GetUtcNow() >= deadline);

    // Set by a sliceable handler when it stops at a safe checkpoint with more
    // work remaining. The processor then re-queues THIS row (one logical job,
    // one row) instead of marking it succeeded: Status → queued, SliceNumber++,
    // CheckpointJson persisted, Attempts reset (a checkpoint is forward
    // progress, not a retry). `reason` is one of JobYieldReasons.*.
    public bool ContinuationRequested { get; private set; }
    public string? ContinuationReason { get; private set; }
    public string? ContinuationCheckpoint { get; private set; }

    public void RequestContinuation(string reason, string? checkpointJson = null)
    {
        ContinuationRequested = true;
        ContinuationReason = reason;
        ContinuationCheckpoint = checkpointJson;
    }

    // ---- logging + progress -----------------------------------------------

    // Concise, numbers-only progress/log line. MUST NOT include paths, storage
    // keys, raw metadata, or tokens.
    public void Log(string message) => _log(message);

    // Update coarse progress (current/total/message). Safe to call in a tight
    // loop: writes are throttled by the processor (and also flushed on each
    // heartbeat and at completion), so thousands of calls collapse to a handful
    // of DB updates. The message must be a short phase/count string only.
    public Task ReportProgressAsync(
        int? current, int? total = null, string? message = null, CancellationToken cancellationToken = default)
        => _reportProgress(current, total, message, cancellationToken);

    // Called by the worker heartbeat when it observes the cancellation flag.
    internal void MarkCancellationRequested() => _cancellationRequested = true;

    // Called by the worker heartbeat when it observes a higher-priority job
    // waiting — a best-effort accelerator so a long maintenance slice yields
    // before its full budget. The slice budget remains the hard guarantee.
    internal void MarkHigherPriorityWaiting() => _higherPriorityWaiting = true;
}
