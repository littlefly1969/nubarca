namespace NubArca.Api.Tv;

// Raw wire inputs of the TV personal-gallery list request. Parsed/validated by
// the shared GalleryQueryParser so the semantics are byte-identical to the
// authenticated web gallery (/api/images) common surface.
public sealed record TvPersonalGalleryQuery(
    int? Limit = null,
    string? Cursor = null,
    string? Q = null,
    string? Sort = null,
    string? Direction = null,
    bool? Favorite = null,
    int? MinRating = null,
    bool? HasGps = null,
    DateTime? DateTakenFrom = null,
    DateTime? DateTakenTo = null,
    bool? CollapseDuplicates = null,
    string? IncludePeople = null,
    string? ExcludePeople = null,
    string? IncludePeopleMode = null,
    // Slice 100: optional VISUAL semantic residual + server-clamped Top-K. When
    // SemanticQuery is set the gallery becomes physical-filter-first + semantic-
    // ranked (see GallerySemanticQueryService). Absent → unchanged behaviour.
    string? SemanticQuery = null,
    int? SemanticTopK = null);

// Page XOR Error: a non-null Error is a client-safe validation message the
// endpoint returns as 400.
public sealed record TvPersonalGalleryListResult(
    TvPersonalGalleryPageDto? Page,
    string? Error);

public sealed record TvPersonalVideoQuery(int? Limit = null, string? Cursor = null);

public sealed record TvPersonalVideoListResult(
    TvPersonalVideoPageDto? Page,
    string? Error);

public interface ITvPersonalGalleryService
{
    Task<TvPersonalGalleryListResult> ListGalleryAsync(
        Guid ownerUserId, TvPersonalGalleryQuery query,
        CancellationToken cancellationToken = default);

    Task<TvPersonalVideoListResult> ListVideosAsync(
        Guid ownerUserId, TvPersonalVideoQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TvPersonalPersonDto>> ListPeopleAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TvPersonalAlbumDto>> ListAlbumsAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default);

    // Null for files that are missing, foreign, deleted, or not currently
    // gallery-eligible (all collapse to 404 — no existence leak).
    Task<TvPersonalMediaInfoDto?> GetMediaInfoAsync(
        Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken = default);
}
