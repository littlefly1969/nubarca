namespace NubArca.Api.Albums.Sharing;

/// <summary>
/// Link-based public sharing of ONE album, and the seam every public request
/// passes through.
///
/// <para>Split of responsibility with <see cref="IAlbumSharingService"/>, which
/// shares the same word and none of the mechanism: that one invites ACCOUNTS by
/// address and gives them roles; this one mints a capability anybody holding a
/// string may exercise. Neither is the other's fallback, and a caller is never
/// resolved through both.</para>
///
/// <para>Every owner method collapses a missing or foreign album to null, so
/// the HTTP layer answers one generic 404 and the API cannot be used to learn
/// which album ids exist.</para>
/// </summary>
public interface IAlbumShareService
{
    // ── Owner ───────────────────────────────────────────────────────────────

    /// <summary>The album's active link, or null when it has none (or is not the caller's).</summary>
    Task<AlbumShareLinkDto?> GetAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The album's link, made if it has none and REUSED if it has one.
    ///
    /// <para>Reuse is what keeps a link somebody has already sent working: an
    /// owner opening the share panel twice must not silently invalidate the
    /// address thirty people are holding. Rotating is a separate, explicit act.
    /// </para>
    /// </summary>
    Task<AlbumShareLinkDto?> CreateAsync(
        Guid ownerUserId, Guid albumId, Guid createdByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes the current link and mints a new one. The old token stops
    /// opening anything immediately — which is the point, and why this is not
    /// what <see cref="CreateAsync"/> does.
    /// </summary>
    Task<AlbumShareLinkDto?> RotateAsync(
        Guid ownerUserId, Guid albumId, Guid createdByUserId,
        CancellationToken cancellationToken = default);

    Task<AlbumShareLinkDto?> UpdateAsync(
        Guid ownerUserId, Guid albumId, AlbumShareUpdateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the link. Idempotent, and immediate: every public request
    /// re-reads the row, so nothing cached keeps a revoked link alive.
    /// Returns false only when the album is missing or not the caller's.
    /// </summary>
    Task<bool> RevokeAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default);

    /// <summary>Adds an address to the second-factor list, reusing a removed one's row.</summary>
    Task<AlbumShareGuestDto?> AddGuestAsync(
        Guid ownerUserId, Guid albumId, AlbumShareGuestRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes one address. Any device it had verified stops working on its
    /// next request, because a device is only ever as good as the address
    /// behind it.
    /// </summary>
    Task<bool> RemoveGuestAsync(
        Guid ownerUserId, Guid albumId, Guid guestId,
        CancellationToken cancellationToken = default);

    // ── The seam ────────────────────────────────────────────────────────────

    /// <summary>
    /// THE ONE WALK from a public token to an album.
    ///
    /// <para>Answers <c>NotFound</c> for an unknown or revoked token, an
    /// expired link, an album that has gone, and an album that is no longer
    /// this owner's — one generic nothing, as every public surface in this
    /// product does. Answers <c>NeedsSecondFactor</c> only when the link is
    /// genuinely live and the caller simply has not proved who they are.</para>
    /// </summary>
    Task<AlbumShareResolved> ResolveAsync(
        string? token, string? deviceToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims one slot against the link's total ceiling, atomically.
    ///
    /// <para>A conditional single statement, so two phones finishing at the
    /// same moment cannot both take the last slot. Reaching the ceiling REFUSES
    /// and changes nothing else: the link keeps opening for reading, because a
    /// share that switched itself off is one the owner would hear about from
    /// complaints rather than from the product.</para>
    /// </summary>
    Task<AlbumShareUploadResult> TryClaimUploadSlotAsync(
        Guid linkId, CancellationToken cancellationToken = default);

    /// <summary>Hands a claimed slot back when the upload itself then failed.</summary>
    Task ReleaseUploadSlotAsync(Guid linkId, CancellationToken cancellationToken = default);
}
