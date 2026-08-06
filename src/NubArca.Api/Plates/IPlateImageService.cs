namespace NubArca.Api.Plates;

// Owner-private service for the segregated Plates (Targhe) surface. Every method
// is owner-scoped: a foreign or missing id resolves to null/false so the
// endpoint can return a generic 404 (no existence leak). Plate images are never
// FileItems and never enter Files/Gallery/People/Party/TV/Private Vault.
public interface IPlateImageService
{
    // Ingest a completed image upload into the owner's hidden plates container.
    // Stores the bytes in the shared content-addressed blob store (dedup +
    // refcount) and creates the owner-private PlateImage reference. Throws
    // PlateImageValidationException for a non-image / oversized upload (the blob
    // reference is released so nothing leaks).
    Task<PlateImageListItem> CreateFromUploadAsync(
        Guid ownerUserId,
        string? fileName,
        string? clientContentType,
        Stream content,
        CancellationToken cancellationToken = default);

    // Adds an EXISTING owner gallery image into the owner's hidden plates
    // container by fileItemId — no bytes are copied. Acquires ONE additional
    // reference to the gallery blob (dedup + refcount) inside a transaction and
    // creates the owner-private PlateImage. Idempotent: an existing active plate
    // for the same owner+blob is reused. Throws PlateImageValidationException
    // (NotAnImage) for a missing/foreign/non-image fileItem so nothing leaks.
    // Never starts plate analysis and never mutates the source gallery file.
    Task<PlateImageListItem> AddFromGalleryAsync(
        Guid ownerUserId, Guid fileItemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlateImageListItem>> ListAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default);

    Task<PlateImageDetail?> GetDetailAsync(
        Guid ownerUserId, Guid id, CancellationToken cancellationToken = default);

    // Hard-deletes the owner-level plate reference and releases its blob
    // reference (refcount--) in one transaction. The janitor reclaims the blob
    // bytes only if nothing else references them. Returns false for missing /
    // foreign ids.
    Task<bool> DeleteAsync(
        Guid ownerUserId, Guid id, CancellationToken cancellationToken = default);

    // Resolves a derived (small thumbnail / medium preview) JPEG for the plate,
    // rendered on demand from the original. Null for missing/foreign/unknown
    // size / unrenderable source (→ 404).
    Task<PlateDerivativeContent?> RenderDerivativeAsync(
        Guid ownerUserId, Guid id, string size, CancellationToken cancellationToken = default);

    // Resolves the owner-private original image bytes for an explicit
    // authenticated download. Null for missing/foreign (→ 404).
    Task<PlateOriginalContent?> OpenOriginalAsync(
        Guid ownerUserId, Guid id, CancellationToken cancellationToken = default);

    // Resolves the decoded SOURCE rendition (bytes + pixel dims) to be
    // face-redacted for the given source kind: Thumbnail/Preview render the
    // small/medium derivative; Original returns the original bytes. Owner-scoped;
    // null for missing/foreign/unrenderable (→ 404). Internal to the redaction
    // pipeline — the bytes are never served directly.
    Task<PlateRedactionSource?> OpenRedactionSourceAsync(
        Guid ownerUserId, Guid id, Redaction.PlateRedactionSourceKind kind,
        CancellationToken cancellationToken = default);
}

// Thrown when an upload is not a decodable image or exceeds the plate size /
// dimension caps. Code is a stable, client-safe token (never a raw message that
// could echo bytes/paths).
public sealed class PlateImageValidationException : Exception
{
    public string Code { get; }

    public PlateImageValidationException(string code)
        : base(code)
    {
        Code = code;
    }

    // Not a decodable/allowlisted image.
    public const string NotAnImage = "not_an_image";

    // Byte size exceeds Plates:MaxUploadBytes.
    public const string TooLarge = "too_large";

    // Pixel dimensions exceed the decode caps (ImageProcessing:*), so a preview
    // could never be produced.
    public const string DimensionsTooLarge = "dimensions_too_large";
}
