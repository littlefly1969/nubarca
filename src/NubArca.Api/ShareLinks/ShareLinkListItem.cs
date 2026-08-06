namespace NubArca.Api.ShareLinks;

// Response item for the global GET /api/share-links listing. Carries enough
// file context (FileName + FolderPath) for the owner to identify which file a
// link points at across their whole tree, plus the same precomputed status
// booleans as ShareLinkSummary.
//
// Deliberately omits the raw token, TokenHash, OwnerUserId, FileItemId,
// BlobObjectId, StorageKey, and every physical path. The raw token is
// recoverable only at creation time by design. FolderPath is built from the
// owner's own folder names — their own data, not a leak.
//
// Audience: MetadataAudience.Owner. Embedded metadata (GPS, EXIF camera /
// lens / capture settings, raw document) is intentionally NOT included — a
// share-link listing is a management view, not a metadata-exposure surface.
// See NubArca.Api.Metadata.MetadataExposurePolicy.
public sealed record ShareLinkListItem(
    Guid Id,
    string FileName,
    string? FolderPath,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    DateTime? RevokedAt,
    int? MaxDownloads,
    int DownloadCount,
    DateTime? LastAccessedAt,
    bool IsRevoked,
    bool IsExpired,
    bool IsExhausted);

// Paginated envelope. Unlike ImageListResponse (which omits a total to avoid
// COUNT(*)), the management page benefits from a real total so it can render
// "showing N of Total" and stop paging at the end — the COUNT is one cheap
// owner-scoped query.
public sealed record ShareLinkListResponse(
    IReadOnlyList<ShareLinkListItem> Items,
    int Limit,
    int Offset,
    int Total);
