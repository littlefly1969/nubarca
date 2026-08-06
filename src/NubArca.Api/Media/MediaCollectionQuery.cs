using NubArca.Api.Files;
using NubArca.Api.MediaLibrary;

namespace NubArca.Api.Media;

// Slice 5: the unified media-collection query contract shared by
//   GET /api/media                    (Source = Library)
//   GET /api/albums/{albumId}/media   (Source = Album)
// The ONLY difference between the two surfaces is the source predicate; every
// other dimension (kind, scope, filters, sort, cursor, limit) is identical.

// Discriminated collection source. `Album` carries an owner-validated album id;
// the query narrows to that album's members. `Library` is the whole owner-scoped
// media library.
public abstract record MediaCollectionSource
{
    public sealed record Library : MediaCollectionSource;

    public sealed record Album(Guid AlbumId) : MediaCollectionSource;
}

// Common filters — valid for every kind (the only filters offered in "Tutti").
public sealed record CommonMediaFilters(
    string? Query = null,
    bool? Favorite = null,
    int? MinRating = null,
    DateTime? DateTakenFrom = null,
    DateTime? DateTakenTo = null,
    // Library-only: 'assigned' / 'unassigned' / 'any'. Rejected on the album
    // source (every album item is a member) — see MediaCollectionQueryService.
    AlbumMembershipFilter AlbumMembership = AlbumMembershipFilter.Any)
{
    public bool HasAny =>
        !string.IsNullOrWhiteSpace(Query)
        || Favorite is not null
        || MinRating is not null
        || DateTakenFrom is not null
        || DateTakenTo is not null
        || AlbumMembership != AlbumMembershipFilter.Any;
}

// Photo-only filters — valid ONLY with kind=image. `HasAny` drives the
// kind/filter compatibility check (a photo filter under kind=all|video → 400).
public sealed record PhotoMediaFilters(
    bool? HasGps = null,
    bool CollapseDuplicates = false,
    Guid? SimilarTo = null,
    IReadOnlyList<Guid>? IncludePeople = null,
    IReadOnlyList<Guid>? ExcludePeople = null,
    PeopleFilterMode IncludePeopleMode = PeopleFilterMode.All)
{
    public bool HasAny =>
        HasGps is not null
        || CollapseDuplicates
        || SimilarTo is not null
        || IncludePeople is { Count: > 0 }
        || ExcludePeople is { Count: > 0 };
}

// Video-only filters — valid ONLY with kind=video.
public sealed record VideoMediaFilters(
    double? DurationMinSeconds = null,
    double? DurationMaxSeconds = null,
    int? MinHeight = null,
    string? Codec = null,
    bool? HasAudio = null)
{
    public bool HasAny =>
        DurationMinSeconds is not null
        || DurationMaxSeconds is not null
        || MinHeight is not null
        || !string.IsNullOrWhiteSpace(Codec)
        || HasAudio is not null;
}

public sealed record MediaCollectionQuery(
    MediaCollectionSource Source,
    MediaLibraryScope LibraryScope,
    MediaKindScope MediaKind,
    CommonMediaFilters Common,
    PhotoMediaFilters? Photo,
    VideoMediaFilters? Video,
    ImageSortField Sort,
    ImageSortDirection Direction,
    string? Cursor,
    int Limit);

// Outcome of a unified query. `Ok` carries the page; the error statuses map to a
// single HTTP status each at the endpoint so no internal detail leaks.
public enum MediaCollectionStatus
{
    Ok,
    AlbumNotFound,      // 404 — foreign/missing album (no existence leak)
    IncompatibleFilters, // 400 — kind/filter mismatch or album-membership in album
    BadCursor,          // 400 — malformed / wrong sort / wrong filter fingerprint
    InvalidLimit,       // 400 — limit out of [1, 100]
}

public sealed record MediaCollectionResult(
    MediaCollectionStatus Status,
    MediaPage? Page,
    string? Error)
{
    public static MediaCollectionResult Success(MediaPage page) =>
        new(MediaCollectionStatus.Ok, page, null);

    public static MediaCollectionResult Failure(MediaCollectionStatus status, string error) =>
        new(status, null, error);
}
