namespace NubArca.Api.Ai.Video;

public enum VideoSemanticSegmentationOutcomeKind
{
    // A fresh manifest was built and committed.
    Completed,

    // A terminal manifest for this (blob, version) already existed — completed,
    // or permanently skipped. No work was done and nothing was overwritten.
    AlreadyTerminal,

    // A permanent, content-related reason not to segment this blob.
    Skipped,

    // A retryable failure (process, storage, database, application).
    Failed,
}

// The result of ONE segmentation attempt on ONE blob. Counts and a sanitized
// code only — nothing owner-specific, no paths, no raw detector output.
public sealed record VideoSemanticSegmentationOutcome(
    VideoSemanticSegmentationOutcomeKind Kind,
    string? ErrorCode = null,
    int SegmentCount = 0,
    int SampleCount = 0,
    int CandidateCount = 0,
    bool FallbackUsed = false)
{
    public static VideoSemanticSegmentationOutcome Skipped(string errorCode)
        => new(VideoSemanticSegmentationOutcomeKind.Skipped, errorCode);

    public static VideoSemanticSegmentationOutcome Failed(string errorCode)
        => new(VideoSemanticSegmentationOutcomeKind.Failed, errorCode);
}
