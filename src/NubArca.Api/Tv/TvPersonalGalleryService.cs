using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Albums;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Metadata;

namespace NubArca.Api.Tv;

// TV projection of the owner image gallery. The QUERY semantics (eligibility,
// filters, search, sorting, cursoring) are the authoritative shared ones —
// GalleryQueryParser + IFileItemService.ListImagesPageAsync — so results match
// the authenticated web gallery item-for-item. This class only projects rows
// into TV-safe DTOs (grant-gated /api/tv/personal media URLs, favorite state,
// curated metadata) and never re-implements filter/sort/paging logic.
public sealed class TvPersonalGalleryService : ITvPersonalGalleryService
{
    private readonly AppDbContext _db;
    private readonly IFileItemService _files;
    private readonly IMetadataService _metadata;
    private readonly IAlbumService _albums;
    private readonly PeopleService _people;
    private readonly GallerySemanticQueryService _semantic;

    public TvPersonalGalleryService(
        AppDbContext db,
        IFileItemService files,
        IMetadataService metadata,
        IAlbumService albums,
        PeopleService people,
        GallerySemanticQueryService semantic)
    {
        _db = db;
        _files = files;
        _metadata = metadata;
        _albums = albums;
        _people = people;
        _semantic = semantic;
    }

    public async Task<TvPersonalGalleryListResult> ListGalleryAsync(
        Guid ownerUserId, TvPersonalGalleryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!GalleryQueryParser.TryParseCommon(
            query.Limit, query.Q, query.Sort, query.Direction,
            query.Favorite, query.MinRating, query.HasGps,
            query.DateTakenFrom, query.DateTakenTo, query.CollapseDuplicates,
            query.IncludePeople, query.ExcludePeople, query.IncludePeopleMode,
            out var common, out var commonError))
        {
            return new TvPersonalGalleryListResult(null, commonError);
        }

        // PHYSICAL-FILTER-FIRST semantic path: when a visual semantic residual is
        // present, the physical filters build the candidate set and the active
        // text tower ranks INSIDE it (see GallerySemanticQueryService). The
        // semantic service owns its own (score, id) cursor + fingerprint, so we
        // do NOT bind the metadata-sort cursor here.
        if (!string.IsNullOrWhiteSpace(query.SemanticQuery))
        {
            var semanticFilters = common.Filters with
            {
                SemanticQuery = query.SemanticQuery,
                SemanticTopK = query.SemanticTopK,
            };

            GallerySemanticPage semanticPage;
            try
            {
                semanticPage = await _semantic.SearchAsync(
                    ownerUserId, common.Limit, query.Cursor, semanticFilters, cancellationToken);
            }
            catch (SemanticSearchCursorException)
            {
                return new TvPersonalGalleryListResult(
                    null, "'cursor' was issued for a different filter / query set.");
            }

            var semanticItems = await ProjectAsync(semanticPage.Items, cancellationToken);
            var status = !semanticPage.Available ? "unavailable"
                : semanticPage.StillIndexingManyItems ? "indexing" : "ok";
            return new TvPersonalGalleryListResult(
                new TvPersonalGalleryPageDto(
                    semanticItems, semanticPage.NextCursor, semanticPage.HasMore, semanticPage.TotalCount,
                    SemanticActive: true, SemanticTopK: semanticPage.SemanticTopK, SemanticStatus: status),
                null);
        }

        // The TV surface exposes no folder/album/similar dimensions — the
        // common filters ARE the final filters, and the cursor binds to them.
        if (!GalleryQueryParser.TryBindCursor(
            query.Cursor, common.Filters, common.Sort, common.Direction,
            out var cursor, out var cursorError))
        {
            return new TvPersonalGalleryListResult(null, cursorError);
        }

        var page = await _files.ListImagesPageAsync(
            ownerUserId, common.Limit, cursor, common.Filters,
            common.Sort, common.Direction, cancellationToken);

        var items = await ProjectAsync(page.Items, cancellationToken);

