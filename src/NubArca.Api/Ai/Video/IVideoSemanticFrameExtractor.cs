namespace NubArca.Api.Ai.Video;

// VSEM-02: batch-oriented frame extraction for ONE video blob.
//
// The contract is deliberately batch-shaped: the implementation stages the
// original immutable blob ONCE per video and serves every requested sample
// timestamp from that one staged local file — never re-downloading per sample,
// and never touching the poster, preview strip, HLS renditions or any other
// derivative. Frames are transient in-memory payloads for inference; nothing
// is ever persisted as a media derivative.
//
// This interface is also the REPLACEMENT SEAM for future extraction
// strategies (chunked / one-pass); callers depend only on the per-request
// results, not on how many processes produced them.
//
// FRAME RESOLUTION IS THE CALLER'S: `frameMaxEdge` is passed per invocation and
// never read from configuration here. The extractor is shared by pipelines with
// genuinely different resolution needs (SigLIP2 semantic embedding vs face
// detection), and each owns its own setting — changing one must never move the
// other.
public interface IVideoSemanticFrameExtractor
{
    Task<VideoSemanticFrameBatchResult> ExtractFramesAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        IReadOnlyList<VideoSemanticFrameRequest> requests,
        int frameMaxEdge,
        CancellationToken cancellationToken);
}

// VFACE-01: the STREAMING form of the same extraction, for callers whose frame
// budget is far larger than VSEM-02's handful of samples.
//
// Identical staging and per-frame semantics — the blob is still staged exactly
// ONCE for the whole request list — but each frame is handed to the callback and
// then released, so peak memory is ONE frame instead of the whole plan. Video
// face analysis samples hundreds of frames per video and could not hold them all
// at once within any sane bound.
//
// Implemented by the same FFmpeg extractor; the batch contract above is built ON
// TOP of this one, so there is a single extraction code path.
public interface IVideoSemanticFrameStreamExtractor
{
    // Returns null when every requested frame was attempted (individual failures
    // are reported per frame through `onFrame`), or a batch-level staging error
    // code when the blob could not be staged at all.
    Task<string?> ExtractFramesStreamingAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        IReadOnlyList<VideoSemanticFrameRequest> requests,
        int frameMaxEdge,
        Func<VideoSemanticFrameResult, CancellationToken, Task> onFrame,
        CancellationToken cancellationToken);
}

// One requested sample frame: the sample identity plus its manifest timestamp.
public sealed record VideoSemanticFrameRequest(Guid SampleId, long TimestampMilliseconds);

// The outcome for one requested sample. `ImageBytes` is a JPEG in source
// aspect (display-rotated); null with a sanitized VideoSemanticErrorCodes
// value on failure. Failures are per-sample: one bad seek never poisons the
// batch.
public sealed record VideoSemanticFrameResult(
    Guid SampleId,
    long TimestampMilliseconds,
    byte[]? ImageBytes,
    string? ErrorCode)
{
    public bool Succeeded => ImageBytes is not null && ErrorCode is null;
}

// The whole-batch outcome. `StagingErrorCode` is set when the blob could not
// even be staged (blob storage / temp disk) — a batch-level retryable failure
// with no per-sample results.
public sealed record VideoSemanticFrameBatchResult(
    string? StagingErrorCode,
    IReadOnlyList<VideoSemanticFrameResult> Frames)
{
    public bool Staged => StagingErrorCode is null;

    public static VideoSemanticFrameBatchResult StagingFailure(string errorCode)
        => new(errorCode, Array.Empty<VideoSemanticFrameResult>());
}
