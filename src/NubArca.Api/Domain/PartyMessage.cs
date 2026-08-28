namespace NubArca.Api.Domain;

// PARTY-GUEST-MESSAGES-01: a short TEXT greeting left by an anonymous party
// guest, alongside (never inside) the photo/video stream.
//
// Deliberately NOT a PartyUploadItem. A message has no FileItem, no blob, no
// derivative, and never enters the media pipeline; it has its own lifecycle
// (optional approval) and its own presentation metadata (Hero promotion). The
// two share a moderation vocabulary and nothing else.
//
// SCOPE IS THE PARTY LINK, NOT THE ALBUM. PartyAlbumLinkId is required, so a
// message belongs to ONE event. Re-enabling party mode mints a NEW link (see
// PartyLinkService.EnableAsync), and last year's messages therefore do not
// reappear at this year's party — without anyone having to find and rewrite
// each row. A disabled, revoked or expired link stops projecting its messages
// for the same reason: the projection is recomputed from the link's CURRENT
// state on every read, exactly like the rest of Party.
public class PartyMessage
{
    public Guid Id { get; set; }

    // The event this message was written at. Required: a message with no party
    // is unrepresentable, which is what makes the event isolation above hold.
    public Guid PartyAlbumLinkId { get; set; }

    // Denormalised ONLY because every owner/TV query needs it and the row is
    // otherwise unreachable from an album id without a join through the links
    // table on every poll. Set from the resolved link at insert time and never
    // rewritten; the link remains the authority on which event a message is in.
    public Guid AlbumId { get; set; }

    // The album owner at submission time. Scopes the owner-side listing the same
    // way PartyUploadItem.OwnerUserId does.
    public Guid OwnerUserId { get; set; }

    // Which participant session wrote it, when known. Nullable, never
    // backfilled, and NEVER exposed through any DTO — it exists so an abusive
    // guest's whole run can be found in the database during an incident, not so
    // the owner can group a party's guests.
    public Guid? PartyParticipantId { get; set; }

    // Optional signature the guest typed. Null or a normalised, non-empty
    // string — never an empty string, so "no name" has exactly one
    // representation on the TV.
    public string? DisplayName { get; set; }

    // The message itself. Normalised plain text (see PartyMessageText). Never
    // HTML, never Markdown, never a link the client is expected to activate.
    public string Body { get; set; } = string.Empty;

    // One of PartyMessageStatuses. Only Visible is ever projected.
    public string Status { get; set; } = PartyMessageStatuses.Visible;

    public DateTime CreatedAt { get; set; }

    // When a manager last changed the status. Null while the message still holds
    // the state it was born in.
    public DateTime? ModeratedAt { get; set; }

    // The owner or delegate who made that change. Owner-private; never in a DTO.
    public Guid? ModeratedByUserId { get; set; }

    // Hero promotion is a TIMESTAMP, not a bool: it orders the Hero rotation
    // deterministically, records when the decision was taken, and leaves room
    // for a future priority without a second column. `isHero` in the DTOs is
    // simply this being non-null.
    public DateTime? HeroPromotedAt { get; set; }

    public Guid? HeroPromotedByUserId { get; set; }

    public DateTime UpdatedAt { get; set; }

    // Only a Visible message may be presented, and only a Visible message may
    // BE a Hero. Demotion is therefore not required when hiding or rejecting:
    // the predicate stops matching, which is what keeps a hidden Hero from
    // surviving in a projection that forgot to clear the timestamp.
    public bool IsPresentable => Status == PartyMessageStatuses.Visible;

    public bool IsHero => Status == PartyMessageStatuses.Visible && HeroPromotedAt is not null;
}

// Party message moderation states. Short string constants, matching
// PartyUploadStatuses and MediaCategories rather than an enum, so the stored
// value is greppable and a new state cannot silently renumber an old one.
public static class PartyMessageStatuses
{
    // Awaiting approval (the party has RequireMessageApproval on). Not projected.
    public const string Pending = "pending";

    // Live: the ONLY state the Ribbon, the Hero rotation, or any TV projection
    // will show.
    public const string Visible = "visible";

    // A manager took a live message down. Not projected; restorable.
    public const string Hidden = "hidden";

    // A manager declined a pending message. Not projected; restorable.
    public const string Rejected = "rejected";

    public static readonly IReadOnlyList<string> All = [Pending, Visible, Hidden, Rejected];

    public static bool IsKnown(string? status) => status is not null && All.Contains(status);

    public static bool IsPublicVisible(string status) => status == Visible;
}

// What a manager can DO to a message. Named for the action rather than for the
// state it lands on, because two of them land on the same state from different
// places: approving is a decision about something nobody has read yet, and
// restoring is a decision to undo one that was already taken.
public enum PartyMessageModeration
{
    Approve,
    Reject,
    Hide,
    Restore,
}

// The moderation state machine, in one table.
//
// This lives here rather than in the service because it is the DOMAIN's answer
// to "what can happen to a message", and because a matrix is the only shape in
// which the whole answer can be read at once. Validating the target state alone
// — which is all a `status` parameter can express — quietly permits
// `visible → rejected` and `pending → hidden`: states the UI never offers, that
// no route is named after, and that leave an audit trail describing a decision
// nobody made.
//
// v1 is deliberately STRICT rather than idempotent. `approve` on something
// already visible is refused instead of silently succeeding, because the two
// possible meanings — "you are late, somebody else approved it" and "done,
// nothing happened" — are different things to tell a manager, and a state
// machine that answers the first is easier to reason about than four routes
// with four different notions of a no-op.
public static class PartyMessageTransitions
{
    // The state this action moves a message to, or null when the domain refuses
    // it. Every refusal is the same refusal: there is no partial success and no
    // silent no-op.
    public static string? Target(string? currentStatus, PartyMessageModeration action) =>
        (currentStatus, action) switch
        {
            // Nobody has read it yet: it can go live, or it can be declined.
            (PartyMessageStatuses.Pending, PartyMessageModeration.Approve) => PartyMessageStatuses.Visible,
            (PartyMessageStatuses.Pending, PartyMessageModeration.Reject) => PartyMessageStatuses.Rejected,

            // It is on the wall: it can come down.
            (PartyMessageStatuses.Visible, PartyMessageModeration.Hide) => PartyMessageStatuses.Hidden,

            // It was taken down, either before or after going up. Both are
            // recoverable, and both recover through the same route — the
            // manager's intent is "put it back", whichever way it left.
            (PartyMessageStatuses.Hidden, PartyMessageModeration.Restore) => PartyMessageStatuses.Visible,
            (PartyMessageStatuses.Rejected, PartyMessageModeration.Restore) => PartyMessageStatuses.Visible,

            _ => null,
        };
}

// The ONE place the message size limits are stated. The API validator, the
// owner UI, the guest counter and the tests all quote these numbers.
public static class PartyMessageLimits
{
    public const int MaxDisplayNameLength = 40;
    public const int MaxBodyLength = 120;
}
