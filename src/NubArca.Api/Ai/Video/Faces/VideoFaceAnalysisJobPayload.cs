namespace NubArca.Api.Ai.Video.Faces;

// VFACE-01: payload of ai.videos.faces.backfill. Carries only flags, optional
// profile STABLE KEYS, an optional blob id and optional versions — never an
// owner id, person id, person name, filename, storage key or path.
//
// DetectionProfileKey and EmbeddingProfileKey are accepted separately to mirror
// the schema's profile pair. In this codebase one AiProfile encapsulates the
// whole face package (detector + recognizer), so supplying either one selects
// that package; supplying two DIFFERENT keys is rejected as a configuration
// error rather than silently mixing recognition spaces.
public sealed record VideoFaceAnalysisJobPayload(
    Guid? BlobObjectId = null,
    int? SegmentationVersion = null,
    int? AnalysisVersion = null,
    string? DetectionProfileKey = null,
    string? EmbeddingProfileKey = null,
    int? Limit = null,
    bool FailedOnly = false,
    bool DryRun = false);
