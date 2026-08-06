namespace NubArca.Api.ShareLinks;

// Response body for POST /api/files/{fileId}/share-links. Carries the raw token
// (returned exactly once at creation time — only the SHA-256 hash is persisted).
// Never includes TokenHash, OwnerUserId, FileItemId, or any storage internals.
//
// Audience: MetadataAudience.Owner. Embedded metadata is out of scope here —
// the response is about the LINK, not the file. Public share-link downloads
// serve the original bytes, which may include embedded metadata; that is
// documented in MetadataExposurePolicy.ShareLinkBytesIncludeEmbeddedMetadata
// and surfaced to the owner via the ShareLinkPanel warning.
public sealed record ShareLinkCreatedResponse(
    Guid Id,
    string Token,
    string Url,
    DateTime? ExpiresAt,
    int? MaxDownloads);