        return new TvPersonalGalleryListResult(
            new TvPersonalGalleryPageDto(items, page.NextCursor, page.HasMore, page.TotalCount), null);
    }

    public async Task<TvPersonalVideoListResult> ListVideosAsync(
        Guid ownerUserId, TvPersonalVideoQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit ?? 50, 1, 100);
        var filters = new ImageFilters();
        const ImageSortField sort = ImageSortField.Created;
        const ImageSortDirection direction = ImageSortDirection.Desc;

        ImageCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            if (!ImageCursor.TryParse(query.Cursor, out var parsed))
                return new TvPersonalVideoListResult(null, "'cursor' is malformed.");
            if (!parsed.MatchesSort(sort, direction))
                return new TvPersonalVideoListResult(null, "'cursor' was issued for a different sort/direction.");
            if (!parsed.MatchesFilter(filters.Fingerprint()))
                return new TvPersonalVideoListResult(null, "'cursor' was issued for a different filter / query set.");
            cursor = parsed;
        }

        var page = await _files.ListVideosPageAsync(
            ownerUserId, limit, cursor, filters, sort, direction, cancellationToken);
        // ListVideosPageAsync is the shared web/TV projection source and already
        // exposes rotation-aware DISPLAY dimensions.
        var items = page.Items.Select(v => new TvPersonalVideoItemDto(
            v.Id, v.Name, v.Width, v.Height, v.CreatedAt, v.DurationSeconds,
            v.VideoCodec, v.AudioCodec, v.HasAudio,
            PosterUrl(v.Id), VideoUrl(v.Id), PreviewStripUrl(v.Id), v.OccurrenceCount)).ToList();
        return new TvPersonalVideoListResult(
            new TvPersonalVideoPageDto(items, page.NextCursor, page.HasMore, page.TotalCount), null);
    }

    // Projects gallery rows into TV-safe DTOs with one bounded favorite lookup
    // per PAGE (not per row). Only rows with a user-metadata row AND
    // IsFavorite=true need fetching — absence means not favorite, matching the
    // web filter semantics.
    private async Task<List<TvPersonalGalleryItemDto>> ProjectAsync(
        IReadOnlyList<ImageItem> rows, CancellationToken cancellationToken)
    {
        var pageIds = rows.Select(i => i.Id).ToList();
        var favoriteIds = pageIds.Count == 0
            ? new HashSet<Guid>()
            : (await _db.FileItemUserMetadata.AsNoTracking()
                .Where(u => pageIds.Contains(u.FileItemId) && u.IsFavorite)
                .Select(u => u.FileItemId)
                .ToListAsync(cancellationToken)).ToHashSet();

        // ImageItem already carries the same EXIF-aware DISPLAY dimensions used
        // by the frontend Library/Album projection.
        return rows
            .Select(i => new TvPersonalGalleryItemDto(
                i.Id,
                i.Name,
                MediaType: "image",
                i.Width,
                i.Height,
                i.CreatedAt,
                ThumbnailUrl(i.Id),
                PreviewUrl(i.Id),
                Favorite: favoriteIds.Contains(i.Id),
                i.OccurrenceCount))
            .ToList();
    }

    public async Task<IReadOnlyList<TvPersonalPersonDto>> ListPeopleAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        // The SAME person identities/order the web people filter offers, minus
        // everything the TV must not see (representative face refs/boxes).
        var people = await _people.ListPeopleAsync(ownerUserId, cancellationToken);
        return people
            .Select(p => new TvPersonalPersonDto(p.PersonId, p.Name, p.FaceCount))
            .ToList();
    }

    public async Task<IReadOnlyList<TvPersonalAlbumDto>> ListAlbumsAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var albums = await _albums.ListAsync(ownerUserId, cancellationToken);
        return albums
            .Select(a => new TvPersonalAlbumDto(a.Id, a.Name, a.ItemCount))
            .ToList();
    }

    public async Task<TvPersonalMediaInfoDto?> GetMediaInfoAsync(
        Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken = default)
    {
        // Strictly gallery-scoped: an id the gallery would never list (foreign,
        // deleted, non-image, media-library-hidden, vaulted) is a generic 404.
        if (!await _files.IsGalleryImageAsync(ownerUserId, fileItemId, cancellationToken))
        {
            return null;
        }

        var meta = await _metadata.GetFileMetadataAsync(ownerUserId, fileItemId, cancellationToken);
        if (meta is null)
        {
            return null;
        }

        var e = meta.Blob.Embedded;
        return new TvPersonalMediaInfoDto(
            meta.Id,
            meta.Name,
            meta.SizeBytes,
            meta.Blob.Width,
            meta.Blob.Height,
            meta.Effective.DateTaken,
            meta.Effective.DateTakenSource,
            e?.CameraMake,
            e?.CameraModel,
            e?.LensModel,
            e?.Iso,
            e?.Aperture,
            e?.ExposureTime,
            e?.FocalLength,
            HasGps: e?.HasGps ?? false,
            meta.User.Title,
            meta.User.Description,
            meta.User.Tags,
            meta.User.Rating,
            meta.User.Favorite,
            meta.Effective.Location);
    }

    private static string ThumbnailUrl(Guid id) => $"/api/tv/personal/media/{id}/thumbnail";
    private static string PreviewUrl(Guid id) => $"/api/tv/personal/media/{id}/preview";
    private static string PosterUrl(Guid id) => $"/api/tv/personal/media/{id}/poster";
    private static string VideoUrl(Guid id) => $"/api/tv/personal/media/{id}/video";
    private static string PreviewStripUrl(Guid id) => $"/api/tv/personal/media/{id}/video-preview-strip";
}
