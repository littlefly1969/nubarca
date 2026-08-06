namespace NubArca.Api.Domain;

// Owner-private membership of one image in the "Aesthetics Lab" (Laboratorio
// estetico) — an isolated, opt-in, experimental space for HumanAesExpert
// analysis. Modeled directly on PlateImage (Targhe): it is deliberately NOT a
// FileItem and is isolated by construction (a separate table that no
// library/gallery/search/share/TV/Party query ever joins).
//
// The physical bytes live in the shared content-addressed blob store (dedup +
// refcount); BlobObjectId is the single blob reference this row owns. The
// reference is acquired when the item is created (from a gallery FileItem's blob
// via IBlobService.AcquireExistingAsync, or from a direct upload via StoreAsync)
// and released when the item is HARD-deleted (Targhe-style; no soft-delete /
// Trash / restore in this first version). The row is counted by
// BlobReferenceAuditService so refcount repair never zeroes a live lab blob.
public class AestheticLabItem
{
    public Guid Id { get; set; }

    // Owner boundary. Every query is scoped to this; a foreign owner sees 404.
    public Guid OwnerUserId { get; set; }

    // The single content-addressed blob this lab item references. Deduped and
    // refcounted like every other blob; never exposed through any DTO/API.
    public Guid BlobObjectId { get; set; }

    // Provenance ONLY: the gallery FileItem this image was added from, when it
    // came from the gallery (null for a direct lab upload). Deleting that
    // FileItem must NOT remove this lab item; this is a soft, nullable pointer
    // with no FK cascade from the file tree.
    public Guid? SourceFileItemId { get; set; }

    // Display-only original file name (sanitized; never used as a storage path).
    public string OriginalFileName { get; set; } = string.Empty;

    // Server-detected content type (image/jpeg, …) — trusted, not client MIME.
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    // Dimensions from a header-only decode (null if unavailable).
    public int? Width { get; set; }
    public int? Height { get; set; }

    // Deterministic, non-reversible, owner-scoped hidden container key. Internal
    // only: documents the logical container and groups an owner's lab items.
    // NEVER returned in an API response; not usable to infer the owner id.
    public string LogicalContainerKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
