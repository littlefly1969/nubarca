namespace NubArca.Api.Domain.Mail;

// MAILARCHIVE-01: the owner-private persistence model of an imported Outlook
// PST. See docs/mailarchive/contract.md §3–§6.
//
// None of these rows is a FileItem. A message's content is a normalized,
// immutable document blob owned by its mail_messages row (counted by
// BlobReferenceAuditService, like a PlateImage), so no generic file route can
// rename, move or rewrite it. Attachments ARE ordinary FileItems; the link row
// below holds no blob reference of its own.
//
// Source coordinates (PST node ids, attachment keys) and the source
// fingerprint are internal: they never cross the API boundary.

/// <summary>One PST source (one exact PST version) of one owner.</summary>
public class MailArchive
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }

    // Keyed fingerprint of the PST bytes (ContentFingerprint, never the raw
    // SHA-256). Same bytes ⇒ same archive; changed bytes ⇒ a new archive.
    public string SourceFingerprint { get; set; } = string.Empty;
    public string FingerprintScheme { get; set; } = string.Empty;

    // The FileItem the PST was imported from. SET NULL when that file is
    // purged: the archive outlives its source.
    public Guid? SourceFileItemId { get; set; }
    public string? SourceFileName { get; set; }
    public long SourceSizeBytes { get; set; }

    public string DisplayName { get; set; } = string.Empty;
    // "unicode" | "ansi", from the engine probe; null before the first probe.
    public string? Format { get; set; }

    // Set once a scan has persisted the whole folder tree and manifest.
    public DateTime? ScanCompletedAt { get; set; }

    // Denormalized for the archive list; recomputed at the end of every run
    // and kept in step by message deletion.
    public int MessageCount { get; set; }
    public int FolderCount { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastImportedAt { get; set; }
}

/// <summary>One execution of the import of an archive.</summary>
public class MailImportRun
{
    public Guid Id { get; set; }
    public Guid ArchiveId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid? JobId { get; set; }

    public string Status { get; set; } = MailImportRunStatuses.Queued;
    public string? Phase { get; set; }

    // Progress projection. The manifest (mail_import_items) is authoritative:
    // the message counters are recomputed from it when the run finalizes.
    public int TotalMessages { get; set; }
    public int ProcessedMessages { get; set; }
    public int ImportedMessages { get; set; }
    public int AlreadyPresentMessages { get; set; }
    public int SkippedDeletedMessages { get; set; }
    public int FailedMessages { get; set; }
    public int AttachmentsCreated { get; set; }
    public int AttachmentsReused { get; set; }
    public int AttachmentsSkipped { get; set; }
    // Folders/messages the engine could not even enumerate during the scan.
    public int ScanErrors { get; set; }

    // Sanitized: a stable code and a short message, never an exception text
    // carrying a path, a key or message content.
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class MailImportRunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsActive(string status) => status is Queued or Running;
}

public static class MailImportPhases
{
    public const string Preparing = "preparing";
    public const string Scanning = "scanning";
    public const string Importing = "importing";
    public const string Finalizing = "finalizing";
}

/// <summary>
/// One message of the PST manifest. Per ARCHIVE, so it outlives runs: a
/// re-import resumes from these states instead of starting over.
/// </summary>
public class MailImportItem
{
    public Guid Id { get; set; }
    public Guid ArchiveId { get; set; }
    public Guid OwnerUserId { get; set; }

    // Engine message id ("m:" + 8 hex). Internal.
    public string SourceMessageKey { get; set; } = string.Empty;
    public string SourceFolderKey { get; set; } = string.Empty;
    // Scan order; the import's keyset key.
    public int Ordinal { get; set; }

    public string Status { get; set; } = MailImportItemStatuses.Pending;
    public int Attempts { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }

    public Guid? MessageId { get; set; }

