namespace NubArca.Api.MailArchive;

// Owner-private DTOs (contract §5). They carry ids the owner can act on and
// display fields — never a storage key, hash, fingerprint, physical path, PST
// node id or the raw document.

public sealed record MailStatusDto(bool Available, string Reason);

public sealed record MailSourceCandidateDto(
    Guid FileItemId, string Name, long SizeBytes, DateTime CreatedAt, Guid? AlreadyImportedArchiveId);

public sealed record MailRunDto(
    Guid Id,
    Guid ArchiveId,
    string Status,
    string? Phase,
    int TotalMessages,
    int ProcessedMessages,
    int ImportedMessages,
    int AlreadyPresentMessages,
    int SkippedDeletedMessages,
    int FailedMessages,
    int AttachmentsCreated,
    int AttachmentsReused,
    int AttachmentsSkipped,
    bool CancellationRequested,
    string? ErrorCode,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int ScanErrors,
    IReadOnlyList<MailScanIssueDto> ScanIssues);

// One folder or message the scan could not enumerate. FolderPath is by name
// (null when the engine could not place it); never a PST node id.
public sealed record MailScanIssueDto(string Scope, string Code, IReadOnlyList<string>? FolderPath);

public sealed record MailArchiveDto(
    Guid Id,
    string DisplayName,
    string? Format,
    bool SourceAvailable,
    string? SourceFileName,
    int MessageCount,
    int FolderCount,
    DateTime CreatedAt,
    DateTime? LastImportedAt,
    MailRunDto? LatestRun);

public sealed record MailStartImportRequest(Guid FileItemId);

public sealed record MailStartImportResponse(MailArchiveDto Archive, MailRunDto Run);

public sealed record MailFolderDto(Guid Id, Guid? ParentId, string Name, int Depth, int MessageCount);

public sealed record MailFailureDto(IReadOnlyList<string> FolderPath, int Position, string Code, string? Message);

public sealed record MailFailurePage(IReadOnlyList<MailFailureDto> Items, string? NextCursor);

public sealed record MailAddressDto(string? Name, string? Address);

public sealed record MailMessageSummaryDto(
    Guid Id,
    Guid ArchiveId,
    Guid FolderId,
    string? Subject,
    MailAddressDto? From,
    string? ToSummary,
    DateTime? Date,
    bool HasAttachments,
    int AttachmentCount,
    string? Preview);

// BodySearchIncomplete: the term was also looked for in the message text,
// but some of the owner's messages have no searchable text yet (imported
// before it existed; a background job is adding it).
public sealed record MailMessagePage(IReadOnlyList<MailMessageSummaryDto> Items, string? NextCursor,
    bool BodySearchIncomplete = false);

public sealed record MailAttachmentViewDto(
    Guid Id,
    string Name,
    string? MimeType,
    long? SizeBytes,
    bool Inline,
    string? ContentId,
    string? EmbeddedPath,
    string State,
    string? SkipReason,
    Guid? FileItemId,
    string? CurrentFileName);

public sealed record MailEmbeddedMessageDto(
    string Path, string? Subject, MailAddressDto? From, DateTime? Date, string? BodyText);

public sealed record MailBodyDto(string Kind, string? Html, string? Text, bool Truncated);

public sealed record MailMessageDetailDto(
    Guid Id,
    Guid ArchiveId,
    Guid FolderId,
    string? Subject,
    MailAddressDto? From,
    string? ToSummary,
    DateTime? Date,
    bool HasAttachments,
    int AttachmentCount,
    string? Preview,
    string ArchiveName,
    IReadOnlyList<string> FolderPath,
    IReadOnlyList<MailAddressDto> To,
    IReadOnlyList<MailAddressDto> Cc,
    IReadOnlyList<MailAddressDto> Bcc,
    DateTime? SentAt,
    DateTime? ReceivedAt,
    string? InternetMessageId,
    string? MessageClass,
    MailBodyDto Body,
    IReadOnlyList<MailAttachmentViewDto> Attachments,
    IReadOnlyList<MailEmbeddedMessageDto> EmbeddedMessages);

public sealed record MailFileOriginDto(
    Guid MessageId,
    Guid ArchiveId,
    string ArchiveName,
    string? Subject,
    MailAddressDto? From,
    DateTime? Date,
    string AttachmentName);

public static class MailAttachmentStates
{
    public const string Available = "available";
    public const string FileDeleted = "file_deleted";
    public const string NotExtracted = "not_extracted";
}

public sealed record MailMessageQuery(
    Guid? ArchiveId,
    Guid? FolderId,
    string? Q,
    string? From,
    string? To,
    string? Subject,
    DateTime? After,
    DateTime? Before,
    bool? HasAttachments,
    string? Cursor,
    int Limit);
