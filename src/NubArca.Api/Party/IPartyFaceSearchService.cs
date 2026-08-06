namespace NubArca.Api.Party;

// Album-scoped, owner-scoped anonymous "find your face" search for party guests.
// A guest uploads a selfie; the backend detects the most prominent face, embeds
// it with the SAME face package used for the owner's library, and finds visible
// members of THIS party album whose stored face embeddings are similar enough.
//
// SAFETY (see CLAUDE.md + docs/current-work.md party face search notes):
//   * The selfie is processed in memory and NEVER stored.
//   * The query embedding is NEVER persisted — search is synchronous; only the
//     matched FileItem ids (rank order) are kept in a short-lived session.
//   * Candidates are ONLY the currently-visible members of the given owner's given
//     album (approved, non-vault, active); visibility is re-derived on every read.
//   * No cross-owner search, no similarity score / face id / person id / person
//     name / cluster id / raw vector ever leaves this service.
//   * When AI/the face model is disabled or unavailable, the search returns a safe
//     "unavailable" state (an environment/config state, never a content failure).
public interface IPartyFaceSearchService
{
    // Run a face search. `selfieBytes` are validated + processed in memory only.
    // Returns a safe outcome (status + short-lived session id when a search was
    // recorded + the live-visible matched file ids in rank order).
    Task<PartyFaceSearchOutcome> SearchAsync(
        Guid ownerUserId,
        Guid albumId,
        Guid? partyAlbumLinkId,
        byte[] selfieBytes,
        string? declaredContentType,
        CancellationToken cancellationToken = default);

    // Re-load a stored search's currently-visible matches (rank order), scoped to
    // owner+album, only while it has not expired. Null when missing/expired/foreign.
    Task<PartyFaceSearchView?> GetAsync(
        Guid ownerUserId,
        Guid albumId,
        Guid searchId,
        CancellationToken cancellationToken = default);

    // Explicitly activate a search as the album's TV face filter. A search is
    // local to the guest's phone until this succeeds. Server-side ordering: the
    // accepted activation gets a monotonic per-album version, so the newest
    // accepted activation replaces the previous one, an empty search can never
    // be activated, and a stale request (a search older than the currently
    // active one) is rejected.
    Task<PartyFaceSearchActivationResult> ActivateForTvAsync(
        Guid ownerUserId,
        Guid albumId,
        Guid searchId,
        CancellationToken cancellationToken = default);

    // Delete a search (session + rank rows + stored face crop). Idempotent —
    // deleting a missing/already-deleted search is a no-op; a concurrent delete
    // from the phone and the TV completes safely. Row-scoped, so cancelling an
    // older search never touches a newer active one.
    Task DeleteAsync(
        Guid ownerUserId,
        Guid albumId,
        Guid searchId,
        CancellationToken cancellationToken = default);

    // TV: the album's ACTIVE face filter — the highest-activation-version
    // explicitly activated, unexpired, ready search with visible matches.
    // Null when nothing is activated or the album is missing/foreign/not-TV.
    Task<PartyFaceSearchActiveView?> GetActiveAsync(
        Guid ownerUserId,
        Guid albumId,
        CancellationToken cancellationToken = default);

    // TV: deactivate the album's active face filter(s) WITHOUT deleting the
    // searches (guests may still be using them locally). Idempotent. Returns
    // false only when the album is missing/foreign/not-ShowOnTv.
    Task<bool> ClearActiveAsync(
        Guid ownerUserId,
        Guid albumId,
        CancellationToken cancellationToken = default);

    // TV: delete a specific search (BACK on the TV). Same row-scoped idempotent
    // delete as DeleteAsync, but gated on the album being the owner's ShowOnTv
    // album (mirrors the other TV endpoints). Returns false only for a
    // missing/foreign/not-ShowOnTv album.
    Task<bool> DeleteForTvAsync(
        Guid ownerUserId,
        Guid albumId,
        Guid searchId,
        CancellationToken cancellationToken = default);

    // TV: open the stored face-crop thumbnail of a search that is (still)
    // activated for this owner's ShowOnTv album. Null → generic 404.
    Task<NubArca.Api.Files.ThumbnailContent?> OpenFaceCropAsync(
        Guid ownerUserId,
        Guid albumId,
        Guid searchId,
        CancellationToken cancellationToken = default);
}

public enum PartyFaceSearchActivationStatus
{
    // Accepted: this search is now the album's active TV filter.
    Activated,

    // Unknown/expired/foreign search, or not a ready search.
    NotFound,

    // The search has no currently-visible matches — an empty result must never
    // be sent to the TV.
    NoMatches,

    // A newer search is currently active; this stale activation was rejected.
    StaleSearch,
}

public sealed record PartyFaceSearchActivationResult(
    PartyFaceSearchActivationStatus Status,
    long? ActivationVersion = null);

// Safe outcome of a face search POST. `Status` is a PartyFaceSearchStatuses code.
// `SearchId` is set only when a ready session was recorded. `FileItemIds` are the
// live-visible matches in internal rank order (never a score/vector/face id).
public sealed record PartyFaceSearchOutcome(
    string Status,
    Guid? SearchId,
    int ResultCount,
    IReadOnlyList<Guid> FileItemIds)
{
    public static PartyFaceSearchOutcome State(string status) =>
        new(status, null, 0, Array.Empty<Guid>());
}

// A stored search re-projected against the live album: its id + currently-visible
// matched file ids in rank order.
public sealed record PartyFaceSearchView(Guid SearchId, IReadOnlyList<Guid> FileItemIds);

// The album's ACTIVE TV face filter: the search id, its server-assigned
// activation version + server activation time, whether a face-crop indicator
// thumbnail exists, and the live-visible matches in rank order.
public sealed record PartyFaceSearchActiveView(
    Guid SearchId,
    long ActivationVersion,
    DateTime ActivatedAt,
    bool HasFaceCrop,
    IReadOnlyList<Guid> FileItemIds);
