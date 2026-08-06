using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;

namespace NubArca.Api.Ai.Video;

// VSEM-02: sample-frame extraction via an external FFmpeg process.
//
// Strategy (Gate 3): SEQUENTIAL ACCURATE SEEKS against one staged local file.
// The blob is staged exactly once per video; each requested timestamp is one
// short-lived process doing an input-side seek (`-ss` before `-i`: jump to the
// nearest preceding keyframe, then decode forward to the exact position) that
// emits ONE frame to stdout. Process count per video therefore equals the
// requested sample count — bounded by VSEM-01's MaximumSamplesPerVideo — run
// strictly sequentially (concurrency 1) with a per-process timeout. Accuracy
// is within one frame duration of the requested timestamp. The batch interface
// is the seam for a future chunked/one-pass strategy if measurement ever
// demands it.
//
// Invocation safety (contract, mirrored from FfmpegVideoSemanticSegmenter):
//   * separate argument tokens via IProcessRunner — no shell, no quoting;
//   * the input is an opaque GUID-named temp file; the frame goes to stdout
//     (`pipe:1`), so there is no output filename at all;
//   * the filter graph is a CONSTANT with exactly one interpolated value, the
//     caller-supplied validator-bounded integer frameMaxEdge — no user-controlled
//     filters. The VALUE belongs to the calling pipeline (VSEM-02 and VFACE-01
//     have independent resolution settings); only the process/output caps below
//     are shared transport limits;
//   * autorotation stays enabled (FFmpeg default; `-noautorotate` is never
//     passed), the scale filter preserves source aspect (no crop, no pad, no
//     16:9 staging) and applies `setsar=1`;
//   * `-nostdin` plus a local file path: no URL, no other protocol;
//   * hard per-process timeout, bounded stdout, cooperative cancellation;
//   * temp file removal on success, failure and cancellation;
//   * raw process output is never logged; only counts and sanitized codes.
public sealed class FfmpegVideoSemanticFrameExtractor
    : IVideoSemanticFrameExtractor, IVideoSemanticFrameStreamExtractor
{
    private readonly IOptions<VideoVisualEmbeddingOptions> _options;
    private readonly IOptions<MediaOptions> _media;
    private readonly IProcessRunner _runner;
    private readonly ILogger<FfmpegVideoSemanticFrameExtractor> _logger;

    public FfmpegVideoSemanticFrameExtractor(
        IOptions<VideoVisualEmbeddingOptions> options,
        IOptions<MediaOptions> media,
        IProcessRunner runner,
        ILogger<FfmpegVideoSemanticFrameExtractor> logger)
    {
        _options = options;
        _media = media;
        _runner = runner;
        _logger = logger;
    }

    // Batch form (VSEM-02): the streaming pass with every frame collected. One
    // extraction code path, so staging, timeout, classification and cleanup can
    // never drift between the two contracts.
    public async Task<VideoSemanticFrameBatchResult> ExtractFramesAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        IReadOnlyList<VideoSemanticFrameRequest> requests,
        int frameMaxEdge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var results = new List<VideoSemanticFrameResult>(requests.Count);
        var stagingError = await ExtractFramesStreamingAsync(
            openBlobContent,
            requests,
            frameMaxEdge,
            (frame, _) =>
            {
                results.Add(frame);
                return Task.CompletedTask;
            },
            cancellationToken);

        return stagingError is not null
            ? VideoSemanticFrameBatchResult.StagingFailure(stagingError)
            : new VideoSemanticFrameBatchResult(null, results);
    }

    public async Task<string?> ExtractFramesStreamingAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        IReadOnlyList<VideoSemanticFrameRequest> requests,
        int frameMaxEdge,
        Func<VideoSemanticFrameResult, CancellationToken, Task> onFrame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openBlobContent);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(onFrame);

        if (requests.Count == 0)
        {
            return null;
        }

        var options = _options.Value;
        var tempFile = Path.Combine(Path.GetTempPath(), $"nc-vframe-{Guid.NewGuid():N}.tmp");
        try
        {
            var stagingError = await StageAsync(openBlobContent, tempFile, cancellationToken);
            if (stagingError is not null)
            {
                return stagingError;
            }

            string? startFailure = null;

            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A negative timestamp cannot come from a valid manifest; it is
                // rejected without ever reaching the process.
                if (request.TimestampMilliseconds < 0)
                {
                    await onFrame(Failure(request, VideoSemanticErrorCodes.FrameExtraction), cancellationToken);
                    continue;
                }

                // Once the binary itself failed to start there is no point in
                // spawning it once per remaining sample.
                if (startFailure is not null)
                {
                    await onFrame(Failure(request, startFailure), cancellationToken);
                    continue;
                }

                ProcessRunResult run;
                try
                {
                    run = await _runner.RunAsync(
                        new ProcessRunRequest(
                            _media.Value.FfmpegPath,
                            BuildFrameArguments(tempFile, request.TimestampMilliseconds, frameMaxEdge),
                            options.FrameTimeoutSeconds,
                            options.MaximumFrameOutputBytes),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "video-embed: frame extractor could not start ({ExceptionType}).", ex.GetType().Name);
                    startFailure = VideoSemanticErrorCodes.ProcessStart;
                    await onFrame(Failure(request, startFailure), cancellationToken);
                    continue;
                }

                await onFrame(Classify(request, run), cancellationToken);
            }

            return null;
        }
        finally
        {
            TryDeleteTempFile(tempFile);
        }
    }

    private static VideoSemanticFrameResult Failure(VideoSemanticFrameRequest request, string code)
        => new(request.SampleId, request.TimestampMilliseconds, null, code);

    private static VideoSemanticFrameResult Classify(
        VideoSemanticFrameRequest request, ProcessRunResult run)
    {
        if (run.TimedOut)
        {
            return Failure(request, VideoSemanticErrorCodes.ProcessTimeout);
        }

        if (run.OutputTruncated)
        {
            return Failure(request, VideoSemanticErrorCodes.ProcessOutputLimit);
        }

        // A non-zero exit, or a zero exit with no/garbage output (e.g. a seek
        // past the end of the stream), are the same per-sample outcome: no
        // usable frame at this timestamp.
        if (run.ExitCode != 0 || !IsValidJpeg(run.StdoutBytes))
        {
            return Failure(request, VideoSemanticErrorCodes.FrameExtraction);
        }

        return new VideoSemanticFrameResult(
            request.SampleId, request.TimestampMilliseconds, run.StdoutBytes, null);
    }

    // Copies the blob to an opaque temp file (staged ONCE per batch). Returns
    // null on success, or a classified batch-level failure code.
    private async Task<string?> StageAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent, string tempFile,
        CancellationToken cancellationToken)
    {
        Stream source;
        try
        {
            source = await openBlobContent(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "video-embed: blob content could not be opened ({ExceptionType}).", ex.GetType().Name);
            return VideoSemanticErrorCodes.BlobStorage;
        }

        await using (source)
        {
            try
            {
                await using var destination = new FileStream(
                    tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                await source.CopyToAsync(destination, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "video-embed: temporary staging failed ({ExceptionType}).", ex.GetType().Name);
                return VideoSemanticErrorCodes.TemporaryStorage;
            }
        }

        return null;
    }

    // Separate tokens only — handed to ProcessStartInfo.ArgumentList verbatim.
    internal static IReadOnlyList<string> BuildFrameArguments(
        string inputPath, long timestampMilliseconds, int frameMaxEdge)
    {
        // Integral milliseconds → invariant seconds ("12.345"): no culture, no
        // floating-point drift.
        var seconds = (timestampMilliseconds / 1000d).ToString("0.###", CultureInfo.InvariantCulture);
        return
        [
            "-nostdin",
            "-v", "error",
            // Input-side seek: fast (keyframe jump) AND accurate (decodes
            // forward to the exact requested time).
            "-ss", seconds,
            "-i", inputPath,
            "-an", "-sn", "-dn",
            "-frames:v", "1",
            // Fit within the box, source aspect preserved — no crop, no pad,
            // no stretch. Autorotation is on by default, so iw/ih are already
            // the display-rotated dimensions.
            "-vf", $"scale={frameMaxEdge}:{frameMaxEdge}:force_original_aspect_ratio=decrease,setsar=1",
            "-f", "image2",
            "-q:v", "2",
            // The frame goes to stdout: no output filename exists at all.
            "pipe:1",
        ];
    }

    private static bool IsValidJpeg(byte[] bytes)
        => bytes is { Length: > 3 } && bytes[0] == 0xFF && bytes[1] == 0xD8;

    private void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "video-embed: temporary input file could not be deleted ({ExceptionType}).",
                ex.GetType().Name);
        }
    }
}
