using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Files;

/// <summary>
/// Extracts a real poster frame from a video file by invoking an external
/// ffmpeg process. Falls back to <see cref="SyntheticVideoPosterProvider"/>
/// on any failure (missing binary, non-zero exit, timeout, oversized or
/// invalid output, corrupt video).
///
/// Local filesystem streams are passed directly to ffmpeg, avoiding a full
/// source copy for every derivative. Other stream types are written to a
/// GUID-named temp file. Neither the blob storage key nor the physical path are
/// ever logged or exposed. ffmpeg stderr is discarded (not read into logs) to
/// avoid leaking invocation details. Only the exit code, timed-out flag, or
/// output-validation result appears in log messages.
/// </summary>
public sealed class FfmpegVideoPosterProvider : IVideoPosterProvider
{
    // JPEG magic bytes — output that doesn't start with FF D8 is rejected.
    private static readonly byte[] JpegMagic = [0xFF, 0xD8];

    private readonly IOptions<MediaOptions> _options;
    private readonly IProcessRunner _runner;
    private readonly SyntheticVideoPosterProvider _fallback;
    private readonly ILogger<FfmpegVideoPosterProvider> _logger;
    private readonly MediaDerivativesOptions _derivatives;

    public FfmpegVideoPosterProvider(
        IOptions<MediaOptions> options,
        IProcessRunner runner,
        SyntheticVideoPosterProvider fallback,
        ILogger<FfmpegVideoPosterProvider> logger,
        IOptions<MediaDerivativesOptions>? derivatives = null)
    {
        _options = options;
        _runner = runner;
        _fallback = fallback;
        _logger = logger;
        _derivatives = derivatives?.Value ?? new MediaDerivativesOptions();
    }

