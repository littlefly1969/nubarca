using System.Text.Json.Serialization;

using NubArca.Api.Print;

namespace NubArca.Api.Party;

// Party Crew's wire shapes.
//
// NOTHING here carries a raw token, a hash, an owner id, an album id, a device
// token or a guest's personal data. The invite link is the one exception and it
// is returned exactly once, from the mutation that minted it, and never from a
// read — a list endpoint that could re-issue links would make every owner
// session a way to re-open every collaborator's access.

/// <summary>One collaborator, as the OWNER's settings surface lists them.</summary>
public sealed record PartyCollaboratorDto(
    Guid Id,
    string DisplayName,
    /// Owner-private. It is in this DTO because the owner chose it and must be
    /// able to correct it; it is in no other projection anywhere.
    string Email,
    string RoleKey,
    int Version,
    DateTime CreatedAt,
    /// What this role currently grants, so the surface can explain a choice
    /// rather than make the owner guess what "Regista" means.
    IReadOnlyList<string> Capabilities,
    /// Devices in use, and the ceiling. "2/2" is a product statement.
    int ActiveDevices,
    int MaxDevices,
    /// A link exists and has not expired or been used. The link ITSELF is not here.
    bool HasPendingInvite,
    DateTime? InviteExpiresAt,
    IReadOnlyList<PartyCrewDeviceDto> Devices);

/// <summary>
/// One paired device, in the only terms anybody needs to recognise it.
///
/// <para>A label and two timestamps. No token, no hash, no IP history, no
/// fingerprint: enough for a person to say "that is my old phone", and nothing
/// that would make this list worth stealing.</para>
/// </summary>
public sealed record PartyCrewDeviceDto(
    Guid GrantId,
    string Label,
    DateTime PairedAt,
    DateTime? LastUsedAt,
    /// True for the device asking. Only ever set on the crew's own view of
    /// itself; the owner's list has no "current" device.
    bool IsCurrent);

/// <summary>The one time a link is returned: straight after it was minted.</summary>
public sealed record PartyCollaboratorInviteDto(
    Guid CollaboratorId,
    /// Absolute, on the operator's configured public origin — never on a
    /// request's Host header.
    string InviteUrl,
    DateTime ExpiresAt);

/// <summary>Creating a collaborator. The owner supplies all three; nothing is inferred.</summary>
public sealed record PartyCollaboratorWriteDto(
    string DisplayName,
    string Email,
    string RoleKey);

/// <summary>Editing one. The version is the concurrency check, as everywhere else in Party.</summary>
public sealed record PartyCollaboratorUpdateDto(
    string DisplayName,
    string Email,
    string RoleKey,
    int Version);

/// <summary>What the owner's Collaborators panel needs in one read.</summary>
public sealed record PartyCrewOverviewDto(
    IReadOnlyList<PartyCollaboratorDto> Collaborators,
    /// Party Crew cannot work without outbound mail: the second factor IS an
    /// email. Said plainly rather than by minting a link that cannot be used.
    bool MailAvailable,
    /// The roles this release lets an owner pick, in product order.
    IReadOnlyList<string> AssignableRoles);

// ── The pairing surface ─────────────────────────────────────────────────────

/// <summary>
/// What the invited person is shown before anything is sent.
///
/// <para>Party-safe only: the party's name, the role they were invited as, and
/// a MASKED address so they can tell whether the code is coming somewhere they
/// can reach. Never the full address — a forwarded link would otherwise leak
/// it — and never an owner, album or party id.</para>
/// </summary>
public sealed record PartyCrewChallengeStartedDto(
    string PartyTitle,
    string RoleKey,
    /// <c>l••••@example.com</c>. Enough to recognise, not enough to learn.
    string MaskedEmail,
    DateTime ExpiresAt);

/// <summary>
/// The outcome of a correct code.
///
/// <para>On the wire as a NAME, not an ordinal: a client reading <c>1</c> would
/// have to know the declaration order, and a value inserted above it later
/// would silently change what every existing client believes happened.</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PartyCrewVerifyOutcome>))]
public enum PartyCrewVerifyOutcome
{
    /// A device was created and the session cookie is set. Go to the party.
    Paired,

