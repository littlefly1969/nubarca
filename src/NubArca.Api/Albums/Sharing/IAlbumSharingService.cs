namespace NubArca.Api.Albums.Sharing;

// SHARE-ALBUM-01: invitation lifecycle + the recipient-facing read model for
// live shared albums.
//
// Split of responsibility with IAlbumAccessResolver: the resolver answers "may
// this caller act on this album" and is consulted on every single request; this
// service performs the owner's management actions and builds the DTOs a caller
// who ALREADY holds a grant is allowed to see. It never decides access for a
// media request.
public interface IAlbumSharingService
{
    // ── Owner side ──────────────────────────────────────────────────────────

    // Confirms that an exact email belongs to an invitable account and returns
    // only its display name. Null for: album missing/foreign, no such account,
    // a disabled account, a malformed address, or the owner's own address —
    // one indistinguishable answer, so this cannot be used to probe which
    // addresses are registered or which accounts are disabled.
    Task<ResolveAlbumRecipientResponse?> ResolveRecipientAsync(
        Guid ownerUserId, Guid albumId, string? email,
        CancellationToken cancellationToken = default);

    // The album's members and outstanding invitations. Null when the album is
    // missing or not the caller's.
    Task<IReadOnlyList<AlbumMemberDto>?> ListMembersAsync(
        Guid ownerUserId, Guid albumId,
        CancellationToken cancellationToken = default);

    // Invites an active user by exact email. Re-inviting somebody who declined
    // or was revoked REUSES their row: state back to pending, decision
    // timestamps cleared, a fresh InvitedAt.
    Task<(InviteAlbumMemberResult Result, AlbumMemberDto? Member)> InviteAsync(
        Guid ownerUserId, Guid albumId, InviteAlbumMemberRequest request,
        CancellationToken cancellationToken = default);

    // Changes one member's original-download permission. Takes effect on the
    // member's next request; nothing is cached.
    Task<(AlbumMemberMutationResult Result, AlbumMemberDto? Member)> UpdateMemberAsync(
        Guid ownerUserId, Guid albumId, Guid membershipId, bool allowOriginalDownload,
        CancellationToken cancellationToken = default);

    // SHARE-ALBUM-02: promotes Viewer → Contributor or demotes Contributor →
    // Viewer. Owner-only. Editor is refused (RoleNotAssignable).
    //
    // A DEMOTION does not touch the member's existing contributions: they stay
    // in the album and the member keeps the right to withdraw them, because
    // that right follows from owning the file and having contributed it, not
    // from the role they hold now.
    Task<(InviteAlbumMemberResult Result, AlbumMemberDto? Member)> ChangeMemberRoleAsync(
        Guid ownerUserId, Guid albumId, Guid membershipId, string? role,
        CancellationToken cancellationToken = default);

    // Cancels a pending invitation or revokes an accepted membership — the same
    // operation, because both mean "this person no longer has, and no longer
    // will get, access". Idempotent: revoking an already-revoked row succeeds.
    //
    // SHARE-ALBUM-02: also WITHDRAWS every item that member contributed to this
    // album, in the SAME transaction as the revocation, and reports the
    // withdrawn file ids so the endpoint can audit each one. Source files are
    // never touched.
    Task<(AlbumMemberMutationResult Result, RevokedMembership? Revoked)> RevokeMemberAsync(
        Guid ownerUserId, Guid albumId, Guid membershipId,
        CancellationToken cancellationToken = default);

    // ── Contribution (SHARE-ALBUM-02) ───────────────────────────────────────

    // Links media the ACTOR owns into a shared album as a revocable
    // contribution. No copy is made, no FileItem is created, and ownership does
    // not move: the album gains a reference to the actor's own file.
    Task<AlbumContributionResult> ContributeAsync(
        Guid actorUserId, Guid albumId, Guid fileItemId,
        CancellationToken cancellationToken = default);

    // The contributor taking their own contribution back. Permitted when the
    // actor both OWNS the file and ADDED it — independent of whether they
    // currently hold Contributor or have been downgraded to Viewer. Never
    // deletes the file.
    Task<AlbumItemRemovalResult> WithdrawContributionAsync(
        Guid actorUserId, Guid albumId, Guid fileItemId,
        CancellationToken cancellationToken = default);

    // The album owner removing ANY item from their album — their own or a
    // contribution. Removes the album membership only; the source file is never
    // passed to a deletion path. Returns the provenance of what was removed so
    // the endpoint can audit the source owner separately from the actor.
    Task<(AlbumItemRemovalResult Result, RemovedAlbumItem? Removed)> RemoveItemAsOwnerAsync(
        Guid ownerUserId, Guid albumId, Guid fileItemId,
        CancellationToken cancellationToken = default);

    // The curator's moderation view: the owner's items and every contribution,
    // with provenance and current source state. Null when the album is missing
    // or the caller may not curate it. Additive — nothing merges into the
    // owner's gallery, library or album workspace.
    //
    // SHARE-ALBUM-03: reachable by the Owner AND by an Editor, through the same
    // grant the editorial mutations use. Carries the album's concurrency token
    // so a curator can reorder or remove without a second read.
    Task<AlbumContentResponse?> ListAlbumContentAsync(
        Guid actorUserId, Guid albumId,
        CancellationToken cancellationToken = default);

    // ── Recipient side ──────────────────────────────────────────────────────

    // Live albums currently shared WITH the caller (accepted, not revoked, owner
    // account active). Never includes the caller's own albums.
    Task<IReadOnlyList<SharedAlbumSummary>> ListSharedWithMeAsync(
        Guid actorUserId, CancellationToken cancellationToken = default);

    // Invitations addressed to the caller and not yet answered.
    Task<IReadOnlyList<AlbumInvitationDto>> ListInvitationsAsync(
        Guid actorUserId, CancellationToken cancellationToken = default);

    // The recipient's explicit answer. Only the invited user can answer, and
    // only while the invitation is still pending and unrevoked.
    Task<AlbumInvitationResponseResult> RespondToInvitationAsync(
        Guid actorUserId, Guid membershipId, bool accept,
        CancellationToken cancellationToken = default);

    // ── Shared read model (caller already holds a grant) ────────────────────

    Task<SharedAlbumDetail> GetSharedAlbumAsync(
        AlbumAccessGrant grant, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SharedAlbumItem>> ListSharedItemsAsync(
        AlbumAccessGrant grant, CancellationToken cancellationToken = default);
}

// What a revocation actually did, so the endpoint can audit the membership and
// each automatic withdrawal separately.
public sealed record RevokedMembership(
    Guid MemberUserId,
    IReadOnlyList<Guid> WithdrawnFileItemIds);

// The provenance of an item the owner removed: who owned the media and who had
// put it in the album. For a valid row these are the same user; the pair is
// reported rather than a boolean so the audit records both facts as observed,
// instead of a conclusion drawn from them.
public sealed record RemovedAlbumItem(
    Guid SourceOwnerUserId,
    Guid AddedByUserId);
