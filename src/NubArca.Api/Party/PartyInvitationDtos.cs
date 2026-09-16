using System.Text.Json;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

// --- OWNER (party.access, owner-scoped) -------------------------------------
//
// Owner-private by construction: these carry names, addresses, phone numbers,
// allergies and answers, and are returned only on owner-authenticated routes
// filtered by Party.OwnerUserId. None of them carries a token, a token hash or a
// capability id — the owner never needs the personal link itself, only whether
// it has been sent.

/// <summary>The whole guest list of one party, with the counts the host reads first.</summary>
public sealed record PartyGuestListDto(
    Guid PartyId,
    string PartyStatus,
    // Whether an invitation could be emailed at all on this installation: SMTP
    // configured AND a public origin to build the link on. False is a state the
    // host is told about, never a send that silently fails.
    bool MailAvailable,
    // Whether the personal link can be handed to the host at all (WhatsApp,
    // copy): a public origin to build it on. SMTP is not needed for it.
    bool ShareAvailable,
    PartyRsvpSummaryDto Summary,
    IReadOnlyList<PartyInvitationGroupDto> Groups,
    IReadOnlyList<PartyRsvpQuestionDto> Questions);

/// <summary>
/// The counts, with ONE definition each so the UI, the API and the tests cannot
/// invent competing ones. Invited = named guests. MissingResponses = named
/// guests still pending. Attending = every guest, named or +1, who is coming.
/// Declined = named guests who said no. ExpectedPeople = Attending. None of it is
/// attendance: nobody checked in.
/// </summary>
public sealed record PartyRsvpSummaryDto(
    int Groups,
    int Invited,
    int MissingResponses,
    int Attending,
    int Declined,
    int ExpectedPeople,
    int UnansweredGroups);

public sealed record PartyInvitationGroupDto(
    Guid Id,
    string Label,
    string RecipientEmail,
    string? Phone,
    int MaxAdditionalGuests,
    int Version,
    // Named guests first, in the host's order, then the +1s the group added.
    IReadOnlyList<PartyGuestDto> Guests,
    int AdditionalGuestsUsed,
    int PendingCount,
    int AttendingCount,
    int DeclinedCount,
    // Every stored answer, including answers to questions since deactivated:
    // they are the host's private history.
    IReadOnlyList<PartyRsvpAnswerDto> Answers,
    PartyInvitationDeliveryStateDto Delivery,
    bool CanSend,
    bool CanRemind,
    // The link may be handed to the host to share (WhatsApp, copy): the party
    // still takes invitations and the installation has a public origin.
    bool CanShare);

public sealed record PartyGuestDto(
    Guid Id,
    string Name,
    string? Email,
    string? Phone,
    bool IsAdditionalGuest,
    string Status,
    string? DietaryNotes,
    DateTime? RespondedAt);

public sealed record PartyRsvpAnswerDto(Guid QuestionId, JsonElement Value);

/// <summary>
/// Where the CURRENT link generation stands. A rotation starts it again at
/// <c>not_sent</c>: a delivery that carried a dead link invited nobody.
/// <c>sent</c>: an email SMTP accepted. <c>shared</c>: no such email, but the
/// link was handed to the host (WhatsApp, copy) — which is not proof of a
/// message. <c>pending</c> is "not confirmed" — an email attempt was recorded and
/// its outcome never was — and is shown as exactly that.
///
/// <para>The <c>LastAttempt*</c> fields describe the most recent delivery of
/// the current link on ANY channel: the one line a list card shows.</para>
/// </summary>
public sealed record PartyInvitationDeliveryStateDto(
    string State,
    DateTime? LastAttemptAt,
    string? LastAttemptKind,
    string? LastAttemptStatus,
    DateTime? LastSentAt,
    string? LastAttemptChannel);

public static class PartyInvitationDeliveryStates
{
    public const string NotSent = "not_sent";
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Shared = "shared";
}

public sealed record PartyRsvpQuestionDto(
    Guid Id,
    string Prompt,
    string Kind,
    bool Required,
    IReadOnlyList<string> Options,
    bool IsActive,
    int SortOrder,
    int Version,
    int AnswerCount,
    // Answered at least once: what it asks is frozen, and only its activation
    // and position may change.
    bool Locked);

/// <summary>One delivery attempt as the host sees it. No address, no body, no SMTP text.</summary>
public sealed record PartyInvitationDeliveryDto(
    string Kind,
    string Status,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    // True when this request's id had already been used: the answer is the
    // earlier attempt, and nothing was sent again.
    bool Replayed,
    string Channel);

/// <summary>
/// The personal link handed to the HOST, to share themselves. Returned only on
/// the owner's share route, <c>no-store</c>, and never persisted, logged or
/// audited: the ledger keeps that it was shared, not what.
/// </summary>
public sealed record PartyInvitationShareDto(
    string Channel,
    string Kind,
    // Always "shared": NubArca handed the link over. Not sent, delivered or read.
    string Status,
    // The group's current personal invitation, on the operator's public origin.
    string Url,
    // The message, composed in the host's language.
    string Text,
    // WhatsApp click-to-chat for the whatsapp channel (a direct chat only for a
    // certain international number), null for copy.
    string? WhatsappUrl,
    DateTime CreatedAt,
    bool Replayed);

