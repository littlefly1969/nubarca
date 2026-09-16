using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// Who arrived — the single writer of attendance, for both of its sources.
///
/// <para>The HOST's calls are owner-scoped at the database boundary: the party
/// is matched on <c>OwnerUserId</c> first, and a guest or a recorded person is
/// then matched on BOTH its own id and that party's — a guest through its group
/// — so a foreign id is the same not-found as a missing one. The GUEST's calls
/// arrive with a resolved personal invitation, and a guest id is then matched on
/// that invitation's GROUP: another group's person, at this party or any other,
/// is the same not-found as one that does not exist.</para>
///
/// <para>It writes attendance and nothing else. No RSVP moves, no party is
/// published or started, and no <see cref="PartyParticipant"/> is created, read
/// or changed: an arrival is not a browser, and a browser is not an arrival.</para>
/// </summary>
public sealed class PartyAttendanceService : IPartyAttendanceService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public PartyAttendanceService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<PartyAttendanceDto?> GetAsync(
        Guid ownerUserId, Guid partyId, CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        return status is null ? null : await ProjectAsync(partyId, status, cancellationToken);
    }

    // --- The host: people on the guest list ---------------------------------------

    public async Task<PartyAttendanceResult> CheckInGuestAsync(
        Guid ownerUserId, Guid partyId, Guid guestId, CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        if (status is null || !await IsGuestOfPartyAsync(partyId, guestId, cancellationToken))
        {
            return new(PartyAttendanceOutcome.NotFound);
        }
        if (!PartyAttendancePolicy.IsOpen(status)) return await NotOpenAsync(partyId, status, cancellationToken);

        var recorded = await RecordArrivalAsync(
            guestId, PartyAttendanceSources.Owner,
            ct => IsGuestOfPartyAsync(partyId, guestId, ct), cancellationToken);
        return recorded is bool changed
            ? new(PartyAttendanceOutcome.Ok, await ProjectAsync(partyId, status, cancellationToken), Changed: changed)
            : new(PartyAttendanceOutcome.NotFound);
    }

    public async Task<PartyAttendanceResult> UndoGuestCheckInAsync(
        Guid ownerUserId, Guid partyId, Guid guestId, CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        if (status is null || !await IsGuestOfPartyAsync(partyId, guestId, cancellationToken))
        {
            return new(PartyAttendanceOutcome.NotFound);
        }
        if (!PartyAttendancePolicy.IsOpen(status)) return await NotOpenAsync(partyId, status, cancellationToken);

        // A correction, not a departure: the arrival is removed as if it had
        // never been recorded, whichever side recorded it — the host's list is
        // the host's to correct. A second undo finds nothing and changes nothing.
        var deleted = await _db.PartyGuestAttendances
            .Where(a => a.PartyGuestId == guestId)
            .ExecuteDeleteAsync(cancellationToken);

        return new(PartyAttendanceOutcome.Ok, await ProjectAsync(partyId, status, cancellationToken), Changed: deleted > 0);
    }

    // --- The host: people who are not on the guest list ---------------------------

    public async Task<PartyAttendanceResult> CreateOtherGuestAsync(
        Guid ownerUserId, Guid partyId, string? name, Guid clientRequestId,
        CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        if (status is null) return new(PartyAttendanceOutcome.NotFound);
        var normalized = NormalizeName(name);
        if (normalized is null) return new(PartyAttendanceOutcome.InvalidRequest, Error: "invalid_name");
        if (clientRequestId == Guid.Empty) return new(PartyAttendanceOutcome.InvalidRequest, Error: "invalid_request");

        // THE SAME ADD, AGAIN. A retry or a second tap carrying this add's id is
        // answered by the row the first one made, and names nobody twice.
        if (await FindByRequestAsync(partyId, clientRequestId, cancellationToken) is Guid replayed)
        {
            return new(
                PartyAttendanceOutcome.Ok, await ProjectAsync(partyId, status, cancellationToken),
                AttendanceGuestId: replayed);
        }
        if (!PartyAttendancePolicy.IsOpen(status)) return await NotOpenAsync(partyId, status, cancellationToken);
        if (await _db.PartyAttendanceGuests.CountAsync(g => g.PartyId == partyId, cancellationToken)
            >= PartyAttendanceLimits.MaxOtherGuestsPerParty)
        {
            return new(PartyAttendanceOutcome.InvalidRequest, Error: "too_many_arrivals");
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var row = new PartyAttendanceGuest
        {
            Id = Guid.NewGuid(),
            PartyId = partyId,
            Name = normalized,
            SearchText = PartySearchText.ForName(normalized),
            ClientRequestId = clientRequestId,
            CheckedInAt = now,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.PartyAttendanceGuests.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The unique index refused it: the same add arrived twice and the
            // other copy recorded first. Its row is the answer.
            _db.ChangeTracker.Clear();
            if (await FindByRequestAsync(partyId, clientRequestId, CancellationToken.None) is Guid winner)
            {
                return new(
                    PartyAttendanceOutcome.Ok, await ProjectAsync(partyId, status, cancellationToken),
                    AttendanceGuestId: winner);
            }
            throw;
        }
        _db.ChangeTracker.Clear();

        return new(
            PartyAttendanceOutcome.Ok, await ProjectAsync(partyId, status, cancellationToken),
            Changed: true, AttendanceGuestId: row.Id);
    }

    public async Task<PartyAttendanceResult> UpdateOtherGuestAsync(
        Guid ownerUserId, Guid partyId, Guid attendanceGuestId, string? name, int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        if (status is null || !await IsOtherGuestOfPartyAsync(partyId, attendanceGuestId, cancellationToken))
        {
            return new(PartyAttendanceOutcome.NotFound);
        }
        var normalized = NormalizeName(name);
        if (normalized is null) return new(PartyAttendanceOutcome.InvalidRequest, Error: "invalid_name");
        if (!PartyAttendancePolicy.IsOpen(status)) return await NotOpenAsync(partyId, status, cancellationToken);

        // The version is spent by the statement that writes the name, so two
        // corrections quoting one version cannot both land.
        var now = _clock.GetUtcNow().UtcDateTime;
        var updated = await _db.PartyAttendanceGuests
            .Where(g => g.Id == attendanceGuestId && g.PartyId == partyId && g.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.Name, normalized)
                .SetProperty(g => g.SearchText, PartySearchText.ForName(normalized))
                .SetProperty(g => g.Version, g => g.Version + 1)
                .SetProperty(g => g.UpdatedAt, now), cancellationToken);
        if (updated == 0)
        {
            return await IsOtherGuestOfPartyAsync(partyId, attendanceGuestId, cancellationToken)
                ? new(
                    PartyAttendanceOutcome.VersionConflict,
                    await ProjectAsync(partyId, status, cancellationToken),
                    "version_conflict")
                : new(PartyAttendanceOutcome.NotFound);
        }

        return new(
            PartyAttendanceOutcome.Ok, await ProjectAsync(partyId, status, cancellationToken),
            Changed: true, AttendanceGuestId: attendanceGuestId);
    }

    public async Task<PartyAttendanceResult> DeleteOtherGuestAsync(
        Guid ownerUserId, Guid partyId, Guid attendanceGuestId, CancellationToken cancellationToken = default)
    {
        var status = await OwnedPartyStatusAsync(ownerUserId, partyId, cancellationToken);
        if (status is null || !await IsOtherGuestOfPartyAsync(partyId, attendanceGuestId, cancellationToken))
        {
            return new(PartyAttendanceOutcome.NotFound);
        }
        if (!PartyAttendancePolicy.IsOpen(status)) return await NotOpenAsync(partyId, status, cancellationToken);

        var deleted = await _db.PartyAttendanceGuests
            .Where(g => g.Id == attendanceGuestId && g.PartyId == partyId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 0
            ? new(PartyAttendanceOutcome.NotFound)
            : new(
                PartyAttendanceOutcome.Ok, await ProjectAsync(partyId, status, cancellationToken),
                Changed: true, AttendanceGuestId: attendanceGuestId);
    }

    // --- The guest's own group, on its personal invitation ------------------------
    //
    // The personal token reaches its own group's people and nothing else: no
    // summary, no other group, no other arrival. Its answers are the invitation
    // view, composed by the caller, so a refusal here carries no attendance.

    public async Task<PartyAttendanceResult> CheckInFromInvitationAsync(
        PartyInvitationAccess access, Guid guestId, CancellationToken cancellationToken = default)
    {
        if (!await IsGuestOfGroupAsync(access.GroupId, guestId, cancellationToken))
        {
            return new(PartyAttendanceOutcome.NotFound);
        }
        if (!access.CanCheckIn) return new(PartyAttendanceOutcome.NotOpen, Error: "attendance_not_open");

        var recorded = await RecordArrivalAsync(
            guestId, PartyAttendanceSources.Invitation,
            ct => IsGuestOfGroupAsync(access.GroupId, guestId, ct), cancellationToken);
        return recorded is bool changed
            ? new(PartyAttendanceOutcome.Ok, Changed: changed)
            : new(PartyAttendanceOutcome.NotFound);
    }

    public async Task<PartyAttendanceResult> UndoFromInvitationAsync(
        PartyInvitationAccess access, Guid guestId, CancellationToken cancellationToken = default)
    {
        if (!await IsGuestOfGroupAsync(access.GroupId, guestId, cancellationToken))
        {
            return new(PartyAttendanceOutcome.NotFound);
        }
        if (!access.CanCheckIn) return new(PartyAttendanceOutcome.NotOpen, Error: "attendance_not_open");

        // Only the group's OWN "Sono qui". The source is part of the statement,
        // so an arrival the host recorded cannot be taken back from a phone,
        // however the two requests interleave.
        var deleted = await _db.PartyGuestAttendances
            .Where(a => a.PartyGuestId == guestId && a.Source == PartyAttendanceSources.Invitation)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0) return new(PartyAttendanceOutcome.Ok, Changed: true);

        return await _db.PartyGuestAttendances.AnyAsync(a => a.PartyGuestId == guestId, cancellationToken)
            ? new(PartyAttendanceOutcome.RecordedByHost, Error: "attendance_recorded_by_host")
            : new(PartyAttendanceOutcome.Ok);
    }

    // --- The one arrival row ------------------------------------------------------

    /// <summary>
    /// Writes the guest's arrival, or finds it already written. True: this call
    /// recorded it. False: it was already recorded — by either source — and
    /// nothing changed, neither its moment nor its source. Null: the guest is
    /// gone. The primary key is the boundary: two check-ins meeting at the same
    /// instant, from the same side or from both, leave one row.
    /// </summary>
    private async Task<bool?> RecordArrivalAsync(
        Guid guestId, string source, Func<CancellationToken, Task<bool>> guestStillExists,
        CancellationToken cancellationToken)
    {
        if (await _db.PartyGuestAttendances.AnyAsync(a => a.PartyGuestId == guestId, cancellationToken))
        {
            return false;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        _db.PartyGuestAttendances.Add(new PartyGuestAttendance
        {
            PartyGuestId = guestId,
            CheckedInAt = now,
            Source = source,
            CreatedAt = now,
        });
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // The key refused it: another check-in of this person committed
            // first, and ITS moment and source are the arrival. Or the person
            // was taken off the list under us, which is the same not-found as
            // one never on it.
            _db.ChangeTracker.Clear();
            if (await _db.PartyGuestAttendances.AnyAsync(a => a.PartyGuestId == guestId, CancellationToken.None))
            {
                return false;
            }
            if (!await guestStillExists(CancellationToken.None)) return null;
            throw;
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    // --- Projection -----------------------------------------------------------------

    private async Task<PartyAttendanceDto> ProjectAsync(
        Guid partyId, string partyStatus, CancellationToken cancellationToken)
    {
        var groupsOfParty = _db.PartyInvitationGroups.AsNoTracking().Where(g => g.PartyId == partyId);
        var groups = await groupsOfParty
            .OrderBy(g => g.CreatedAt).ThenBy(g => g.Id)
            .Select(g => new { g.Id, g.Label })
            .ToListAsync(cancellationToken);

        var guests = await (
            from guest in _db.PartyGuests.AsNoTracking()
            join g in groupsOfParty on guest.PartyInvitationGroupId equals g.Id
            join r in _db.PartyRsvps.AsNoTracking() on guest.Id equals r.PartyGuestId into rsvps
            from r in rsvps.DefaultIfEmpty()
            join a in _db.PartyGuestAttendances.AsNoTracking() on guest.Id equals a.PartyGuestId into arrivals
            from a in arrivals.DefaultIfEmpty()
            select new
            {
                guest.Id,
                guest.PartyInvitationGroupId,
                guest.Name,
                guest.IsAdditionalGuest,
                guest.SortOrder,
                guest.CreatedAt,
                Status = r == null ? PartyRsvpStatuses.Pending : r.Status,
                CheckedInAt = a == null ? (DateTime?)null : a.CheckedInAt,
                Source = a == null ? null : a.Source,
            }).ToListAsync(cancellationToken);
        var guestsByGroup = guests.ToLookup(x => x.PartyInvitationGroupId);

        var others = await _db.PartyAttendanceGuests.AsNoTracking()
            .Where(g => g.PartyId == partyId)
            .OrderByDescending(g => g.CheckedInAt).ThenBy(g => g.Id)
            .Select(g => new PartyAttendanceOtherGuestDto(g.Id, g.Name, g.CheckedInAt, g.Version))
            .ToListAsync(cancellationToken);

        return new PartyAttendanceDto(
            partyId,
            partyStatus,
            PartyAttendancePolicy.IsOpen(partyStatus),
            Summarize(guests.Select(x => (x.Status, x.CheckedInAt is not null)), others.Count),
            groups.Select(group => new PartyAttendanceGroupDto(
                group.Id,
                group.Label,
                guestsByGroup[group.Id]
                    .OrderBy(x => x.IsAdditionalGuest)
                    .ThenBy(x => x.SortOrder)
                    .ThenBy(x => x.CreatedAt)
                    .Select(x => new PartyAttendanceGuestDto(
                        x.Id, x.Name, x.IsAdditionalGuest, x.Status, x.CheckedInAt, x.Source))
                    .ToList())).ToList(),
            others);
    }

    /// <summary>
    /// The counts, as one pure function over (what each guest declared, whether
    /// they arrived) and how many other people arrived. An open party passes no
    /// guests at all, and its total is its other arrivals.
    /// </summary>
    public static PartyAttendanceSummaryDto Summarize(
        IEnumerable<(string RsvpStatus, bool Arrived)> guests, int otherArrivals)
    {
        var expected = 0;
        var expectedArrived = 0;
        var unexpectedKnown = 0;
        var knownArrived = 0;
        foreach (var (rsvpStatus, arrived) in guests)
        {
            if (rsvpStatus == PartyRsvpStatuses.Attending)
            {
                expected++;
                if (arrived) expectedArrived++;
            }
            else if (arrived)
            {
                unexpectedKnown++;
            }
            if (arrived) knownArrived++;
        }
        return new PartyAttendanceSummaryDto(
            ExpectedPeople: expected,
            ExpectedArrived: expectedArrived,
            ExpectedMissing: expected - expectedArrived,
            UnexpectedKnownGuests: unexpectedKnown,
            OtherArrivals: otherArrivals,
            TotalArrivals: knownArrived + otherArrivals);
    }

    // --- Helpers ----------------------------------------------------------------------

    private async Task<string?> OwnedPartyStatusAsync(
        Guid ownerUserId, Guid partyId, CancellationToken cancellationToken) =>
        await _db.Parties.AsNoTracking()
            .Where(p => p.Id == partyId && p.OwnerUserId == ownerUserId)
            .Select(p => p.Status)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The guest belongs to THIS party, through its group — never trusted by id alone.</summary>
    private Task<bool> IsGuestOfPartyAsync(Guid partyId, Guid guestId, CancellationToken cancellationToken) =>
        (from guest in _db.PartyGuests.AsNoTracking()
         join g in _db.PartyInvitationGroups.AsNoTracking() on guest.PartyInvitationGroupId equals g.Id
         where guest.Id == guestId && g.PartyId == partyId
         select guest.Id).AnyAsync(cancellationToken);

    /// <summary>The guest belongs to THIS group — the whole of a personal invitation's reach.</summary>
    private Task<bool> IsGuestOfGroupAsync(Guid groupId, Guid guestId, CancellationToken cancellationToken) =>
        _db.PartyGuests.AsNoTracking()
            .AnyAsync(g => g.Id == guestId && g.PartyInvitationGroupId == groupId, cancellationToken);

    private Task<bool> IsOtherGuestOfPartyAsync(
        Guid partyId, Guid attendanceGuestId, CancellationToken cancellationToken) =>
        _db.PartyAttendanceGuests.AsNoTracking()
            .AnyAsync(g => g.Id == attendanceGuestId && g.PartyId == partyId, cancellationToken);

    private async Task<Guid?> FindByRequestAsync(
        Guid partyId, Guid clientRequestId, CancellationToken cancellationToken) =>
        await _db.PartyAttendanceGuests.AsNoTracking()
            .Where(g => g.PartyId == partyId && g.ClientRequestId == clientRequestId)
            .Select(g => (Guid?)g.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<PartyAttendanceResult> NotOpenAsync(
        Guid partyId, string partyStatus, CancellationToken cancellationToken) =>
        new(PartyAttendanceOutcome.NotOpen,
            await ProjectAsync(partyId, partyStatus, cancellationToken),
            "attendance_not_open");

    private static string? NormalizeName(string? name)
    {
        var normalized = PartyInvitationLimits.Normalize(name);
        return normalized is not null && PartyInvitationLimits.Fits(normalized, PartyAttendanceLimits.MaxNameLength)
            ? normalized
            : null;
    }
}
