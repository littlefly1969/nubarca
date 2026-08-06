namespace NubArca.Api.Files;

// Slice 75: one logical occurrence of a duplicated file.
// Returned by GET /api/files/{id}/duplicates — all active FileItems owned by
// the same user that point to the same underlying blob.
//
// Deliberately omits BlobObjectId, Sha256, StorageKey, OwnerUserId, and
// any raw metadata — those are storage-internal and must never reach a
// response. ParentFolderId is the public folder ID (already exposed in other
// DTOs/endpoints) and lets the client navigate to the file's location.
public sealed record DuplicateOccurrence(
    Guid FileItemId,
    string Name,
    Guid? ParentFolderId,
    string MimeType,
    long SizeBytes,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
