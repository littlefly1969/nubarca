namespace NubArca.Api.Files;

// Shared parsing/validation for the gallery query surface. This is the single
// place the "common" gallery request semantics live: limit clamping, q length,
// sort/direction vocabulary, filter validation, people-id CSV parsing, and
// cursor binding. Both the authenticated web gallery (/api/images) and the TV
// personal-gallery projection (/api/tv/personal/gallery) parse through here so
// the two surfaces cannot drift apart. Endpoint-specific dimensions (folder,
// album, similar-to restriction, legacy offset) stay in the endpoints — they
// compose the returned ImageFilters before binding the cursor.
public static class GalleryQueryParser
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxPeopleFilterIds = 50;
    // Cap matches the FileItem.Name max length (255) plus a little slack.
    public const int MaxQueryLength = 256;

    public sealed record CommonQuery(
        int Limit,
        ImageFilters Filters,
        ImageSortField Sort,
        ImageSortDirection Direction);

    // Parses everything except the cursor (which must be bound AFTER the caller
    // has composed any endpoint-specific filter dimensions). Returns false with
    // a client-safe `error` on the first invalid input.
    public static bool TryParseCommon(
        int? limit,
        string? q,
        string? sort,
        string? direction,
        bool? favorite,
        int? minRating,
        bool? hasGps,
        DateTime? dateTakenFrom,
        DateTime? dateTakenTo,
        bool? collapseDuplicates,
        string? includePeople,
        string? excludePeople,
        string? includePeopleMode,
        out CommonQuery parsed,
        out string? error)
    {
        parsed = default!;
        error = null;

        var effectiveLimit = limit ?? DefaultLimit;
        if (effectiveLimit < 1) effectiveLimit = 1;
        if (effectiveLimit > MaxLimit) effectiveLimit = MaxLimit;

        // Whitespace q is equivalent to no q. A non-whitespace q over the length
        // cap returns 400 (the alternative — silently clamping — could surprise
        // a caller who pastes a huge identifier and expects a clean miss).
        string? effectiveQuery = null;
        if (!string.IsNullOrWhiteSpace(q))
        {
            if (q.Length > MaxQueryLength)
            {
                error = $"'q' must be {MaxQueryLength} characters or fewer.";
                return false;
            }
            effectiveQuery = q;
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

        if (minRating is int r && (r < 0 || r > 5))
        {
            error = "'minRating' must be between 0 and 5.";
            return false;
        }
        if (dateTakenFrom is DateTime f && dateTakenTo is DateTime t && f > t)
        {
            error = "'dateTakenFrom' must be earlier than or equal to 'dateTakenTo'.";
            return false;
        }

        // People filters (owner-private). CSV of person ids; a foreign id simply
        // matches nothing (owner-scoped join), so no validation/existence-leak.
        if (!ImagePeopleFilter.TryParseIds(includePeople, MaxPeopleFilterIds, out var includePersonIds))
        {
            error = "'includePeople' must be a comma-separated list of ids.";
            return false;
        }
        if (!ImagePeopleFilter.TryParseIds(excludePeople, MaxPeopleFilterIds, out var excludePersonIds))
        {
            error = "'excludePeople' must be a comma-separated list of ids.";
            return false;
        }
        var peopleMode = string.Equals(includePeopleMode, "any", StringComparison.OrdinalIgnoreCase)
            ? PeopleFilterMode.Any
            : PeopleFilterMode.All;

        var filters = new ImageFilters
        {
            Query = effectiveQuery,
            Favorite = favorite,
            MinRating = minRating,
            HasGps = hasGps,
            DateTakenFrom = dateTakenFrom,
            DateTakenTo = dateTakenTo,
            CollapseDuplicates = collapseDuplicates ?? false,
            IncludePersonIds = includePersonIds,
            ExcludePersonIds = excludePersonIds,
            IncludePeopleMode = peopleMode,
        };

        parsed = new CommonQuery(effectiveLimit, filters, sortField, sortDirection);
        return true;
    }

    // Shared `albumMembership` vocabulary for the photo and video galleries.
    // Absent / empty → Any (no constraint). Anything outside the vocabulary is
    // rejected rather than silently treated as Any, so a typo cannot quietly
    // widen a gallery the user believes is filtered.
    public static bool TryParseAlbumMembership(
        string? value, out AlbumMembershipFilter membership, out string? error)
    {
        membership = AlbumMembershipFilter.Any;
        error = null;
        if (string.IsNullOrWhiteSpace(value)) return true;

        switch (value.Trim().ToLowerInvariant())
        {
            case "any":
                membership = AlbumMembershipFilter.Any;
                return true;
            case "assigned":
                membership = AlbumMembershipFilter.Assigned;
                return true;
            case "unassigned":
                membership = AlbumMembershipFilter.Unassigned;
                return true;
            default:
                error = "'albumMembership' must be one of: any, assigned, unassigned.";
                return false;
        }
    }

    // Slice 3: the media-library scope of a gallery view. Only 'active'
    // (default) and 'excluded' are accepted from the wire — 'all' is an
    // internal scope the ordinary galleries never expose.
    public static bool TryParseMediaScope(
        string? value, out NubArca.Api.MediaLibrary.MediaLibraryScope scope, out string? error)
    {
        scope = NubArca.Api.MediaLibrary.MediaLibraryScope.Active;
        error = null;
        if (string.IsNullOrWhiteSpace(value)) return true;

        switch (value.Trim().ToLowerInvariant())
        {
            case "active":
                scope = NubArca.Api.MediaLibrary.MediaLibraryScope.Active;
                return true;
            case "excluded":
                scope = NubArca.Api.MediaLibrary.MediaLibraryScope.Excluded;
                return true;
            default:
                error = "'mediaScope' must be one of: active, excluded.";
                return false;
        }
    }

    // Binds an opaque wire cursor against the FINAL composed filters + sort.
    // A malformed cursor, a sort/direction mismatch, or a filter-fingerprint
    // mismatch each yield a distinct client-safe error (never nonsense pages).
    // A null/whitespace cursor binds successfully to "first page".
    public static bool TryBindCursor(
        string? cursor,
        ImageFilters filters,
        ImageSortField sort,
        ImageSortDirection direction,
        out ImageCursor? parsed,
        out string? error)
    {
        parsed = null;
        error = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        if (!ImageCursor.TryParse(cursor, out var candidate))
        {
            error = "'cursor' is malformed.";
            return false;
        }
        if (!candidate.MatchesSort(sort, direction))
        {
            error = "'cursor' was issued for a different sort/direction.";
            return false;
        }
        if (!candidate.MatchesFilter(filters.Fingerprint()))
        {
            error = "'cursor' was issued for a different filter / query set.";
            return false;
        }

        parsed = candidate;
        return true;
    }
}
