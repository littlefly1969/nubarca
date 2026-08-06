namespace NubArca.Api.Files;

// Listing projection for soft-deleted FileItem rows shown in the trash UI.
// Carries the parent folder id so a client can show "originally located in
// ..." context. Deliberately omits OwnerUserId, BlobObjectId, StorageKey —
// those are storage-internal concerns that must never reach a response.
public sealed record FileTrashSummary(
    Guid Id,
    string Name,
    string MimeType,
    long SizeBytes,
    Guid? ParentFolderId,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime DeletedAt,
    int? Width = null,
    int? Height = null);
