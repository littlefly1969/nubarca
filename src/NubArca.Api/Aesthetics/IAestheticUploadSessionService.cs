namespace NubArca.Api.Aesthetics;

// Owner-private lifecycle of the TV "Beauty Lab" QR mobile-upload capability.
// The token grants EXACTLY ONE authority: upload images into the owner's
// Aesthetics Lab (via the existing IAestheticLabService.AddFromUploadAsync). It
// can never list, read, analyze, or delete. Only a hash of the random 256-bit
// token is stored; a server restart cannot resurrect a raw token.
public interface IAestheticUploadSessionService
{
    // Mint a fresh session for the owner. Returns the safe DTO whose UploadUrl
    // embeds the one-time raw token (for the QR). The token is never returned
    // again and never persisted in plaintext.
    Task<AestheticUploadSessionCreatedDto> CreateAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default);

    // Grant-gated status read (TV polling). Null for a foreign/unknown id.
    Task<AestheticUploadSessionStatusDto?> GetStatusAsync(
        Guid ownerUserId, Guid id, CancellationToken cancellationToken = default);

    // Explicit revocation (QR screen closed / owner leaves). Idempotent; returns
    // false only when the session is missing or not owned by the caller.
    Task<bool> RevokeAsync(
        Guid ownerUserId, Guid id, CancellationToken cancellationToken = default);

    // PUBLIC, by token: the mobile page's lifecycle/progress view. No owner info.
    // Null when the token is unknown (mobile shows a generic invalid state).
    Task<AestheticUploadPublicStateDto?> GetPublicStateByTokenAsync(
        string rawToken, CancellationToken cancellationToken = default);

    // PUBLIC, by token: resolve an ACTIVE (not expired/revoked/full) session to
    // its owner id for a single upload attempt. Null when the token is unknown,
    // expired, revoked, or already at capacity.
    Task<AestheticUploadSessionResolution?> ResolveActiveByTokenAsync(
        string rawToken, CancellationToken cancellationToken = default);

    // Record one file's outcome against a session (accepted increments the count
    // and used-bytes; anything else increments the reject count). Best-effort,
    // safe on a vanished row.
    Task RecordResultAsync(
        Guid sessionId, bool accepted, long bytes, CancellationToken cancellationToken = default);

    // Reclaim expired/revoked rows past the retention window. Returns the number
    // deleted. Used by the background cleanup sweeper.
    Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default);
}

// Owner + remaining-capacity snapshot for a valid upload attempt.
public sealed record AestheticUploadSessionResolution(
    Guid SessionId,
    Guid OwnerUserId,
    int RemainingFiles,
    long RemainingBytes);
