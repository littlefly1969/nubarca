namespace NubArca.Api.Domain.Ai;

// VSEM-02: aggregate visual-embedding readiness of ONE temporal manifest under
// ONE AI profile.
//
// Deliberately a SEPARATE entity from the manifest head: segmentation readiness
// (VideoSemanticIndex.Status) and embedding readiness are independent axes — an
// inference failure must never mark the temporal manifest failed, and a
// completed manifest may carry different embedding states for different
// profiles. BlobAiArtifactStatus was considered and rejected for this role: it
// is keyed (blob, profile, capability) and cannot represent the segmentation
// version or the per-sample progress counts without overloading it.
//
// One row exists per (VideoSemanticIndexId, ProfileId). The segmentation
// version is implied by the manifest the row points at, so a reindex gets its
// own aggregate rows next to the old ones. A MISSING row means "not attempted
// yet" (implicit pending — never materialised up front).
//
// Status is one of VideoSemanticEmbeddingStatuses:
//   completed → every sample of the manifest has a completed embedding
//   partial   → at least one completed and at least one failed/missing sample
//   failed    → processing ran and produced no usable sample embedding
//   skipped   → a permanent content-level reason (e.g. no eligible reference)
// Provider-unavailable / feature-disabled runs write NO row at all (substrate
// rule: environment state is never persisted as a blob outcome).
public class VideoSemanticEmbeddingStatus
{
    public Guid Id { get; set; }

    public Guid VideoSemanticIndexId { get; set; }

    public Guid ProfileId { get; set; }

    public string Status { get; set; } = VideoSemanticEmbeddingStatuses.Completed;

    // Samples the manifest requires (its SampleCount at processing time).
    public int ExpectedSampleCount { get; set; }

    public int CompletedSampleCount { get; set; }

    public int FailedSampleCount { get; set; }

    // Sanitized machine-readable code from VideoSemanticErrorCodes describing
    // the dominant/aggregate failure. Null when completed.
    public string? ErrorCode { get; set; }

    public int AttemptCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    // Set when Status reaches `completed` (all samples embedded).
    public DateTime? CompletedAt { get; set; }
}
