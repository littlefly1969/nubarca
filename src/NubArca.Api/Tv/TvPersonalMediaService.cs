using NubArca.Api.Albums;
using NubArca.Api.Files;
using NubArca.Api.Media;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Media.Semantic;

namespace NubArca.Api.Tv;

// TV projection of the owner's UNIFIED media workspace.
//
// The television used to browse two independent surfaces — a photo gallery and
// a video list — with two query models, two filter vocabularies and two paging
// implementations. The video list had no filters at all. This class retires
// that split: the TV asks the SAME MediaCollectionQueryBinder +
// IMediaCollectionQueryService that serve GET /api/media and
// GET /api/albums/{id}/media on the web, so "Tutti | Foto | Video", the filter
// compatibility rules, the album-membership restriction, the sort vocabulary
// and the cursor fingerprint are shared by construction rather than by
// agreement. A filter that means one thing on the web cannot come to mean
// another here, because there is only one implementation of what it means.
//
// This class therefore owns exactly one thing: turning a MediaItem into a
// TV-safe DTO whose media URLs are the grant-gated /api/tv/personal ones. It
// re-implements no eligibility, filter, sort or paging logic.
public sealed class TvPersonalMediaService : ITvPersonalMediaService
{
    private readonly IMediaCollectionQueryService _media;
    private readonly IAlbumService _albums;
    private readonly MediaSemanticSearchService _semantic;
    private readonly GallerySemanticQueryService _photoSemantic;
    private readonly IFileItemService _files;

    public TvPersonalMediaService(
        IMediaCollectionQueryService media,
        IAlbumService albums,
        MediaSemanticSearchService semantic,
        GallerySemanticQueryService photoSemantic,
        IFileItemService files)
    {
        _media = media;
        _albums = albums;
        _semantic = semantic;
        _photoSemantic = photoSemantic;
        _files = files;
    }

    public async Task<TvPersonalMediaListResult> QueryAsync(
        Guid ownerUserId, MediaCollectionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = await _media.QueryAsync(ownerUserId, query, cancellationToken);
        if (result.Status != MediaCollectionStatus.Ok)
        {
            return new TvPersonalMediaListResult(null, result.Status, result.Error);
        }

        var page = result.Page!;
        return new TvPersonalMediaListResult(
            new TvPersonalMediaPageDto(
                page.Items.Select(Project).ToList(),
                page.NextCursor,
                page.HasMore,
                page.TotalCount,
                page.PhotoCount,
                page.VideoCount),
            MediaCollectionStatus.Ok,
            null);
    }

    // SEMANTIC retrieval, delegated ENTIRELY to the canonical services — and to
    // the RIGHT one for each kind, which is the correction this method exists to
    // carry.
    //
    //   image        → GallerySemanticQueryService, the photo pipeline the web's
    //                  photo tab uses. It is physical-filter-FIRST: the People,
    //                  GPS, duplicate-collapse and ALBUM constraints build the
    //                  candidate set before ranking, so a photo semantic search
    //                  inside an album really is album-scoped.
    //   all | video  → MediaSemanticSearchService, the unified cross-kind route.
    //
    // An earlier version sent every kind to the unified service. That produced
    // the right-looking results for the wrong reason: photos were ranked by the
    // media pipeline rather than the photo one, and — worse — a photo search
    // inside an album silently searched the owner's WHOLE library, because the
    // unified route is library-scoped and takes no album.
    //
    // The reason the photo route was avoided is real and is solved here rather
    // than accepted: it hydrates into ImageItem, which carries no favorite,
    // rating or takenAt, so projecting it would have made semantic photo cards
    // visibly poorer than ordinary ones in the same grid. The fix is to take the
    // RANKED IDS and re-hydrate them through ListGalleryMediaByRankAsync, which
    // returns MediaItem in the supplied relevance order. Canonical ranking,
    // canonical cursor, unified DTO.
    public async Task<TvPersonalMediaSemanticResult> SearchSemanticAsync(
        Guid ownerUserId,
        string query,
        MediaKindScope kind,
        int limit,
        string? cursor,
        ImageFilters filters,
        Guid? albumId,
        int semanticTopK,
        CancellationToken cancellationToken = default)
    {
        return kind == MediaKindScope.Image
            ? await SearchPhotosAsync(
                ownerUserId, query, limit, cursor, filters, albumId, semanticTopK, cancellationToken)
            : await SearchMediaAsync(
                ownerUserId, query, kind, limit, cursor, filters, cancellationToken);
    }

    private async Task<TvPersonalMediaSemanticResult> SearchPhotosAsync(
        Guid ownerUserId,
        string query,
        int limit,
        string? cursor,
        ImageFilters filters,
        Guid? albumId,
        int semanticTopK,
        CancellationToken cancellationToken)
    {
        // AlbumId is what makes an in-album photo search album-scoped: it is
        // part of the physical candidate set AND of the cursor fingerprint, so a
        // cursor issued inside an album can never replay library-wide.
        var photoFilters = filters with
        {
            SemanticQuery = query,
            SemanticTopK = semanticTopK,
            AlbumId = albumId,
        };

        GallerySemanticPage page;
        try
        {
            page = await _photoSemantic.SearchAsync(
                ownerUserId, limit, cursor, photoFilters, cancellationToken);
        }
        catch (SemanticSearchCursorException)
        {
            throw;
        }

        if (!page.Available)
        {
            return new TvPersonalMediaSemanticResult(null, false, page.UnavailableReason, false);
        }

        // Re-hydrate the RANKED ids into unified MediaItem, preserving the
        // relevance order the ranking produced. This is why a semantic photo
        // card is indistinguishable from an ordinary one.
        var rankedIds = page.Items.Select(item => item.Id).ToList();
        var media = await _files.ListGalleryMediaByRankAsync(
            ownerUserId, rankedIds, cancellationToken);

        var items = media.Select(Project).ToList();
        return new TvPersonalMediaSemanticResult(
            new TvPersonalMediaPageDto(
                items, page.NextCursor, page.HasMore, page.TotalCount, items.Count, 0),
            true, null, false);
    }

