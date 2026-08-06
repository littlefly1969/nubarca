namespace NubArca.Api.Domain;

// Owner-private derived (small/medium) rendition of an AestheticLabItem, stored
// as content-addressed DERIVED blob bytes (mirrors FileThumbnail /
// PlateRedactedMedia). Each row owns exactly one reference to its derived blob;
// the reference is released when the row is removed or its lab item is removed.
// Never full-resolution: the lab grid/viewer serve only these derivatives.
public class AestheticLabDerivative
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }
    public Guid AestheticLabItemId { get; set; }

    // Derivative size key (ThumbnailSizes.Small | ThumbnailSizes.Medium).
    public string Size { get; set; } = string.Empty;

    // The DERIVED content-addressed blob these bytes live in.
    public Guid BlobObjectId { get; set; }

    public string ContentType { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public long SizeBytes { get; set; }

    public DateTime CreatedAt { get; set; }
}
