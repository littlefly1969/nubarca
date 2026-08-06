namespace NubArca.Api.Plates.Alpr;

// Pluggable ALPR pipeline contracts. Implementations are swappable (deterministic
// dev/test today; ONNX detector/OCR in the future) and must not depend on
// People/Face identity, embeddings, clustering, or any cross-owner data. All
// geometry is NORMALIZED to [0..1] fractions of the image, matching FaceDetection.

// A normalized rectangle in [0..1] image-fraction space.
public readonly record struct PlateBox(double X, double Y, double Width, double Height);

// A single normalized point in [0..1] space (for optional polygons).
public readonly record struct PlatePoint(double X, double Y);

// The decoded source image handed to the pipeline. Bytes are the owner-private
// original; width/height are the EXIF-oriented dimensions the boxes are relative
// to. No blob id / storage key / path is ever carried here.
public sealed record PlateImageInput(byte[] Bytes, int Width, int Height);

// One candidate plate region from the detector stage.
public sealed record PlateDetectionCandidate(
    PlateBox BoundingBox,
    double Confidence,
    IReadOnlyList<PlatePoint>? Polygon = null);

// The bytes of a cropped candidate region handed to the OCR stage.
public sealed record PlateCropInput(byte[] Bytes, int Width, int Height, PlateBox SourceBox);

// The OCR stage result for one crop.
public sealed record PlateOcrResult(
    string Text,
    string NormalizedText,
    double Confidence,
    string? CountryHint = null,
    string? RegionHint = null);

// One fully-resolved detection: geometry + recognized text + per-stage scores.
public sealed record PlateAnalysisDetection(
    PlateBox BoundingBox,
    string Text,
    string NormalizedText,
    double PlateConfidence,
    double OcrConfidence,
    double CombinedConfidence,
    string? CountryHint,
    string? RegionHint,
    IReadOnlyList<PlatePoint>? Polygon);

// The full pipeline result for one image: the accepted detections + sanitized
// model identity + timing, for the PlateAnalysisModelRun audit row.
public sealed record PlateAnalysisResult(
    IReadOnlyList<PlateAnalysisDetection> Detections,
    long DurationMs,
    string ProfileKey,
    string? DetectorName,
    string? DetectorVersion,
    string? OcrName,
    string? OcrVersion);

public interface IPlateDetector
{
    Task<IReadOnlyList<PlateDetectionCandidate>> DetectAsync(
        PlateImageInput image, CancellationToken cancellationToken);

    string Name { get; }
    string Version { get; }
}

public interface IPlateOcrReader
{
    Task<PlateOcrResult> ReadAsync(PlateCropInput crop, CancellationToken cancellationToken);

    string Name { get; }
    string Version { get; }
}

public interface IPlateAnalysisPipeline
{
    // True when a usable detector+OCR backend is available for the current
    // config (a selected, runnable provider). False → the caller records a
    // safe outcome (see UnavailableReason; an environment/config state, not a
    // content failure — no People/Face artifacts, no cross-owner effects).
    bool IsAvailable { get; }

    // Sanitized reason the pipeline is unavailable (a PlateAnalysisErrorCodes
    // value), or null when available/unspecified. Never a path/stack trace.
    string? UnavailableReason => null;

    Task<PlateAnalysisResult> AnalyzeAsync(
        PlateImageInput image, CancellationToken cancellationToken);
}
