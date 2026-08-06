namespace NubArca.Api.Domain;

// Derived, owner-private cache of a face-redacted plate media rendition, so the
// (comparatively expensive) detect + pixelate + re-encode work is not repeated
// on every request. Mirrors the FileThumbnail derived-artifact model: the bytes
// live in the shared content-addressed blob store (derived root, dedup +
// refcount) and BlobObjectId is the single reference this row owns. The
// reference is acquired when the rendition is cached and released when the row
// is invalidated or its PlateImage is deleted, and the row is registered with
// BlobReferenceAuditService so refcount repair never zeroes a live cache blob.
//
// This is cache/regenerable: a missing/absent derived blob simply triggers a
// re-render. BlobObjectId is NEVER exposed through any DTO/API.
public class PlateRedactedMedia
{
    public Guid Id { get; set; }

    // Owner boundary.
    public Guid OwnerUserId { get; set; }

    public Guid PlateImageId { get; set; }

    // Which source rendition was redacted (thumbnail / preview / original). See
    // PlateRedactionSourceKinds.
    public string SourceKind { get; set; } = string.Empty;

    // Always true in this slice (only redacted renditions are cached); kept as a
    // column so a future non-redacted cache variant stays representable and the
    // cache key is explicit.
    public bool BlurFaces { get; set; } = true;

    // Redaction cache-key parameters. A mismatch on any of these is a cache miss
    // (and stale rows for the same image/kind are invalidated), so bumping the
    // profile key / mode / block size transparently regenerates.
    public string ProfileKey { get; set; } = string.Empty;
    public string RedactionMode { get; set; } = string.Empty;
    public int PixelBlockSize { get; set; }

    // The single derived blob holding the redacted JPEG bytes.
    public Guid BlobObjectId { get; set; }

    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// Stable source-rendition vocabulary for the redacted-media cache.
public static class PlateRedactionSourceKinds
{
    public const string Thumbnail = "thumbnail";
    public const string Preview = "preview";
    public const string Original = "original";
}
