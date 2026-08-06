namespace NubArca.Api.ShareLinks;

// Response item for GET /api/files/{fileId}/share-links. Deliberately omits
// TokenHash, OwnerUserId, FileItemId, raw token, and every storage internal
// — the owner already knows which file they queried, and the raw token is
// recoverable only at creation time by design.
//
// The three boolean status fields are precomputed against the server clock
// so a client does not have to know "now" to render the link's state.
//
// Audience: MetadataAudience.Owner. No embedded-metadata fields, no raw
// metadata JSON, no GPS, no serials. See MetadataExposurePolicy.
public sealed record ShareLinkSummary(
    Guid Id,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    DateTime? RevokedAt,
    int? MaxDownloads,
    int DownloadCount,
    DateTime? LastAccessedAt,
    bool IsRevoked,
    bool IsExpired,
    bool IsExhausted);
