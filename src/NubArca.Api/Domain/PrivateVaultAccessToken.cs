namespace NubArca.Api.Domain;

// Short-lived proof that the owner unlocked their Private Vault. Mirrors the
// share/export token scheme: a 256-bit random token is returned ONCE at unlock
// and only its SHA-256 hex hash is persisted here. The raw token lives only in
// frontend memory; on refresh/new tab it is gone and the user re-unlocks.
//
// Every private-vault browse/mutation endpoint requires a token that: hashes to
// a stored row, is owned by the current user, is not revoked, and is not
// expired. "Lock" sets RevokedAt. Expiry is enforced on every lookup.
public class PrivateVaultAccessToken
{
    public Guid Id { get; set; }
    public Guid PrivateVaultId { get; set; }
    public Guid OwnerUserId { get; set; }

    // SHA-256 hex of the raw token. Unique. Never exposed.
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    // Set when the user locks the vault (or the token is otherwise invalidated).
    public DateTime? RevokedAt { get; set; }
}
