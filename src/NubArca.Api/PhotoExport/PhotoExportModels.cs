namespace NubArca.Api.PhotoExport;

// The background job that builds a session's snapshot references the session by
// id only — no paths, keys, tokens, or user strings in the payload.
public sealed record PhotoExportJobPayload(Guid SessionId);

// Returned ONCE at session creation. `Token` is the raw export token — shown to
// the owner so they can build the download command; it is never returned again
// and only its hash is stored.
public sealed record PhotoExportCreatedResponse(
    Guid SessionId,
    string Token,
    string Status,
    DateTime ExpiresAt);

// Safe, owner-facing session status. No token, no file ids, no internals.
// `Status` is the EFFECTIVE status (revoked/expired derived from the row).
public sealed record PhotoExportStatusResponse(
    Guid SessionId,
    string Status,
    int FileCount,
    long TotalBytes,
    string? ErrorSummary,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    DateTime ExpiresAt,
    bool ManifestReady);

// One manifest line (JSON Lines). Safe fields only: an opaque entry id, the
// logical export path, name, size, content type, a relative download URL, and a
// last-modified timestamp. NEVER BlobObjectId / SHA / StorageKey / FileItemId /
// physical path / raw metadata.
public sealed record PhotoExportManifestEntry(
    string entryId,
    string relativePath,
    string name,
    long size,
    string? contentType,
    string downloadUrl,
    DateTime? lastModified);
