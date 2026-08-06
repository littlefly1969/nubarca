namespace NubArca.Api.Domain;

// Slice 70: durable, retryable background job. Stored in PostgreSQL/SQLite via
// EF Core. The payload is a small, explicit JSON document containing ONLY
// operation flags (limits / booleans) — never storage keys, physical paths,
// raw metadata, tokens, or internal blob ids. Error fields are sanitized and
// truncated (exception type name + short message), never stack traces.
public class BackgroundJob
{
    public Guid Id { get; set; }

    // One of JobTypes.* — drives which handler runs.
    public string Type { get; set; } = string.Empty;

    // One of JobStatuses.* — queued / running / succeeded / failed / cancelled.
    public string Status { get; set; } = string.Empty;

    // Small, validated JSON payload (flags only). Never sensitive data.
    public string PayloadJson { get; set; } = "{}";

    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 3;

    // Lower number = higher priority. Default 100.
    public int Priority { get; set; } = 100;

    public DateTime CreatedAt { get; set; }

    // The job is eligible to run once AvailableAt <= now. Retries push this
    // forward by the configured retry delay.
    public DateTime AvailableAt { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // The lease owner currently holding the running job (machine + GUID). Only
    // this owner may transition the job to a terminal state, so a job reclaimed
    // after lease expiry can never be double-completed by the old worker.
    // Internal-only — never exposed by admin APIs / UI (it carries a hostname).
    public string? LockOwner { get; set; }

    // Slice 89: explicit lease + heartbeat. While a worker runs the job it
    // periodically extends LeaseUntil and stamps HeartbeatAt. A running job is
    // reclaimable once LeaseUntil < now — so a crashed worker frees the job
    // after a bounded delay, while a healthy long-running job keeps its lease
    // alive. (Slice 90 removed the legacy LockedAt stale-window: the lease is
    // now the single source of truth for reclaim.)
    public DateTime? LeaseUntil { get; set; }
    public DateTime? HeartbeatAt { get; set; }

    // Cooperative cancellation flag. An operator (endpoint/UI, future slice)
    // sets this; the running handler observes it via JobContext and stops, and
    // the job ends as `cancelled` (terminal, not retried). Safe to set while
    // queued or running.
    public bool CancellationRequested { get; set; }

    // Coarse progress for safe status reporting: numbers + a short, handler-
    // authored message (counts/phase only — never paths, keys, or metadata).
    public int? ProgressCurrent { get; set; }
    public int? ProgressTotal { get; set; }
    public string? ProgressMessage { get; set; }

    // Sanitized failure info. Code = exception type name; Message = truncated
    // exception message. Never a stack trace, path, key, or token.
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }

    // Optional dedup key. When set, enqueue is idempotent: a second enqueue
    // with the same key while a job is still queued/running returns the
    // existing job instead of creating a duplicate.
    public string? IdempotencyKey { get; set; }

    public DateTime UpdatedAt { get; set; }

    // ---- cooperative slicing (scheduler v2) -------------------------------
    // A long-running job runs as ONE logical row executed over multiple
    // slices. Each slice processes a bounded amount of work, checkpoints, then
    // voluntarily yields; the worker re-queues this same row (Status → queued,
    // AvailableAt = now) and re-selects the highest-priority eligible job.
    // There is no hard preemption and no row-per-slice explosion.

    // 0 on first run, incremented each time the job is re-queued as a
    // continuation. Surfaced (safely) on the admin dashboard.
    public int SliceNumber { get; set; }

    // Versioned, INTERNAL-ONLY checkpoint state sufficient to resume the next
    // slice safely (e.g. a keyset cursor + processed/failed counts). Unbounded
    // text — future AI/OCR/embedding jobs may need larger state. Never exposed
    // by any admin API/DTO; contains counts + cursor ids only, never storage
    // keys, paths, raw metadata, or tokens. Null = first slice / not sliced.
    public string? CheckpointJson { get; set; }

    // Why the last slice yielded (one of JobYieldReasons.*). Display-only.
    public string? YieldReason { get; set; }
}
