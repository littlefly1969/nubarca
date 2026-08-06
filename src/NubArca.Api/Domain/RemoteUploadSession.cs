namespace NubArca.Api.Domain;

// Slice 93: a web remote-staging upload session. Staging is TEMPORARY
// acquisition space — bytes land under the configured staging root (never the
// blob store) and become NubArca files only after the verified session is
// handed off to the existing admin-import pipeline (admin_import_runs /
// admin_import_items / Background Jobs v2).
//
// No absolute physical path is ever stored or exposed: the session's staging
// directory is derived from the configured root + the session id.
public class RemoteUploadSession
{
    public Guid Id { get; set; }

    // Who created the session (the uploader). Owner-scoping key.
    public Guid CreatedByUserId { get; set; }

    // Whose library receives the files at import. Equals CreatedByUserId for
    // normal users; admins may target another user (admin-import convention).
    public Guid TargetUserId { get; set; }

    // Destination logical folder in the target user's library; null = root.
    public Guid? DestinationFolderId { get; set; }

    // Import skip options chosen at session creation, carried onto the linked
    // AdminImportRun at import hand-off so the choice survives resume (default
    // false = unchanged behaviour). See AdminImportRun for semantics.
    public bool SkipPreviouslyDeleted { get; set; }
    public bool SkipExistingContent { get; set; }

    // Optional display name for the session (bounded, UI only).
    public string Name { get; set; } = string.Empty;

    // See RemoteUploadSessionStatuses.
    public string Status { get; set; } = RemoteUploadSessionStatuses.Draft;

    // The session directory name relative to the staging root ("{id:N}").
    // Stored for diagnostics/cleanup; always relative, never absolute.
    public string StagingRelativeRoot { get; set; } = string.Empty;

    // Manifest totals (fixed once the manifest is accepted).
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }

    // Upload progress (denormalized; updated when an ITEM completes so chunk
    // uploads don't contend on this row).
    public int ReceivedFiles { get; set; }
    public long ReceivedBytes { get; set; }

    public int VerifiedFiles { get; set; }
    public int FailedFiles { get; set; }

    // Slice 93 handoff: the admin-import run executing the import (null until
    // import starts). Not a foreign key — the run may outlive the session.
    public Guid? AdminImportRunId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Sessions expire (staging is temporary). The cleanup sweeper marks
    // overdue sessions expired and reclaims their staging directories.
    public DateTime ExpiresAt { get; set; }

    // Sanitized error category + short message (never paths or stack traces).
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
}

// Lifecycle: draft → manifest_received → uploading → verifying →
// ready_to_import → importing → imported. failed / cancelled / expired are
// terminal; verifying falls back to uploading when verification finds gaps.
public static class RemoteUploadSessionStatuses
{
    public const string Draft = "draft";
    public const string ManifestReceived = "manifest_received";
    public const string Uploading = "uploading";
    public const string Verifying = "verifying";
    public const string ReadyToImport = "ready_to_import";
    public const string Importing = "importing";
    public const string Imported = "imported";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";

    public static bool IsTerminal(string? status) => status is
        Imported or Failed or Cancelled or Expired;

    public static bool IsKnown(string? status) => status is
        Draft or ManifestReceived or Uploading or Verifying or ReadyToImport
        or Importing or Imported or Failed or Cancelled or Expired;
}
