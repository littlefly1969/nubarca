using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Security;

namespace NubArca.Api.Party;

public sealed class PartyModerationService : IPartyModerationService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public PartyModerationService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<PartyUploadListDto?> ListAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default)
    {
        var owns = await _db.Albums
            .AsNoTracking()
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!owns)
        {
            return null;
        }

        // Guest uploads joined to their (still-active, non-vault — the FileItems
        // global filter) FileItem + blob metadata for the display name/media type.
        // A file the owner separately deleted drops out via the DeletedAt filter.
        var rows = await _db.PartyUploadItems
            .AsNoTracking()
            .Where(pu => pu.OwnerUserId == ownerUserId && pu.AlbumId == albumId)
            .Join(_db.FileItems.AsNoTracking(),
                pu => pu.FileItemId,
                f => f.Id,
                (pu, f) => new { pu.FileItemId, pu.Status, pu.UploadedAt, pu.ModeratedAt, f.Name, f.BlobObjectId, f.OwnerUserId, f.DeletedAt })
            .Where(x => x.OwnerUserId == ownerUserId && x.DeletedAt == null)
            .Join(_db.BlobMetadata.AsNoTracking(),
                x => x.BlobObjectId,
                m => m.BlobObjectId,
                (x, m) => new { x.FileItemId, x.Status, x.UploadedAt, x.ModeratedAt, x.Name, m.MediaCategory, m.DetectedContentType })
            .OrderByDescending(x => x.UploadedAt)
            .ToListAsync(cancellationToken);

        var approvalMode = await CurrentApprovalModeAsync(ownerUserId, albumId, cancellationToken);

        var items = rows
            .Select(x => new PartyUploadItemDto(
                x.FileItemId,
                x.Name,
                x.MediaCategory == MediaCategories.Video && SafeContentType.IsTrustedVideo(x.DetectedContentType)
                    ? "video"
                    : "image",
                x.Status,
                ThumbnailUrl(x.FileItemId),
                x.UploadedAt,
                x.ModeratedAt))
            .ToList();

        return new PartyUploadListDto(albumId, approvalMode, items);
    }

    public async Task<bool> SetStatusAsync(
        Guid ownerUserId, Guid albumId, Guid fileItemId, string status,
        Guid moderatedByUserId, CancellationToken cancellationToken = default)
    {
        var row = await _db.PartyUploadItems
            .FirstOrDefaultAsync(
                pu => pu.OwnerUserId == ownerUserId && pu.AlbumId == albumId && pu.FileItemId == fileItemId,
                cancellationToken);
        if (row is null)
        {
            return false;
        }

        row.Status = status;
        row.ModeratedAt = _clock.GetUtcNow().UtcDateTime;
        row.ModeratedByUserId = moderatedByUserId;

        if (status == PartyUploadStatuses.Approved)
        {
            var canRestore = await _db.Albums.AnyAsync(
                    a => a.Id == albumId && a.OwnerUserId == ownerUserId,
                    cancellationToken)
                && await _db.FileItems.AnyAsync(
                    f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null,
                    cancellationToken);
            if (!canRestore)
            {
                return false;
            }

            var alreadyInAlbum = await _db.AlbumItems.AnyAsync(
                ai => ai.AlbumId == albumId && ai.FileItemId == fileItemId,
                cancellationToken);
            if (!alreadyInAlbum)
            {
                var nextOrder = (await _db.AlbumItems
                    .Where(ai => ai.AlbumId == albumId)
                    .Select(ai => (int?)ai.SortOrder)
                    .MaxAsync(cancellationToken) ?? 0) + 1;
                _db.AlbumItems.Add(new AlbumItem
                {
                    Id = Guid.NewGuid(),
                    AlbumId = albumId,
                    FileItemId = fileItemId,
                    AddedAt = _clock.GetUtcNow().UtcDateTime,
                    SortOrder = nextOrder,
                    // Restoring a guest party upload: the file is already
                    // owner-owned by then (checked just above), and it is the
                    // owner's moderation decision that puts it back.
                    AddedByUserId = ownerUserId,
                });
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> MarkRemovedFromAlbumAsync(
        Guid ownerUserId, Guid albumId, Guid fileItemId,
        Guid moderatedByUserId, CancellationToken cancellationToken = default)
    {
        var row = await _db.PartyUploadItems
            .FirstOrDefaultAsync(
                pu => pu.OwnerUserId == ownerUserId && pu.AlbumId == albumId && pu.FileItemId == fileItemId,
                cancellationToken);
        if (row is null)
        {
            return false;
        }

        row.Status = PartyUploadStatuses.RemovedFromAlbum;
        row.ModeratedAt = _clock.GetUtcNow().UtcDateTime;
        row.ModeratedByUserId = moderatedByUserId;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // The album's current approval-mode, read from its active party link (false
    // when no active link — a disabled album can't accept new uploads anyway).
    private async Task<bool> CurrentApprovalModeAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return await _db.PartyAlbumLinks
            .AsNoTracking()
            .Where(p => p.AlbumId == albumId && p.OwnerUserId == ownerUserId
                && p.Enabled && p.RevokedAt == null
                && (p.ExpiresAt == null || p.ExpiresAt > now))
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => p.RequireUploadApproval)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string ThumbnailUrl(Guid id) => $"/api/files/{id}/thumbnail";
}
