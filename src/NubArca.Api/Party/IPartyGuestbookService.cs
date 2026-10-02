using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// THE GUEST BOOK: the party's third contribution — memories, each a photograph
/// from the party's album, framed, with a dedication and a signature.
///
/// <para>It is a sibling of <see cref="IPartyMessageService"/> and deliberately
/// not a mode of it: there is no method here that reads a
/// <see cref="PartyMessage"/>, and no shape shared with one. A memory is
/// written to be kept. Nothing forbids a later surface — a television showing
/// the book — from READING this book; what keeps the two apart is that they
/// are two resources, not that one of them is hidden.</para>
///
/// <para>The moderation VOCABULARY is shared — <see cref="PartyMessageStatuses"/>
/// and <see cref="PartyMessageTransitions"/>, unchanged — because "what can
/// happen to something a guest left" has one answer in this product and a
/// second state machine would be a second place for it to drift.</para>
///
/// <para>Every URL in what this returns is built for the capability that asked
/// (the guest's token, or the manager's route) and is never stored.</para>
/// </summary>
public interface IPartyGuestbookService
{
    /// <summary>
    /// The book as a guest reads it, or null when this party keeps none (or is
    /// past the point where its pages are reachable) — one generic not-found
    /// upstream, exactly like every other absent Party capability.
    ///
    /// <para><paramref name="participantId"/> is this browser's anonymous guest,
    /// and is what lets the page say how many memories they have left before
    /// they compose one the server would refuse.</para>
    /// </summary>
    Task<PartyGuestbookPageDto?> GetPublicPageAsync(
        PartyAccess access,
        string token,
        Guid? participantId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The photographs a guest may make a memory from right now — the main
    /// album's photographs, never a video — or null when no memory may be
    /// written here.
    /// </summary>
    Task<PartyGuestbookPhotosDto?> ListPhotosAsync(
        PartyAccess access, string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <paramref name="fileItemId"/> is one of those photographs: the
    /// rule the chooser's own thumbnails are served by.
    /// </summary>
    Task<bool> IsChoosableAsync(
        PartyAccess access, Guid fileItemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a memory: re-checks the photograph, claims the guest's slot,
    /// takes the memory's own reference to the photograph's blob and writes the
    /// row, as ONE transaction. Refuses — storing nothing, spending nothing —
    /// when the party keeps no book, the text or signature is invalid, the
    /// template or framing is unknown, or the photograph is not one the guest
    /// may choose now.
    /// </summary>
    Task<PartyGuestbookSubmissionResult> SubmitAsync(
        PartyAccess access,
        string token,
        PartyGuestbookSubmission submission,
        Guid? participantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A public memory's picture (a derived preview, metadata not yet
    /// stripped), or null when it is not in this party's book.
    /// </summary>
    Task<Stream?> OpenPublicPhotoAsync(
        PartyAccess access, Guid entryId, Guid? participantId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The book as a PAIRED TELEVISION draws it: every VISIBLE memory of the
    /// party, in the book's own order, each with its picture on the television's
    /// route. Never a pending, hidden or rejected one. The caller has already
    /// established that this television may show this party's book NOW.
    /// </summary>
    Task<IReadOnlyList<PartyGuestbookEntryDto>> ListForTelevisionAsync(
        Guid partyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A visible memory's picture for a paired television — the same derived,
    /// regenerable preview the guest page is drawn with; null when the memory is
    /// not a visible memory of that party.
    /// </summary>
    Task<Stream?> OpenTelevisionPhotoAsync(
        Guid partyId, Guid entryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The manager's whole book — every state, newest first — or null when the
    /// caller may not moderate this party's contributions.
    ///
    /// <para>Reachable whether or not the book is currently switched on, for
    /// the reason the product already applies to the greetings queue: closing a
    /// channel must never lock somebody out of the queue it filled.</para>
    ///
    /// <para><paramref name="photoUrl"/> says where each memory's picture is
    /// served for THIS caller: the host's route and the Party Crew's differ.</para>
    /// </summary>
    Task<PartyGuestbookManagerListDto?> ListForManagerAsync(
        Guid partyId,
        Guid actorUserId,
        Func<Guid, string> photoUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Any memory's picture, in any state, for somebody who may moderate the
    /// book; null otherwise.
    /// </summary>
    Task<Stream?> OpenManagedPhotoAsync(
        Guid partyId, Guid actorUserId, Guid entryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves one memory through the shared moderation state machine.
    /// </summary>
    Task<PartyMessageMutation> ModerateAsync(
        Guid partyId,
        Guid actorUserId,
        Guid entryId,
        PartyMessageModeration action,
        CancellationToken cancellationToken = default);
}
