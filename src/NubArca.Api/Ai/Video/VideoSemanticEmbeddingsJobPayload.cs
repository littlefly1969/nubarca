namespace NubArca.Api.Ai.Video;

// VSEM-02: payload of ai.videos.embeddings.backfill. Carries only flags, an
// optional profile STABLE KEY, an optional blob id and an optional
// segmentation version — never an owner id, filename, storage key or path.
public sealed record VideoSemanticEmbeddingsJobPayload(
    string? ProfileKey = null,
    Guid? BlobObjectId = null,
    int? SegmentationVersion = null,
    int? Limit = null,
    bool FailedOnly = false,
    bool DryRun = false);
