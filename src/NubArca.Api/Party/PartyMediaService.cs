using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Security;

namespace NubArca.Api.Party;

public sealed class PartyMediaService : IPartyMediaService
{
    private readonly AppDbContext _db;

    public PartyMediaService(AppDbContext db) => _db = db;

    public async Task<PartyAlbumHeader?> GetAlbumAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default)
    {
        var album = await _db.Albums
            .AsNoTracking()
            // Owner-owned, and nothing else. A party's media is reachable
            // because the party resolved to this album, not because a
            // television was ever enabled for it.
            .Where(a => a.Id == albumId && a.OwnerUserId == ownerUserId)
            .Select(a => new { a.Name, a.CoverFileItemId })
            .FirstOrDefaultAsync(cancellationToken);
        if (album is null)
        {
            return null;
        }

        var members = DisplayableMembers(ownerUserId, albumId);
        var count = await members.CountAsync(cancellationToken);
        var configured = album.CoverFileItemId is Guid cover
            && await members.AnyAsync(x => x.Id == cover, cancellationToken) ? cover : (Guid?)null;
        var fallback = configured ?? await members.OrderBy(x => x.AddedAt).ThenBy(x => x.Id)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
        return new PartyAlbumHeader(album.Name, count, fallback, configured);
    }

    public async Task<IReadOnlyList<PartyMediaItem>?> ListItemsAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default)
    {
        var albumOk = await _db.Albums
            .AsNoTracking()
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!albumOk)
        {
            return null;
        }

        var rows = await DisplayableMembers(ownerUserId, albumId)
            // Id is a STABLE tie-break: bulk-added items share AddedAt, and without
            // a deterministic secondary key each poll could return them in a
            // different order, so the public party grid would keep reshuffling
            // ("a slideshow of the whole gallery").
            .OrderBy(x => x.AddedAt)
            .ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.MediaCategory, x.DetectedContentType })
            .ToListAsync(cancellationToken);

        return rows
            .Select(x => new PartyMediaItem(x.Id, Classify(x.MediaCategory, x.DetectedContentType)))
            .ToList();
    }

    public async Task<PartyMediaKind?> GetVisibleMediaKindAsync(
        Guid ownerUserId, Guid albumId, Guid fileItemId,
        CancellationToken cancellationToken = default)
    {
        var albumOk = await _db.Albums
            .AsNoTracking()
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!albumOk)
        {
            return null;
        }

        var row = await DisplayableMembers(ownerUserId, albumId)
            .Where(x => x.Id == fileItemId)
            .Select(x => new { x.MediaCategory, x.DetectedContentType })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : Classify(row.MediaCategory, row.DetectedContentType);
    }

    private IQueryable<MemberRow> DisplayableMembers(Guid ownerUserId, Guid albumId) =>
        DisplayableMembers(_db, ownerUserId, albumId);

    // Displayable (image/video) members of the album, owner-owned, active,
    // non-vault (FileItems carries the Private-Vault global filter). Projects to
    // an anonymous type — matching TvMediaService, which EF composes cleanly.
    // Guest uploads that are not APPROVED (pending/hidden/rejected) are excluded,
    // so moderation is enforced on every public party surface; owner-added
    // content has no moderation row and is always shown.
    //
    // Internal and static so the guest book's photograph chooser asks THIS rule
    // rather than a copy of it: "what may a guest see in the party's album" has
    // one answer, and the guest book only narrows it further.
    internal static IQueryable<MemberRow> DisplayableMembers(
        AppDbContext db, Guid ownerUserId, Guid albumId) =>
        db.AlbumItems
            .AsNoTracking()
            .Where(ai => ai.AlbumId == albumId)
            .Join(db.FileItems.AsNoTracking(),
                ai => ai.FileItemId,
                f => f.Id,
                (ai, f) => new
                {
                    ai.AddedAt, f.Id, f.BlobObjectId, f.OwnerUserId, f.DeletedAt, f.MediaLibraryState,
                    f.Width, f.Height,
                })
            // Slice 3: a Party surface never shows a file the owner moved out of
            // the media library (Excluded), even though its AlbumItem persists.
            .Where(x => x.OwnerUserId == ownerUserId
                && x.DeletedAt == null
                && x.MediaLibraryState == Domain.MediaLibraryState.Active)
            .Where(x => !db.PartyUploadItems.Any(pu =>
                pu.FileItemId == x.Id && pu.Status != Domain.PartyUploadStatuses.Approved))
            .Join(db.BlobMetadata.AsNoTracking(),
                x => x.BlobObjectId,
                m => m.BlobObjectId,
                (x, m) => new MemberRow
                {
                    Id = x.Id,
                    AddedAt = x.AddedAt,
                    MediaCategory = m.MediaCategory,
                    DetectedContentType = m.DetectedContentType,
                    BlobObjectId = x.BlobObjectId,
                    FileWidth = x.Width,
                    FileHeight = x.Height,
                    BlobWidth = m.Width,
                    BlobHeight = m.Height,
                    Orientation = m.Orientation,
                })
            .Where(x => x.MediaCategory == Domain.MediaCategories.Image || x.MediaCategory == Domain.MediaCategories.Video);

    private static PartyMediaKind Classify(string mediaCategory, string? detectedContentType) =>
        mediaCategory == Domain.MediaCategories.Video && SafeContentType.IsTrustedVideo(detectedContentType)
            ? PartyMediaKind.Video
            : PartyMediaKind.Image;

    // A class (not a positional record) so EF Core can bind its members in the
    // Join result selector and still compose Count/Where/OrderBy over it.
    internal sealed class MemberRow
    {
        public Guid Id { get; set; }
        public DateTime AddedAt { get; set; }
        public string MediaCategory { get; set; } = string.Empty;
        public string? DetectedContentType { get; set; }
        // Owner-private plumbing for server-side callers (the guest book takes
        // its own reference to this blob). Never projected into a DTO.
        public Guid BlobObjectId { get; set; }
        // CODED dimensions and the EXIF orientation, resolved to display
        // dimensions by ImageDisplayDimensions — FileItem first, blob fallback.
        public int? FileWidth { get; set; }
        public int? FileHeight { get; set; }
        public int? BlobWidth { get; set; }
        public int? BlobHeight { get; set; }
        public int? Orientation { get; set; }
    }
}
