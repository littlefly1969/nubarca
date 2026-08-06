namespace NubArca.Api.Domain;

// Server-side Personal Area unlock grant for one paired TV session. Minted only
// after a successful server-side PIN verification; the raw token is returned
// once to the TV (kept in application memory there, never persisted) and only
// its SHA-256 hex is stored here — same scheme as PrivateVaultAccessToken.
//
// A grant is valid ONLY while ALL of these hold (checked on every personal
// call): the referenced TV session is live (not revoked/expired), the grant is
// not revoked, not expired, belongs to the presenting session AND its owner,
// and PinGeneration matches the owner's current TvPersonalPin.Generation.
// Leaving the Personal Area revokes the session's grants (idempotent); the
// bounded ExpiresAt is a secondary safety limit, not the primary lifecycle.
public class TvPersonalUnlockGrant
{
    public Guid Id { get; set; }
    public Guid TvSessionId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public int PinGeneration { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
