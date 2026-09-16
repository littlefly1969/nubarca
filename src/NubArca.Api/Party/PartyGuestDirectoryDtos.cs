using System.Text.Json.Serialization;

namespace NubArca.Api.Party;

// THE GUEST DIRECTORY — the host's "Ospiti" console, one page at a time.
//
// The full guest list (PartyGuestListDto) states every group, person, answer
// and delivery at once, which is right for a screen that edits a handful and
// wrong for one that must stay usable with a thousand groups. The directory is
// the scalable projection beside it: a party's groups and its other arrivals,
// searched and filtered IN THE DATABASE, read in stable keyset pages, each item
// only as wide as a card needs. The detail of one group is a second read, on
// demand.
//
// Owner-private exactly like the guest list — owner-authenticated routes,
// filtered by Party.OwnerUserId, no-store — and narrower: an item carries names
// and states, never an address, a phone number, a dietary note, an answer, a
// token, a hash or a capability id. The detail carries what the guest list
// already showed the host for that one group.

/// <summary>One request for a page. Every field is optional; see <see cref="PartyGuestDirectoryStates"/>.</summary>
public sealed record PartyGuestDirectoryQuery(string? Q, string? State, string? Cursor, int? Take);

/// <summary>
/// The filters, one closed vocabulary for every phase. Each is a rule about a
/// GROUP ("at least one of its people …") or about an other arrival, stated once
/// in <c>PartyGuestDirectoryService</c> and mirrored by the client contract.
/// </summary>
public static class PartyGuestDirectoryStates
{
    public const string All = "all";

    // Before the party: what people declared, and whether they were invited.
    public const string Pending = "pending";
    public const string Attending = "attending";
    public const string Declined = "declined";
    public const string NotInvited = "not_invited";

    // During and after: who arrived.
    public const string ToArrive = "to_arrive";
    public const string Arrived = "arrived";
    public const string Unexpected = "unexpected";

    public static bool IsKnown(string? state) =>
        state is All or Pending or Attending or Declined or NotInvited or ToArrive or Arrived or Unexpected;

    /// <summary>Other arrivals are arrivals nobody expected: they belong to these filters only.</summary>
    public static bool IncludesOtherArrivals(string state) => state is All or Arrived or Unexpected;
}

public static class PartyGuestDirectoryLimits
{
    public const int DefaultTake = 40;
    public const int MaxTake = 100;
}

public sealed record PartyGuestDirectoryPageDto(
    Guid PartyId,
    string PartyStatus,
    bool MailAvailable,
    bool ShareAvailable,
    // The counts, on the FIRST page only (no cursor): a later page is the same
    // list continued, and its counts would only restate the first.
    PartyGuestDirectorySummaryDto? Summary,
    // Other arrivals first — latest first, the freshest facts at the door —
    // then the groups by label.
    IReadOnlyList<PartyGuestDirectoryItemDto> Items,
    // Opaque. Null when there is nothing more.
    string? NextCursor);

/// <summary>
/// The whole party's counts, with the definitions the guest list and the
/// attendance projection already own: <see cref="PartyRsvpSummaryDto"/> is what
/// people declared, <see cref="PartyAttendanceSummaryDto"/> who arrived. The
/// directory restates neither.
/// </summary>
public sealed record PartyGuestDirectorySummaryDto(
    int Groups,
    int OtherArrivals,
    PartyRsvpSummaryDto Rsvp,
    PartyAttendanceSummaryDto Attendance);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PartyGuestDirectoryGroupItemDto), "group")]
[JsonDerivedType(typeof(PartyGuestDirectoryOtherItemDto), "other")]
public abstract record PartyGuestDirectoryItemDto;

