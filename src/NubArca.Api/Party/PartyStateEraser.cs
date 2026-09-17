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
        // The host's "not tonight" decisions. They name a CHALLENGE, so they go
        // before the deck is deleted below, for the same reason a round does.
        await _db.PartyGameExclusions
            .Where(e => gameSessionIds.Contains(e.PartyGameSessionId))
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

        // A television's permission to SHOW this party. It carries a
        // restricting key to the link for the same reason the assignment does,
        // so it has to go before the link — and it goes rather than being
        // revoked, because the party it names is about to stop existing and a
        // revoked row pointing at nothing is not worth keeping.
        await _db.PartyDisplayGrants
            .Where(g => linkIds.Contains(g.PartyAlbumLinkId))
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

        // THE GUEST LIST — who was invited, what each person and group
        // answered, the questions the host asked, and every invitation email's
        // ledger row. Private facts of the Before, and they go with the party
        // they describe. It holds no key to the link or the participants above,
        // so its place in the order is decided by its own keys alone: the groups'
        // rows before the groups, and the questions after the answers that name
        // them.
        await EraseInvitationGroupsAsync(
            _db, _db.PartyInvitationGroups.Where(g => g.PartyId == partyId), cancellationToken);
        await _db.PartyRsvpQuestions
            .Where(q => q.PartyId == partyId)
            .ExecuteDeleteAsync(cancellationToken);

        // ATTENDANCE. A guest's arrival went with the guest list above; the
        // people recorded who were not on it hold a restricting key to the
        // party itself, so they go before the root. Neither was ever tied to a
        // participant, so the participants' own erasure above owes them nothing.
        await _db.PartyAttendanceGuests
            .Where(g => g.PartyId == partyId)
            .ExecuteDeleteAsync(cancellationToken);

        // PARTY CREW. The collaborators hold a restricting key to the party, so
        // they and everything naming them go before the root — and in their own
        // key order: the challenges name an invite AND a collaborator, the
        // device grants name a device AND a collaborator, and both the plain
        // grants and the invites name a collaborator.
        var collaboratorIds = _db.PartyCollaborators
            .Where(c => c.PartyId == partyId)
            .Select(c => c.Id);

        // The devices this party's grants point at, remembered BEFORE the grants
        // are deleted — a device is party-agnostic and is only this party's to
        // delete if nothing else still holds it.
        var touchedDeviceIds = await _db.PartyCollaboratorDeviceGrants
            .Where(g => collaboratorIds.Contains(g.PartyCollaboratorId))
            .Select(g => g.PartyCrewDeviceId)
            .Distinct()
            .ToListAsync(cancellationToken);

        await _db.PartyCollaboratorAuthChallenges
            .Where(c => collaboratorIds.Contains(c.PartyCollaboratorId))
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PartyCollaboratorDeviceGrants
            .Where(g => collaboratorIds.Contains(g.PartyCollaboratorId))
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PartyCollaboratorGrants
            .Where(g => collaboratorIds.Contains(g.PartyCollaboratorId))
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PartyCollaboratorInvites
            .Where(i => collaboratorIds.Contains(i.PartyCollaboratorId))
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PartyCollaborators
            .Where(c => c.PartyId == partyId)
            .ExecuteDeleteAsync(cancellationToken);

        // A device that helped at TWO parties keeps working at the other one:
        // only the devices left holding nothing are this party's to take. The
        // person is not signed out of a party that still exists because a
        // different one was torn down.
        if (touchedDeviceIds.Count > 0)
        {
            await _db.PartyCrewDevices
                .Where(d => touchedDeviceIds.Contains(d.Id))
                .Where(d => !_db.PartyCollaboratorDeviceGrants.Any(g => g.PartyCrewDeviceId == d.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

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

    /// <summary>
    /// Everything a set of invitation groups owns, in foreign-key order: the
    /// delivery ledger, the group's answers, each person's arrival and RSVP, the
    /// people, and the groups themselves — which takes each group's personal
    /// link with it, since the hash lives on the row. Used by the party's
    /// erasure above and by removing a single group, so what a group owns is
    /// stated once, here.
    /// </summary>
    internal static async Task EraseInvitationGroupsAsync(
        AppDbContext db, IQueryable<PartyInvitationGroup> groups, CancellationToken cancellationToken)
    {
        var groupIds = groups.Select(g => g.Id);
        var guestIds = db.PartyGuests
            .Where(g => groupIds.Contains(g.PartyInvitationGroupId))
            .Select(g => g.Id);

        await db.PartyInvitationDeliveries
            .Where(d => groupIds.Contains(d.PartyInvitationGroupId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.PartyRsvpAnswers
            .Where(a => groupIds.Contains(a.PartyInvitationGroupId))
            .ExecuteDeleteAsync(cancellationToken);
        // Each person's ARRIVAL names the person, so it goes before them.
        await db.PartyGuestAttendances
            .Where(a => guestIds.Contains(a.PartyGuestId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.PartyRsvps
            .Where(r => guestIds.Contains(r.PartyGuestId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.PartyGuests
            .Where(g => groupIds.Contains(g.PartyInvitationGroupId))
            .ExecuteDeleteAsync(cancellationToken);
        await groups.ExecuteDeleteAsync(cancellationToken);
    }
}
