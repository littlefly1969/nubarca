namespace NubArca.Api.Domain;

public class FileItem
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid? ParentFolderId { get; set; }
    public Guid BlobObjectId { get; set; }

    // Private Vault scope (v0). NULL = normal, user-visible content (the default
    // for every existing and newly-imported file). Non-null = the file belongs
    // to the owner's Private Vault and is excluded from ALL normal flows by a
    // global EF query filter (gallery, files, search, organizer, export, AI,
    // derivatives, public shares). Set/cleared by logical DB-only move-in/out;
    // blob bytes are never touched. See PrivateVault.
    public Guid? PrivateVaultId { get; set; }

    // Slice 3 (media organization): per-file media-library membership. Active
    // by default. Excluded = suppressed from the media surfaces (galleries,
    // albums, search, similarity, people, TV, Party, AI discovery) while
    // remaining a normal, browsable, downloadable file with all metadata,
    // album membership, blob, derivatives, and embeddings intact. Toggled by
    // MediaLibraryExclusionService (DB-only; blob bytes and artifacts are never
    // touched); there is deliberately NO global query filter for it — media
    // surfaces opt in via the shared MediaLibraryScope policy. See
    // MediaLibraryState.
    public MediaLibraryState MediaLibraryState { get; set; } = MediaLibraryState.Active;

    // When the media-library membership last changed (UTC). Null for files that
    // have never been excluded/restored. Diagnostic / audit only.
    public DateTime? MediaLibraryStateChangedAt { get; set; }

    public string Name { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    // Denormalized, query-optimized projection of the effective capture date
    // used for gallery "Date taken" sorting/filtering. It mirrors the layered
    // precedence (user DateTakenOverride → embedded BlobMetadata.DateTaken →
    // CreatedAt) so the gallery can ORDER BY / seek a single indexed column
    // instead of correlated subqueries. The two underlying tables remain the
    // sources of truth; this column is kept in sync by the write paths (upload/
    // import, user-override change, metadata re-extraction, blob repoint) and
    // can be rebuilt by the `metadata recompute-effective-dates` command.
    public DateTime EffectiveDateTaken { get; set; }

    // Which source produced EffectiveDateTaken: "user", "embedded", or
    // "uploaded". Diagnostic only — never required for correctness.
    public string? EffectiveDateTakenSource { get; set; }
}
