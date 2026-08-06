namespace NubArca.Api.Domain.Ai;

// VFACE-01: the bounded, sanitized vocabularies of the canonical video
// face-track substrate. Every value that reaches the database or a log line
// comes from one of these closed sets — never raw FFmpeg output, exception
// text, paths, storage keys, person names or filenames.

// Aggregate outcome of ONE face analysis of ONE temporal manifest under one
// (analysis version, detection profile, embedding profile). Reuses the
// AiArtifactStatuses strings and adds `partial`, which only makes sense for a
// multi-frame aggregate (some frames processed, some failed).
public static class VideoFaceAnalysisStatuses
{
    // Every planned frame was processed and the analysis is valid.
    public const string Completed = AiArtifactStatuses.Completed;

    // Usable tracks exist but at least one planned frame failed.
    public const string Partial = "partial";

    // Processing ran and produced no usable result.
    public const string Failed = AiArtifactStatuses.Failed;

    // A permanent, non-retryable outcome: no eligible reference, no faces found,
    // or no track survived the minimum-evidence rule.
    public const string Skipped = AiArtifactStatuses.Skipped;
}

// Sanitized machine-readable outcome codes for video face analysis. Stored in
// VideoFaceAnalysisStatus.ErrorCode and safe to log.
//
// The content/environment split matters exactly as it does in VSEM: a content
// outcome is a PERMANENT skip (the bytes will never yield tracks at this
// version), while a process/storage/database/provider problem is a retryable
// failure and must never mark a blob permanently skipped.
public static class VideoFaceErrorCodes
{
    // ---- content outcomes (permanent skip) ---------------------------------

    // Every FileItem referencing the blob is deleted, excluded from the media
    // library, or Private-Vault-only.
    public const string NoEligibleReference = "no_eligible_reference";

    // Frames were processed successfully and contained no accepted face. A
    // terminal, NON-RETRYABLE result: re-running would find nothing again.
    public const string NoFacesFound = "no_faces_found";

    // Faces were found but every candidate track fell below the minimum
    // evidence rule (MinimumTrackDetections). Terminal and non-retryable at
    // this analysis version.
    public const string NoTracksRetained = "no_tracks_retained";

    // ---- eligibility / configuration states (NO row is written) ------------

    // No completed temporal manifest exists at the requested segmentation
    // version. Premature scheduling — never a blob outcome.
    public const string SegmentationMissing = "segmentation_missing";

    // The resolved profile cannot host face detection/embedding, or declares no
    // usable dimension. A configuration state, never a blob outcome.
    public const string ProfileMissing = "profile_missing";

    // The face backend is not installed/ready. An environment state, never a
    // blob outcome.
    public const string ProviderUnavailable = "provider_unavailable";

    // ---- environment / transient outcomes (retryable failure) --------------

    // FFmpeg produced no usable frame at a planned timestamp.
    public const string FrameExtractFailed = "frame_extract_failed";

    // The detector threw for a frame.
    public const string FaceDetectionFailed = "face_detection_failed";

    // The recognizer threw for a frame's faces.
    public const string FaceEmbeddingFailed = "face_embedding_failed";

    // A produced vector does not match the profile's declared dimension.
    public const string DimensionMismatch = "dimension_mismatch";

    // The association pass itself failed (a defect, not a content property).
    public const string TrackingFailed = "tracking_failed";

    // A temporary file could not be written (disk full, no temp dir).
    public const string TemporaryStorage = "temporary_storage";

    // The whole-video analysis budget elapsed, or one frame process timed out.
    public const string Timeout = "timeout";

    public const string Database = "database";

    // The blob content could not be opened/read from storage.
    public const string BlobStorage = "blob_storage";

    // Cooperative cancellation. Never recorded as a failure — present so callers
    // can label the outcome without inventing a code.
    public const string Cancelled = "cancelled";

    // A defect in our own code. Retryable: the next deploy may fix it.
    public const string ApplicationBug = "application_bug";
}
