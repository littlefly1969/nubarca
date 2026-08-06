namespace NubArca.Api.Domain;

// A short-lived, owner-scoped capability that lets a phone (reached via a QR
// code shown on the TV "Beauty Lab" screen) upload images DIRECTLY into the
// owner's Aesthetics Lab — nothing else. It is deliberately NOT a Party token
// and grants NO read/list/delete/analyze authority: purpose is scoped by the
// table itself (only the Aesthetics Lab direct-upload service consumes it).
//
// Security model (mirrors the Party/TV token convention):
//   * the raw token is a cryptographically random 256-bit value shown ONCE in
//     the create response (embedded in the QR URL); only its SHA-256 hash is
//     ever persisted, so a server restart can never resurrect a raw token;
//   * owner-scoped: uploads are charged to OwnerUserId only;
//   * short expiry (ExpiresAt) + explicit revocation (RevokedAt);
//   * bounded file count / total bytes so a leaked token can't be abused;
//   * NO plaintext token, filename, or storage internal is stored here.
public class AestheticUploadSession
{
    public Guid Id { get; set; }

    // Owner boundary: every upload lands in THIS owner's lab; also the scope key
    // the TV uses to read/revoke the session.
    public Guid OwnerUserId { get; set; }

    // SHA-256 (hex) of the random capability token. The raw token is never
    // persisted; matching is hash-only, like the Party upload token.
    public string TokenHash { get; set; } = string.Empty;

    // Upload caps. A leaked/over-shared token can never exceed these regardless
    // of how many devices scan it.
    public int MaxFiles { get; set; }
    public long MaxTotalBytes { get; set; }

    // Safe, monotonic counters (never a token, filename, or metric). Reported to
    // the TV and the mobile page as aggregate progress only.
    public int AcceptedCount { get; set; }
    public int RejectedCount { get; set; }
    public long UsedBytes { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    // Set when the owner (via the TV) explicitly revokes the session — e.g. the
    // QR screen closes. A revoked session refuses every further upload.
    public DateTime? RevokedAt { get; set; }
}

// Coarse, client-safe lifecycle state for the Beauty Lab upload session. Derived
// from RevokedAt/ExpiresAt/capacity at read time — never persisted, so a restart
// re-derives the same answer.
public static class AestheticUploadSessionStates
{
    public const string Active = "active";
    public const string Full = "full";     // capacity reached (file count or bytes)
    public const string Expired = "expired";
    public const string Revoked = "revoked";
}
