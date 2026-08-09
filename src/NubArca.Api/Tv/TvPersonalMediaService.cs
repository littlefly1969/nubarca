using NubArca.Api.Albums;
using NubArca.Api.Files;
using NubArca.Api.Media;

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

    public TvPersonalMediaService(IMediaCollectionQueryService media, IAlbumService albums)
    {
        _media = media;
        _albums = albums;
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
