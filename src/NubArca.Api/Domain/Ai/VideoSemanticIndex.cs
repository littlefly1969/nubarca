namespace NubArca.Api.Domain.Ai;

// VSEM-01: the canonical, versioned TEMPORAL MANIFEST head for one video blob.
//
// Blob-level by construction, exactly like BlobMetadata / BlobEmbedding: the
// segmentation is deterministic on the immutable bytes, so every FileItem that
// references the blob shares ONE manifest. Nothing owner-specific is stored
// here — no OwnerUserId, no FileItemId, no PersonId, no filename, no folder or
// album, no user tags, no storage key, no path. Owner isolation is enforced
// later, when a caller resolves which FileItems the viewer may actually see.
//
// One row exists per (BlobObjectId, SegmentationVersion). A MISSING row means
// "not processed yet" — pending rows are never materialised up front (same
// sparse rule as BlobAiArtifactStatus). Old and new versions coexist so a
// reindex under a new version never destroys the previous manifest.
//
// Status is one of AiArtifactStatuses (completed | skipped | failed). A
// completed row is only ever written together with its full, valid segment and
// sample tree inside one transaction, so a partial manifest can never appear
// completed.
public class VideoSemanticIndex
{
    public Guid Id { get; set; }

    public Guid BlobObjectId { get; set; }

    // Bumped whenever the algorithm OR the effective configuration changes the
    // temporal output. See VideoSemanticSegmentationOptions.SegmentationVersion.
    public int SegmentationVersion { get; set; }

    // One of AiArtifactStatuses. No stored "pending" (absence means that).
    public string Status { get; set; } = AiArtifactStatuses.Completed;

    // Sanitized machine-readable code from VideoSemanticErrorCodes. Never raw
    // FFmpeg output, exception text, paths or storage keys.
    public string? ErrorCode { get; set; }

    // True when the outcome will not be retried: a content-related permanent
    // skip, or an exhausted failure. A provider/environment problem is NEVER
    // permanent.
    public bool IsPermanentFailure { get; set; }

    public int AttemptCount { get; set; }

    // Normalized duration the manifest covers, in whole milliseconds (the last
    // segment always reaches it). Null until a run completes.
    public long? DurationMilliseconds { get; set; }

    public int SegmentCount { get; set; }

    public int SampleCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    // Set only when Status reaches a terminal outcome for this attempt.
    public DateTime? CompletedAt { get; set; }
}
