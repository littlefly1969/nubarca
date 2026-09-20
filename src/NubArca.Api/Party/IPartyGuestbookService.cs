using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// THE GUEST BOOK: the party's third contribution, and the one nothing
/// projects.
///
/// <para>It is a sibling of <see cref="IPartyMessageService"/> and deliberately
/// not a mode of it. There is no method here that promotes a dedication to the
/// slideshow, none that reads a <see cref="PartyMessage"/>, and no projection
/// any television calls. That is the invariant the separation exists to make
/// structural rather than remembered: a keepsake cannot reach a wall because
/// there is no code path from one to the other.</para>
///
/// <para>The moderation VOCABULARY is shared — <see cref="PartyMessageStatuses"/>
/// and <see cref="PartyMessageTransitions"/>, unchanged — because "what can
/// happen to something a guest left" has one answer in this product and a
/// second state machine would be a second place for it to drift.</para>
/// </summary>
public interface IPartyGuestbookService
{
    /// <summary>
    /// The book as a guest reads it, or null when this party keeps none (or is
    /// past the point where its pages are reachable) — one generic not-found
    /// upstream, exactly like every other absent Party capability.
    /// </summary>
    /// <para><paramref name="participantId"/> is this browser's anonymous guest,
    /// and is what lets the page say how many dedications they have left before
    /// they compose one the server would refuse. Absent simply means the count
    /// is not known here.</para>
    Task<PartyGuestbookPageDto?> GetPublicPageAsync(
        PartyAccess access, Guid? participantId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a dedication. Refuses — without storing anything — when the party
    /// keeps no book, when the text is empty or over the limit, or when the
    /// signature is over the limit.
    /// </summary>
    Task<PartyGuestbookSubmissionResult> SubmitAsync(
        PartyAccess access,
        string? authorDisplayName,
        string? body,
        Guid? participantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The manager's whole book — every state, newest first — or null when the
    /// caller may not moderate this party's contributions.
    ///
    /// <para>Reachable whether or not the book is currently switched on, for
    /// the reason the product already applies to the greetings queue: closing a
    /// channel must never lock somebody out of the queue it filled.</para>
    /// </summary>
    Task<PartyGuestbookManagerListDto?> ListForManagerAsync(
        Guid partyId, Guid actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves one dedication through the shared moderation state machine.
    /// </summary>
    Task<PartyMessageMutation> ModerateAsync(
        Guid partyId,
        Guid actorUserId,
        Guid entryId,
        PartyMessageModeration action,
        CancellationToken cancellationToken = default);
}
