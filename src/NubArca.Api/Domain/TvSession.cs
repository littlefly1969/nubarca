namespace NubArca.Api.Domain;

public class TvSession
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string SessionTokenHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? DeviceLabel { get; set; }
    public string? UserAgent { get; set; }

    // Personal Area PIN brute-force state, per paired session. Consecutive
    // failures accumulate; past the free-attempt threshold each further failure
    // sets a progressive PersonalPinLockedUntil cooldown (bounded — never a
    // permanent lockout). Reset on a successful unlock. Persisted so a client
    // restart cannot clear the throttle.
    public int PersonalPinFailedAttempts { get; set; }
    public DateTime? PersonalPinLockedUntil { get; set; }
}
