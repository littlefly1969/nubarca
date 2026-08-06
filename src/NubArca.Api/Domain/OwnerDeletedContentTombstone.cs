namespace NubArca.Api.Domain;

// Per-owner memory that a specific EXACT content was intentionally deleted by
// its owner. Written only when the owner explicitly deletes the LAST active
// occurrence of that content (see FileDeleteReason.MayRecordTombstone and
// DeletedContentTombstoneService). Import/sync can then skip re-importing
// content the owner already threw away.
//
// Privacy: the content is identified ONLY by an opaque, keyed fingerprint
// (HMAC-SHA256 of the blob's content hash under a configured pepper — see
// ContentFingerprint). The raw SHA-256 / BlobObjectId / StorageKey are NEVER
// stored here and NEVER cross the API boundary. The optional name/path
// snapshots are safe owner-visible strings kept for internal diagnostics only
// and are never serialized into any response.
public class OwnerDeletedContentTombstone
{
    public Guid Id { get; set; }

    // The owner this memory belongs to. Every lookup is scoped by this — there
    // is no cross-owner deleted-content behaviour.
    public Guid OwnerUserId { get; set; }

    // Opaque keyed fingerprint of the exact content (hex). Never the raw SHA.
    public string ContentFingerprint { get; set; } = string.Empty;

    // Versioned scheme that produced ContentFingerprint (e.g. "hmac-sha256-v1")
    // so the pepper/algorithm can be rotated without ambiguity.
    public string FingerprintScheme { get; set; } = string.Empty;

    public DateTime FirstDeletedAt { get; set; }
    public DateTime LastDeletedAt { get; set; }

    // How many times the final active occurrence of this content was deleted by
    // this owner (re-import → delete cycles increment it).
    public int DeletedCount { get; set; }

    // Safe owner-visible snapshots for internal diagnostics only — NEVER exposed
    // through any DTO/API/log. Nullable (e.g. omitted for vault-origin deletes).
    public string? LastFileNameSnapshot { get; set; }
    public string? LastDeletedFromPathSnapshot { get; set; }

    // Non-sensitive provenance label (e.g. "manual_delete", "bulk_delete").
    public string? Source { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
