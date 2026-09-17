using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

/// <summary>
/// WHICH PHOTOGRAPHS A COLLABORATOR MAY NAME, and see.
///
/// <para>The host may put any of their own eligible images on a party — the
/// menu's photograph may be a graphic that never goes in the album, and
/// choosing it files it nowhere. A collaborator may not: their world is this
/// evening, and this evening's photographs are the party's MAIN ALBUM. Anything
/// else is the host's library, which a collaborator has no account for and no
/// business enumerating.</para>
///
/// <para><b>The UI is not the boundary.</b> The picker shows the album because
/// that is all the crew route returns, but a hand-built request carrying a
/// <c>FileItemId</c> from somewhere else in the host's library has to be
/// refused by the server — so every crew route that accepts a file id asks
/// here first.</para>
///
/// <para>The one widening is deliberate: a file the party ALREADY references —
/// a cover the host chose from outside the album, a slot photograph they set
/// before adding a collaborator — stays visible and stays settable, or a
/// collaborator editing the wording of a card would silently strip its picture.
/// </para>
/// </summary>
internal static class PartyCrewMedia
{
    /// <summary>Whether this file is one the crew of this party may name.</summary>
    internal static async Task<bool> MayNameAsync(
        AppDbContext db, PartyCrewAccessContext ctx, Guid fileItemId, CancellationToken ct)
    {
        if (ctx.MainAlbumId is not Guid albumId) return false;

        var inAlbum = await db.AlbumItems
            .AnyAsync(i => i.AlbumId == albumId && i.FileItemId == fileItemId, ct);
        if (inAlbum) return true;

        return await AlreadyReferencedAsync(db, ctx, fileItemId, ct);
    }

    /// <summary>
    /// A file this party already points at, wherever it came from.
    ///
    /// <para>Covers, guest-content slots and activities. Without this a
    /// collaborator correcting a typo on a card the host had illustrated from
    /// outside the album would have to drop the photograph to save the words.
    /// </para>
    /// </summary>
    private static async Task<bool> AlreadyReferencedAsync(
        AppDbContext db, PartyCrewAccessContext ctx, Guid fileItemId, CancellationToken ct)
    {
        if (await db.Parties.AnyAsync(
                p => p.Id == ctx.PartyId
                    && (p.InvitationCoverFileItemId == fileItemId
                        || p.LiveCoverFileItemId == fileItemId), ct))
        {
            return true;
        }

        if (await db.PartyGuestContents.AnyAsync(
                c => c.PartyId == ctx.PartyId && c.MediaFileItemId == fileItemId, ct))
        {
            return true;
        }

        return ctx.MainAlbumId is Guid albumId
            && await db.PartyChallenges.AnyAsync(
                c => c.AlbumId == albumId && c.MediaFileItemId == fileItemId, ct);
    }
}
