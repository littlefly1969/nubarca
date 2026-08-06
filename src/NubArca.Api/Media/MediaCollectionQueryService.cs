using NubArca.Api.Albums;
using NubArca.Api.Files;

namespace NubArca.Api.Media;

// Slice 5: the single application service behind GET /api/media and
// GET /api/albums/{albumId}/media. It owns the cross-cutting rules both surfaces
// must share so they cannot drift:
//   * limit bounds (1..100),
//   * kind/filter compatibility (photo filters ⇒ kind=image, video filters ⇒
//     kind=video; neither may accompany kind=all),
//   * album-membership is library-only,
//   * album ownership (foreign/missing album ⇒ 404, no existence leak),
//   * cursor binding (malformed / wrong sort / wrong filter fingerprint ⇒ 400).
// The actual predicate composition + projection is delegated to the shared,
// battle-tested gallery engine (IFileItemService.ListMediaPageAsync), so the
// unified surface reuses the same owner-isolation, Private-Vault exclusion,
// media-library scope, cursor seek and ordering as the legacy galleries.
public interface IMediaCollectionQueryService
{
    Task<MediaCollectionResult> QueryAsync(
        Guid ownerUserId,
        MediaCollectionQuery query,
        CancellationToken cancellationToken);
}

public sealed class MediaCollectionQueryService : IMediaCollectionQueryService
{
    public const int MinLimit = 1;
    public const int MaxLimit = 100;

    private readonly IFileItemService _files;
    private readonly IAlbumService _albums;

    public MediaCollectionQueryService(IFileItemService files, IAlbumService albums)
    {
        _files = files;
        _albums = albums;
    }

    public async Task<MediaCollectionResult> QueryAsync(
        Guid ownerUserId,
        MediaCollectionQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit < MinLimit || query.Limit > MaxLimit)
        {
            return MediaCollectionResult.Failure(
                MediaCollectionStatus.InvalidLimit,
                $"'limit' must be between {MinLimit} and {MaxLimit}.");
        }

        // Kind/filter compatibility: incompatible filters are rejected, never
        // silently applied to a mismatched kind.
        if (query.Photo is { HasAny: true } && query.MediaKind != MediaKindScope.Image)
        {
            return MediaCollectionResult.Failure(
                MediaCollectionStatus.IncompatibleFilters,
                "Photo filters are only valid with kind=image.");
        }
        if (query.Video is { HasAny: true } && query.MediaKind != MediaKindScope.Video)
        {
            return MediaCollectionResult.Failure(
                MediaCollectionStatus.IncompatibleFilters,
                "Video filters are only valid with kind=video.");
        }

        // Album-membership is meaningless inside a specific album.
        if (query.Source is MediaCollectionSource.Album
            && query.Common.AlbumMembership != AlbumMembershipFilter.Any)
        {
            return MediaCollectionResult.Failure(
                MediaCollectionStatus.IncompatibleFilters,
                "'albumMembership' is not valid inside an album.");
        }

        // Album source: owner-validate up front so a foreign/missing album is a
        // clean 404 (no existence leak) rather than a silently-empty page.
        Guid? albumId = null;
        if (query.Source is MediaCollectionSource.Album album)
        {
            var detail = await _albums.GetByIdAsync(album.AlbumId, ownerUserId, cancellationToken);
            if (detail is null)
            {
                return MediaCollectionResult.Failure(
                    MediaCollectionStatus.AlbumNotFound, "Album not found.");
            }
            albumId = album.AlbumId;
        }

        var filters = BuildFilters(query, albumId);

        ImageCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            if (!ImageCursor.TryParse(query.Cursor, out var parsed))
            {
                return MediaCollectionResult.Failure(
                    MediaCollectionStatus.BadCursor, "'cursor' is malformed.");
            }
            if (!parsed.MatchesSort(query.Sort, query.Direction))
            {
                return MediaCollectionResult.Failure(
                    MediaCollectionStatus.BadCursor, "'cursor' was issued for a different sort/direction.");
            }
            if (!parsed.MatchesFilter(query.MediaKind.MediaCursorFingerprint(filters)))
            {
                return MediaCollectionResult.Failure(
                    MediaCollectionStatus.BadCursor, "'cursor' was issued for a different filter / query set.");
            }
            cursor = parsed;
        }

        var page = await _files.ListMediaPageAsync(
            ownerUserId, query.Limit, cursor, filters, query.MediaKind,
            query.Sort, query.Direction, cancellationToken);

        return MediaCollectionResult.Success(page);
    }

    // Compose ImageFilters from the typed query. Kind-specific groups are folded
    // in ONLY for their matching kind, mirroring the compatibility check above —
    // so even a defensively-populated Photo/Video group cannot reach the engine
    // under the wrong kind.
    private static ImageFilters BuildFilters(MediaCollectionQuery query, Guid? albumId)
    {
        var c = query.Common;
        var filters = new ImageFilters
        {
            Query = c.Query,
            Favorite = c.Favorite,
            MinRating = c.MinRating,
            DateTakenFrom = c.DateTakenFrom,
            DateTakenTo = c.DateTakenTo,
            AlbumId = albumId,
            AlbumMembership = query.Source is MediaCollectionSource.Album
                ? AlbumMembershipFilter.Any
                : c.AlbumMembership,
            Scope = query.LibraryScope,
        };

        if (query.MediaKind == MediaKindScope.Image && query.Photo is { } p)
        {
            filters = filters with
            {
                HasGps = p.HasGps,
                CollapseDuplicates = p.CollapseDuplicates,
                SimilarToFileId = p.SimilarTo,
                IncludePersonIds = p.IncludePeople,
                ExcludePersonIds = p.ExcludePeople,
                IncludePeopleMode = p.IncludePeopleMode,
            };
        }
        else if (query.MediaKind == MediaKindScope.Video && query.Video is { } v)
        {
            filters = filters with
            {
                DurationMinSeconds = v.DurationMinSeconds,
                DurationMaxSeconds = v.DurationMaxSeconds,
                MinHeight = v.MinHeight,
                VideoCodec = v.Codec,
                HasAudio = v.HasAudio,
            };
        }

        return filters;
    }
}