public sealed record PartyInvitationGroupWrite(
    string? Label,
    string? RecipientEmail,
    string? Phone,
    int MaxAdditionalGuests,
    // NAMED guests only. The +1s belong to the group's own RSVP, so the host's
    // editor never has to round-trip them to keep them.
    IReadOnlyList<PartyNamedGuestWrite>? Guests);

public sealed record PartyNamedGuestWrite(Guid? Id, string? Name, string? Email = null, string? Phone = null);

public sealed record PartyRsvpQuestionWrite(
    string? Prompt,
    string? Kind,
    bool Required,
    IReadOnlyList<string?>? Options,
    bool IsActive = true);

public enum PartyInvitationOutcome
{
    Ok,
    NotFound,
    InvalidRequest,
    VersionConflict,
    QuestionLocked,
    InvitationsClosed,
    ReminderNotAllowed,
    MailUnavailable,
    PartyVersionConflict,
    // No public origin to build a personal link on, so there is nothing to share.
    LinkUnavailable,
}

public sealed record PartyInvitationResult(
    PartyInvitationOutcome Outcome,
    PartyGuestListDto? GuestList = null,
    string? Error = null,
    // An update that changed the recipient address also replaced the link.
    bool LinkRotated = false,
    // The group this call created or changed, so a caller that asked for no
    // list still learns which row it is.
    Guid? GroupId = null);

public sealed record PartyInvitationSendResult(
    PartyInvitationOutcome Outcome,
    PartyInvitationDeliveryDto? Delivery = null,
    PartyGuestListDto? GuestList = null,
    PartyDto? Party = null,
    string? Error = null);

public sealed record PartyInvitationShareResult(
    PartyInvitationOutcome Outcome,
    PartyInvitationShareDto? Share = null,
    PartyDto? Party = null,
    // The group as the directory now lists it, so the page updates one card.
    // Typed as the union so it serializes with its "kind", like a page's items.
    PartyGuestDirectoryItemDto? Item = null,
    string? Error = null);

// --- PUBLIC (the personal invitation capability) ----------------------------
//
// What ONE invitation group sees on its own link: the party's public face and
// its own RSVP. Never another group's names, never the recipient address or a
// phone number (the guest has no use for either), never an owner id, a party
// id, a token, a hash or a capability id.

/// <summary>
/// A resolved personal invitation — the whole of what one request on the
/// invitation token may touch. Never leaves the service layer.
/// </summary>
public sealed record PartyInvitationAccess(
    Guid GroupId,
    Guid PartyId,
    Guid OwnerUserId,
    string PartyStatus,
    PartyGuestExperience Experience)
{
    /// <summary>
    /// Replies are open exactly while the party is announced and not yet
    /// happening. Live and Ended still SHOW the invitation; they do not take
    /// answers. (The experience is always Full here: nothing else resolves.)
    /// </summary>
    public bool CanRespond => PartyStatus == PartyStatuses.Published;

    /// <summary>
    /// "Sono qui" is open exactly while the party is happening. Before it
    /// nobody has arrived; after it only the host corrects the record.
    /// </summary>
    public bool CanCheckIn => PartyStatus == PartyStatuses.Live;
}

public sealed record PartyInvitationViewDto(PartyInvitationPartyDto Party, PartyInvitationRsvpDto Invitation);

public sealed record PartyInvitationPartyDto(
    string Title,
    string? Description,
    DateTime? EventStartsAt,
    string Phase,
    // An address on the INVITATION token, never the party's public one.
    string? CoverUrl,
    IReadOnlyList<PartyGuestContentViewDto> Content,
    // The party's own PUBLIC address ("Entra nel Party"), present only while
    // the party is live AND that address actually opens it right now — the
    // same capability a guest gets by scanning the room's QR, no more. It is
    // navigation, not identity: following it mints a participant the way any
    // browser does, and binds nothing to this group.
    string? PartyUrl);

public sealed record PartyInvitationRsvpDto(
    string Label,
    int Version,
    bool CanRespond,
    int MaxAdditionalGuests,
    int AdditionalGuestsUsed,
    IReadOnlyList<PartyInvitationGuestDto> Guests,
    IReadOnlyList<PartyInvitationQuestionDto> Questions,
    // The group may record its own people's arrival ("Sono qui"): the party is live.
    bool CanCheckIn);

public sealed record PartyInvitationGuestDto(
    Guid Id,
    string Name,
    bool IsAdditionalGuest,
    string Status,
    string? DietaryNotes,
    // THIS person's own arrival, and only ever a person of this group. Null:
    // not recorded as arrived. The source says whether the group may take it
    // back ("invitation") or it is the host's record ("owner").
    DateTime? CheckedInAt,
    string? CheckInSource);

public sealed record PartyInvitationQuestionDto(
    Guid Id,
    string Prompt,
    string Kind,
    bool Required,
    IReadOnlyList<string> Options,
    // A string for text and choice, a boolean for yes/no, null when unanswered.
    JsonElement? Answer);

