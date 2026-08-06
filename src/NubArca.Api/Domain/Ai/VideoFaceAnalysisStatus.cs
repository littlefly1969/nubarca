namespace NubArca.Api.Domain.Ai;

// VFACE-01: aggregate face-track readiness of ONE temporal manifest under ONE
// (analysis version, detection profile, embedding profile).
//
// Deliberately a SEPARATE entity from the manifest head and from
// VideoSemanticEmbeddingStatus: segmentation readiness, SigLIP2 visual-embedding
// readiness and face-track readiness are three INDEPENDENT axes. A face-analysis
// failure must never mark the temporal manifest failed, and face analysis never
// waits for (or is invalidated by) VSEM-02 visual embeddings.
//
// BLOB-LEVEL AND OWNER-FREE, like the whole video substrate: no OwnerUserId, no
// FileItemId, no PersonId, no person name, no filename, no folder/album, no user
// tag, no storage key, no path. Identity and every user decision are owner-level
// and belong to VFACE-02.
//
// One row exists per (VideoSemanticIndexId, AnalysisVersion, DetectionProfileId,
// EmbeddingProfileId). The segmentation version is implied by the manifest the
// row points at, so a reindex or a new analysis version gets its own row next to
// the old ones. A MISSING row means "not attempted yet" (implicit pending — never
// materialised up front). Feature-disabled / provider-unavailable runs write NO
// row at all (substrate rule: environment state is never a blob outcome).
public class VideoFaceAnalysisStatus
{
    public Guid Id { get; set; }

    public Guid VideoSemanticIndexId { get; set; }

    // Bumped whenever the sampling policy OR the tracking/aggregation semantics
    // change the produced tracks. See VideoFaceAnalysisOptions.AnalysisVersion.
    public int AnalysisVersion { get; set; }

    // The face package profile that detected the faces. In this codebase one
    // AiProfile encapsulates detector + recognizer, so this normally equals
    // EmbeddingProfileId — the pair is stored explicitly so a future split
    // detector/recognizer packaging needs no schema change.
    public Guid DetectionProfileId { get; set; }

    public Guid EmbeddingProfileId { get; set; }

    // One of VideoFaceAnalysisStatuses.
    public string Status { get; set; } = VideoFaceAnalysisStatuses.Completed;

    // Frames the deterministic sampling policy planned for this manifest.
    public int PlannedFrameCount { get; set; }

    // Frames that yielded a usable image and completed detection.
    public int ProcessedFrameCount { get; set; }

    public int FailedFrameCount { get; set; }

    // Tracks persisted by this attempt.
    public int TrackCount { get; set; }

    // Sanitized machine-readable code from VideoFaceErrorCodes describing the
    // dominant/aggregate outcome. Null when completed.
    public string? ErrorCode { get; set; }

    public int AttemptCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    // Set when Status reaches a terminal outcome for this attempt.
    public DateTime? CompletedAt { get; set; }
}
