namespace NubArca.Api.Files;

// Slice 5: unified media-workspace projection for the /api/media and
// /api/albums/{id}/media surfaces. One discriminated DTO carries both images
// and videos so the "Tutti" tab can render a mixed, server-ordered grid without
// the client merging two lists. `Kind` is the discriminator ("image" | "video");
// kind-specific fields are null on the other kind.
//
// Safety mirrors ImageItem/VideoItem: NO OwnerUserId, BlobObjectId, StorageKey,
// ParentFolderId, DeletedAt, SHA/hash, embeddings or raw metadata are ever
// present. GPS is exposed as presence-only (HasGps), never coordinates.
public sealed record MediaItem(
    Guid Id,
    // "image" | "video" — the DTO discriminator the frontend switches on.
    string Kind,
    // Logical file name (unchanged by a title; the download/diagnostic name).
    string Name,
    // Owner-scoped user title (FileItemUserMetadata.Title); null when unset.
    string? Title,
    // Title when present, else Name (MediaDisplayName rule).
    string DisplayName,
    string MimeType,
    long SizeBytes,
    int? Width,
    int? Height,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    // Effective capture instant (user override → embedded → upload). Never null
    // in practice (falls back to CreatedAt), but modelled nullable for the DTO.
    DateTime? TakenAt,
    bool Favorite,
    int? Rating,
    // Small thumbnail (image) or poster (video) — the grid card image source.
    string ThumbnailUrl,
    // Library-wide occurrence count for this owner's identical blobs (1 when not
    // collapsing / no duplicates). Never exposes the blob id or SHA.
    int OccurrenceCount = 1,
    // ---- image-only (null on videos) ----
    bool? HasGps = null,
    // ---- video-only (null on images) ----
    string? PosterUrl = null,
    double? DurationSeconds = null,
    string? VideoCodec = null,
    bool? HasAudio = null,
    string? PosterSource = null,
    string? PreviewStripUrl = null)
{
    public bool HasDuplicates => OccurrenceCount > 1;
}

// A page of unified media items (cursor-paginated). `TotalCount` is the
// server-authoritative filtered total for the CURRENT query (paging-independent,
// duplicate-collapse aware). `PhotoCount`/`VideoCount` split that total by kind
// so the "Tutti | Foto | Video" tabs can show counts without extra round-trips;
// on a single-kind query the other count is 0.
public sealed record MediaPage(
    IReadOnlyList<MediaItem> Items,
    string? NextCursor,
    bool HasMore,
    int TotalCount,
    int PhotoCount,
    int VideoCount);

// HTTP response envelope for GET /api/media and GET /api/albums/{id}/media.
public sealed record MediaListResponse(
    IReadOnlyList<MediaItem> Items,
    int Limit,
    int Count,
    string? NextCursor,
    bool HasMore,
    int Total,
    int PhotoCount,
    int VideoCount);
