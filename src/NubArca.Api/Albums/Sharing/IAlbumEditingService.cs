namespace NubArca.Api.Albums.Sharing;

// SHARE-ALBUM-03: the album's COLLABORATIVE mutation surface — title,
// description, cover, order, and removing any item.
//
// ONE implementation for Owner and Editor. The owner is not a special case with
// its own path: when they use the collaborative surface they go through exactly
// the same authorization, the same optimistic-concurrency check and the same
// audit as an Editor. A second owner-only implementation is how the two drift.
//
// THE CONCURRENCY CONTRACT, uniformly:
//   1. resolve the caller's CURRENT grant (never a cached one)
//   2. validate the payload
//   3. claim the version conditionally on Album.Version == expectedVersion
//   4. zero rows updated  →  conflict; nothing was written, nothing audited
//   5. otherwise: mutate, normalize the order, clear a stale cover, AUDIT
//   6. commit — all of the above in ONE transaction
//   7. return the new state and the new version
//
// The audit is written INSIDE the transaction (IAuditLogger.WriteAsync, which
// does not swallow), so a curation change can never commit without the entry
// that explains it. That is why these methods take the caller's ip address:
// the endpoint no longer audits after the fact.
//
// WHAT MOVES THE VERSION: everything that changes what the album LOOKS like —
// title, description, cover, order, and any item entering or leaving it
// (including the automatic withdrawal a revocation performs).
//
// WHAT DOES NOT: invitations, role changes and allowOriginalDownload. Those
// change WHO MAY LOOK, not what is there. Bumping the content version for them
// would invalidate every open editor's form for a change that does not affect
// the representation they are editing — and a revoked editor is already stopped
// by the grant re-check in step 1, which is the correct mechanism for that.
public interface IAlbumEditingService
{
    // Title and description. Either may be omitted to leave it unchanged.
    Task<AlbumEditResult> UpdateDetailsAsync(
        Guid actorUserId, Guid albumId, int expectedVersion,
        string? name, string? description, string? ipAddress,
        CancellationToken cancellationToken = default);

    // Sets or clears the album's chosen cover.
    //
    // A cover may only be set to an item that is CURRENTLY a member of this
    // album and currently servable — a cover is a pointer into the album, not a
    // way to name a file. It confers no access of its own: every consumer still
    // resolves the media through the album grant.
    Task<AlbumEditResult> SetCoverAsync(
        Guid actorUserId, Guid albumId, int expectedVersion, Guid? fileItemId, string? ipAddress,
        CancellationToken cancellationToken = default);

    // Reorders the album.
    //
    // The payload is the COMPLETE ordered list of AlbumItem ids. A partial list
    // is rejected rather than interpreted: "these three first, the rest
    // somehow" has no single correct answer, and guessing one is how two
    // concurrent reorders silently produce a third order neither user asked
    // for. The list must be exactly the album's current active items — no
    // duplicates, no omissions, nothing from another album.
    //
    // Positions are normalized server-side to a contiguous 1..n, so the stored
    // order never depends on what the client sent as indices.
    Task<AlbumEditResult> ReorderAsync(
        Guid actorUserId, Guid albumId, int expectedVersion,
        IReadOnlyList<Guid> orderedAlbumItemIds, string? ipAddress,
        CancellationToken cancellationToken = default);

    // Editorial removal of ANY item — the caller's own, another
    // collaborator's, or the owner's.
    //
    // Removes the AlbumItem and nothing else. The source FileItem is never
    // passed to a deletion path, so an Editor cannot destroy media they do not
    // own, and cannot destroy the owner's media either.
    //
    // Distinct from a contributor withdrawing their own item
    // (IAlbumSharingService.WithdrawContributionAsync) — the two are different
    // ACTIONS with different audit meanings, and which one happened is decided
    // by the endpoint the caller invoked, not inferred from their identity.
    Task<AlbumEditResult> RemoveItemAsync(
        Guid actorUserId, Guid albumId, int expectedVersion, Guid albumItemId, string? ipAddress,
        CancellationToken cancellationToken = default);
}

public enum AlbumEditOutcome
{
    Ok,
    // No album, or the caller holds no active accepted grant on it. One value
    // for both: a non-member must not learn the album exists.
    NotAccessible,
    // The caller's grant does not permit editing (Viewer, Contributor).
    RoleNotPermitted,
    // Album.Version had moved on. NOTHING was written and nothing was audited.
    VersionConflict,
    // The payload is not a valid command for the album's current contents:
    // a cover that is not a servable member, a reorder that is not exactly the
    // current active set, a name outside its bounds.
    InvalidCommand,
    // The addressed item is not in this album.
    ItemNotFound,
}

// The result of an editorial mutation. On success it carries the NEW version so
// a client can chain edits without re-reading; on conflict it carries the
// CURRENT version and state so the client can refresh and show the user what
// actually happened, instead of silently retrying a destructive command.
public sealed record AlbumEditResult(
    AlbumEditOutcome Outcome,
    int? Version = null,
    string? Name = null,
    string? Description = null,
    Guid? CoverFileItemId = null,
    // Populated on VersionConflict so the caller can explain the collision
    // without a second round-trip.
    string? Message = null)
{
    public bool IsOk => Outcome == AlbumEditOutcome.Ok;
}

// ── Wire contracts ──────────────────────────────────────────────────────────

public sealed record EditAlbumDetailsRequest(
    int ExpectedVersion,
    string? Name = null,
    string? Description = null);

public sealed record SetAlbumCoverRequest(
    int ExpectedVersion,
    // Null clears the chosen cover and returns the album to the derived one.
    Guid? FileItemId = null);

public sealed record ReorderAlbumRequest(
    int ExpectedVersion,
    IReadOnlyList<Guid> AlbumItemIds);
