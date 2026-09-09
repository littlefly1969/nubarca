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
    IReadOnlyList<PartyMediaSourceDto> MediaSources,
    /// <summary>
    /// False once ANY capability has ever been minted for this party — active,
    /// revoked or superseded. The main media source is then fixed, because
    /// participants, uploads, greetings, prints, games and face searches are all
    /// scoped to a link that names that album: changing it would turn a UI edit
    /// into a domain migration. Carried on the DTO so the owner surface can say
    /// so plainly instead of discovering it from a refusal.
    /// </summary>
    bool CanChangeMainMediaSource);

/// <summary>
/// One album a party draws on, named for the owner. Carries the album's NAME
/// rather than only its id so the owner surface needs no second request, and
/// nothing else about the album: this is a description of the party, not a
/// projection of somebody's library.
/// </summary>
public sealed record PartyMediaSourceDto(Guid AlbumId, string AlbumName, string Role, int SortOrder);

/// <summary>
/// One party in the owner's list.
///
/// <para>Deliberately NOT a <see cref="PartyDto"/>: a list does not render
/// guest quotas, game intervals, print budgets or approval modes, and a summary
/// that carried them would be a second, heavier read of everything a party
/// happens to be configured with. What is here is what the cards show.</para>
/// </summary>
public sealed record PartySummaryDto(
    Guid Id,
    string Title,
    string Status,
    DateTime? EventStartsAt,
    DateTime? LiveStartedAt,
    DateTime? LiveEndedAt,
    DateTime UpdatedAt,
    Guid? MainAlbumId,
    string? MainAlbumName);

/// <summary>What the owner may write into a party. Every field is data; none is state.</summary>
public sealed record PartyMetadataRequest(
    string? Title,
    string? Description,
    DateTime? EventStartsAt,
    DateTime? GuestAccessExpiresAt,
    // When the MEMORIES stop, which is a different decision from when the guest
    // experience does: it may outlive it, and the whole point of the After
    // surface is that it can. It rides on this mutation rather than a second
    // endpoint, because it is the party's data and shares the party's version.
    DateTime? LibraryAccessExpiresAt);

/// <summary>
/// How an owner mutation of the root ended.
///
/// <para>One vocabulary for every owner write — create, metadata, media source
/// and the three lifecycle moves — so the HTTP layer maps outcomes to status
/// codes in ONE place rather than growing a switch per route. Members that
/// cannot arise from a given call simply never do.</para>
/// </summary>
public enum PartyMutationOutcome
{
    Ok,

    /// <summary>Unknown party, or not this owner's. Always a generic 404 upstream.</summary>
    NotFound,

    /// <summary>Somebody else moved the party since the caller read it.</summary>
    VersionConflict,

    /// <summary>The move does not exist in <see cref="PartyLifecycle"/> from the current state.</summary>
    InvalidTransition,

    /// <summary>Title missing, or a field longer than <see cref="PartyTextLimits"/> allows.</summary>
    InvalidRequest,

    /// <summary>A capability has existed for this party, so its main album is fixed.</summary>
    MediaSourceLocked,

    /// <summary>That album is already another party's main source.</summary>
    AlbumAlreadyInUse,
}

public sealed record PartyMutationResult(PartyMutationOutcome Outcome, PartyDto? Party = null)
{
    public static PartyMutationResult Ok(PartyDto party) => new(PartyMutationOutcome.Ok, party);

    /// <summary>A refusal that carries the CURRENT state, so a client can refresh rather than guess.</summary>
    public static PartyMutationResult Refused(PartyMutationOutcome outcome, PartyDto? party = null) =>
        new(outcome, party);
}

