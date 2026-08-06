namespace NubArca.Api.Files;

// Gallery projection for an active image file. ThumbnailUrl points at the
// existing GET /api/files/{id}/thumbnail endpoint; a client that hits it gets
// 200 + JPEG bytes when a thumbnail row exists, or 404 if thumbnail
// generation failed at upload time. Deliberately omits OwnerUserId,
// BlobObjectId, StorageKey, ParentFolderId, DeletedAt — those are storage-
// internal concerns that must never reach a response.
public sealed record ImageItem(
    Guid Id,
    // Logical file name — unchanged by a title, still the download/diagnostic
    // name and the thing the file browser shows.
    string Name,
    // Owner-scoped user title (FileItemUserMetadata.Title); null when unset.
    string? Title,
    // What the gallery renders: Title when present, else Name. See
    // MediaDisplayName — the same rule the metadata endpoint's
    // EffectiveMetadata.DisplayName uses.
    string DisplayName,
    string MimeType,
    long SizeBytes,
    int? Width,
    int? Height,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string ThumbnailUrl,
    // Slice 75: duplicate collapsing. When collapseDuplicates=true is used,
    // OccurrenceCount is the number of active FileItems owned by this user
    // that point to the same underlying blob (library-wide, not filtered).
    // Default 1 = no duplicates / collapsing not requested.
    // Never exposes SHA-256, BlobObjectId, StorageKey, or internal IDs.
    int OccurrenceCount = 1)
{
    public bool HasDuplicates => OccurrenceCount > 1;
}
