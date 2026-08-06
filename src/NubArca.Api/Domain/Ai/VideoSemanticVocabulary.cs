namespace NubArca.Api.Domain.Ai;

// VSEM-01: the bounded, sanitized vocabularies of the video temporal substrate.
// Every value that reaches the database or a log line comes from one of these
// closed sets — never raw FFmpeg output, exception text, paths or storage keys.

// Why a segment starts where it does. Diagnostic only.
public static class VideoSemanticBoundaryReasons
{
    // The mandatory first boundary at 0 ms.
    public const string Start = "start";

    // A scene-change candidate reported by the detector survived normalization.
    public const string Scene = "scene";

    // An over-long segment was split into balanced parts.
    public const string Split = "split";

    // Produced by the bounded uniform fallback (no usable scene candidates).
    public const string Uniform = "uniform";

    // Produced by the deterministic rebuild that enforces the per-video segment
    // cap.
    public const string Cap = "cap";
}

// Why a sample timestamp was selected. Diagnostic only.
public static class VideoSemanticSelectionReasons
{
    // The single representative frame of a segment (its inward midpoint).
    public const string Midpoint = "midpoint";

    // One of several evenly spaced interior positions.
    public const string Interior = "interior";
}

// VSEM-02: aggregate visual-embedding readiness of a manifest under a profile.
// Reuses the AiArtifactStatuses strings and adds `partial`, which only makes
// sense for a multi-sample aggregate (some samples embedded, some not).
public static class VideoSemanticEmbeddingStatuses
{
    public const string Completed = AiArtifactStatuses.Completed;
    public const string Partial = "partial";
    public const string Failed = AiArtifactStatuses.Failed;
    public const string Skipped = AiArtifactStatuses.Skipped;
}

// Sanitized machine-readable outcome codes for segmentation. Stored in
// VideoSemanticIndex.ErrorCode and safe to log.
//
// The content/environment split matters: a content problem is a PERMANENT skip
// (the bytes will never segment), while a process/storage/database problem is a
// retryable failure and must never mark a blob permanently skipped.
public static class VideoSemanticErrorCodes
{
    // ---- content outcomes (permanent) --------------------------------------

    // The blob is not a video we segment (wrong media category).
    public const string UnsupportedInput = "unsupported_input";

    // The detector read the container but the media is unusable.
    public const string CorruptMedia = "corrupt_media";

    // Authoritative video metadata says there is no usable video stream.
    public const string NoVideoStream = "no_video_stream";

    // Persisted duration is absent, non-finite, zero or negative.
    public const string InvalidDuration = "invalid_duration";

    // Every FileItem referencing the blob is deleted, excluded from the media
    // library, or Private-Vault-only.
    public const string NoEligibleReference = "no_eligible_reference";

    // The video is longer than this configuration can represent without
    // violating MaximumSegmentsPerVideo × MaximumSegmentSeconds. Permanent for
    // the CURRENT segmentation version only — a later version with different
    // limits gets its own row and may process the video.
    public const string SegmentationCapacityExceeded = "segmentation_capacity_exceeded";

    // ---- environment / transient outcomes (retryable) ----------------------

    // Authoritative video metadata has not been persisted yet. Scheduling is
    // supposed to prevent this; it is a retry, never a content skip.
    public const string MetadataMissing = "metadata_missing";

    // The detector process could not be started (missing binary, permissions).
    public const string ProcessStart = "process_start";

    public const string ProcessTimeout = "process_timeout";

    // The detector exited non-zero.
    public const string ProcessExit = "process_exit";

    // The detector produced more output than the configured cap.
    public const string ProcessOutputLimit = "process_output_limit";

    // A temporary file could not be written (disk full, no temp dir).
    public const string TemporaryStorage = "temporary_storage";

    // The blob content could not be opened/read from storage.
    public const string BlobStorage = "blob_storage";

    public const string Database = "database";

    // Cooperative cancellation. Never recorded as a failure — present so
    // callers can label the outcome without inventing a code.
    public const string Cancelled = "cancelled";

    // A defect in our own code. Retryable: the next deploy may fix it.
    public const string ApplicationBug = "application_bug";

    // ---- VSEM-02 embedding outcomes (all retryable) ------------------------
    // Embedding problems are never content skips: the same sample may embed
    // fine after a model fix, a redeploy or a retry.

    // FFmpeg produced no usable frame for a sample timestamp.
    public const string FrameExtraction = "frame_extraction";

    // The image embedder threw for a frame (inference error).
    public const string ProviderInference = "provider_inference";

    // The produced vector does not match the profile's declared dimension.
    public const string EmbeddingDimensionMismatch = "embedding_dimension_mismatch";

    // The produced vector is empty, zero-norm, NaN or infinite.
    public const string EmbeddingInvalidVector = "embedding_invalid_vector";
}
