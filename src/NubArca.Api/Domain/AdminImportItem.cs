namespace NubArca.Api.Domain;

// Slice 92: one row per discovered source entry of an admin import run — the
// persisted import manifest. Item state is the SOURCE OF TRUTH for resume:
// a re-queued/retried run skips items already `imported` instead of re-walking
// the whole source tree, and run counters/progress derive from item statuses.
//
// Safety: RelativePath is the validated source-relative path under the run's
// whitelisted root — never an absolute physical path. FailureMessage is a
// sanitized category-grade message (never an exception stack, storage key, or
// internal id). FileItemId is stored for internal bookkeeping only and is
// never serialized into any API response.
public class AdminImportItem
{
    public Guid Id { get; set; }

    public Guid ImportRunId { get; set; }

    // Discovery order within the run (1-based, assigned by the scan phase).
    // Stable keyset/pagination key — paths can be too long to index safely.
    public int Ordinal { get; set; }

    // "file" | "directory" (directories preserve empty-dir import behaviour).
    public string Kind { get; set; } = AdminImportItemKinds.File;

    // Source-relative path under the run's root + source subpath ("a/b/c.txt").
    public string RelativePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    // Source file mtime captured at scan time; used to detect a file that
    // changed between scan and import (size/mtime mismatch → safe failure).
    public DateTime? SourceModifiedAt { get; set; }

    // pending | importing | imported | skipped | conflict | failed | cancelled
    // (see AdminImportItemStatuses).
    public string Status { get; set; } = AdminImportItemStatuses.Pending;

    // The created FileItem (set when imported). INTERNAL ONLY — never exposed
    // through any DTO (the admin surface never carries per-file ids).
    public Guid? FileItemId { get; set; }

    // Stable failure category (see AdminImportFailureCategories) + a short,
    // sanitized human message. Never raw exception text.
    public string? FailureCategory { get; set; }
    public string? FailureMessage { get; set; }

    // "preexisting" | "already-imported-this-run" (see AdminImportConflictCategories).
    public string? ConflictCategory { get; set; }

    // Times this item entered `importing` (a crash mid-file leaves it
    // `importing`; resume resets it to pending and the count shows the retry).
    public int Attempts { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public static class AdminImportItemKinds
{
    public const string File = "file";
    public const string Directory = "directory";
}

// Deliberately small, documented status vocabulary (slice 92):
//   pending    — discovered by scan, not yet processed.
//   importing  — claimed by the import loop (a crash leaves it here; resume
//                resets it to pending — CreateAsync is atomic, so no partial
//                FileItem can exist for an `importing` item).
//   imported   — a complete FileItem exists (includes resume-detected items,
//                marked with ConflictCategory = already-imported-this-run).
//   skipped    — deliberately not imported (symlink/special/unreadable at scan
//                time, or source vanished before import).
//   skipped_previously_deleted — exact content matched the owner's deleted-
//                content tombstone ledger and the import opted to skip it.
//   skipped_already_present    — exact content is already an active file in the
//                owner's normal library and the import opted to skip it.
//   conflict   — an active sibling with the same name pre-existed the run.
//   failed     — import errored (see FailureCategory); retryable by a new run.
//   cancelled  — the run was cancelled before this item was processed.
public static class AdminImportItemStatuses
{
    public const string Pending = "pending";
    public const string Importing = "importing";
    public const string Imported = "imported";
    public const string Skipped = "skipped";
    // deleted-content-import-skip: two disjoint, user-facing skip reasons.
    public const string SkippedPreviouslyDeleted = "skipped_previously_deleted";
    public const string SkippedAlreadyPresent = "skipped_already_present";
    public const string Conflict = "conflict";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsKnown(string? status) => status is
        Pending or Importing or Imported or Skipped or SkippedPreviouslyDeleted
        or SkippedAlreadyPresent or Conflict or Failed or Cancelled;
}

// Stable, safe failure/skip categories (never raw exception content).
public static class AdminImportFailureCategories
{
    public const string SymbolicLink = "symlink";
    public const string SpecialFile = "special_file";
    public const string Unreadable = "unreadable";
    public const string PathTooLong = "path_too_long";
    public const string SourceMissing = "source_missing";
    public const string SourceChanged = "source_changed";
    public const string FolderError = "folder_error";
    public const string QuotaExceeded = "quota_exceeded";
    public const string TooLarge = "too_large";
    public const string IoError = "io_error";
    public const string InvalidName = "invalid_name";
    public const string Cancelled = "cancelled";
}

public static class AdminImportConflictCategories
{
    // Collided with an active sibling that existed BEFORE this run started.
    public const string Preexisting = "preexisting";
    // Re-encountered a file THIS run already imported (resume/retry path).
    public const string AlreadyImportedThisRun = "already-imported-this-run";
}
