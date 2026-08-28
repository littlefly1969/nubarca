using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

public sealed class PartyMessageAccessResolver : IPartyMessageAccessResolver
{
    private readonly AppDbContext _db;

    public PartyMessageAccessResolver(AppDbContext db) => _db = db;

    public async Task<PartyMessageManagerGrant?> ResolveAsync(
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

        // The owner's authority comes from the Album row alone and never from a
        // membership row, so it cannot be revoked, cleared, or made inconsistent
        // by anything in album_memberships.
        if (album.OwnerUserId == actorUserId)
        {
            return new PartyMessageManagerGrant(album.Id, album.OwnerUserId, actorUserId, IsOwner: true);
        }

        // A delegate's authority is derived FROM the owner, so it ends when the
        // owner's account does — the same rule AlbumAccessResolver applies to a
        // shared album's media.
        var ownerActive = await _db.Users
            .AsNoTracking()
            .AnyAsync(u => u.Id == album.OwnerUserId && u.DisabledAt == null, cancellationToken);
        if (!ownerActive)
        {
            return null;
        }

        // State, revocation and the capability are all in the predicate, so a
        // revoked membership or a cleared flag stops granting on this very read.
        var delegated = await _db.AlbumMemberships
            .AsNoTracking()
            .AnyAsync(m => m.AlbumId == albumId
                && m.MemberUserId == actorUserId
                && m.State == AlbumMembershipStates.Accepted
                && m.RevokedAt == null
                && m.CanManagePartyMessages, cancellationToken);

        return delegated
            ? new PartyMessageManagerGrant(album.Id, album.OwnerUserId, actorUserId, IsOwner: false)
            : null;
    }
}
