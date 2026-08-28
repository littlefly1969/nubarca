namespace NubArca.Api.Party;

// Who may moderate an album's party MESSAGES. This is the single place that
// answers the question, so no endpoint ever re-derives it:
//
//     owner || activeMembership.CanManagePartyMessages
//
// Every resolve re-reads the database — no caching, no per-session grant —
// because revoking the membership or clearing the capability has to take effect
// on the very next request, exactly like AlbumAccessResolver.
//
// The album ROLE is deliberately absent from that predicate. An `editor`
// curates an album; running the party is not curation, and widening the role
// would hand every existing editor a capability nobody granted them.
public interface IPartyMessageAccessResolver
{
    // Null when the album is missing, the actor is neither owner nor an active
    // delegate, or the owner's account is disabled — all collapse to one
    // generic not-found upstream so a stranger cannot probe for album ids.
    Task<PartyMessageManagerGrant?> ResolveAsync(
        Guid albumId, Guid actorUserId, CancellationToken cancellationToken = default);
}

// A resolved right to moderate one album's party messages. `IsOwner` separates
// the two holders: both moderate messages, but only the owner may change the
// party's settings, and the client needs to know which surface to render.
public sealed record PartyMessageManagerGrant(
    Guid AlbumId,
    Guid OwnerUserId,
    Guid ActorUserId,
    bool IsOwner);
