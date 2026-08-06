namespace NubArca.Api.Domain;

public class Folder
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid? ParentFolderId { get; set; }
    public string Name { get; set; } = string.Empty;

    // Private Vault scope (v0). NULL = normal folder (default). Non-null = this
    // folder is inside the owner's Private Vault and is excluded from all normal
    // flows by a global EF query filter. When a folder is moved into the vault
    // it and ALL descendant folders + files are marked; move is DB-only.
    public Guid? PrivateVaultId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    // ── Slice 94: DENORMALIZED media-library eligibility ────────────────────
    // Effective exclusion state computed from MediaLibraryRule rows, kept in
    // sync EXCLUSIVELY by MediaLibraryService (same pattern as
    // FileItem.EffectiveDateTaken — the rules table stays authoritative).
    // Gallery / media-job queries join ONLY these flags (one indexed lookup,
    // no tree walk, no N+1). Defaults are false = included, so existing data
    // keeps full gallery visibility after migration.

    // Files DIRECTLY inside this folder are excluded from the photo/video
    // media library.
    public bool MediaPhotosExcluded { get; set; }
    public bool MediaVideosExcluded { get; set; }

    // What this folder's CHILDREN inherit (differs from the flags above when
    // a rule has AppliesToChildren = false). Used to seed new folders cheaply
    // and by the recompute walk; queries never read these.
    public bool MediaPhotosExcludedForChildren { get; set; }
    public bool MediaVideosExcludedForChildren { get; set; }
}
