namespace NubArca.Api.Party;

// Guest party MESSAGES: submission, owner/delegate moderation, and the TV
// projection. A text-only domain that lives beside the media pipeline and never
// inside it — see PartyMessage for why it is not a PartyUploadItem.
//
// Every read is recomputed from the album's CURRENTLY ACTIVE party link. That
// single rule is what gives the feature its event isolation (last year's
// greetings do not reappear at this year's party), its live revocation
// (disabling party empties the feed on the next poll), and its manager queue
// scope (an owner moderates the party that is happening, not an archive).
public interface IPartyMessageService
{
    // A guest writes a greeting through a validated party link. Normalises and
    // validates the text, then stores it Visible or Pending according to the
    // link's message-approval mode. `access` must come from a resolved upload
    // token; the service does not re-validate the capability.
    Task<PartyMessageSubmissionResult> SubmitAsync(
        PartyAccess access,
        string? displayName,
        string? text,
        Guid? participantId,
        CancellationToken cancellationToken = default);

    // The manager queue for an album's current party, newest first, every
    // status. Null when the caller may not manage this album's messages or the
    // album does not exist (one generic not-found upstream). An album with no
    // active party yields PartyActive=false and an empty list rather than null,
    // so the owner UI can explain the difference.
    Task<PartyMessageListDto?> ListForManagerAsync(
        Guid albumId, Guid actorUserId, CancellationToken cancellationToken = default);

    // Move one message to visible / hidden / rejected. `pending` is not a
    // reachable target: it is a birth state, and putting a decided message back
    // into the queue has no product meaning.
    Task<PartyMessageMutation> SetStatusAsync(
        Guid albumId, Guid actorUserId, Guid messageId, string status,
        CancellationToken cancellationToken = default);

    // Promote to (or demote from) Hero. Only a Visible message may be promoted;
    // demotion always succeeds so a manager can always take a card off the wall.
    Task<PartyMessageMutation> SetHeroAsync(
        Guid albumId, Guid actorUserId, Guid messageId, bool hero,
        CancellationToken cancellationToken = default);

    // The TV feed: the current party's Visible messages, oldest first. Null when
    // the album is missing, foreign, or not TV-visible; an active party with
    // nothing to say yields an EMPTY list, which is what lets the TV tell "no
    // messages" apart from "no party" without a second call.
    Task<TvPartyMessagesDto?> GetTvProjectionAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default);
}

// The outcome of a manager mutation, kept as a small closed set so the HTTP
// layer maps it rather than inventing status codes at each call site.
public enum PartyMessageMutation
{
    Ok,

    // The album, the message, or the caller's authority over them is missing.
    // All three collapse to ONE result on purpose: a message id belonging to
    // somebody else's party must be indistinguishable from one that never
    // existed, or the endpoint becomes a probe.
    NotFound,

    // A real message, a real manager, and a transition the domain refuses —
    // promoting something that is not visible, or a status outside the set.
    InvalidTransition,
}
