namespace NubArca.Api.Domain;

// A single license plate detected in an owner-private PlateImage. Owner-private
// metadata attached to the PlateImage only. The bounding box is NORMALIZED to
// [0..1] fractions of the image width/height (matching FaceDetection), so the
// same box drives any server crop and the frontend overlay regardless of the
// rendered image size. No People/Face identity, embedding, or cross-owner data.
public class PlateDetection
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid PlateImageId { get; set; }
    public Guid PlateAnalysisJobId { get; set; }

    // Raw OCR text and the conservatively normalized form (uppercase,
    // alphanumeric only). Never empty.
    public string Text { get; set; } = string.Empty;
    public string NormalizedText { get; set; } = string.Empty;

    // Optional country/region hints from the OCR stage (labels only).
    public string? CountryHint { get; set; }
    public string? RegionHint { get; set; }

    // Confidences in [0..1]. Combined is the pipeline's aggregate signal.
    public double PlateConfidence { get; set; }
    public double OcrConfidence { get; set; }
    public double CombinedConfidence { get; set; }

    // Normalized bounding box, fractions of image width/height [0..1].
    public double BoundingBoxX { get; set; }
    public double BoundingBoxY { get; set; }
    public double BoundingBoxWidth { get; set; }
    public double BoundingBoxHeight { get; set; }

    // Optional refined polygon as JSON ([{ "x":.., "y":.. }, …], normalized
    // [0..1]). Internal only — never serialized to a DTO. Null when the
    // detector produced only a rectangle.
    public string? PolygonJson { get; set; }

    // ALPR model profile key that produced this detection (label only).
    public string ModelProfileKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
