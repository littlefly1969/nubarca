using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
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
    private readonly ILogger<TvPartyPresentationService> _logger;

    public TvPartyPresentationService(
        AppDbContext db, TimeProvider clock, IPartyLinkService links,
        ILogger<TvPartyPresentationService>? logger = null)
    {
        _db = db;
        _clock = clock;
        _links = links;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TvPartyPresentationService>.Instance;
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
                l.GuestbookEnabled,
                l.GuestbookTvActive,
                Game = _db.PartyGameSessions.AsNoTracking()
                    .Where(s => s.PartyAlbumLinkId == l.Id)
                    .Select(s => new { s.Status, s.Phase, s.FinishedAt })
                    .FirstOrDefault(),
                // The book is the PARTY's, whichever link the television is on.
                HasVisibleMemory = _db.PartyGuestbookEntries.AsNoTracking()
                    .Any(e => e.PartyId == l.PartyId && e.Status == PartyMessageStatuses.Visible),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (link is null) return TvPartyState.Unavailable;

        // The regia's request, and whether it can be honoured: only while the
        // party is live, the book is open and there is a memory to show. An
        // empty book yields to the slideshow rather than a blank screen.
        var guestbookRequested = link.GuestbookTvActive
            && link.GuestbookEnabled
            && access.Experience.AllowsLiveCapabilities
            && link.HasVisibleMemory;

        var presentation = TvPartyPresentations.Decide(
            partyShowable: true,
            gameEnabled: link.GameEnabled && link.AlbumId == access.MainAlbumId,
            gamesPermitted: access.Capabilities.Games,
            gameStatus: link.Game?.Status,
            gamePhase: link.Game?.Phase,
            finishedAt: link.Game?.FinishedAt,
            now: _clock.GetUtcNow().UtcDateTime,
            guestbookRequested: guestbookRequested);

        if (presentation == TvPartyPresentations.Game && link.GuestbookTvActive)
        {
            // Both takeovers held at once. The commands make this impossible;
            // should stored state say otherwise, the game wins (it may be
            // mid-activity) and the operator is told. Ids only.
            _logger.LogWarning(
                "party.presentation.invariant_violation LinkId={LinkId} Holder={Holder} Ignored={Ignored}",
                partyAlbumLinkId, TvPartyPresentations.Game, TvPartyPresentations.Guestbook);
        }

        return new TvPartyState(presentation, link.AlbumId, access);
    }
}
