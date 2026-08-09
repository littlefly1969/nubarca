namespace NubArca.Api.Tv;

public interface ITvPersonalAreaService
{
    // --- Owner-side (normal authenticated user, NOT the TV session) ---

    Task<TvPersonalPinStatusDto> GetPinStatusAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default);

    // Owner-authenticated set/change/reset of the DIRECTIONAL code (no old code
    // required — the owner session IS the authorization). Creating and
    // replacing share one atomic operation: on replace the Generation is
    // bumped, every outstanding grant of this owner is revoked, and all of the
    // owner's TV-session cooldown state is cleared — in a single transaction.
    // Replacing a legacy numeric PIN row uses the SAME path, so an installation
    // never holds two live schemes at once.
    Task<TvPersonalPinSetResult> SetDpadCodeAsync(
        Guid ownerUserId, string? code, string? confirmCode,
        CancellationToken cancellationToken = default);

    // --- TV-side (limited TV session cookie) ---

    // `secret` is a directional code or (for a not-yet-upgraded installation) a
    // legacy numeric PIN. Which one is acceptable is decided by the stored
    // row's scheme, never by the shape of the input.
    Task<TvPersonalUnlockOutcome> UnlockAsync(
        string? sessionToken, string? secret, CancellationToken cancellationToken = default);

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
    int GrantsRevoked = 0,
    string? Scheme = null);

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