/// <summary>The WHOLE form, every time: the server validates the graph before writing any of it.</summary>
public sealed record PartyRsvpWrite(
    int Version,
    IReadOnlyList<PartyRsvpGuestWrite>? Guests,
    IReadOnlyList<PartyRsvpAdditionalGuestWrite>? AdditionalGuests,
    IReadOnlyList<PartyRsvpAnswerWrite>? Answers);

public sealed record PartyRsvpGuestWrite(Guid GuestId, string? Status, string? DietaryNotes);

public sealed record PartyRsvpAdditionalGuestWrite(Guid? GuestId, string? Name, string? DietaryNotes);

public sealed record PartyRsvpAnswerWrite(Guid QuestionId, JsonElement Value);

public enum PartyRsvpOutcome
{
    Ok,
    NotFound,
    InvalidRequest,
    VersionConflict,
    Closed,
}

public sealed record PartyRsvpResult(
    PartyRsvpOutcome Outcome,
    PartyInvitationViewDto? View = null,
    string? Error = null);

// --- Services -----------------------------------------------------------------

/// <summary>The host's guest list: groups, named guests, questions, and the summary.</summary>
public interface IPartyInvitationService
{
    Task<PartyGuestListDto?> GetGuestListAsync(
        Guid ownerUserId, Guid partyId, CancellationToken cancellationToken = default);

    Task<PartyInvitationResult> CreateGroupAsync(
        Guid ownerUserId, Guid partyId, PartyInvitationGroupWrite write,
        CancellationToken cancellationToken = default);

    Task<PartyInvitationResult> UpdateGroupAsync(
        Guid ownerUserId, Guid partyId, Guid groupId, PartyInvitationGroupWrite write,
        int expectedVersion, CancellationToken cancellationToken = default);

    Task<PartyInvitationResult> DeleteGroupAsync(
        Guid ownerUserId, Guid partyId, Guid groupId, int expectedVersion,
        CancellationToken cancellationToken = default);

    Task<PartyInvitationResult> RotateLinkAsync(
        Guid ownerUserId, Guid partyId, Guid groupId, int expectedVersion,
        CancellationToken cancellationToken = default);

    Task<PartyInvitationResult> CreateQuestionAsync(
        Guid ownerUserId, Guid partyId, PartyRsvpQuestionWrite write,
        CancellationToken cancellationToken = default);

    Task<PartyInvitationResult> UpdateQuestionAsync(
        Guid ownerUserId, Guid partyId, Guid questionId, PartyRsvpQuestionWrite write,
        int expectedVersion, CancellationToken cancellationToken = default);

    Task<PartyInvitationResult> ReorderQuestionsAsync(
        Guid ownerUserId, Guid partyId, IReadOnlyList<Guid> questionIds,
        CancellationToken cancellationToken = default);
}

/// <summary>Invitation emails: send, resend and remind, idempotent by request id.</summary>
public interface IPartyInvitationDeliveryService
{
    /// <summary>
    /// The first invitation, or a resend for the current link. On a Draft party
    /// the first send is the announcement, so it publishes through the lifecycle
    /// — which is why it needs the party version it read.
    /// </summary>
    Task<PartyInvitationSendResult> SendAsync(
        Guid ownerUserId, Guid partyId, Guid groupId, Guid clientRequestId, int? partyVersion,
        CancellationToken cancellationToken = default);

    Task<PartyInvitationSendResult> RemindAsync(
        Guid ownerUserId, Guid partyId, Guid groupId, Guid clientRequestId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands the group's CURRENT personal link to the host to share themselves
    /// (<c>whatsapp</c> or <c>copy</c>), and records that it did. Idempotent by
    /// request id, and — like the first email — publishes a Draft through the
    /// lifecycle, quoting the party version the page read.
    /// </summary>
    Task<PartyInvitationShareResult> ShareAsync(
        Guid ownerUserId, Guid partyId, Guid groupId, string? channel, Guid clientRequestId, int? partyVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>The guest's side of the personal invitation: see it, answer it.</summary>
public interface IPartyRsvpService
{
    /// <summary>
    /// The invitation this token opens right now, or null — one generic answer
    /// for an unknown token, a rotated one, a removed group, a Draft party, a
    /// closed window and a host who may no longer run parties.
    /// </summary>
    Task<PartyInvitationAccess?> ResolveAsync(string token, CancellationToken cancellationToken = default);

    Task<PartyInvitationViewDto> ViewAsync(
        PartyInvitationAccess access, string token, CancellationToken cancellationToken = default);

    Task<PartyRsvpResult> SubmitAsync(
        string token, PartyRsvpWrite write, CancellationToken cancellationToken = default);

    /// <summary>
    /// The photograph the invitation opens on: the party's own cover choice,
    /// else the album's CHOSEN cover, else none. <c>AlbumId</c> is set only for
    /// the album's, which is served through album membership.
    /// </summary>
    Task<(string Which, Guid FileId, Guid? AlbumId)?> CoverAsync(
        PartyInvitationAccess access, CancellationToken cancellationToken = default);
}