    public async Task<VideoPosterResult?> TryGetPosterAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        var posterSize = _derivatives.PosterSize;
        var tempFile = Path.Combine(Path.GetTempPath(), $"nc-poster-{Guid.NewGuid():N}.tmp");
        try
        {
            var inputFile = await PrepareSeekableInputAsync(
                openBlobContent, tempFile, cancellationToken);

            // Preserve the SOURCE aspect ratio: scale the (already display-
            // rotated) frame to fit within a maxEdge×maxEdge box without cropping,
            // padding, or distortion — a portrait/square video yields a portrait/
            // square poster. No 16:9 staging and no baked-in backdrop: the client
            // draws the blurred backdrop behind a `contain` foreground, so the
            // tile can use the video's real shape (task: proportional media wall).
            // Arguments are passed as a list — no shell interpolation.
            var maxEdge = Math.Max(posterSize.Width, posterSize.Height);
            var posterFilter =
                $"scale={maxEdge}:{maxEdge}:force_original_aspect_ratio=decrease,setsar=1";
            var args = new[]
            {
                "-y",
                "-ss", opts.VideoPosterSeekSeconds.ToString(CultureInfo.InvariantCulture),
                "-i", inputFile,
                "-vframes", "1",
                "-vf", posterFilter,
                "-f", "image2",
                "-q:v", "2",
                "pipe:1",
            };

            var result = await _runner.RunAsync(
                new ProcessRunRequest(
                    opts.FfmpegPath,
                    args,
                    opts.VideoPosterTimeoutSeconds,
                    opts.VideoPosterMaxOutputBytes),
                cancellationToken);

            if (result.TimedOut)
            {
                _logger.LogWarning(
                    "FFmpeg poster extraction timed out after {TimeoutSeconds}s; using synthetic fallback.",
                    opts.VideoPosterTimeoutSeconds);
                return await _fallback.TryGetPosterAsync(openBlobContent, cancellationToken);
            }

            // A seek PAST THE END yields no frame: ffmpeg either exits non-zero
            // or writes nothing. That is the normal case for clips SHORTER than
            // the configured seek offset (a 2 s phone clip vs -ss 3), and it is
            // why a large batch of short videos was stuck on placeholder
            // posters — every regeneration silently fell back to synthetic.
            // Retry once from the very first frame before giving up.
            var usable = result.ExitCode == 0 && IsValidJpeg(result.StdoutBytes);
            if (!usable && opts.VideoPosterSeekSeconds > 0)
            {
                _logger.LogInformation(
                    "FFmpeg poster extraction produced no frame at the configured seek offset; retrying from the start.");
                var retryArgs = args.ToArray();
                var seekAt = Array.IndexOf(retryArgs, "-ss");
                if (seekAt >= 0 && seekAt + 1 < retryArgs.Length)
                {
                    retryArgs[seekAt + 1] = "0";
                }
                result = await _runner.RunAsync(
                    new ProcessRunRequest(
                        opts.FfmpegPath,
                        retryArgs,
                        opts.VideoPosterTimeoutSeconds,
                        opts.VideoPosterMaxOutputBytes),
                    cancellationToken);
                usable = !result.TimedOut && result.ExitCode == 0 && IsValidJpeg(result.StdoutBytes);
            }

            if (!usable)
            {
                _logger.LogWarning(
                    "FFmpeg poster extraction failed (exit {ExitCode}, {Bytes} bytes); using synthetic fallback.",
                    result.ExitCode, result.StdoutBytes.Length);
                return await _fallback.TryGetPosterAsync(openBlobContent, cancellationToken);
            }

            var ms = new MemoryStream(result.StdoutBytes);
            return new VideoPosterResult(ms, VideoPosterSources.Ffmpeg);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "FFmpeg poster extraction raised an exception ({ExceptionType}); using synthetic fallback.",
                ex.GetType().Name);
            return await _fallback.TryGetPosterAsync(openBlobContent, cancellationToken);
        }
        finally
        {
            // Always remove the temp input file, even on failure.
            TryDeleteTempFile(tempFile);
        }
    }

    public async Task<VideoPreviewStripResult?> TryGetPreviewStripAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        double? durationSeconds,
        CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        var stripSpec = _derivatives.VideoPreviewStripSize;
        var tempFile = Path.Combine(Path.GetTempPath(), $"nc-video-strip-{Guid.NewGuid():N}.tmp");
        try
        {
            var inputFile = await PrepareSeekableInputAsync(
                openBlobContent, tempFile, cancellationToken);

            var duration = IsUsableDuration(durationSeconds)
                ? durationSeconds
                : await TryProbeDurationAsync(inputFile, opts, cancellationToken);
            if (!IsUsableDuration(duration))
            {
                _logger.LogWarning("Cannot determine video duration for preview strip generation.");
                return null;
            }

            // Avoid common opening/closing fades: sample the centre of six
            // evenly-spaced buckets within the central 90% of the timeline.
            // Each timestamp is an independent input-side seek (`-ss` before
            // `-i`). This is essential for long videos: an fps/select filter on
            // one input would decode the entire interval between the first and
            // last sample, making work proportional to video duration.
            var safeDuration = Math.Max(0.25, duration!.Value);
            var timestamps = Enumerable.Range(0, stripSpec.FrameCount)
                .Select(index => safeDuration *
                    (0.05 + (index + 0.5) * 0.90 / stripSpec.FrameCount))
                .ToArray();

            var args = new List<string> { "-y" };
            foreach (var timestamp in timestamps)
            {
                args.Add("-ss");
                args.Add(timestamp.ToString("0.########", CultureInfo.InvariantCulture));
                args.Add("-i");
                args.Add(inputFile);
            }

            var filterParts = new List<string>();
            var cells = new List<string>();
            for (var index = 0; index < stripSpec.FrameCount; index++)
            {
                filterParts.Add(
                    $"[{index}:v]split=2[bg{index}][fg{index}];"
                    + $"[bg{index}]scale={stripSpec.FrameWidth}:{stripSpec.FrameHeight}:force_original_aspect_ratio=increase,"
                    + $"crop={stripSpec.FrameWidth}:{stripSpec.FrameHeight},gblur=sigma=10[back{index}];"
                    + $"[fg{index}]scale={stripSpec.FrameWidth}:{stripSpec.FrameHeight}:force_original_aspect_ratio=decrease[front{index}];"
                    + $"[back{index}][front{index}]overlay=(W-w)/2:(H-h)/2,setsar=1[cell{index}]");
                cells.Add($"[cell{index}]");
            }
            filterParts.Add(string.Concat(cells)
                + $"hstack=inputs={stripSpec.FrameCount}[strip]");

            args.AddRange([
                "-filter_complex", string.Join(";", filterParts),
                "-map", "[strip]",
                "-frames:v", "1",
                "-an",
                "-f", "image2",
                "-q:v", "3",
                "pipe:1",
            ]);
            var result = await _runner.RunAsync(
                new ProcessRunRequest(
                    opts.FfmpegPath,
                    args,
                    opts.VideoPreviewStripTimeoutSeconds,
                    opts.VideoPosterMaxOutputBytes),
                cancellationToken);

            if (result.TimedOut || result.ExitCode != 0 || !IsValidJpeg(result.StdoutBytes))
            {
                _logger.LogWarning(
                    "FFmpeg preview strip failed (exit {ExitCode}, timedOut {TimedOut}, bytes {Bytes}).",
                    result.ExitCode, result.TimedOut, result.StdoutBytes.Length);
                return null;
            }

            return new VideoPreviewStripResult(
                new MemoryStream(result.StdoutBytes),
                stripSpec.Width,
                stripSpec.Height,
                stripSpec.FrameCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Preview strip extraction raised an exception ({ExceptionType}).",
                ex.GetType().Name);
            return null;
        }
        finally
        {
            TryDeleteTempFile(tempFile);
        }
    }

    private async Task<double?> TryProbeDurationAsync(
        string tempFile,
        MediaOptions opts,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            new ProcessRunRequest(
                opts.FfprobePath,
                ["-v", "error", "-show_entries", "format=duration",
                    "-of", "default=noprint_wrappers=1:nokey=1", tempFile],
                opts.VideoProbeTimeoutSeconds,
                opts.VideoProbeMaxOutputBytes),
            cancellationToken);
        if (result.TimedOut || result.ExitCode != 0)
        {
            return null;
        }
        var value = Encoding.UTF8.GetString(result.StdoutBytes).Trim();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && IsUsableDuration(seconds)
                ? seconds
                : null;
    }

    private static async Task<string> PrepareSeekableInputAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        string tempFile,
        CancellationToken cancellationToken)
    {
        await using var source = await openBlobContent(cancellationToken);

        // Production's LocalFileSystemBlobStorage returns a FileStream opened
        // after storage-key validation. ffmpeg can seek that same read-only
        // file directly; avoiding a multi-gigabyte copy is critical for bulk
        // video backfills. The path stays process-internal and is never logged.
        if (source is FileStream fileStream && File.Exists(fileStream.Name))
        {
            return fileStream.Name;
        }

        // Non-file storage implementations still get the portable, seekable
        // GUID temp-file path. The caller's finally block removes it.
        await using var destination = new FileStream(
            tempFile, FileMode.Create, FileAccess.Write, FileShare.None,
            65536, useAsync: true);
        await source.CopyToAsync(destination, cancellationToken);
        return tempFile;
    }

    private static bool IsUsableDuration(double? seconds) =>
        seconds is > 0 && double.IsFinite(seconds.Value);

    private static bool IsValidJpeg(byte[] bytes)
    {
        if (bytes.Length < JpegMagic.Length) return false;
        for (var i = 0; i < JpegMagic.Length; i++)
        {
            if (bytes[i] != JpegMagic[i]) return false;
        }
        return true;
    }

    private void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete FFmpeg temp file (type: {Type}).", ex.GetType().Name);
        }
    }
}
