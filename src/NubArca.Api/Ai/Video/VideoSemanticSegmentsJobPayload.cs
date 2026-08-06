namespace NubArca.Api.Ai.Video;

// VSEM-01: payload for ai.videos.segments.backfill.
//
// Flags, an optional BLOB id and an optional version — nothing else. No owner
// id, no FileItem id, no filename, no storage key, no path. The blob id is the
// server-side single-target scope used by the post-metadata chaining, so a
// freshly probed video is segmented with a bounded point lookup instead of a
// library scan.
public sealed record VideoSemanticSegmentsJobPayload(
    Guid? BlobObjectId = null,
    int? Limit = null,
    bool FailedOnly = false,
    bool DryRun = false,
    int? SegmentationVersion = null);
