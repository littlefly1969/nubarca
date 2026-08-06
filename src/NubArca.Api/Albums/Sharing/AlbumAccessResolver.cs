using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Security;

namespace NubArca.Api.Albums.Sharing;

// The concrete authorization gate for live album sharing. See
// IAlbumAccessResolver for why this is the only place allowed to conclude that
// a non-owner may see an album's media.
//
// EVERY resolve re-reads the database. There is no per-request or per-session
// caching of a grant, because the contract requires a revoke to take effect
// immediately on every protected representation, and a cached grant would
// survive it for the lifetime of the cache.
public sealed class AlbumAccessResolver : IAlbumAccessResolver
{
    private readonly AppDbContext _db;

    public AlbumAccessResolver(AppDbContext db) => _db = db;

    public async Task<AlbumAccessGrant?> ResolveAsync(
        Guid albumId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var album = await _db.Albums
            .AsNoTracking()
            .Where(a => a.Id == albumId)
            .Select(a => new { a.Id, a.OwnerUserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (album is null)
        {
            return null;
        }

        // (2a) Album ownership. Read from the Album row alone: the owner's
        // authority must not depend on album_memberships existing or being
        // consistent, and there is deliberately no synthetic owner membership
        // row to go stale.
        if (album.OwnerUserId == actorUserId)
        {
            // An owner whose own account is disabled cannot be making this
            // request at all — CookieSessionValidator rejects the cookie first —
            // so there is nothing extra to check here.
            return new AlbumAccessGrant(
                album.Id, album.OwnerUserId, actorUserId,
                AlbumAccessGrant.OwnerRole,
                AllowOriginalDownload: true,
                MembershipId: null);
        }

        // (7a) The ALBUM OWNER's account status. A disabled owner's library is
        // not served to other people: their content stops being reachable
        // through a share the moment the account is disabled, exactly as it
        // stops being reachable through their own session. (The caller's own
        // account status is already enforced on every request by
        // CookieSessionValidator, which rejects a disabled user's cookie.)
        var ownerActive = await _db.Users
            .AsNoTracking()
            .AnyAsync(u => u.Id == album.OwnerUserId && u.DisabledAt == null, cancellationToken);
        if (!ownerActive)
        {
            return null;
        }

        // (2b) + (3) + (7b) An ACTIVE ACCEPTED membership, re-read now. Both the
        // state and the revocation timestamp are part of the predicate, so a
        // revoked row can never satisfy it.
        var membership = await _db.AlbumMemberships
            .AsNoTracking()
            .Where(m => m.AlbumId == albumId
                && m.MemberUserId == actorUserId
                && m.State == AlbumMembershipStates.Accepted
                && m.RevokedAt == null)
            .Select(m => new { m.Id, m.Role, m.AllowOriginalDownload })
            .FirstOrDefaultAsync(cancellationToken);
        if (membership is null)
        {
            return null;
        }

        // A role outside the closed catalog is treated as no access rather than
        // as an unknown-but-probably-fine grant. The check constraint makes this
        // unreachable through the API; it is here so that a hand-edited row
        // fails closed.
        if (!AlbumRoles.IsKnown(membership.Role))
        {
            return null;
        }

        return new AlbumAccessGrant(
            album.Id, album.OwnerUserId, actorUserId,
            membership.Role,
            membership.AllowOriginalDownload,
            membership.Id);
    }

    public async Task<SharedMediaGrant?> ResolveMediaAsync(
        Guid albumId, Guid actorUserId, Guid fileItemId, SharedMediaAccess access,
        CancellationToken cancellationToken = default)
    {
        var grant = await ResolveAsync(albumId, actorUserId, cancellationToken);
        if (grant is null)
        {
            return null;
        }

        // (6) Download permission, checked BEFORE the item is even looked up so
        // that "download is off" and "that item is not in this album" are the
        // same 404 to the caller.
        if (access == SharedMediaAccess.Original && !grant.AllowOriginalDownload)
        {
            return null;
        }

        // (4) + (5) Current membership of the item in THIS album, and current
        // availability of the source file. Both are re-evaluated per request:
        // removing the item from the album, soft-deleting the file, excluding it
        // from the media library, or moving it into the Private Vault each stop
        // access on the next call.
        //
        // Private Vault needs no predicate of its own: _db.FileItems carries the
        // global `PrivateVaultId == null` query filter, so a vaulted file is not
        // visible to this query at all. See the audit note in AlbumSharingService.
        var row = await _db.AlbumItems
            .AsNoTracking()
            .Where(ai => ai.AlbumId == albumId && ai.FileItemId == fileItemId)
            .Join(_db.FileItems.AsNoTracking(),
                ai => ai.FileItemId,
                f => f.Id,
                (ai, f) => new
                {
                    f.Id,
                    f.OwnerUserId,
                    f.BlobObjectId,
                    f.DeletedAt,
                    f.MediaLibraryState,
                    ai.AddedByUserId,
                })
            .Where(x => x.DeletedAt == null && x.MediaLibraryState == MediaLibraryState.Active)
            // SHARE-ALBUM-02. Two shapes are servable, and the second is checked
            // POSITIVELY rather than by removing the old owner-only predicate:
            //
            //   * the ALBUM OWNER's own media — no membership involved, so a
            //     synthetic owner membership is never required;
            //
            //   * a CONTRIBUTION, which must satisfy all of:
            //       - provenance is coherent: whoever added it owns it. A row
            //         where these disagree is corrupt (the API cannot create
            //         one) and is refused rather than guessed at;
            //       - the source owner still holds an ACCEPTED, unrevoked
            //         membership on THIS album — any role, so a contributor
            //         downgraded to Viewer keeps their contribution visible;
            //       - the source owner's account is still active.
            //
            // Revoking a membership already withdraws that member's items in the
            // same transaction, so this predicate is normally redundant. It is
            // here as the fail-closed guarantee: under a race, or against
            // inconsistent data, access ends the moment the source membership
            // does — without waiting for the withdrawal to land.
            .Where(x => x.OwnerUserId == grant.AlbumOwnerUserId
                || (x.AddedByUserId == x.OwnerUserId
                    && _db.Users.Any(u => u.Id == x.OwnerUserId && u.DisabledAt == null)
                    && _db.AlbumMemberships.Any(m =>
                        m.AlbumId == albumId
                        && m.MemberUserId == x.OwnerUserId
                        && m.State == AlbumMembershipStates.Accepted
                        && m.RevokedAt == null)))
            .Join(_db.BlobMetadata.AsNoTracking(),
                x => x.BlobObjectId,
                m => m.BlobObjectId,
                (x, m) => new { x.Id, x.OwnerUserId, m.MediaCategory, m.DetectedContentType })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        // Only server-DETECTED image/video is shareable. A spoofed MIME on
        // non-media bytes has no MediaCategory of its own and collapses to 404,
        // the same as a missing item.
        var kind = row.MediaCategory switch
        {
            MediaCategories.Video => SharedMediaKind.Video,
            MediaCategories.Image => SharedMediaKind.Image,
            _ => (SharedMediaKind?)null,
        };
        if (kind is null)
        {
            return null;
        }

        // A video whose detected type is not trusted is still a video for
        // POSTER purposes (the poster is ffmpeg-produced), so the kind stands;
        // the per-endpoint gates inside the existing owner-scoped services make
        // the finer distinction, unchanged.
        _ = SafeContentType.IsTrustedVideo(row.DetectedContentType);

        return new SharedMediaGrant(albumId, row.OwnerUserId, row.Id, kind.Value);
    }
}
