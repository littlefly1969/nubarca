namespace NubArca.Api.Domain;

// A single detected FACE region in an owner-private PlateImage, persisted so
// redaction is not recomputed on every request. This is PRIVACY metadata only —
// it is NOT identity: no embedding, no cluster, no Person, no cross-owner data,
// and it is never reusable outside Plates. The bounding box is NORMALIZED to
// [0..1] fractions of the image width/height (matching PlateDetection), so the
// same box drives the server-side redaction crop regardless of the rendered
// image size. Boxes are NEVER exposed through any DTO/API — redaction is baked
// into the served media, so the client never needs the coordinates.
public class PlateFaceRedactionBox
{
    public Guid Id { get; set; }

    // Owner boundary. Every query is scoped to this; a foreign owner sees a
    // generic 404.
    public Guid OwnerUserId { get; set; }

    public Guid PlateImageId { get; set; }

    // Detector confidence in [0..1].
    public double Confidence { get; set; }

    // Normalized bounding box, fractions of image width/height [0..1].
    public double BoundingBoxX { get; set; }
    public double BoundingBoxY { get; set; }
    public double BoundingBoxWidth { get; set; }
    public double BoundingBoxHeight { get; set; }

    // Redaction model profile key that produced this box (label only). Bumping
    // the configured profile key forces re-detection and cache invalidation.
    public string ModelProfileKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
