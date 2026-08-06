namespace NubArca.Api.Files;

// HTTP/listing projection of a FileItem. Deliberately omits BlobObjectId,
// OwnerUserId, ParentFolderId, UpdatedAt, DeletedAt — those are storage-internal
// concerns that must never reach a response.
public sealed record FileSummary(
    Guid Id,
    string Name,
    string MimeType,
    long SizeBytes,
    DateTime CreatedAt,
    int? Width = null,
    int? Height = null);
