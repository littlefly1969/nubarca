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
    bool CanRemind);

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
/// <c>not_sent</c>: an email that carried a dead link did not invite anybody.
/// <c>pending</c> is "not confirmed" — the attempt was recorded and its outcome
/// never was — and is shown as exactly that.
/// </summary>
public sealed record PartyInvitationDeliveryStateDto(
    string State,
    DateTime? LastAttemptAt,
    string? LastAttemptKind,
    string? LastAttemptStatus,
    DateTime? LastSentAt);

public static class PartyInvitationDeliveryStates
{
    public const string NotSent = "not_sent";
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";
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
}

public sealed record PartyInvitationResult(
    PartyInvitationOutcome Outcome,
    PartyGuestListDto? GuestList = null,
    string? Error = null,
    // An update that changed the recipient address also replaced the link.
    bool LinkRotated = false);

public sealed record PartyInvitationSendResult(
    PartyInvitationOutcome Outcome,
    PartyInvitationDeliveryDto? Delivery = null,
    PartyGuestListDto? GuestList = null,
    PartyDto? Party = null,
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
    /// answers.
    /// </summary>
    public bool CanRespond =>
        PartyStatus == PartyStatuses.Published && Experience.Access == PartyGuestAccessMode.Full;
}

public sealed record PartyInvitationViewDto(PartyInvitationPartyDto Party, PartyInvitationRsvpDto Invitation);

public sealed record PartyInvitationPartyDto(
    string Title,
    string? Description,
    DateTime? EventStartsAt,
    string Phase,
    // An address on the INVITATION token, never the party's public one.
    string? CoverUrl,
    IReadOnlyList<PartyGuestContentViewDto> Content);

public sealed record PartyInvitationRsvpDto(
    string Label,
    int Version,
    bool CanRespond,
    int MaxAdditionalGuests,
    int AdditionalGuestsUsed,
    IReadOnlyList<PartyInvitationGuestDto> Guests,
    IReadOnlyList<PartyInvitationQuestionDto> Questions);

public sealed record PartyInvitationGuestDto(
    Guid Id,
    string Name,
    bool IsAdditionalGuest,
    string Status,
    string? DietaryNotes);

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
