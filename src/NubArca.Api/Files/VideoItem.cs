namespace NubArca.Api.Files;

// Slice 86: gallery projection for an active video file. PosterUrl points at
// the existing GET /api/files/{id}/poster endpoint (synthetic by default,
// optional FFmpeg) which generates on demand; playback uses
// GET /api/files/{id}/video (Range-enabled). Deliberately omits OwnerUserId,
// BlobObjectId, StorageKey, ParentFolderId, DeletedAt — storage-internal
// concerns that must never reach a response. Mirrors ImageItem's safety.
public sealed record VideoItem(
    Guid Id,
    // Logical file name — unchanged by a title (see ImageItem).
    string Name,
    // Owner-scoped user title (FileItemUserMetadata.Title); null when unset.
    string? Title,
    // Title when present, else Name — same MediaDisplayName rule as the photos.
    string DisplayName,
    string MimeType,
    long SizeBytes,
    int? Width,
    int? Height,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string PosterUrl,
    // ffprobe-derived, sourced from BlobMetadata (null when not probed yet or no
    // provider). Width/Height above are also filled from BlobMetadata for videos
    // (FileItem-level dims are null for video, which the signature detector does
    // not measure).
    double? DurationSeconds,
    // Duplicate collapsing, same semantics as ImageItem (library-wide count of
    // active FileItems owned by this user sharing the same blob). Default 1.
    int OccurrenceCount = 1,
    // Slice 95: poster provenance ("synthetic" | "ffmpeg" | ... | "unknown";
    // null when no poster row exists yet). Lets the UI mark placeholder
    // posters so they are not mistaken for real frames. Carries no internals.
    string? PosterSource = null,
    // Curated video-probe fields for the card/badges (non-sensitive). Null until
    // probed.
    string? VideoCodec = null,
    string? AudioCodec = null,
    bool HasAudio = false,
    double? FrameRate = null,
    string? PreviewStripUrl = null)
{
    public bool HasDuplicates => OccurrenceCount > 1;
}

// Page of video gallery items (cursor-paginated). Mirrors ImagePage, including
// the server-authoritative `TotalCount` for the current filter set.
public sealed record VideoPage(
    IReadOnlyList<VideoItem> Items,
    string? NextCursor,
    bool HasMore,
    int TotalCount);

// HTTP response envelope for GET /api/videos. Mirrors ImageListResponse so the
// frontend can reuse the same cursor-pagination handling.
public sealed record VideoListResponse(
    IReadOnlyList<VideoItem> Items,
    int Limit,
    int Offset,
    int Count,
    string? NextCursor,
    bool HasMore);
