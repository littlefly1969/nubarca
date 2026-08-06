namespace NubArca.Api.Files;

// Video-hls slice 1: produces the two-rendition fMP4 HLS ladder for one source
// video file into a caller-owned staging directory. The caller (the HLS
// generation service) owns the temp source file, the staging directory, the DB
// lifecycle row and the atomic publish into HlsDerivativeStorage; the
// transcoder only turns bytes into ladder files and validates its own output.
public interface IVideoHlsTranscoder
{
    Task<VideoHlsTranscodeResult> TranscodeAsync(
        VideoHlsTranscodeRequest request, CancellationToken cancellationToken);
}

// The copy/encode decisions are made by the CALLER from probe data
// (BlobMetadata or an on-the-fly ffprobe) — the transcoder never inspects the
// source itself. HasAudio must be accurate: mapping an audio stream that does
// not exist fails the whole run.
public sealed record VideoHlsTranscodeRequest(
    // Seekable local temp file holding the source bytes (GUID-named, no
    // relation to the storage key).
    string SourceFilePath,
    // Staging directory the ladder is written into (created by the caller).
    string OutputDirectory,
    // True → the "high" rendition stream-copies the source video (already
    // H.264 at/below the height cap); false → re-encode capped at the cap.
    bool CopyVideo,
    // True → copy the source audio into "high" (already AAC); false → encode
    // AAC. Ignored when HasAudio is false.
    bool CopyAudio,
    bool HasAudio,
    // False for sources at/below the low rung's height (a single-rendition
    // ladder — upscaling would waste CPU and bytes).
    bool IncludeLowRendition);

public sealed record VideoHlsTranscodeResult(bool Success, string? ErrorCode)
{
    public static VideoHlsTranscodeResult Ok { get; } = new(true, null);
    public static VideoHlsTranscodeResult Fail(string errorCode) => new(false, errorCode);
}

// Sanitized machine-readable failure codes (never raw tool output).
public static class VideoHlsErrorCodes
{
    public const string ProviderUnavailable = "provider_unavailable";
    public const string ProbeFailed = "probe_failed";
    public const string Timeout = "timeout";
    public const string TranscodeFailed = "transcode_failed";
    public const string InvalidOutput = "invalid_output";
    public const string IoError = "io_error";
}

// Registered when Media:VideoHlsProvider=none (the default): HLS generation is
// disabled and every request fails softly. The generation service guards on
// MediaOptions.VideoHlsEnabled before doing any work, so this is a belt-and-
// braces backstop, mirroring NoopVideoMetadataExtractor.
public sealed class NoopVideoHlsTranscoder : IVideoHlsTranscoder
{
    public Task<VideoHlsTranscodeResult> TranscodeAsync(
        VideoHlsTranscodeRequest request, CancellationToken cancellationToken)
        => Task.FromResult(VideoHlsTranscodeResult.Fail(VideoHlsErrorCodes.ProviderUnavailable));
}
