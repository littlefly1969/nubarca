namespace NubArca.Api.Domain;

// Phase 2: a persisted "Organize photos by date" run. The run row is the source
// of truth for status/progress so the owner UI can poll it and progress
// survives a process restart; the BackgroundJob only carries this row's Id.
//
// Owner-scoped: a run organizes exactly one user's own files. No physical paths
// or storage internals are stored — OptionsJson holds the validated logical
// options (template, target root name, scope) and the counters are a
// denormalized projection used by the status API.
public class PhotoOrganizerRun
{
    public Guid Id { get; set; }

    // The owner whose files are being organized (also who started the run).
    public Guid OwnerUserId { get; set; }

    // Discriminator for future organizers (date-taken is the only kind today).
    public string Kind { get; set; } = PhotoOrganizerKinds.DateTaken;

    // queued | running | succeeded | partial | failed | cancelled
    // (see PhotoOrganizerStatuses).
    public string Status { get; set; } = PhotoOrganizerStatuses.Queued;

    // Validated logical options (scope, template, target root, missing-date
    // behaviour, conflict policy) — see PhotoOrganizerOptions. Never any path,
    // storage key, or sensitive data.
    public string OptionsJson { get; set; } = string.Empty;

    // Snapshot of the dry-run summary that informed this run (aggregate counts
    // only), persisted so the UI can show "what we expected to do".
    public string? DryRunSummaryJson { get; set; }

    // Total candidate photos in scope at run-creation time (drives progress %).
    public int CandidateCount { get; set; }

    // Live denormalized counters (the manifest + checkpoint are authoritative;
    // these power the status API without per-run aggregate queries).
    public int MovedCount { get; set; }
    public int AlreadyOrganizedCount { get; set; }
    public int SkippedMissingDateCount { get; set; }
    public int SkippedConflictCount { get; set; }
    public int ExactDuplicateRemovedCount { get; set; }
    public int FailedCount { get; set; }
    public int FoldersCreatedCount { get; set; }

    // Sanitized terminal error summary (exception type + short message) — never
    // a stack trace, path, or storage key.
    public string? ErrorSummary { get; set; }

    // The BackgroundJob that executes this run.
    public Guid? JobId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class PhotoOrganizerKinds
{
    public const string DateTaken = "date_taken";
}

public static class PhotoOrganizerStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    // Completed but some files failed to move.
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string? status) => status is
        Succeeded or Partial or Failed or Cancelled;
}
