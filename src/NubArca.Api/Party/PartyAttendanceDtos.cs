namespace NubArca.Api.Party;

// --- OWNER (party.access, owner-scoped) -------------------------------------
//
// Owner-private by construction: names and arrival times, returned only on
// owner-authenticated routes filtered by Party.OwnerUserId. Deliberately none
// of the guest list's contact details — no email, no phone, no dietary note, no
// answer: the door needs a name and whether that person is here, nothing more.
//
// The personal invitation sees a far smaller slice — its own group's people and
// their own arrivals, on PartyInvitationGuestDto — and none of this.

/// <summary>
/// Who has arrived at one party. The same shape for every party: an open party
/// simply has no groups, and a party with a guest list may also have people
/// who were not on it. Nothing here says which kind of party it is, because
/// there is no such thing — only which rows exist.
/// </summary>
public sealed record PartyAttendanceDto(
    Guid PartyId,
    string PartyStatus,
    // Live and Ended: arrivals may be recorded and corrected. Before the party
    // the list is readable and nobody can have arrived.
    bool CanEdit,
    PartyAttendanceSummaryDto Summary,
    // The guest list's groups in the host's order, each with its people.
    IReadOnlyList<PartyAttendanceGroupDto> Groups,
    // People recorded as arrived who are not on the guest list, latest first.
    IReadOnlyList<PartyAttendanceOtherGuestDto> OtherGuests);

/// <summary>
/// The counts, with ONE definition each — separate from
/// <see cref="PartyRsvpSummaryDto"/>, which describes what people declared.
///
/// <para>ExpectedPeople = guests whose RSVP is attending (named or +1).
/// ExpectedArrived = those of them who arrived; ExpectedMissing = those who have
/// not. UnexpectedKnownGuests = guests on the list who arrived with an RSVP of
/// pending or declined. OtherArrivals = people recorded who are not on the list.
/// TotalArrivals = every guest who arrived plus every other arrival.</para>
/// </summary>
public sealed record PartyAttendanceSummaryDto(
    int ExpectedPeople,
    int ExpectedArrived,
    int ExpectedMissing,
    int UnexpectedKnownGuests,
    int OtherArrivals,
    int TotalArrivals);

public sealed record PartyAttendanceGroupDto(
    Guid GroupId,
    string Label,
    // Named guests first, in the host's order, then the group's +1s.
    IReadOnlyList<PartyAttendanceGuestDto> Guests);

public sealed record PartyAttendanceGuestDto(
    Guid GuestId,
    string Name,
    bool IsAdditionalGuest,
    // What the person declared. Never changed by an arrival.
    string RsvpStatus,
    // Null: not recorded as arrived.
    DateTime? CheckedInAt,
    // Who recorded it first: "owner" or "invitation". Null with CheckedInAt.
    string? CheckInSource);

public sealed record PartyAttendanceOtherGuestDto(
    Guid Id,
    string Name,
    DateTime CheckedInAt,
    int Version);

public enum PartyAttendanceOutcome
{
    Ok,
    NotFound,
    InvalidRequest,
    NotOpen,
    VersionConflict,
    // A guest asked to undo an arrival the HOST recorded. It is the host's
    // record, and only the host corrects it.
    RecordedByHost,
}

public sealed record PartyAttendanceResult(
    PartyAttendanceOutcome Outcome,
    PartyAttendanceDto? Attendance = null,
    string? Error = null,
    // Whether THIS request changed anything. A repeated check-in, a second undo
    // and a replayed add answer with the current state and record nothing.
    bool Changed = false,
    Guid? AttendanceGuestId = null);

/// <summary>Who arrived: the host's record, and the guest's own group's part of it.</summary>
public interface IPartyAttendanceService
{
    Task<PartyAttendanceDto?> GetAsync(
        Guid ownerUserId, Guid partyId, CancellationToken cancellationToken = default);

    /// <summary>This guest arrived. Idempotent: the first moment and source recorded are kept.</summary>
    Task<PartyAttendanceResult> CheckInGuestAsync(
        Guid ownerUserId, Guid partyId, Guid guestId, CancellationToken cancellationToken = default);

    /// <summary>This arrival was recorded by mistake, whoever recorded it. Idempotent. Not a check-out.</summary>
    Task<PartyAttendanceResult> UndoGuestCheckInAsync(
        Guid ownerUserId, Guid partyId, Guid guestId, CancellationToken cancellationToken = default);

    /// <summary>Somebody not on the guest list arrived. Idempotent by <paramref name="clientRequestId"/>.</summary>
    Task<PartyAttendanceResult> CreateOtherGuestAsync(
        Guid ownerUserId, Guid partyId, string? name, Guid clientRequestId,
        CancellationToken cancellationToken = default);

    Task<PartyAttendanceResult> UpdateOtherGuestAsync(
        Guid ownerUserId, Guid partyId, Guid attendanceGuestId, string? name, int expectedVersion,
        CancellationToken cancellationToken = default);

    Task<PartyAttendanceResult> DeleteOtherGuestAsync(
        Guid ownerUserId, Guid partyId, Guid attendanceGuestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// "Sono qui", from the group's own personal invitation: one of ITS people
    /// arrived. Only while the party is live. Converges with the host's
    /// check-in on the same row.
    /// </summary>
    Task<PartyAttendanceResult> CheckInFromInvitationAsync(
        PartyInvitationAccess access, Guid guestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The group takes back its OWN "Sono qui", only while the party is live. An
    /// arrival the host recorded is refused: it is the host's to correct.
    /// </summary>
    Task<PartyAttendanceResult> UndoFromInvitationAsync(
        PartyInvitationAccess access, Guid guestId, CancellationToken cancellationToken = default);
}
