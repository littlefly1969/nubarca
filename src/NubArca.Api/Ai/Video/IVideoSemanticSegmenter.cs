namespace NubArca.Api.Ai.Video;

// VSEM-01: raw scene-boundary CANDIDATE extraction for one video blob.
//
// The segmenter's only job is to run the detector and hand back timestamps in
// seconds. It applies no policy: merging, splitting, capping and sampling all
// happen later in the pure VideoSemanticManifestBuilder. It never throws for a
// media or process problem — every such path resolves to a sanitized error
// code. Cancellation is the ONE exception and propagates unchanged.
public interface IVideoSemanticSegmenter
{
    Task<VideoSemanticSegmenterResult> DetectSceneCandidatesAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        CancellationToken cancellationToken);
}

// A successful result may legitimately carry ZERO candidates (a single-shot
// video): that is not a failure, it is the input to the uniform fallback.
public sealed record VideoSemanticSegmenterResult(
    bool Succeeded,
    IReadOnlyList<double> CandidateSeconds,
    string? ErrorCode,
    int? ProcessExitCode = null)
{
    public static VideoSemanticSegmenterResult Ok(IReadOnlyList<double> candidates)
        => new(true, candidates, null);

    public static VideoSemanticSegmenterResult Failure(string errorCode, int? exitCode = null)
        => new(false, Array.Empty<double>(), errorCode, exitCode);
}
