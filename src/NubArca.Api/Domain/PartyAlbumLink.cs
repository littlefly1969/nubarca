namespace NubArca.Api.Domain;

// Owner + album scoped PUBLIC read-only "party" access link. When Enabled (and
// not revoked/expired) an unauthenticated visitor holding the link's token may
// view party-safe DERIVED media (metadata-stripped thumbnails/previews) of the
// album — never originals, never metadata, never owner/AI/face internals.
//
// SECURITY: only the token HASH is stored (TokenHash). The raw token is NEVER
// persisted. The raw token is a deterministic, high-entropy value derived on
// demand via HMAC-SHA256(serverSecret, Id) so an owner-authorized surface (the
// owner settings API and the owner's own paired TV) can re-render the QR
// without the server ever holding the raw secret at rest. Re-enabling party
// mode creates a NEW row (new Id → new token) so a previously-shared QR/link
// stops working (see PartyLinkService.EnableAsync).
public class PartyAlbumLink
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid AlbumId { get; set; }

    // SHA-256 hex of the derived public VIEW/download token. Raw token never stored.
    public string TokenHash { get; set; } = string.Empty;

    // SHA-256 hex of the derived public UPLOAD token — a SEPARATE token from the
    // view token so upload can be controlled/revoked independently. Nullable so
    // the additive migration needs no backfill; new links always set it. Raw
    // token never stored.
    public string? UploadTokenHash { get; set; }

    // The party-mode on/off state for this link (the master switch). A disabled
    // or revoked link is rejected on every public request (live revocation).
    public bool Enabled { get; set; }

    // Sub-switch: whether anonymous UPLOAD is currently accepted for this link.
    // Only meaningful while Enabled; disabling the party master kills upload too.
    public bool UploadEnabled { get; set; }

    // When true, new anonymous uploads land as PENDING (PartyUploadItem) and are
    // NOT visible on the public party page / TV until the owner approves them.
    // Default false: uploads stay immediately visible, matching the low-friction
    // party behavior. Never rotates the view/upload tokens when toggled.
    public bool RequireUploadApproval { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Set when party mode is turned off (or superseded by a re-enable). A
    // revoked link never grants access again.
    public DateTime? RevokedAt { get; set; }

    // Optional hard expiry. Null in the basic slice (no expiry).
    public DateTime? ExpiresAt { get; set; }

    // Who enabled it (owner or, in future, a delegated user). Owner-scoped.
    public Guid? CreatedByUserId { get; set; }
}
