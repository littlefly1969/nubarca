namespace NubArca.Api.Domain;

// Per-user Personal Area PIN for the TV surface. Created ONCE from the
// authenticated web pairing-approval flow (never on the TV itself) and verified
// server-side on every TV unlock. Exactly one row per owner (unique index).
//
// Only the ASP.NET Core PasswordHasher output (PBKDF2 — self-describing string
// embedding its own salt + KDF parameters) is stored; the plaintext PIN is
// never persisted, logged, audited, or returned by any API.
//
// Generation is bumped whenever the PIN is replaced (future flow); every
// TvPersonalUnlockGrant records the generation it was minted under, so a PIN
// change immediately invalidates all outstanding grants.
public class TvPersonalPin
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string PinHash { get; set; } = string.Empty;
    public int Generation { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