    /// <summary>
    /// The code was right and the person is who they said — but the
    /// collaborator already holds two devices, so no third grant was created.
    ///
    /// <para>Deliberately not an error: the challenge stays verified, and the
    /// same browser may now list those two devices, revoke one, and complete
    /// without typing a second code.</para>
    /// </summary>
    DeviceLimitReached,
}

public sealed record PartyCrewVerifyResultDto(
    PartyCrewVerifyOutcome Outcome,
    /// Present only for <see cref="PartyCrewVerifyOutcome.Paired"/>.
    Guid? PartyId,
    string? PartyTitle,
    string? RoleKey,
    IReadOnlyList<string>? Capabilities,
    /// Present only for <see cref="PartyCrewVerifyOutcome.DeviceLimitReached"/>:
    /// this collaborator's own two devices, so one can be chosen and removed.
    IReadOnlyList<PartyCrewDeviceDto>? Devices);

/// <summary>
/// One party this browser may operate, as a switcher would draw it.
///
/// <para>Derived from this DEVICE's grants alone. It never enumerates the
/// owner's other parties, carries no album, no owner and no capability, and
/// exists because one browser may legitimately hold several assignments.</para>
/// </summary>
public sealed record PartyCrewAssignmentDto(
    Guid PartyId,
    string PartyTitle,
    string DisplayName,
    string RoleKey);

/// <summary>
/// The party's print profile, as a COLLABORATOR may write it.
///
/// <para>It is the owner's request shape minus two fields, and their absence is
/// the boundary: <c>PrintStationId</c> and <c>PrinterDeviceId</c> name the
/// venue's hardware, which outlives this evening and belongs to whoever runs
/// the installation. <c>party-crew.print.manage</c> is what guests may print
/// and how much of it — not which machine it comes out of.</para>
///
/// <para>A separate type rather than a validated one on purpose: a request that
/// cannot carry a field cannot smuggle one, and there is no branch anywhere
/// that could be got wrong later.</para>
/// </summary>
public sealed record PartyCrewPrintProfileRequest(
    bool? Enabled,
    bool? PhotoEnabled,
    int? PhotoMaxPrints,
    int? PhotoPrintsPerGuest,
    bool? StripEnabled,
    int? StripMaxPrints,
    int? StripPrintsPerGuest,
    string? FooterText);

/// <summary>
/// The party's print profile, as a COLLABORATOR may read it.
///
/// <para>The owner's projection carries <c>PrintStationId</c> and
/// <c>PrinterDeviceId</c>. A collaborator cannot change them — the write shape
/// has no such fields — but they have no business SEEING them either: those
/// identify the installation's hardware, which outlives this evening and is
/// not the party's data. What matters here is whether printing is set up at
/// all, which is a fact about tonight.</para>
/// </summary>
public sealed record PartyCrewPrintProfileDto(
    bool Enabled,
    /// <summary>A printer is configured. Which one is the host's business.</summary>
    bool PrinterConfigured,
    PartyPrintProductSettingsDto Photo,
    PartyPrintProductSettingsDto Strip,
    string? FooterText,
    int FooterMaxLength,
    int MinBudget,
    int MaxBudget)
{
    public static PartyCrewPrintProfileDto From(PartyPrintProfileDto profile) => new(
        profile.Enabled,
        profile.PrintStationId is not null && profile.PrinterDeviceId is not null,
        profile.Photo,
        profile.Strip,
        profile.FooterText,
        profile.FooterMaxLength,
        profile.MinBudget,
        profile.MaxBudget);
}

/// <summary>Everything this browser may operate. Nothing else about the owner.</summary>
public sealed record PartyCrewMeDto(IReadOnlyList<PartyCrewAssignmentDto> Assignments);

/// <summary>The crew's own view of where they are and what they may do.</summary>
public sealed record PartyCrewSessionDto(
    Guid PartyId,
    string PartyTitle,
    string DisplayName,
    string RoleKey,
    IReadOnlyList<string> Capabilities);