    // Which run last decided this item, and how (MailImportOutcomes). Lets a
    // finalizing run recount its own results from persisted state.
    public Guid? LastRunId { get; set; }
    public string? LastOutcome { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class MailImportItemStatuses
{
    public const string Pending = "pending";
    public const string Importing = "importing";
    public const string Imported = "imported";
    public const string Failed = "failed";
    // A tombstone said the owner deleted this message: never imported again.
    public const string SkippedDeleted = "skipped_deleted";
    // The owner deleted the imported message.
    public const string Deleted = "deleted";

    public static bool IsFinal(string status) => status is Imported or SkippedDeleted or Deleted;
}

public static class MailImportOutcomes
{
    public const string Imported = "imported";
    public const string AlreadyPresent = "already_present";
    public const string SkippedDeleted = "skipped_deleted";
    public const string Failed = "failed";
}

/// <summary>The original folder tree of an archive.</summary>
public class MailFolder
{
    public Guid Id { get; set; }
    public Guid ArchiveId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid? ParentId { get; set; }

    public string SourceFolderKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    // Display path from the IPM subtree root, as a JSON array of names.
    public string PathJson { get; set; } = "[]";
    public int Depth { get; set; }
    public int Ordinal { get; set; }
    public string? ContainerClass { get; set; }

    // Complete messages in this folder (denormalized, like the archive count).
    public int MessageCount { get; set; }
}

/// <summary>One message occurrence: one archive, one folder.</summary>
public class MailMessage
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid ArchiveId { get; set; }
    public Guid FolderId { get; set; }

    public string SourceMessageKey { get; set; } = string.Empty;

    // importing | complete. Only complete rows are ever visible.
    public string Status { get; set; } = MailMessageStatuses.Importing;

    // The normalized document (nubarca-mail-message). Set in the SAME
    // transaction that flips Status to complete. RESTRICT FK; the reference is
    // released with the row's hard delete.
    public Guid? BlobObjectId { get; set; }
    public string DocumentFormat { get; set; } = MailDocumentFormats.Message;
    public int DocumentVersion { get; set; }
    public long DocumentSizeBytes { get; set; }

    public string? MessageClass { get; set; }
    public string? Subject { get; set; }
    public string? FromName { get; set; }
    public string? FromAddress { get; set; }
    public string? ToSummary { get; set; }
    public string? Preview { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? ReceivedAt { get; set; }
    // Never null: received → sent → created → import time. The list order.
    public DateTime SortDate { get; set; }
    public string? InternetMessageId { get; set; }
    public bool HasAttachments { get; set; }
    public int AttachmentCount { get; set; }

    // Lower-cased search projections (provider-neutral substring search).
    public string SearchSubject { get; set; } = string.Empty;
    public string SearchFrom { get; set; } = string.Empty;
    public string SearchRecipients { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime? ImportedAt { get; set; }
}

public static class MailMessageStatuses
{
    public const string Importing = "importing";
    public const string Complete = "complete";
}

public static class MailDocumentFormats
{
    public const string Message = "nubarca-mail-message";
    public const int CurrentVersion = 1;
}

/// <summary>
/// The application link between a message and one of its attachments. The
/// attachment itself is an ordinary FileItem the owner may rename, move or
/// delete; this row owns no blob reference.
/// </summary>
public class MailAttachment
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid MessageId { get; set; }

    // Dotted source path ("0", "2.0"). Internal.
    public string AttachmentKey { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public string OriginalName { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public string? ContentId { get; set; }
    public bool IsInline { get; set; }
    public long? SizeBytes { get; set; }
    public string Method { get; set; } = string.Empty;

    // linked | skipped. A skipped attachment was described but not extracted.
    public string Status { get; set; } = MailAttachmentStatuses.Pending;
    public string? SkipReason { get; set; }

    // SET NULL when the file is purged.
    public Guid? FileItemId { get; set; }

    public DateTime CreatedAt { get; set; }
}

public static class MailAttachmentStatuses
{
    public const string Pending = "pending";
    public const string Linked = "linked";
    public const string Skipped = "skipped";
}

/// <summary>
/// Minimal memory that the owner deleted a message of one exact PST version,
/// so a re-import of that version never brings it back. No content.
/// </summary>
public class MailDeletionTombstone
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string SourceFingerprint { get; set; } = string.Empty;
    public string SourceMessageKey { get; set; } = string.Empty;
    public Guid DeletedMessageId { get; set; }
    public DateTime DeletedAt { get; set; }
}
