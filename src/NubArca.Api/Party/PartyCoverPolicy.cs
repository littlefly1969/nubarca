using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;

namespace NubArca.Api.Party;

/// <summary>
/// WHICH photograph opens a guest page, as far as the party's own choices go.
///
/// <para>Two covers, in a fixed order. On the INVITATION, the invitation's
/// cover. While the party is ON — and in its memories, which are the same page —
/// the live cover first, and the invitation's after it. When neither applies the
/// answer is null and the caller falls back to the album, exactly as before the
/// covers existed.</para>
///
/// <para>The invitation SLOT's photograph is deliberately not a candidate. It is
/// part of what the invitation says, and it sits in its section like any other
/// slot's; a cover is a separate decision about how the page LOOKS.</para>
///
/// <para>One function for the guest context and for the route that serves the
/// bytes, so the page can never be told about a cover the route then refuses,
/// and the route can never serve a cover the page is not showing.</para>
/// </summary>
public static class PartyCoverPolicy
{
    public const string Invitation = "invitation";
    public const string Live = "live";

    /// <summary>
    /// The party-chosen cover for this surface, or null. A cover whose file no
    /// longer qualifies (<see cref="PartyMediaReference"/>) is skipped, so a
    /// photograph sent to Trash hands the page to the next one in line.
    /// </summary>
    public static (string Which, Guid FileId)? Choose(
        bool allowsAlbumMedia, Guid? invitationCover, Guid? liveCover, IReadOnlySet<Guid> eligible)
    {
        if (allowsAlbumMedia && liveCover is Guid live && eligible.Contains(live))
        {
            return (Live, live);
        }
        return invitationCover is Guid invitation && eligible.Contains(invitation)
            ? (Invitation, invitation)
            : null;
    }

    public static async Task<(string Which, Guid FileId)?> ResolveAsync(
        AppDbContext db, PartyAccess access, Guid? invitationCover, Guid? liveCover,
        CancellationToken cancellationToken = default)
    {
        var candidates = new[] { invitationCover, liveCover }.OfType<Guid>().Distinct().ToList();
        if (candidates.Count == 0) return null;
        var eligible = await PartyMediaReference.EligibleAmongAsync(
            db, access.OwnerUserId, candidates, cancellationToken);
        return Choose(access.Experience.AllowsAlbumMedia, invitationCover, liveCover, eligible);
    }

    /// <summary>The same answer, reading the party's two choices itself.</summary>
    public static async Task<(string Which, Guid FileId)?> ResolveAsync(
        AppDbContext db, PartyAccess access, CancellationToken cancellationToken = default)
    {
        var covers = await db.Parties.AsNoTracking()
            .Where(p => p.Id == access.PartyId)
            .Select(p => new { p.InvitationCoverFileItemId, p.LiveCoverFileItemId })
            .FirstOrDefaultAsync(cancellationToken);
        return covers is null
            ? null
            : await ResolveAsync(
                db, access, covers.InvitationCoverFileItemId, covers.LiveCoverFileItemId,
                cancellationToken);
    }
}