/// <summary>One invitation group, as wide as its card.</summary>
public sealed record PartyGuestDirectoryGroupItemDto(
    Guid GroupId,
    string Label,
    int Version,
    int MaxAdditionalGuests,
    int AdditionalGuestsUsed,
    // Named guests in the host's order, then the group's +1s. Bounded by the
    // domain (20 named, 10 more), so a card never pages its own people.
    IReadOnlyList<PartyGuestDirectoryPersonDto> People,
    PartyGuestDirectoryCountsDto Counts,
    // Where the current link stands, and its most recent delivery on any channel.
    PartyInvitationDeliveryStateDto Invitation,
    // The group's number is certain enough for WhatsApp to open its chat
    // directly. The number itself is not in the item.
    bool WhatsappDirect,
    bool CanSend,
    bool CanRemind,
    bool CanShare) : PartyGuestDirectoryItemDto;

public sealed record PartyGuestDirectoryPersonDto(
    Guid GuestId,
    string Name,
    bool IsAdditionalGuest,
    string RsvpStatus,
    DateTime? CheckedInAt,
    string? CheckInSource,
    // This person — not only their group — matched the search.
    bool Matched);

/// <summary>
/// The group's numbers, with the guest list's definitions: pending and declined
/// count NAMED guests, attending counts everybody coming, arrived everybody
/// recorded as arrived.
/// </summary>
public sealed record PartyGuestDirectoryCountsDto(int Attending, int Pending, int Declined, int Arrived);

/// <summary>Somebody recorded as arrived who is not on the guest list.</summary>
public sealed record PartyGuestDirectoryOtherItemDto(
    Guid Id,
    string Name,
    DateTime CheckedInAt,
    int Version) : PartyGuestDirectoryItemDto;

// --- One group, on demand -------------------------------------------------------

/// <summary>
/// Everything the host may see about ONE group: the guest list's own group row
/// (people, addresses, notes, answers), its arrivals, the history of its link,
/// the prompts its answers answer — and the directory's card and the party's
/// counts, so a page that just changed the group updates both without reading
/// the whole list again.
/// </summary>
public sealed record PartyInvitationGroupDetailDto(
    Guid PartyId,
    string PartyStatus,
    bool MailAvailable,
    bool ShareAvailable,
    PartyInvitationGroupDto Group,
    bool WhatsappDirect,
    IReadOnlyList<PartyGuestArrivalDto> Arrivals,
    // Latest first, every link generation; CurrentLink marks the one that opens.
    IReadOnlyList<PartyInvitationHistoryEntryDto> History,
    // The questions this group's answers refer to, retired ones included.
    IReadOnlyList<PartyRsvpQuestionDto> Questions,
    PartyGuestDirectoryItemDto Item,
    PartyGuestDirectorySummaryDto Summary);

public sealed record PartyGuestArrivalDto(Guid GuestId, DateTime CheckedInAt, string CheckInSource);

public sealed record PartyInvitationHistoryEntryDto(
    string Channel,
    string Kind,
    string Status,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    bool CurrentLink);

public enum PartyGuestDirectoryOutcome
{
    Ok,
    NotFound,
    InvalidRequest,
}

public sealed record PartyGuestDirectoryResult(
    PartyGuestDirectoryOutcome Outcome,
    PartyGuestDirectoryPageDto? Page = null,
    string? Error = null);

/// <summary>The host's guest console: pages of the directory, one group's detail, the questions.</summary>
public interface IPartyGuestDirectoryService
{
    Task<PartyGuestDirectoryResult> PageAsync(
        Guid ownerUserId, Guid partyId, PartyGuestDirectoryQuery query,
        CancellationToken cancellationToken = default);

    Task<PartyInvitationGroupDetailDto?> GroupAsync(
        Guid ownerUserId, Guid partyId, Guid groupId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PartyRsvpQuestionDto>?> QuestionsAsync(
        Guid ownerUserId, Guid partyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One group as the directory lists it, for a caller that has already
    /// resolved ownership (the share route answers with it).
    /// </summary>
    Task<PartyGuestDirectoryGroupItemDto?> ItemAsync(
        Guid partyId, string partyStatus, Guid groupId, CancellationToken cancellationToken = default);
}
