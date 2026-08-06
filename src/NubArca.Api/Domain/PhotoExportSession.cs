namespace NubArca.Api.Domain;

// A read-only "photo archive export" session. The session is the source of truth
// for status/progress so the owner UI can poll it and a multi-hour download stays
// stable. It owns a SNAPSHOT of exportable photos (PhotoExportEntry rows) built
// once at creation by a background job — the export never changes halfway through
// even if files move/are added later.
//
// Owner-scoped. No physical paths, storage keys, SHA, or blob ids are stored;
// only logical export information. The access token is stored HASHED (never
// plaintext), mirroring the share-link pattern.
public class PhotoExportSession
{
    public Guid Id { get; set; }

    // The owner whose photos are exported (also who created the session).
    public Guid OwnerUserId { get; set; }

    // SHA-256 hex of the random export token. The raw token is returned ONCE at
    // creation and never persisted. Grants read-only access to this session's
    // manifest + file entries only.
    public string TokenHash { get; set; } = string.Empty;

    // pending | building | ready | failed | revoked (see PhotoExportStatuses).
    // "expired" is derived from ExpiresAt, not stored.
    public string Status { get; set; } = PhotoExportStatuses.Pending;

    // Denormalized snapshot counters (the entry rows are authoritative; these
    // power the status API without an aggregate query).
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }

    // Sanitized terminal error summary (exception type + short message) — never a
    // stack trace, path, or storage key.
    public string? ErrorSummary { get; set; }

    // The background job that builds the snapshot.
    public Guid? JobId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    // The session (and its token) stop working after this instant.
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// One snapshot entry: a single exportable photo at its logical export path.
// `Id` is the opaque export entry id surfaced to the downloader — NOT the
// FileItemId. FileItemId is internal-only (used to stream the original content)
// and is never exposed in any DTO/manifest.
public class PhotoExportEntry
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }

    // Internal reference to stream the correct original content. NEVER exposed.
    public Guid FileItemId { get; set; }

    // Logical export path relative to the archive root, e.g.
    // "Holiday/2024/IMG_0001.jpg" (no leading slash; root-level files are just
    // the name). Preserves the current NubArca folder tree — never reorganized.
    public string RelativePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    // Server-detected safe content type, or null. Display-safe only.
    public string? ContentType { get; set; }
    public DateTime? LastModified { get; set; }
}

public static class PhotoExportStatuses
{
    public const string Pending = "pending";
    public const string Building = "building";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Revoked = "revoked";
    // Derived (not stored): the session is past ExpiresAt.
    public const string Expired = "expired";

    // The build job should stop touching a session in any of these states.
    public static bool IsJobTerminal(string? status) => status is Ready or Failed or Revoked;
}
