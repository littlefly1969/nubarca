namespace NubArca.Api.Domain;

// ATTENDANCE — who actually arrived, recorded by the host.
//
// Three facts about people meet at a party, and this file adds the third
// without touching the other two:
//
//   PartyRsvp        what a person DECLARED before the party
//   PartyParticipant the anonymous BROWSER that uses the public party
//   attendance       who the host saw ARRIVE
//
// They are allowed to disagree, and nothing reconciles them. A guest who
// declined and came anyway keeps "declined" as the historical answer and gains
// an arrival; a guest who confirmed and never came keeps "attending" and has no
// arrival. Scanning the party's QR is not an arrival: the phone that scanned it
// is a PartyParticipant, and nothing here binds one to the other — not by name,
// email, phone, QR, cookie, IP or device.
//
// The guest list is OPTIONAL. A party with no invitation group at all — an
// open party — records its arrivals as PartyAttendanceGuest rows only, and a
// party with a guest list records the people on it as PartyGuestAttendance and
// everybody else as PartyAttendanceGuest. There is no party "type": which of
// the two a party has is simply which rows exist.
//
// Every row is owner-private. It reaches no public party surface, no personal
// invitation, the TV, the game, print, face search, a link preview, a log line
// or an audit record (audit lines carry ids only).

/// <summary>
/// The arrival of a person who is on the guest list. One row = arrived; no row
/// = not recorded as arrived. The key IS the guest, so a person arrives once —
/// however many times, from however many places, and however concurrently.
///
/// <para>TWO SOURCES, ONE FACT. The host may record it from the workspace, and
/// the guest's own group may record it from its personal invitation while the
/// party is live ("Sono qui"). Both write this same row; whichever succeeds
/// first sets <see cref="CheckedInAt"/> and <see cref="Source"/>, and every
/// later check-in from either side changes neither.</para>
///
/// <para>Deliberately no status, no check-out, no participant id, no token,
/// no device and no IP: this records that somebody arrived, not where they are
/// now, and not which phone they carry.</para>
/// </summary>
public class PartyGuestAttendance
{
    /// <summary>Primary key and foreign key: one arrival per guest, never two.</summary>
    public Guid PartyGuestId { get; set; }

    /// <summary>When the arrival was first recorded. Kept by a repeated check-in.</summary>
    public DateTime CheckedInAt { get; set; }

    /// <summary>Who recorded it first — see <see cref="PartyAttendanceSources"/>.</summary>
    public string Source { get; set; } = PartyAttendanceSources.Owner;

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Where an arrival was recorded from. Closed, and held by the database as well
/// as here: a future source — a door scanner, a kiosk — is a slice that widens
/// the constraint and decides what that source may do, not a free string.
/// </summary>
public static class PartyAttendanceSources
{
    /// <summary>The host, from the party's workspace.</summary>
    public const string Owner = "owner";

    /// <summary>The guest's own group, from its personal invitation, while the party is live.</summary>
    public const string Invitation = "invitation";

    public static bool IsKnown(string? source) => source is Owner or Invitation;
}

/// <summary>
/// A person the host recorded as arrived who is NOT a <see cref="PartyGuest"/>
/// — every arrival at an open party, and anybody at an invited party who was
/// not on its list. A name and a moment, and nothing else: no RSVP, no
/// invitation, no email, no capability and no participant.
///
/// <para><see cref="ClientRequestId"/> is minted by the host's browser once per
/// "add" and reused for that add's retries, so a double tap or a replayed
/// request names the person once. <see cref="Version"/> guards a rename.</para>
/// </summary>
public class PartyAttendanceGuest
{
    public Guid Id { get; set; }
    public Guid PartyId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The name folded by <see cref="PartySearchText"/>. Derived.</summary>
    public string SearchText { get; set; } = string.Empty;

    public Guid ClientRequestId { get; set; }

    public DateTime CheckedInAt { get; set; }

    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>When the host may record, correct and remove arrivals.</summary>
public static class PartyAttendancePolicy
{
    /// <summary>
    /// While the party is happening and after it: Ended stays writable so a
    /// forgotten or mistaken arrival can be corrected once the evening is over.
    /// Before the party nobody has arrived, and recording an arrival never
    /// publishes or starts a party.
    /// </summary>
    public static bool IsOpen(string? partyStatus) =>
        partyStatus is PartyStatuses.Live or PartyStatuses.Ended;
}

/// <summary>What a host may type, measured in Unicode code points like every Party text limit.</summary>
public static class PartyAttendanceLimits
{
    public const int MaxNameLength = 120;

    /// <summary>
    /// A ceiling against accidents and abuse, not a product limit: the whole
    /// list is one owner read, and no door records more people than this.
    /// </summary>
    public const int MaxOtherGuestsPerParty = 5000;
}
