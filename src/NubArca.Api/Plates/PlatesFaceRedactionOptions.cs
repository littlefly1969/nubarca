namespace NubArca.Api.Plates;

// Configuration for the owner-private, PRIVACY-ONLY face redaction of plate
// images. Bound from the "Plates:FaceRedaction" section (env:
// Plates__FaceRedaction__*). Intentionally SEPARATE from Ai:Face*, People,
// Party:FaceSearch*, Gallery, and TV config — redaction here is NOT identity:
// it never creates FaceDetection/FaceEmbedding/FaceCluster/Person/
// PersonFaceAssignment rows, never uses People face embeddings, and produces no
// cross-owner data. It only blurs/pixelates detected face regions in served
// plate media so an owner can hide bystanders' faces.
//
// Disabled by default. This slice ships only a deterministic dev/test detector
// (no ONNX weights committed). Production MUST keep Enabled=false until a real
// privacy-only face detector is deployed with a valid DetectorModelPath. When
// disabled, requesting blurFaces=true returns a safe "not configured" error —
// it NEVER silently serves the unredacted image.
public sealed class PlatesFaceRedactionOptions
{
    public const string SectionName = "Plates:FaceRedaction";

    public bool Enabled { get; set; }

    // Runner selection: Disabled | DeterministicDev | ExistingNubArcaFaceDetector
    // | OnnxDedicatedFaceDetector. When empty, falls back to the legacy Enabled
    // bool (Enabled=true → DeterministicDev). See
    // PlateProviderParsing.ResolveFaceRedaction.
    public string Provider { get; set; } = string.Empty;

    // Label recorded on persisted redaction boxes and cache rows; bumping it
    // invalidates cached redacted media and forces boxes to be re-detected.
    public string ProfileKey { get; set; } = "plate-face-redaction-v1";

    // When Provider=ExistingNubArcaFaceDetector, the AI-substrate face profile
    // key whose SCRFD detector is reused for BOUNDING BOXES ONLY (no embeddings/
    // clusters/people). Empty → the capability's default face profile is used.
    public string ExistingDetectorProfileKey { get; set; } = string.Empty;

    // Model file path for a future dedicated ONNX privacy-only face detector
    // (Provider=OnnxDedicatedFaceDetector, not implemented this slice). Empty in
    // this slice (deterministic + existing-detector need none). Never
    // logged/exposed (basename only).
    public string DetectorModelPath { get; set; } = string.Empty;

    // NMS threshold reserved for a dedicated ONNX detector; the existing detector
    // applies its own NMS internally.
    public double NmsThreshold { get; set; } = 0.45;

    // Minimum detector confidence [0..1] for a face box to be redacted.
    public double MinConfidence { get; set; } = 0.35;

    // Each detected box is expanded by this fraction of its own size on every
    // side (then clamped to the image) so hair/chin/ears fall inside the
    // redacted region. 0.35 = +35% each side.
    public double BoxExpansionRatio { get; set; } = 0.35;

    // Redaction mode. Only "aggressive_pixelation" is implemented this slice.
    public string Mode { get; set; } = PlateRedactionModes.AggressivePixelation;

    // Nominal pixel block size for the aggressive pixelation (scaled to the
    // rendered region so small previews stay heavily obscured).
    public int PixelBlockSize { get; set; } = 32;

    // Hard ceiling on the decoded source pixels a redaction render will accept.
    // A larger source returns a safe "image too large" error (413).
    public long MaxImagePixels { get; set; } = 25_000_000;

    // Cap on the number of face boxes redacted per image (defence against a
    // pathological detector result).
    public int MaxFacesPerImage { get; set; } = 64;

    // When true, rendered redacted media is cached (as an owner-private derived
    // blob + a plate_redacted_media row) so it is not recomputed every request.
    public bool CacheEnabled { get; set; } = true;

    // Effective runner after applying the Provider/Enabled fallback.
    public PlateFaceRedactionProvider ResolveProvider()
        => PlateProviderParsing.ResolveFaceRedaction(Provider, Enabled);
}

// Stable redaction-mode vocabulary. Kept separate from any AI/face identity
// enum. Only AggressivePixelation is implemented in this slice.
public static class PlateRedactionModes
{
    public const string AggressivePixelation = "aggressive_pixelation";
}
