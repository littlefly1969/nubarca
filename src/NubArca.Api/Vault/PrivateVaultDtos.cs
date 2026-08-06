namespace NubArca.Api.Vault;

// ── Sanitized DTOs ──────────────────────────────────────────────────────────
// Every DTO below is display-safe. NONE of them may ever carry BlobObjectId,
// StorageKey, physical path, SHA/hash, PasswordHash, TokenHash, KDF params, raw
// metadata JSON, raw vectors, or stack traces.

// Owner-only vault status. Reveals ONLY whether a vault password has been
// configured for this account (so the UI can show "create" vs "unlock") plus
// the non-secret label / forward-looking encryption mode. Reveals NOTHING about
// content: no counts, no names, no empty/non-empty signal.
public sealed record PrivateVaultStatus(bool Configured, string DisplayName, string EncryptionMode);

public enum PrivateVaultSetupOutcome
{
    Created,
    AlreadyConfigured,
    InvalidPassword,
}

// Returned ONCE on unlock. Token lives only in frontend memory.
public sealed record PrivateVaultUnlockResult(string Token, DateTime ExpiresAt);

public sealed record VaultFolderDto(Guid Id, string Name);

// One vault file, enriched for the visual grid (slice 4). Still display-safe:
// carries NO BlobObjectId / StorageKey / SHA / path / raw metadata. `MediaKind`
// is the coarse bucket ("image" | "video" | "other") derived from the
// server-detected content type (falling back to the client MIME). `DisplayName`
// is the label the grid shows (`Title ?? Name`); the original filename stays in
// `Name` for the details panel. The `*Available` flags let the grid skip a
// doomed fetch for a file that has no such derivative (no generation happens
// either way).
public sealed record VaultFileDto(
    Guid Id,
    string Name,
    string? Title,
    string DisplayName,
    string MediaKind,
    string MimeType,
    long SizeBytes,
    DateTime CreatedAt,
    int? Width,
    int? Height,
    bool ThumbnailAvailable,
    bool PosterAvailable);

// Coarse media buckets for VaultFileDto / VaultMediaInfo. Lowercase to match
// the frontend union type exactly.
public static class VaultMediaKinds
{
    public const string Image = "image";
    public const string Video = "video";
    public const string Other = "other";

    // Derived from the server-detected content type (BlobMetadata) when known,
    // falling back to the client-supplied MIME for pre-metadata blobs — the
    // SAME precedence the normal galleries use for membership.
    public static string From(string? detectedContentType, string mimeType)
    {
        var effective = !string.IsNullOrEmpty(detectedContentType) ? detectedContentType : mimeType;
        if (effective.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return Image;
        }
        if (effective.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return Video;
        }
        return Other;
    }
}

// Sanitized owner-private detail for the vault viewer / info panel (slice 4).
// Read-only. Carries ONLY curated, display-safe fields: no BlobObjectId,
// StorageKey, SHA, physical path, raw metadata JSON, embeddings, face data, or
// internal derivative ids. GPS coordinates are deliberately NOT surfaced here;
// `Location` is the owner's free-text location override only.
public sealed record VaultMediaInfo(
    Guid Id,
    string Name,
    string? Title,
    string DisplayName,
    string MediaKind,
    string MimeType,
    long SizeBytes,
    int? Width,
    int? Height,
    DateTime CreatedAt,
    DateTime? TakenAt,
    string? Description,
    IReadOnlyList<string> Tags,
    int? Rating,
    bool Favorite,
    string? Location,
    bool ThumbnailAvailable,
    bool PreviewAvailable,
    bool PosterAvailable);

// One level of the vault tree. `FolderId` is null at the vault root; otherwise
// echoes the folder being listed (already validated in-vault).
public sealed record VaultListing(
    Guid? FolderId,
    IReadOnlyList<VaultFolderDto> Folders,
    IReadOnlyList<VaultFileDto> Files);

// Aggregate move result (owner-only counts; no names/ids of what moved).
public sealed record VaultMoveResult(int MovedFiles, int MovedFolders);

// ── Request bodies ──────────────────────────────────────────────────────────
public sealed record VaultSetupRequest(string? Password);
public sealed record VaultUnlockRequest(string? Password);
public sealed record VaultMoveRequest(IReadOnlyList<Guid>? FileIds, IReadOnlyList<Guid>? FolderIds);
