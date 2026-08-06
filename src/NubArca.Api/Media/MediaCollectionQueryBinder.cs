using NubArca.Api.Files;

namespace NubArca.Api.Media;

// Slice 5: turns the raw query-string parameters of GET /api/media and
// GET /api/albums/{albumId}/media into a typed MediaCollectionQuery. This is the
// ONE place the unified wire vocabulary is parsed, so both endpoints stay
// identical. It performs FORMAT validation only (vocabulary, ranges, CSV shape)
// and returns a single client-safe error on the first problem; SEMANTIC
// validation (kind/filter compatibility, album ownership, cursor binding, limit
// bounds) lives in MediaCollectionQueryService so it too cannot drift.
//
// Photo and video groups are populated from whatever params are present
// REGARDLESS of kind — the service is what rejects an incompatible combination
// (e.g. a photo filter under kind=all → 400) rather than silently dropping it.
public static class MediaCollectionQueryBinder
{
    public const int DefaultLimit = 50;

    public static bool TryBind(
        MediaCollectionSource source,
        int? limit,
        string? cursor,
        string? scope,
        string? kind,
        string? q,
        bool? favorite,
        int? minRating,
        DateTime? dateTakenFrom,
        DateTime? dateTakenTo,
        string? albumMembership,
        string? sort,
        string? direction,
        bool? hasGps,
        bool? collapseDuplicates,
        Guid? similarTo,
        string? includePeople,
        string? excludePeople,
        string? includePeopleMode,
        double? durationMin,
        double? durationMax,
        int? minHeight,
        string? codec,
        bool? hasAudio,
        out MediaCollectionQuery query,
        out string? error)
    {
        query = default!;
        error = null;

        if (!MediaKindScopeParser.TryParse(kind, out var mediaKind))
        {
            error = "'kind' must be one of: all, image, video.";
            return false;
        }
        if (!GalleryQueryParser.TryParseMediaScope(scope, out var libraryScope, out var scopeError))
        {
            error = scopeError;
            return false;
        }
        if (!ImageSort.TryParseField(sort, out var sortField))
        {
            error = "'sort' must be one of: created, name, size, datetaken.";
            return false;
        }
        if (!ImageSort.TryParseDirection(direction, out var sortDirection))
        {
            error = "'direction' must be 'asc' or 'desc'.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(q) && q.Length > GalleryQueryParser.MaxQueryLength)
        {
            error = $"'q' must be {GalleryQueryParser.MaxQueryLength} characters or fewer.";
            return false;
        }
        if (minRating is int r && (r < 0 || r > 5))
        {
            error = "'minRating' must be between 0 and 5.";
            return false;
        }
        if (dateTakenFrom is DateTime df && dateTakenTo is DateTime dt && df > dt)
        {
            error = "'dateTakenFrom' must be earlier than or equal to 'dateTakenTo'.";
            return false;
        }
        if (!GalleryQueryParser.TryParseAlbumMembership(albumMembership, out var membership, out var membershipError))
        {
            error = membershipError;
            return false;
        }

        // Photo params.
        if (!ImagePeopleFilter.TryParseIds(includePeople, GalleryQueryParser.MaxPeopleFilterIds, out var includeIds))
        {
            error = "'includePeople' must be a comma-separated list of ids.";
            return false;
        }
        if (!ImagePeopleFilter.TryParseIds(excludePeople, GalleryQueryParser.MaxPeopleFilterIds, out var excludeIds))
        {
            error = "'excludePeople' must be a comma-separated list of ids.";
            return false;
        }
        var peopleMode = string.Equals(includePeopleMode, "any", StringComparison.OrdinalIgnoreCase)
            ? PeopleFilterMode.Any
            : PeopleFilterMode.All;

        // Video params.
        if (durationMin is double dmin && dmin < 0)
        {
            error = "'durationMin' must be non-negative.";
            return false;
        }
        if (durationMax is double dmax && dmax < 0)
        {
            error = "'durationMax' must be non-negative.";
            return false;
        }
        if (durationMin is double a && durationMax is double b && a > b)
        {
            error = "'durationMin' must be <= 'durationMax'.";
            return false;
        }
        if (minHeight is int mh && mh < 0)
        {
            error = "'minHeight' must be non-negative.";
            return false;
        }
        string? effectiveCodec = null;
        if (!string.IsNullOrWhiteSpace(codec))
        {
            if (codec.Length > 64)
            {
                error = "'codec' must be 64 characters or fewer.";
                return false;
            }
            effectiveCodec = codec.Trim();
        }

        var common = new CommonMediaFilters(
            Query: string.IsNullOrWhiteSpace(q) ? null : q,
            Favorite: favorite,
            MinRating: minRating,
            DateTakenFrom: dateTakenFrom,
            DateTakenTo: dateTakenTo,
            AlbumMembership: membership);

        var photo = new PhotoMediaFilters(
            HasGps: hasGps,
            CollapseDuplicates: collapseDuplicates ?? false,
            SimilarTo: similarTo,
            IncludePeople: includeIds,
            ExcludePeople: excludeIds,
            IncludePeopleMode: peopleMode);

        var video = new VideoMediaFilters(
            DurationMinSeconds: durationMin,
            DurationMaxSeconds: durationMax,
            MinHeight: minHeight,
            Codec: effectiveCodec,
            HasAudio: hasAudio);

        query = new MediaCollectionQuery(
            source, libraryScope, mediaKind, common,
            photo.HasAny ? photo : null,
            video.HasAny ? video : null,
            sortField, sortDirection, cursor,
            limit ?? DefaultLimit);
        return true;
    }
}
