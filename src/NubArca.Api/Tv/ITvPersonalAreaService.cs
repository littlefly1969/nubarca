namespace NubArca.Api.Tv;

public interface ITvPersonalAreaService
{
    // --- Owner-side (normal authenticated user, NOT the TV session) ---

    Task<TvPersonalPinStatusDto> GetPinStatusAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default);

    // Owner-authenticated set/change/reset (no old PIN required — the owner
    // session IS the authorization). Creating and replacing share one atomic
    // operation: on replace the Generation is bumped, every outstanding grant
    // of this owner is revoked, and all of the owner's TV-session cooldown
    // state is cleared — in a single transaction.
    Task<TvPersonalPinSetResult> SetPinAsync(
        Guid ownerUserId, string? pin, string? confirmPin,
        CancellationToken cancellationToken = default);

    // --- TV-side (limited TV session cookie) ---

    Task<TvPersonalUnlockOutcome> UnlockAsync(
        string? sessionToken, string? pin, CancellationToken cancellationToken = default);

    // Revokes every live grant of the presenting session. Idempotent. Returns
    // the session id for auditing, or null when the cookie does not resolve to
    // an existing session row.
    Task<Guid?> LockAsync(string? sessionToken, CancellationToken cancellationToken = default);

    // Null when the TV session is invalid (revoked/expired/missing).
    Task<TvPersonalStatusDto?> GetStatusAsync(
        string? sessionToken, string? grantToken, CancellationToken cancellationToken = default);

    Task<TvPersonalAccessResult> ResolveAccessAsync(
        string? sessionToken, string? grantToken, CancellationToken cancellationToken = default);

    Task<TvPersonalHomeDto?> GetHomeAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default);
}

public enum TvPersonalPinSetOutcome
{
    Created,
    Changed,
    InvalidPin,
    PinMismatch,
}

// GrantsRevoked is for endpoint-side auditing only — never serialized.
public sealed record TvPersonalPinSetResult(
    TvPersonalPinSetOutcome Outcome,
    DateTime? UpdatedAt = null,
    int GrantsRevoked = 0);

public enum TvPersonalUnlockStatus
{
    // TV session missing/revoked/expired → 401 (same as every TV endpoint).
    SessionInvalid,
    // Wrong PIN, malformed PIN, or no PIN configured — deliberately one bucket.
    Invalid,
    // Progressive cooldown in effect → 429 + RetryAfterSeconds.
    Throttled,
    Unlocked,
}

// TvSessionId/OwnerUserId are for endpoint-side auditing only — never serialized.
public sealed record TvPersonalUnlockOutcome(
    TvPersonalUnlockStatus Status,
    TvPersonalUnlockDto? Grant = null,
    int? RetryAfterSeconds = null,
    Guid? TvSessionId = null,
    Guid? OwnerUserId = null);

public enum TvPersonalAccessStatus
{
    SessionInvalid,
    GrantInvalid,
    // The grant's PIN generation is stale — the owner changed the PIN. Clients
    // show the "PIN was changed" notice and drop to mode selection (the TV
    // pairing itself stays valid).
    GrantStalePinChanged,
    Ok,
}

public sealed record TvPersonalAccessResult(
    TvPersonalAccessStatus Status,
    Guid? TvSessionId = null,
    Guid? OwnerUserId = null);
