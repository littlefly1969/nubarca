using NubArca.Api.Domain;

namespace NubArca.Api.Jobs;

public interface IJobQueue
{
    // Enqueues a job of the given type with a typed payload (serialized to a
    // small flag-only JSON document). When `idempotencyKey` is non-null and a
    // queued/running job with the same key already exists, the existing job is
    // returned instead of creating a duplicate.
    //
    // `priority` is the scheduler ordering key (lower = higher). When null it is
    // resolved from JobScheduling.DefaultPriorityFor(type) — so admin/staging
    // import land in the foreground band and maintenance backfills in the
    // maintenance band automatically, without every call site repeating it.
    Task<BackgroundJob> EnqueueAsync<TPayload>(
        string type,
        TPayload payload,
        int? maxAttempts = null,
        int? priority = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    // Aggregate status counts (queued/running/succeeded/failed/cancelled) plus
    // the most recent jobs. No payloads are returned — counts + safe summary
    // fields only.
    Task<JobQueueSnapshot> GetSnapshotAsync(int recentLimit = 20, CancellationToken cancellationToken = default);

    // Slice 89: request cooperative cancellation of a queued or running job.
    // Sets the persistent CancellationRequested flag; a queued job is finished
    // as `cancelled` when next picked up, a running job's handler observes the
    // flag via its JobContext (refreshed by the worker heartbeat) and stops.
    // Returns false when the job is missing or already in a terminal state.
    Task<bool> RequestCancellationAsync(Guid jobId, CancellationToken cancellationToken = default);

    // Slice 90: admin dashboard — a page of safe job summaries (newest first),
    // optionally filtered by status/type, plus unfiltered status counters.
    // Never returns PayloadJson or LockOwner.
    Task<AdminJobPage> ListAdminJobsAsync(
        AdminJobFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    // Slice 90: one safe job summary by id, or null when missing.
    Task<AdminJobSummary?> GetAdminJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}

public sealed record JobQueueSnapshot(
    int Queued,
    int Running,
    int Succeeded,
    int Failed,
    int Cancelled,
    IReadOnlyList<JobSummary> Recent);

// Safe, no-leak projection for `jobs list`. Never includes PayloadJson. Progress
// fields are handler-authored counts/phase only (no paths, keys, or metadata).
public sealed record JobSummary(
    Guid Id,
    string Type,
    string Status,
    int Attempts,
    int MaxAttempts,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? LastErrorCode,
    int? ProgressCurrent = null,
    int? ProgressTotal = null,
    string? ProgressMessage = null,
    bool CancellationRequested = false);
