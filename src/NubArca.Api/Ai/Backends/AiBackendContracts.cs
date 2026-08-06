using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Backends;

// Phase 0B: provider-agnostic backend contracts. A backend is selected per
// PROFILE (the profile's model decides the provider), never globally. Capability
// interfaces are split so the resolver can ask for exactly the backend it needs
// (e.g. IImageEmbedder) and so a future ONNX/external provider can implement any
// subset without schema changes. Outputs stay keyed by ProfileId upstream.
//
// Backends return raw results (float[] vectors, text, boxes). Serialization to
// the Phase 0A byte[] columns is a separate concern (IAiVectorSerializer).

// Marker/base contract every backend implements.
public interface IAiBackend
{
    // Provider key this backend serves (see AiProviders). Matched against
    // AiModel.Provider during resolution.
    string Provider { get; }

    // Whether this backend can serve the given capability (see AiCapabilities).
    bool Supports(string capability);

    // Phase 2A: environment/config readiness for a specific profile. The resolver
    // calls this AFTER matching a backend, so a configured-but-not-yet-installed
    // backend (e.g. an ONNX provider whose model files are missing) resolves as
    // UNAVAILABLE — an environment/config state, never a content failure. The
    // default is "ready": backends with no external prerequisites (none /
    // deterministic) need not override it.
    AiBackendReadiness CheckReadiness(AiProfile profile) => AiBackendReadiness.Ready;
}

// Readiness of a backend for a profile. `Reason` is a short sanitized token
// (never a path/secret) surfaced as the unavailable reason when not ready.
public readonly record struct AiBackendReadiness(bool IsReady, string? Reason)
{
    public static readonly AiBackendReadiness Ready = new(true, null);

    public static AiBackendReadiness NotReady(string reason) => new(false, reason);
}

public interface IImageEmbedder : IAiBackend
{
    Task<AiEmbeddingResult> EmbedImageAsync(
        ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken cancellationToken = default);
}

public interface ITextEmbedder : IAiBackend
{
    Task<AiEmbeddingResult> EmbedTextAsync(
        string text, AiProfile profile, CancellationToken cancellationToken = default);
}

public interface ITextExtractor : IAiBackend
{
    Task<AiTextExtractionResult> ExtractTextAsync(
        ReadOnlyMemory<byte> contentBytes, string mimeType, AiProfile profile, CancellationToken cancellationToken = default);
}

public interface IFaceDetector : IAiBackend
{
    Task<AiFaceDetectionResult> DetectFacesAsync(
        ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken cancellationToken = default);
}

public interface IFaceEmbedder : IAiBackend
{
    Task<AiEmbeddingResult> EmbedFaceAsync(
        ReadOnlyMemory<byte> faceCropBytes, AiProfile profile, CancellationToken cancellationToken = default);
}

// Optional, higher-fidelity face-embedding path used by the embedding backfill:
// given the FULL image + each detected face's normalized 5-point landmarks, the
// backend decodes once and aligns+recognizes each face internally (landmark
// alignment is what makes ArcFace embeddings comparable — cropping the bbox and
// stretching loses it). A backend that cannot align (e.g. the deterministic test
// backend) simply does not implement this; the backfill falls back to
// IFaceEmbedder.EmbedFaceAsync. The raw vectors never leave the service layer.
public interface IAlignedFaceEmbedder : IFaceEmbedder
{
    // One result per input face, in the same order. Each face is isolated: a bad
    // face yields a non-Ok outcome, it NEVER aborts the others and NEVER throws
    // for a single face. The method may still throw for a whole-image failure
    // (undecodable bytes) or a batch timeout — the caller then marks every pending
    // face for that image with a shared transient reason.
    Task<IReadOnlyList<FaceEmbedAttempt>> EmbedAlignedFacesAsync(
        ReadOnlyMemory<byte> imageBytes,
        IReadOnlyList<IReadOnlyList<FaceLandmark>> normalizedLandmarksPerFace,
        AiProfile profile,
        CancellationToken cancellationToken = default);
}

// Per-face embedding outcome (aligned path). Ok carries a vector; the rest carry
// none and tell the caller how to classify the face (permanent skip vs transient
// failure).
public enum FaceEmbedOutcome
{
    Ok,                 // embedded
    AlignmentInvalid,   // landmarks missing/degenerate → permanent skip
    CropInvalid,        // face crop could not be produced → permanent skip
    RecognitionFailed,  // recognition threw for this one face → transient failure
}

public sealed record FaceEmbedAttempt(AiEmbeddingResult? Embedding, FaceEmbedOutcome Outcome)
{
    public static readonly FaceEmbedAttempt AlignmentInvalid = new(null, FaceEmbedOutcome.AlignmentInvalid);
    public static readonly FaceEmbedAttempt CropInvalid = new(null, FaceEmbedOutcome.CropInvalid);
    public static readonly FaceEmbedAttempt RecognitionFailed = new(null, FaceEmbedOutcome.RecognitionFailed);
    public static FaceEmbedAttempt Ok(AiEmbeddingResult embedding) => new(embedding, FaceEmbedOutcome.Ok);
}

public interface IImageCaptioner : IAiBackend
{
    Task<AiCaptionResult> CaptionImageAsync(
        ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken cancellationToken = default);
}

public interface IAiTagger : IAiBackend
{
    Task<AiTaggingResult> TagImageAsync(
        ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken cancellationToken = default);
}

// ---- result types -------------------------------------------------------

// A raw embedding vector + the space it lives in. Never serialized to a DTO as
// raw floats; callers encode it with IAiVectorSerializer into EmbeddingBytes.
public sealed record AiEmbeddingResult(float[] Vector, int Dimension, string DistanceMetric);

public sealed record AiTextExtractionResult(string Text, string Source, string? Language);

// A single facial landmark in normalized [0..1] image coordinates (fractions of
// image width/height). Used by alignment before recognition embedding.
public sealed record FaceLandmark(double X, double Y);

// Normalized [0..1] bounding box (fractions of image width/height) + score.
// Landmarks (when the detector produces them, e.g. RetinaFace/SCRFD 5-point) are
// carried for alignment; a detector without a keypoint branch leaves them null.
// Coordinates are normalized so no absolute pixel geometry / storage identity is
// implied.
public sealed record DetectedFace(
    double X, double Y, double Width, double Height, double? Confidence,
    IReadOnlyList<FaceLandmark>? Landmarks = null);

public sealed record AiFaceDetectionResult(IReadOnlyList<DetectedFace> Faces);

public sealed record AiCaptionResult(string Caption);

public sealed record AiTag(string Label, double? Confidence);

public sealed record AiTaggingResult(IReadOnlyList<AiTag> Tags);