/// <summary>
/// The Party aggregate root's own service: the owner's parties, their metadata,
/// where each draws its media from, and the lifecycle.
///
/// <para>It deliberately owns NOTHING else. Media, uploads, messages, printing,
/// the game and face search all keep working on <c>(ownerUserId, albumId)</c>
/// through the services that already do it correctly — the party's job is to
/// say which album that is, once.</para>
/// </summary>
public interface IPartyService
{
    /// <summary>
    /// The owner's parties, newest event first. Owner-scoped in the query, never
    /// filtered afterwards.
    /// </summary>
    Task<IReadOnlyList<PartySummaryDto>> ListAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A new party, in Draft, with NOTHING else.
    ///
    /// <para>No album, no capability, no token, no television, no game session
    /// and no print configuration: <b>the party exists before the
    /// photographs</b>. A create that quietly minted a QR would make "is this
    /// party public" a question about when it was made.</para>
    ///
    /// <para>Refuses <see cref="PartyMutationOutcome.InvalidRequest"/> for a
    /// missing or over-long title.</para>
    /// </summary>
    Task<PartyMutationResult> CreateAsync(
        Guid ownerUserId, PartyMetadataRequest request, CancellationToken cancellationToken = default);

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
    /// Writes the party's own DATA — title, description, when it is meant to
    /// start, and when guest access should stop.
    ///
    /// <para>One mutation rather than a route per field, because they are edited
    /// together on one form and share one version. It deliberately cannot write
    /// <c>Status</c>, <c>LiveStartedAt</c> or <c>LiveEndedAt</c>: those belong to
    /// transitions, and a client able to set them could describe an evening that
    /// never happened.</para>
    /// </summary>
    Task<PartyMutationResult> UpdateMetadataAsync(
        Guid ownerUserId,
        Guid partyId,
        PartyMetadataRequest request,
        int expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Points the party at the album it draws its media from.
    ///
    /// <para>Choosable and replaceable until the party has ever had a
    /// capability; fixed from that moment on
    /// (<see cref="PartyMutationOutcome.MediaSourceLocked"/>), because the
    /// guests, greetings, prints and games that may already exist are scoped to
    /// a link naming that album. An album already serving another party is
    /// <see cref="PartyMutationOutcome.AlbumAlreadyInUse"/>; an album that is
    /// not the caller's own is a generic
    /// <see cref="PartyMutationOutcome.NotFound"/> — a shared album's editor
    /// authority is not ownership and never becomes a party's source.</para>
    /// </summary>
    Task<PartyMutationResult> SetMainMediaSourceAsync(
        Guid ownerUserId,
        Guid partyId,
        Guid albumId,
        int expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// TEARS THE PARTY DOWN, keeping its album.
    ///
    /// <para>The evening is over and the host wants it out of their way. What
    /// they must not lose is the photographs — so before any party row is
    /// deleted, the guest media is FINALIZED against the moderation decisions
    /// that were made while the party ran:</para>
    ///
    /// <list type="bullet">
    /// <item>owner-added album media always survives — it was never a guest
    /// contribution and no party ever governed it;</item>
    /// <item>a guest upload survives if and only if its final
    /// <c>PartyUploadItem.Status</c> is <c>approved</c>, which covers both an
    /// automatically approved party and one where the host approved by hand;</item>
    /// <item><c>pending</c>, <c>hidden</c>, <c>rejected</c> and
    /// <c>removed_from_album</c> media go through NubArca's ORDINARY FileItem
    /// deletion lifecycle — into Trash, restorable, reclaimed by the sweeper and
    /// the janitor on their own schedules. Nothing here touches a blob.</item>
    /// </list>
    ///
    /// <para>The provenance rows are then deleted with everything else, and that
    /// is the point: afterwards the album is SELF-CONTAINED. What is visible in
    /// it is decided the way it is decided for every other album — by the files
    /// being active — and no party history has to be consulted to answer it.</para>
    ///
    /// <para>Version-checked like every other owner write.</para>
    /// </summary>
    Task<PartyMutationResult> TeardownAsync(
        Guid ownerUserId,
        Guid partyId,
        int expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies one lifecycle move, server-authoritatively.
    ///
    /// <para>The caller names an ACTION, never a target state, and states the
    /// <paramref name="expectedVersion"/> it read — the same optimistic
    /// concurrency contract an album mutation uses, so two hosts pressing
    /// "start" do not silently overwrite one another.</para>
    /// </summary>
    Task<PartyMutationResult> TransitionAsync(
        Guid ownerUserId,
        Guid partyId,
        PartyLifecycleAction action,
        int expectedVersion,
        CancellationToken cancellationToken = default);
}
