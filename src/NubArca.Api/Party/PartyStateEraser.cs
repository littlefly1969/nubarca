using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tv;

namespace NubArca.Api.Party;

/// <summary>
/// WHAT A PARTY OWNS, stated once.
///
/// <para>Two things erase a party: deleting the album it draws on, and tearing
/// the party down while keeping that album. They are different decisions with
/// different consequences for the photographs, but the list of party rows is
/// the same list — and two copies of it would drift the first time a table was
/// added under one of them.</para>
///
/// <para>Deleted EXPLICITLY, in foreign-key order, rather than left to the
/// cascades some of these rows already carry. The cascade would work; what it
/// would not do is make "what this aggregate owns" a statement somebody has to
/// update. A table added under a party has to appear here, and a silent cascade
/// is exactly what stops anyone noticing that it did not.</para>
///
/// <para>None of this is the audit trail. Party enabled, revoked, uploaded,
/// moderated, printed, published — all of that is in the audit log, which is
/// where the question belongs, as it already is for shares.</para>
/// </summary>
public interface IPartyStateEraser
{
    /// <summary>
    /// Deletes every row that belongs to this party and its album, leaving the
    /// album itself and its files alone. Does not save; the CALLER's unit of
    /// work commits it, so a teardown is one transaction with whatever else it
    /// is part of.
    /// </summary>
    Task EraseAsync(Guid partyId, Guid? albumId, CancellationToken cancellationToken = default);
}

public sealed class PartyStateEraser : IPartyStateEraser
{
    private readonly AppDbContext _db;

    public PartyStateEraser(AppDbContext db) => _db = db;

    public async Task EraseAsync(
        Guid partyId, Guid? albumId, CancellationToken cancellationToken = default)
    {
        var linkIds = await _db.PartyAlbumLinks
            .Where(l => l.PartyId == partyId)
            .Select(l => l.Id)
            .ToListAsync(cancellationToken);

        // Guest greetings: they reference the link AND the participant, so they
        // go before either.
        await _db.PartyMessages
            .Where(m => linkIds.Contains(m.PartyAlbumLinkId))
            .ExecuteDeleteAsync(cancellationToken);

        // The HOSTED GAME's runtime, and it has to go before four of the deletes
        // below rather than one. Its restricting foreign keys reach further than
        // the party link: a vote names a PARTICIPANT, a round names a CHALLENGE,
        // and the session names both the LINK and the ALBUM.
        //
        // This is not a restart. The party itself is going away, so the game's
        // whole history goes with it — see PartyGameService.RestartAsync for the
        // other case, where the session deliberately survives.
        var gameSessionIds = await _db.PartyGameSessions
            .Where(g => linkIds.Contains(g.PartyAlbumLinkId))
            .Select(g => g.Id)
            .ToListAsync(cancellationToken);
        await _db.PartyGameVotes
            .Where(v => gameSessionIds.Contains(v.PartyGameSessionId))
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PartyGameRounds
            .Where(r => gameSessionIds.Contains(r.PartyGameSessionId))
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PartyGameSessions
            .Where(g => gameSessionIds.Contains(g.Id))
            .ExecuteDeleteAsync(cancellationToken);

        await _db.PartyChallengeVotes
            .Where(v => linkIds.Contains(v.PartyAlbumLinkId))
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PartyChallengeCompletions
            .Where(c => linkIds.Contains(c.PartyAlbumLinkId))
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PartyChallengeSessions
            .Where(s => linkIds.Contains(s.PartyAlbumLinkId))
            .ExecuteDeleteAsync(cancellationToken);

        if (albumId is Guid album)
        {
            // The guest-upload PROVENANCE rows. By the time they are deleted the
            // question they answered — is this guest photograph visible? — has
            // already been settled on the files themselves, which is what makes
            // the surviving album self-contained.
            await _db.PartyUploadItems
                .Where(u => u.AlbumId == album)
                .ExecuteDeleteAsync(cancellationToken);

            await _db.PartyChallenges
                .Where(c => c.AlbumId == album)
                .ExecuteDeleteAsync(cancellationToken);

            // Guests' selfie searches. Short-lived by design, and party state:
            // ranked references into an evening that is over.
            var searchIds = await _db.PartyFaceSearchSessions
                .Where(s => s.AlbumId == album)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);
            await _db.PartyFaceSearchResults
                .Where(r => searchIds.Contains(r.PartyFaceSearchSessionId))
                .ExecuteDeleteAsync(cancellationToken);
            await _db.PartyFaceSearchSessions
                .Where(s => searchIds.Contains(s.Id))
                .ExecuteDeleteAsync(cancellationToken);

            // The party's PRINT configuration and its idempotency ledger — the
            // budgets and the counters that spent them. They go with the party,
            // so a new evening on the same album never inherits a previous
            // one's spent sheets. The PrintJobs themselves stay: those are the
            // Print domain's record of paper that actually came out of a
            // printer, and that happened.
            await _db.PartyPrintRequests
                .Where(r => r.PartyAlbumId == album)
                .ExecuteDeleteAsync(cancellationToken);
            await _db.PartyPrintProfiles
                .Where(p => p.PartyAlbumId == album)
                .ExecuteDeleteAsync(cancellationToken);
        }

        await _db.PartyParticipants
            .Where(p => linkIds.Contains(p.PartyAlbumLinkId))
            .ExecuteDeleteAsync(cancellationToken);

        // A paired television pointed at one of this party's links holds a
        // RESTRICTING foreign key to it, so it would block the delete the same
        // way every table above would. It is returned to the general NubArca TV
        // experience rather than unpaired: the party is what is going away, not
        // the television. Both columns move in one statement — the check
        // constraint refuses a general row that still names a party.
        await _db.TvSessions
            .Where(t => t.AssignedPartyAlbumLinkId != null
                && linkIds.Contains(t.AssignedPartyAlbumLinkId.Value))
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.DisplayAssignment, TvDisplayAssignments.General)
                .SetProperty(t => t.AssignedPartyAlbumLinkId, (Guid?)null), cancellationToken);

        await _db.PartyAlbumLinks
            .Where(l => linkIds.Contains(l.Id))
            .ExecuteDeleteAsync(cancellationToken);

        // What the party told its guests, and where it drew its media from.
        await _db.PartyGuestContents
            .Where(c => c.PartyId == partyId)
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PartyMediaSources
            .Where(s => s.PartyId == partyId)
            .ExecuteDeleteAsync(cancellationToken);

        // The ROOT, last: everything above held a restricting foreign key to it
        // or to the album it drew on.
        await _db.Parties
            .Where(p => p.Id == partyId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
