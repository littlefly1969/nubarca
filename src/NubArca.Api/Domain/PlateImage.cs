using NubArca.Api.Plates;

namespace NubArca.Api.Domain;

// Owner-private reference to an image uploaded into the segregated "Plates"
// (Targhe) surface for future license-plate recognition. It is deliberately NOT
// a FileItem: plate images must never appear in Files, Gallery, People, Party,
// TV, or Private Vault, and are isolated by construction (a separate table that
// no library/gallery/share query ever joins).
//
// The physical bytes still live in the shared content-addressed blob store
// (dedup + refcount), so BlobObjectId is the single blob reference this row
// owns. The reference is acquired on upload (IBlobService.StoreAsync) and
// released on delete (IBlobService.ReleaseAsync); the row is registered with
// BlobReferenceAuditService so refcount repair never zeroes a live plate blob.
public class PlateImage
{
    public Guid Id { get; set; }

    // Owner boundary. Every query is scoped to this; a foreign owner sees a
    // generic 404.
    public Guid OwnerUserId { get; set; }

    // The single content-addressed blob this plate references. Deduped and
    // refcounted like every other blob; never exposed through any DTO/API.
    public Guid BlobObjectId { get; set; }

    // Display-only original file name supplied by the client (sanitised for
    // display; never used as a storage path).
    public string OriginalFileName { get; set; } = string.Empty;

    // Server-detected content type (image/jpeg, image/png, …) — the trusted
    // value, not the client-declared MIME.
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    // Image dimensions from a header-only decode (null if unavailable).
    public int? Width { get; set; }
    public int? Height { get; set; }

    // Deterministic, non-reversible, owner-scoped hidden container key
    // (__nubarca_plates_{ownerScopedHash}). Internal only: it documents the
    // logical container concept and groups an owner's plates. NEVER returned in
    // an API response and never usable to infer the owner id. See
    // PlateContainerKey.
    public string LogicalContainerKey { get; set; } = string.Empty;

    // Lifecycle status. Only Uploaded is functionally used in this slice; the
    // Analysis* values are reserved for the future ALPR/OCR worker pipeline
    // (slice 2). See PlateImageStatuses.
    public string Status { get; set; } = PlateImageStatuses.Uploaded;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
