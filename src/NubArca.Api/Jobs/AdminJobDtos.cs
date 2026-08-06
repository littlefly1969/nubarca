namespace NubArca.Api.Jobs;

// Slice 90: safe, no-leak projections for the admin jobs dashboard.
//
// These DTOs intentionally OMIT: PayloadJson (may carry operation context),
// LockOwner (carries a worker hostname), IdempotencyKey, and anything derived
// from storage/blobs/metadata/tokens. LastErrorMessage is already sanitized +
// truncated at write time (exception type name + short message, never a stack
// trace) — see JobProcessor. Every field below is an id, an enum-ish string, a
// count, a timestamp, or a sanitized error string.
public sealed record AdminJobSummary(
    Guid Id,
    string Type,
    string Status,
    int Priority,
    int Attempts,
    int MaxAttempts,
    DateTime CreatedAt,
    DateTime AvailableAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime UpdatedAt,
    DateTime? LeaseUntil,
    DateTime? HeartbeatAt,
    bool CancellationRequested,
    int? ProgressCurrent,
    int? ProgressTotal,
    string? ProgressMessage,
    string? LastErrorCode,
    string? LastErrorMessage,
    // Scheduler v2 (safe, display-only): which slice of a long job is running,
    // and why the previous slice yielded. CheckpointJson is deliberately NOT
    // exposed (internal cursor/counts).
    int SliceNumber = 0,
    string? YieldReason = null)
{
    // Human-readable priority class, derived from the numeric Priority (no
    // stored column). Computed in memory — never part of the SQL projection.
    public string PriorityClass => JobScheduling.ClassForPriority(Priority);
}

// Aggregate status counters for the dashboard header.
public sealed record JobStatusCounts(
    int Queued,
    int Running,
    int Succeeded,
    int Failed,
    int Cancelled);

// One page of the admin jobs list, plus the (unfiltered) status counters.
public sealed record AdminJobPage(
    IReadOnlyList<AdminJobSummary> Items,
    int Page,
    int PageSize,
    int Total,
    JobStatusCounts Counts);

// Optional list filters. Null = no constraint.
public sealed record AdminJobFilter(string? Status, string? Type);
