using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Party;

namespace NubArca.Api.Tv;

/// <summary>
/// The ONE place a party link's paired-display state is decided: whether a
/// television may show that party at all, and which surface it wants now.
///
/// <para>Three things ask, and they must never disagree. The control plane
/// (what the television is told to mount), the display grant (whether the game
/// stage may be authorised — at mint and on every use), and the TV media gate
/// (whether an assigned album that is not ShowOnTv may be read). A second copy
/// of this decision in any of them is exactly how "told to show the game" and
/// "allowed to show the game" would drift apart, so all three call this.</para>
///
/// <para>It READS. It never writes game state, and it is not a second state
/// machine: the rule itself is the pure <see cref="TvPartyPresentations.Decide"/>,
/// and "showable" is the display resolver every display path already uses.</para>
/// </summary>
public interface ITvPartyPresentationService
{
    Task<TvPartyState> ProjectAsync(Guid partyAlbumLinkId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One party link as a paired display sees it right now. INTERNAL: never
/// serialized. <c>Access</c> is the display resolver's answer and is null exactly
/// when the presentation is <c>unavailable</c>; <c>AlbumId</c> is the link's
/// album, present only while the party is showable.
/// </summary>
public sealed record TvPartyState(string Presentation, Guid? AlbumId, PartyAccess? Access)
{
    public static readonly TvPartyState Unavailable = new(TvPartyPresentations.Unavailable, null, null);

    /// Display-resolvable: the control plane would not call it unavailable.
    public bool Showable => Access is not null;
}

public sealed class TvPartyPresentationService : ITvPartyPresentationService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IPartyLinkService _links;

    public TvPartyPresentationService(AppDbContext db, TimeProvider clock, IPartyLinkService links)
    {
        _db = db;
        _clock = clock;
        _links = links;
    }

    /// <summary>
    /// <para>"Showable" is the display resolver's own answer — the same call
    /// that runs the party's status, window, main-album and host-permission
    /// policy for every display — so nothing here can be shown that a display
    /// could not be granted. "Game" additionally requires what the display
    /// snapshot requires: the game switch on this link, the host's Games
    /// capability (phase-folded: only while the party is live), and the link
    /// still describing the party's main album.</para>
    /// </summary>
    public async Task<TvPartyState> ProjectAsync(
        Guid partyAlbumLinkId, CancellationToken cancellationToken = default)
    {
        var access = await _links.ResolveDisplayAsync(partyAlbumLinkId, cancellationToken);
        if (access is null) return TvPartyState.Unavailable;

        var link = await _db.PartyAlbumLinks.AsNoTracking()
            .Where(l => l.Id == partyAlbumLinkId)
            .Select(l => new
            {
                l.AlbumId,
                l.GameEnabled,
                Game = _db.PartyGameSessions.AsNoTracking()
                    .Where(s => s.PartyAlbumLinkId == l.Id)
                    .Select(s => new { s.Status, s.FinishedAt })
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (link is null) return TvPartyState.Unavailable;

        var presentation = TvPartyPresentations.Decide(
            partyShowable: true,
            gameEnabled: link.GameEnabled && link.AlbumId == access.MainAlbumId,
            gamesPermitted: access.Capabilities.Games,
            gameStatus: link.Game?.Status,
            finishedAt: link.Game?.FinishedAt,
            now: _clock.GetUtcNow().UtcDateTime);
        return new TvPartyState(presentation, link.AlbumId, access);
    }
}
