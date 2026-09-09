using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>The owner's view of one party, including where its media comes from.</summary>
public sealed record PartyDto(
    Guid Id,
    string Title,
    string? Description,
    string Status,
    DateTime? EventStartsAt,
    DateTime? LiveStartedAt,
    DateTime? LiveEndedAt,
    DateTime? GuestAccessExpiresAt,
    DateTime? LibraryAccessExpiresAt,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<PartyMediaSourceDto> MediaSources);

/// <summary>
/// One album a party draws on, named for the owner. Carries the album's NAME
/// rather than only its id so the owner surface needs no second request, and
/// nothing else about the album: this is a description of the party, not a
/// projection of somebody's library.
/// </summary>
public sealed record PartyMediaSourceDto(Guid AlbumId, string AlbumName, string Role, int SortOrder);

/// <summary>Why a lifecycle transition did not happen.</summary>
public enum PartyTransitionOutcome
{
    Ok,

    /// <summary>Unknown party, or not this owner's. Always a generic 404 upstream.</summary>
    NotFound,

    /// <summary>The move does not exist in <see cref="PartyLifecycle"/> from the current state.</summary>
    InvalidTransition,

    /// <summary>Somebody else moved the party since the caller read it.</summary>
    VersionConflict,
}

public sealed record PartyTransitionResult(PartyTransitionOutcome Outcome, PartyDto? Party = null);

/// <summary>
/// The Party aggregate root's own service: creating a party for an album, and
/// moving it through its lifecycle.
///
/// <para>It deliberately owns NOTHING else. Media, uploads, messages, printing,
/// the game and face search all keep working on <c>(ownerUserId, albumId)</c>
/// through the services that already do it correctly — the party's job is to
/// say which album that is, once, at the public seam.</para>
/// </summary>
public interface IPartyService
{
    /// <summary>
    /// The party this album is the <c>main</c> media source of, creating it (and
    /// that source) when there is none.
    ///
    /// <para><b>Stages; does not save.</b> The returned party may be a brand-new
    /// tracked entity, and it is the CALLER's unit of work that commits it. That
    /// is deliberate: enabling party mode creates the party and its capability
    /// in one transaction, so a party can never exist because a link failed to
    /// be written half a second later.</para>
    ///
    /// <para>Null when the album is missing or belongs to somebody else.</para>
    /// </summary>
    Task<Domain.Party?> EnsureForAlbumAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default);

    /// <summary>The owner's party, or null when missing/foreign (generic 404 upstream).</summary>
    Task<PartyDto?> GetAsync(
        Guid ownerUserId, Guid partyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies one lifecycle move, server-authoritatively.
    ///
    /// <para>The caller names an ACTION, never a target state, and states the
    /// <paramref name="expectedVersion"/> it read — the same optimistic
    /// concurrency contract an album mutation uses, so two hosts pressing
    /// "start" do not silently overwrite one another.</para>
    /// </summary>
    Task<PartyTransitionResult> TransitionAsync(
        Guid ownerUserId,
        Guid partyId,
        PartyLifecycleAction action,
        int expectedVersion,
        CancellationToken cancellationToken = default);
}
