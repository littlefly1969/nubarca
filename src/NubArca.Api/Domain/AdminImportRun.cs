namespace NubArca.Api.Domain;

// Slice 81: a persisted server-side import run. Tracks progress so the admin
// UI can poll it, and so progress survives a process restart (the run row is
// the source of truth; the BackgroundJob only carries this row's Id).
//
// No physical absolute paths are stored: RootId is the opaque id of a
// whitelisted root, and SourceRelativePath is a validated relative path under
// that root. CurrentRelativePath / ErrorSummary are likewise safe.
public class AdminImportRun
{
    public Guid Id { get; set; }

    // Who started the import (audit/attribution) and who owns the imported
    // files (the selected target user — not necessarily the admin).
    public Guid AdminUserId { get; set; }
    public Guid TargetUserId { get; set; }

    // Destination logical folder in the target user's library; null = root.
    public Guid? DestinationFolderId { get; set; }

    // Import skip options (deleted-content-import-skip). When set, incoming
    // files are skipped without creating a FileItem or enqueuing any post-
    // ingestion work. Default false = unchanged import behaviour.
    //   SkipPreviouslyDeleted — skip content matching the owner's deleted-
    //     content tombstone ledger (exact content, not filename).
    //   SkipExistingContent — skip content already present as an active file in
    //     the owner's normal library (Private Vault excluded, never revealed).
    public bool SkipPreviouslyDeleted { get; set; }
    public bool SkipExistingContent { get; set; }

    // Opaque id of the configured root + the safe relative subpath under it.
    public string RootId { get; set; } = string.Empty;
    public string SourceRelativePath { get; set; } = string.Empty;

    // queued | running | succeeded | partial | failed | cancelled
    // (see AdminImportStatuses).
    public string Status { get; set; } = AdminImportStatuses.Queued;

    // Slice 92: sub-phase of a running job ("scanning" | "importing"; null when
    // not running). Scan builds the persisted admin_import_items manifest;
    // import drains its pending items.
    public string? Phase { get; set; }

    // Slice 92: set once the scan phase has fully persisted the manifest.
    // Resume skips the rescan when this is non-null and trusts item state.
    public DateTime? ScanCompletedAt { get; set; }

    // Slice 91: cancellation is no longer tracked here. The linked BackgroundJob
    // (JobId) carries CancellationRequested as the single source of truth; the
    // handler observes it via JobContext and stops at a safe checkpoint, and the
    // run-status API reports "cancellation pending" from the job.

    // Slice 92: the counters below are a DENORMALIZED projection of the
    // admin_import_items statuses (refreshed periodically and at finalize) so
    // the runs LIST endpoint never needs per-run aggregate queries. The item
    // table is authoritative.
    public int ScannedFiles { get; set; }
    public int ImportedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public int FailedFiles { get; set; }
    // Slice 84: ConflictFiles now means TRUE logical-name conflicts only —
    // an active sibling that already existed BEFORE this run started.
    public int ConflictFiles { get; set; }
    // Files this run had already imported in an earlier slice and re-detected
    // on resume. Slice 92: a SUBSET of ImportedFiles (the FileItems exist),
    // split out so resumed work is not mistaken for fresh ingestion.
    public int AlreadyImportedFiles { get; set; }
    // Slice 92: file items frozen as `cancelled` when the run was cancelled.
    public int CancelledFiles { get; set; }
    // deleted-content-import-skip: files skipped by each import option. Disjoint
    // from SkippedFiles (which stays symlink/unreadable/vanished skips).
    public int SkippedPreviouslyDeletedFiles { get; set; }
    public int SkippedAlreadyPresentFiles { get; set; }
    public long ImportedBytes { get; set; }
    public long TotalBytes { get; set; }
    public int TotalDirectories { get; set; }

    // Safe relative path of the item currently being processed (UI feedback).
    public string? CurrentRelativePath { get; set; }

    // Sanitized terminal error summary (exception type + short message), never
    // a stack trace, path, or storage key.
    public string? ErrorSummary { get; set; }

    // The BackgroundJob that executes this run.
    public Guid? JobId { get; set; }

    // Slice 93: when non-null, this run imports from a remote-staging upload
    // session's directory (under Staging:RootPath) instead of a configured
    // AdminImport root, and its manifest items were pre-populated from the
    // verified staging manifest (so the scan phase is skipped). Not a foreign
    // key — the run outlives the (temporary) session.
    public Guid? StagingSessionId { get; set; }

    // Slice 82: L2 per-phase timing totals (milliseconds) accumulated across
    // all files in the run. Nullable: null when not yet measured / old runs.
    public long? ReadMillis { get; set; }
    public long? HashMillis { get; set; }
    public long? WriteMillis { get; set; }
    public long? BlobDbMillis { get; set; }
    // Slice 95: minimal media detection, split out of MetadataMillis so the
    // latter measures FULL embedded extraction only (0 when deferred).
    public long? DetectMillis { get; set; }
    public long? MetadataMillis { get; set; }
    public long? FileItemMillis { get; set; }
    public long? ThumbnailMillis { get; set; }
    public long? FolderMillis { get; set; }
    // Slice 95: import-item bookkeeping (page claims + terminal marks).
    public long? ItemDbMillis { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class AdminImportStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    // Slice 83: a run that hit its MaxRunMinutes budget; persisted + re-queued
    // to resume in a later slice. Non-terminal (the UI keeps polling).
    public const string Paused = "paused";
}

// Slice 92: sub-phases of a running import job.
public static class AdminImportPhases
{
    public const string Scanning = "scanning";
    public const string Importing = "importing";
}
