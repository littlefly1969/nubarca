using NubArca.Api.Files;

namespace NubArca.Api.Media;

// Owner-private, aggregate-only navigation. Keys are month numbers (YYYY-MM)
// or the exact initial of the existing display-name sort key (n:<initial>).
public sealed record MediaNavigationBucket(string Key, int Count);
public sealed record MediaNavigationIndex(IReadOnlyList<MediaNavigationBucket> Buckets);
public sealed record MediaNavigationWindow(
    IReadOnlyList<MediaItem> Items, string? NextCursor, string? PreviousCursor);
public sealed record MediaNavigationResult(MediaCollectionStatus Status, object? Data, string? Error);

public sealed partial class MediaCollectionQueryService
{
    private async Task<MediaNavigationResult?> ValidateNavigationAsync(
        Guid ownerUserId, MediaCollectionQuery query, CancellationToken cancellationToken)
    {
        var error = await ValidateAsync(ownerUserId, query, cancellationToken);
        if (error is not null) return new(error.Status, null, error.Error);
        if (query.Sort == ImageSortField.Size || query.Photo?.SimilarTo is not null)
            return new(MediaCollectionStatus.IncompatibleFilters, null, "Navigation requires date or name ordering without similarity ranking.");
        return null;
    }

    public async Task<MediaNavigationResult> NavigationAsync(
        Guid ownerUserId, MediaCollectionQuery query, CancellationToken cancellationToken)
    {
        var error = await ValidateNavigationAsync(ownerUserId, query, cancellationToken);
        if (error is not null) return error;
        if (query.Cursor is not null) return new(MediaCollectionStatus.BadCursor, null, "An index cannot have a cursor.");
        var filters = BuildFilters(query, (query.Source as MediaCollectionSource.Album)?.AlbumId);
        return new(MediaCollectionStatus.Ok,
            await _files.MediaNavigationAsync(ownerUserId, filters, query.MediaKind, query.Sort, query.Direction, cancellationToken), null);
    }

    public async Task<MediaNavigationResult> WindowAsync(
        Guid ownerUserId, MediaCollectionQuery query, string? target, bool before, CancellationToken cancellationToken)
    {
        var error = await ValidateNavigationAsync(ownerUserId, query, cancellationToken);
        if (error is not null) return error;
        var filters = BuildFilters(query, (query.Source as MediaCollectionSource.Album)?.AlbumId);
        ImageCursor? cursor = null;
        if (query.Cursor is not null)
        {
            if (target is not null || !ImageCursor.TryParse(query.Cursor, out cursor)
                || !cursor.MatchesSort(query.Sort, query.Direction)
                || cursor.PrimaryKind != (query.Sort == ImageSortField.Name ? ImageCursor.KindString : ImageCursor.KindDate)
                || !cursor.MatchesFilter(query.MediaKind.MediaCursorFingerprint(filters)))
                return new(MediaCollectionStatus.BadCursor, null, "Invalid or mismatched navigation cursor.");
        }
        else if (before || !FileItemService.IsNavigationTarget(query.Sort, target))
            return new(MediaCollectionStatus.BadCursor, null, "Invalid navigation target.");

        var window = await _files.MediaWindowAsync(ownerUserId, filters, query.MediaKind,
            query.Sort, query.Direction, query.Limit, target, cursor, before, cancellationToken);
        // A target may have disappeared since the index was read. Nothing else
        // is substituted: the client keeps its current wall and refreshes the index.
        return window is null
            ? new(MediaCollectionStatus.AlbumNotFound, null, "Navigation target no longer available.")
            : new(MediaCollectionStatus.Ok, window, null);
    }
}
