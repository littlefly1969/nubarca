namespace NubArca.Api.Albums;

// One tile of an album card's cover mosaic (up to four). `Kind` is
// "image" | "video"; `ThumbnailUrl` is the small thumbnail for images and the
// poster for videos. Never a Personal or Excluded item, never storage internals.
public record AlbumCoverItem(
    Guid FileItemId,
    string Kind,
    string ThumbnailUrl);

// Slice 5: the album card gained per-kind counts and a cover mosaic for the
// modernized /albums list. `ItemCount` stays the raw membership count (backward
// compatible). `PhotoCount`/`VideoCount` are ACTIVE (in-library) members by
// kind; `ExcludedCount` is members the owner moved out of the library.
// `CoverItems` is up to four Active members (no Personal, no Excluded).
public record AlbumSummary(
    Guid Id,
    string Name,
    string? Description,
    int ItemCount,
    bool ShowOnTv,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int PhotoCount,
    int VideoCount,
    int ExcludedCount,
    IReadOnlyList<AlbumCoverItem> CoverItems);

public record AlbumDetail(
    Guid Id,
    string Name,
    string? Description,
    bool ShowOnTv,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record AlbumItemSummary(
    Guid FileItemId,
    string Name,
    string MimeType,
    long SizeBytes,
    DateTime AddedAt,
    string? ThumbnailUrl);

// Bulk add/remove request. `FileItemIds` is the full requested set; duplicates
// and foreign/missing ids are handled idempotently and never leak existence.
public record BulkAlbumItemsRequest(IReadOnlyList<Guid> FileItemIds);

// Safe bulk summary. Carries only counts — never which specific ids were
// foreign/missing (that would leak existence of another owner's files). A
// missing album (foreign/not found) is signalled by a null result upstream.
public record BulkAlbumItemsResult(
    int Requested,
    int Succeeded,
    int Skipped);
