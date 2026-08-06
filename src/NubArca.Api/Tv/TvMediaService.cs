using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Metadata;
using NubArca.Api.Party;
using NubArca.Api.Security;

namespace NubArca.Api.Tv;

public sealed class TvMediaService : ITvMediaService
{
    private readonly AppDbContext _db;
    private readonly IPartyLinkService _party;

    public TvMediaService(AppDbContext db, IPartyLinkService party)
    {
        _db = db;
        _party = party;
    }

    public async Task<IReadOnlyList<TvAlbumDto>> ListAlbumsAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var albums = await _db.Albums
            .AsNoTracking()
            .Where(a => a.OwnerUserId == ownerUserId && a.ShowOnTv)
            .OrderBy(a => a.Name)
            .ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.Name })
            .ToListAsync(cancellationToken);
        if (albums.Count == 0)
        {
            return [];
        }

        var albumIds = albums.Select(a => a.Id).ToList();

        // All displayable (image/video) members of the enabled albums in one pass.
        // The join to FileItems applies the Private-Vault global filter, so vaulted
        // or vault-only files never appear.
        var media = await _db.AlbumItems
            .AsNoTracking()
            .Where(ai => albumIds.Contains(ai.AlbumId))
            .Join(_db.FileItems.AsNoTracking(),
                ai => ai.FileItemId,
                f => f.Id,
                (ai, f) => new { ai.AlbumId, ai.AddedAt, f.Id, f.BlobObjectId, f.OwnerUserId, f.DeletedAt, f.MediaLibraryState })
            // Slice 3: files moved out of the media library (Excluded) never
            // appear on TV even while their AlbumItem persists.
            .Where(x => x.OwnerUserId == ownerUserId
                && x.DeletedAt == null
                && x.MediaLibraryState == MediaLibraryState.Active)
            .Join(_db.BlobMetadata.AsNoTracking(),
                x => x.BlobObjectId,
                m => m.BlobObjectId,
                (x, m) => new { x.AlbumId, x.AddedAt, x.Id, m.MediaCategory })
            .Where(x => x.MediaCategory == MediaCategories.Image || x.MediaCategory == MediaCategories.Video)
            // Exclude guest uploads awaiting approval / hidden / rejected.
            .Where(x => !_db.PartyUploadItems.Any(pu =>
                pu.FileItemId == x.Id && pu.Status != PartyUploadStatuses.Approved))
            .ToListAsync(cancellationToken);

        var byAlbum = media
            .GroupBy(x => x.AlbumId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.AddedAt).ThenBy(x => x.Id).ToList());

        var partyUrls = await _party.GetActivePartyUrlsAsync(ownerUserId, albumIds, cancellationToken);

        return albums
            .Select(a =>
            {
                byAlbum.TryGetValue(a.Id, out var items);
                var count = items?.Count ?? 0;
                var coverId = items is { Count: > 0 } ? items[0].Id : (Guid?)null;
                partyUrls.TryGetValue(a.Id, out var party);
                return new TvAlbumDto(
                    a.Id,
                    a.Name,
                    count,
                    coverId is Guid c ? ThumbnailUrl(c) : null,
                    PartyEnabled: party is not null,
                    PartyUrl: party?.ViewUrl,
                    PartyUploadUrl: party?.UploadUrl);
            })
            .ToList();
    }

    public async Task<TvAlbumItemsDto?> ListItemsAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default)
    {
        var album = await _db.Albums
            .AsNoTracking()
            .Where(a => a.Id == albumId && a.OwnerUserId == ownerUserId && a.ShowOnTv)
            .Select(a => new { a.Id, a.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (album is null)
        {
            return null;
        }

        var rows = await _db.AlbumItems
            .AsNoTracking()
            .Where(ai => ai.AlbumId == albumId)
            .Join(_db.FileItems.AsNoTracking(),
                ai => ai.FileItemId,
                f => f.Id,
                (ai, f) => new { ai.AddedAt, f.Id, f.Name, f.BlobObjectId, f.OwnerUserId, f.DeletedAt, f.MediaLibraryState, f.Width, f.Height })
            .Where(x => x.OwnerUserId == ownerUserId
                && x.DeletedAt == null
                && x.MediaLibraryState == MediaLibraryState.Active)
            .Join(_db.BlobMetadata.AsNoTracking(),
                x => x.BlobObjectId,
                m => m.BlobObjectId,
                (x, m) => new
                {
                    x.AddedAt, x.Id, x.Name, m.MediaCategory, m.DetectedContentType,
                    m.VideoExtractionStatus, m.VideoCodec,
                    // Detected dims are CODED pixels (Image.Identify ignores EXIF
                    // orientation; ffprobe ignores the display matrix). The DTO
                    // exposes DISPLAY dims — images swapped by EXIF Orientation,
                    // videos by Rotation — so the tile matches the auto-oriented
                    // thumbnail/poster. FileItem dims fall back to the blob's.
                    FileWidth = x.Width, FileHeight = x.Height,
                    BlobWidth = m.Width, BlobHeight = m.Height, m.Rotation, m.Orientation,
                })
            .Where(x => x.MediaCategory == MediaCategories.Image || x.MediaCategory == MediaCategories.Video)
            // Exclude guest uploads awaiting approval / hidden / rejected.
            .Where(x => !_db.PartyUploadItems.Any(pu =>
                pu.FileItemId == x.Id && pu.Status != PartyUploadStatuses.Approved))
            // Id is a STABLE tie-break: bulk-added items share AddedAt, and
            // without a deterministic secondary key each query (every 15s poll)
            // could return them in a different order, so the TV grid would keep
            // reshuffling ("a slideshow of the whole gallery").
            .OrderBy(x => x.AddedAt)
            .ThenBy(x => x.Id)
            .Select(x => new
            {
                x.Id, x.Name, x.MediaCategory, x.DetectedContentType,
                x.VideoExtractionStatus, x.VideoCodec,
                x.FileWidth, x.FileHeight, x.BlobWidth, x.BlobHeight, x.Rotation, x.Orientation,
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(x =>
            {
                // The TV only ever receives ffmpeg-produced bytes for videos
                // (poster JPEG, HLS segments), so a legacy container confirmed
                // by ffprobe counts as a video here too — otherwise those files
                // showed up with no poster and no playback at all.
                var isVideo = x.MediaCategory == MediaCategories.Video
                    && SafeContentType.IsServerConfirmedVideo(
                        x.DetectedContentType, x.VideoExtractionStatus, x.VideoCodec);
                // DISPLAY dimensions so the tile matches the auto-oriented
                // thumbnail/poster: videos swapped by Rotation, images by EXIF
                // Orientation. FileItem dims are preferred (matching the web
                // gallery) and fall back to the blob's when unprobed.
                var (width, height) = isVideo
                    ? VideoDisplayDimensions.Resolve(
                        x.BlobWidth ?? x.FileWidth, x.BlobHeight ?? x.FileHeight, x.Rotation)
                    : ImageDisplayDimensions.Resolve(
                        x.FileWidth ?? x.BlobWidth, x.FileHeight ?? x.BlobHeight, x.Orientation);
                return new TvAlbumItemDto(
                    x.Id,
                    x.Name,
                    isVideo ? "video" : "image",
                    width,
                    height,
                    ThumbnailUrl(x.Id),
                    PreviewUrl(x.Id),
                    isVideo ? PosterUrl(x.Id) : null,
                    isVideo ? VideoUrl(x.Id) : null,
                    isVideo ? PreviewStripUrl(x.Id) : null);
            })
            .ToList();

        var partyUrls = await _party.GetActivePartyUrlsAsync(ownerUserId, [album.Id], cancellationToken);
        partyUrls.TryGetValue(album.Id, out var party);
        return new TvAlbumItemsDto(
            album.Id, album.Name, items,
            PartyEnabled: party is not null,
            PartyUrl: party?.ViewUrl,
            PartyUploadUrl: party?.UploadUrl);
    }

    public async Task<bool> IsMediaVisibleAsync(
        Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken = default)
    {
        // The file must be an owner-owned, active, non-vault FileItem (the
        // FileItems query below carries the Private-Vault global filter) that is a
        // member of at least one of the owner's currently-enabled TV albums.
        var fileOk = await _db.FileItems
            .AsNoTracking()
            .AnyAsync(
                f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null,
                cancellationToken);
        if (!fileOk)
        {
            return false;
        }

        // A guest upload that is pending/hidden/rejected must not serve media.
        var moderatedOut = await _db.PartyUploadItems
            .AsNoTracking()
            .AnyAsync(pu => pu.FileItemId == fileItemId && pu.Status != PartyUploadStatuses.Approved,
                cancellationToken);
        if (moderatedOut)
        {
            return false;
        }

        return await _db.AlbumItems
            .AsNoTracking()
            .Where(ai => ai.FileItemId == fileItemId)
            .Join(_db.Albums.AsNoTracking(),
                ai => ai.AlbumId,
                a => a.Id,
                (ai, a) => a)
            .AnyAsync(a => a.OwnerUserId == ownerUserId && a.ShowOnTv, cancellationToken);
    }

    private static string ThumbnailUrl(Guid id) => $"/api/tv/media/{id}/thumbnail";
    private static string PreviewUrl(Guid id) => $"/api/tv/media/{id}/preview";
    private static string PosterUrl(Guid id) => $"/api/tv/media/{id}/poster";
    private static string VideoUrl(Guid id) => $"/api/tv/media/{id}/video";
    private static string PreviewStripUrl(Guid id) => $"/api/tv/media/{id}/video-preview-strip";
}
