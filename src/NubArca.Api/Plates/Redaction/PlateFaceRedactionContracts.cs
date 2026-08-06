namespace NubArca.Api.Plates.Redaction;

// PRIVACY-ONLY face redaction contracts for the Plates surface. This is NOT the
// People/Face identity pipeline: implementations must NOT create or read
// FaceDetection/FaceEmbedding/FaceCluster/Person/PersonFaceAssignment rows, must
// NOT use People face embeddings, and must NOT produce cross-owner data. The
// only output is a set of normalized face rectangles used to blur/pixelate the
// served plate media. Geometry is NORMALIZED to [0..1] fractions of the image,
// matching PlateDetection / PlateFaceRedactionBox.

// The decoded source image handed to the detector. Bytes are the owner-private
// original; width/height are the pixel dimensions the boxes are relative to. No
// blob id / storage key / path is ever carried here.
public sealed record PlateRedactionImageInput(byte[] Bytes, int Width, int Height);

// One candidate face region to redact. Normalized [0..1]; confidence [0..1].
public sealed record PlateFaceRedactionCandidate(
    double X, double Y, double Width, double Height, double Confidence);

// Pluggable privacy-only face detector. Swappable (deterministic dev/test today;
// a real ONNX face detector in the future) and dependency-free of People/Face.
public interface IPlateFaceRedactionDetector
{
    // True when a usable detector backend is available for the current config
    // (feature Enabled + a runnable backend). False → the caller returns a safe
    // "not configured" error and NEVER serves the unredacted image.
    bool IsAvailable { get; }

    // Profile key the current backend produces boxes under (config label).
    string ProfileKey { get; }

    Task<IReadOnlyList<PlateFaceRedactionCandidate>> DetectAsync(
        PlateRedactionImageInput image, CancellationToken cancellationToken);
}

// Which source rendition a redaction render/cache entry is for.
public enum PlateRedactionSourceKind
{
    Thumbnail,
    Preview,
    Original,
}

// Thrown when blurFaces=true is requested but redaction is disabled/unavailable
// (feature off, or no runnable detector). Maps to 409 with a stable client-safe
// code. NEVER carries a stack trace, model path, or storage internal.
public sealed class PlateFaceRedactionUnavailableException : Exception
{
    public const string Code = "face_redaction_not_configured";

    public PlateFaceRedactionUnavailableException()
        : base(Code)
    {
    }
}

// Thrown when the source image exceeds the redaction pixel ceiling. Maps to 413
// with a stable client-safe code.
public sealed class PlateRedactionImageTooLargeException : Exception
{
    public const string Code = "image_too_large_for_redaction";

    public PlateRedactionImageTooLargeException()
        : base(Code)
    {
    }
}