    private async Task<TvPersonalMediaSemanticResult> SearchMediaAsync(
        Guid ownerUserId,
        string query,
        MediaKindScope kind,
        int limit,
        string? cursor,
        ImageFilters filters,
        CancellationToken cancellationToken)
    {
        var page = await _semantic.SearchAsync(
            ownerUserId, query, kind, limit, cursor, filters, cancellationToken);

        // Unavailable is NOT an empty page. Returning one would be
        // indistinguishable from "nothing matched", which is exactly the
        // silent degradation this contract forbids.
        if (!page.Available)
        {
            return new TvPersonalMediaSemanticResult(null, false, page.UnavailableReason, false);
        }

        // Only the media survives the boundary. BestMatch/AdditionalMatches carry
        // raw similarity scores and segment evidence, which are internal ranking
        // detail and never cross into a TV DTO.
        var items = page.Items.Select(result => Project(result.Media)).ToList();
        var photoCount = items.Count(item => item.Kind != "video");
        return new TvPersonalMediaSemanticResult(
            new TvPersonalMediaPageDto(
                items, page.NextCursor, page.HasMore, page.Total, photoCount, items.Count - photoCount),
            true, null, page.StillIndexingManyItems);
    }

    public async Task<IReadOnlyList<TvPersonalAlbumCardDto>> ListAlbumsAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        // The owner's own albums, from the same owner-scoped service the web
        // album list uses. ShowOnTv is deliberately NOT consulted: that flag
        // governs the PARTY allowlist, which is a public-facing surface with a
        // different threat model. The Personal Area is gated by the TV session
        // AND the unlock grant AND owner scoping, so it shows the owner every
        // album they own — exactly what they see on the web.
        var albums = await _albums.ListAsync(ownerUserId, cancellationToken);
        return albums
            .Select(a => new TvPersonalAlbumCardDto(
                a.Id,
                a.Name,
                a.ItemCount,
                a.PhotoCount,
                a.VideoCount,
                // Cover tiles are ordinary owner media ids, re-pointed at the
                // grant-gated TV byte routes. The web cover URL is never
                // forwarded: it addresses /api/files, which the TV session
                // cannot reach.
                a.CoverItems
                    .Select(c => c.Kind == "video" ? PosterUrl(c.FileItemId) : ThumbnailUrl(c.FileItemId))
                    .ToList()))
            .ToList();
    }

    public async Task<TvPersonalAlbumCardDto?> GetAlbumAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default)
    {
        // Owner-validated: a foreign or missing album is a null here and a
        // generic 404 at the endpoint — never an existence leak, and never a
        // silently empty grid that looks like a data problem.
        var detail = await _albums.GetByIdAsync(albumId, ownerUserId, cancellationToken);
        if (detail is null) return null;

        var summary = (await _albums.ListAsync(ownerUserId, cancellationToken))
            .FirstOrDefault(a => a.Id == albumId);
        return summary is null
            ? new TvPersonalAlbumCardDto(detail.Id, detail.Name, 0, 0, 0, [])
            : new TvPersonalAlbumCardDto(
                summary.Id, summary.Name, summary.ItemCount,
                summary.PhotoCount, summary.VideoCount,
                summary.CoverItems
                    .Select(c => c.Kind == "video" ? PosterUrl(c.FileItemId) : ThumbnailUrl(c.FileItemId))
                    .ToList());
    }

    // MediaItem → TV DTO. Every URL is rewritten to /api/tv/personal, which
    // re-checks the TV session AND the unlock grant AND current eligibility on
    // every byte. Nothing storage-, blob- or AI-related crosses over: the
    // source DTO carries no StorageKey, BlobObjectId, SHA or raw metadata, and
    // GPS remains presence-only.
    private static TvPersonalMediaItemDto Project(MediaItem item) => new(
        item.Id,
        item.Kind,
        item.DisplayName,
        item.Width,
        item.Height,
        item.CreatedAt,
        item.TakenAt,
        item.Favorite,
        item.Rating,
        item.OccurrenceCount,
        // Grid card image: the small thumbnail for a photo, the poster for a
        // video. One field so the mixed "Tutti" grid needs no branch.
        item.Kind == "video" ? PosterUrl(item.Id) : ThumbnailUrl(item.Id),
        // Viewer image: the medium preview for a photo; a video shows its
        // poster until the player is ready.
        item.Kind == "video" ? PosterUrl(item.Id) : PreviewUrl(item.Id),
        item.Kind == "video" ? VideoUrl(item.Id) : null,
        item.Kind == "video" ? PreviewStripUrl(item.Id) : null,
        item.DurationSeconds,
        item.VideoCodec,
        item.HasAudio);

    private static string ThumbnailUrl(Guid id) => $"/api/tv/personal/media/{id}/thumbnail";
    private static string PreviewUrl(Guid id) => $"/api/tv/personal/media/{id}/preview";
    private static string PosterUrl(Guid id) => $"/api/tv/personal/media/{id}/poster";
    private static string VideoUrl(Guid id) => $"/api/tv/personal/media/{id}/video";
    private static string PreviewStripUrl(Guid id) => $"/api/tv/personal/media/{id}/video-preview-strip";
}
