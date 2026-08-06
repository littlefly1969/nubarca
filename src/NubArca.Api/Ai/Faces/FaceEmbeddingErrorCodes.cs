namespace NubArca.Api.Ai.Faces;

// Sanitized, machine-readable reasons a face embedding attempt did not complete.
// Stored on FaceEmbedding.ErrorCode and surfaced ONLY as aggregate counts (never
// per-row, never with a path/stack trace/secret). PERMANENT codes mark a face
// skipped (never retried); TRANSIENT codes mark it failed (retried on a later
// run).
public static class FaceEmbeddingErrorCodes
{
    // Permanent (→ skipped): the face content itself cannot be embedded.
    public const string CropInvalid = "face-crop-invalid";
    public const string AlignmentInvalid = "face-alignment-invalid";
    public const string TooSmall = "face-too-small";
    public const string QualityTooLow = "face-quality-too-low";
    public const string MaxFacesPerImage = "face-max-faces-per-image";

    // Transient (→ failed): a processing/environment error that may succeed later.
    public const string RecognitionFailed = "face-recognition-failed";
    public const string VectorInsertFailed = "face-vector-insert-failed";
    public const string Unknown = "face-embedding-unknown";
}
