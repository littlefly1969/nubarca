using NubArca.Api.Domain;

namespace NubArca.Api.Tv;

/// <summary>
/// What a paired television is FOR — the other half of the question pairing
/// answers.
///
/// <para>Pairing establishes identity: this device belongs to this owner, and
/// the session token is a credential. Assignment is ordinary mutable state
/// beside it, which is the whole point — an owner moves a television between
/// the general NubArca TV experience and a specific party without a second PIN,
/// a second QR code and a second walk to the television.</para>
///
/// <para>It is a service of its own rather than four more methods on
/// <see cref="ITvPairingService"/> so that separation is real in the code and
/// not only in a comment. Nothing here mints, validates or revokes a
/// credential.</para>
/// </summary>
public interface ITvDisplayAssignmentService
{
    /// <summary>
    /// Points one of this owner's televisions at the general experience or at
    /// one specific party.
    ///
    /// <para>The party is named by ALBUM, never by a link id the caller
    /// supplies: the server resolves the album's own currently-active link, so
    /// there is no identifier a client could guess, replay or borrow from
    /// another account. Both the television and the album are re-checked against
    /// this owner on every call.</para>
    ///
    /// <para>Idempotent — setting the assignment a television already has
    /// succeeds and reports it.</para>
    /// </summary>
    Task<TvAssignmentResult> SetAsync(
        Guid ownerUserId, Guid tvSessionId, string? kind, Guid? albumId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The parties this owner could point a television at right now: their own
    /// albums with an active, unrevoked party link. Never a token or a link id.
    /// </summary>
    Task<IReadOnlyList<TvAssignablePartyDto>> ListAssignablePartiesAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The assignment of each of the named sessions, for the owner's device
    /// list. One query for the whole list rather than one per row, and scoped to
    /// the owner so a foreign session id simply resolves to nothing.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, TvDisplayAssignmentDto>> DescribeAsync(
        Guid ownerUserId, IReadOnlyCollection<Guid> tvSessionIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What THIS television is for, resolved for the device itself, together
    /// with the presentation the party wants on it right now. Re-read on every
    /// call, so an owner changing the assignment — or a game starting,
    /// finishing or restarting — reaches the television on its next poll rather
    /// than on its next pairing.
    /// </summary>
    Task<TvDisplayAssignmentDto> ResolveAsync(
        Guid tvSessionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// What a television is showing, as its owner and the device itself are told.
///
/// <para><c>AlbumId</c> and <c>AlbumName</c> are present only for a party
/// assignment. <c>PartyAvailable</c> is false when the assignment names a party
/// that can no longer be shown — revoked, switched off, expired — an honest
/// "that party is over" rather than a silent fall back to whatever party the
/// album has next, because re-enabling party mode mints a NEW link and a new
/// party is a new party everywhere else in this feature.</para>
///
/// <para><c>Presentation</c> is the server's answer to "which surface" — one
/// of <see cref="TvPartyPresentations"/>: `general`, `slideshow`, `game` or
/// `unavailable`. It is a projection of the party's current state, not a
/// second assignment and not a game phase: the television learns what to
/// mount and nothing about why. An APK built before it existed ignores the
/// field and keeps its previous behaviour.</para>
///
/// <para><c>AssignmentKey</c> is set only on the television's OWN read: an
/// opaque identity of "this device, this party link" that changes whenever the
/// party does — including a new link for the same album — so the device knows
/// to tear down and start again. It is accepted by no endpoint.</para>
///
/// <para>It never carries the link id or any token. The album is the owner's
/// own vocabulary and the only identifier that has to cross.</para>
/// </summary>
public sealed record TvDisplayAssignmentDto(
    string Kind,
    Guid? AlbumId = null,
    string? AlbumName = null,
    bool PartyAvailable = false,
    string Presentation = TvPartyPresentations.General,
    string? AssignmentKey = null)
{
    public static readonly TvDisplayAssignmentDto General = new(TvDisplayAssignments.General);
}

/// One party an owner may point a television at. `GameEnabled` is carried so the
/// picker can say which parties have a game switched on, without a second call.
public sealed record TvAssignablePartyDto(Guid AlbumId, string AlbumName, bool GameEnabled);

public enum TvAssignmentError
{
    /// The television is missing, belongs to another owner, or is revoked or
    /// expired. One generic answer for all four, as everywhere else.
    DeviceNotFound,

    /// Not a member of <see cref="TvDisplayAssignments"/>.
    UnknownKind,

    /// A party assignment that named no album, or a general one that named one.
    AlbumRequired,

    /// The album is missing, is not this owner's, or has no active party link.
    /// One generic answer, so this cannot be used to probe for albums.
    PartyUnavailable,
}

/// <summary>
/// The outcome of one assignment change. A success carries the assignment the
/// television now has, which is what the owner UI renders — there is no second
/// read to get it.
/// </summary>
public sealed record TvAssignmentResult(
    TvDisplayAssignmentDto? Assignment,
    TvAssignmentError? Error)
{
    public static TvAssignmentResult Ok(TvDisplayAssignmentDto assignment) => new(assignment, null);
    public static TvAssignmentResult Fail(TvAssignmentError error) => new(null, error);
}

/// The owner's request. `AlbumId` is required for `party` and must be absent for
/// `general`; the server never accepts a party LINK id from a client.
public sealed record TvAssignmentRequest(string? Kind, Guid? AlbumId);
