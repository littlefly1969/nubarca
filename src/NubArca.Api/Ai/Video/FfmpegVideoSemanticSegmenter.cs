using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;

namespace NubArca.Api.Ai.Video;

// VSEM-01: scene-change candidate detection via an external FFmpeg process.
//
// Invocation safety (all of these are contract, not style):
//   * arguments are SEPARATE TOKENS through IProcessRunner → ProcessStartInfo
//     .ArgumentList; there is no shell, no string concatenation, no quoting;
//   * the input is an opaque GUID-named temp file — the storage key, the blob
//     id and the user-visible filename never reach the command line;
//   * the filter graph is a CONSTANT with exactly one interpolated value, the
//     numeric scene threshold, formatted invariantly and range-checked by the
//     options validator, so no user input can shape the graph;
//   * `-nostdin` plus a local file path: no URL, no pipe, no other protocol;
//   * hard timeout, bounded stdout, and a cancellation token that kills the
//     whole process tree;
//   * stderr is discarded by the runner and raw stdout is never logged or
//     persisted — only the parsed timestamps and a sanitized code escape.
//
// The detector reads the file but produces NO output media: `-f null -` throws
// the decoded frames away and `metadata=print:file=-` writes the scene scores
// of the selected frames to stdout. No frame is ever written to disk.
public sealed class FfmpegVideoSemanticSegmenter : IVideoSemanticSegmenter
{
    private readonly IOptions<VideoSemanticSegmentationOptions> _options;
    private readonly IOptions<MediaOptions> _media;
    private readonly IProcessRunner _runner;
    private readonly ILogger<FfmpegVideoSemanticSegmenter> _logger;

    public FfmpegVideoSemanticSegmenter(
        IOptions<VideoSemanticSegmentationOptions> options,
        IOptions<MediaOptions> media,
        IProcessRunner runner,
        ILogger<FfmpegVideoSemanticSegmenter> logger)
    {
        _options = options;
        _media = media;
        _runner = runner;
        _logger = logger;
    }

    public async Task<VideoSemanticSegmenterResult> DetectSceneCandidatesAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openBlobContent);

        var options = _options.Value;
        var tempFile = Path.Combine(Path.GetTempPath(), $"nc-vseg-{Guid.NewGuid():N}.tmp");
        try
        {
            var staged = await StageAsync(openBlobContent, tempFile, cancellationToken);
            if (staged is not null)
            {
                return staged;
            }

            ProcessRunResult result;
            try
            {
                result = await _runner.RunAsync(
                    new ProcessRunRequest(
                        _media.Value.FfmpegPath,
                        BuildArguments(tempFile, options.SceneThreshold),
                        options.ProcessTimeoutSeconds,
                        options.MaximumProcessOutputBytes),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The binary is missing, not executable, or the OS refused to
                // spawn it. An ENVIRONMENT problem — retryable, never a content
                // skip. Only the exception TYPE is logged (a message can carry
                // the executable path).
                _logger.LogWarning(
                    "video-segments: scene detector could not start ({ExceptionType}).", ex.GetType().Name);
                return VideoSemanticSegmenterResult.Failure(VideoSemanticErrorCodes.ProcessStart);
            }

            if (result.TimedOut)
            {
                _logger.LogWarning(
                    "video-segments: scene detection timed out after {TimeoutSeconds}s.",
                    options.ProcessTimeoutSeconds);
                return VideoSemanticSegmenterResult.Failure(VideoSemanticErrorCodes.ProcessTimeout);
            }

            if (result.OutputTruncated)
            {
                _logger.LogWarning(
                    "video-segments: scene detection exceeded the {MaxBytes}-byte output cap.",
                    options.MaximumProcessOutputBytes);
                return VideoSemanticSegmenterResult.Failure(VideoSemanticErrorCodes.ProcessOutputLimit);
            }

            if (result.ExitCode != 0)
            {
                // A non-zero exit means FFmpeg could not decode the input. The
                // exit code itself is safe to surface (a small integer).
                _logger.LogWarning(
                    "video-segments: scene detection exited with code {ExitCode}.", result.ExitCode);
                return VideoSemanticSegmenterResult.Failure(
                    VideoSemanticErrorCodes.ProcessExit, result.ExitCode);
            }

            return VideoSemanticSegmenterResult.Ok(ParseCandidateSeconds(result.StdoutBytes));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "video-segments: scene detection raised an unexpected {ExceptionType}.", ex.GetType().Name);
            return VideoSemanticSegmenterResult.Failure(VideoSemanticErrorCodes.ApplicationBug);
        }
        finally
        {
            TryDeleteTempFile(tempFile);
        }
    }

    // Copies the blob to an opaque temp file. Returns null on success, or the
    // classified failure. Reading the blob and writing the temp file are
    // distinguished so a full disk is not reported as a storage corruption.
    private async Task<VideoSemanticSegmenterResult?> StageAsync(
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
                "video-segments: blob content could not be opened ({ExceptionType}).", ex.GetType().Name);
            return VideoSemanticSegmenterResult.Failure(VideoSemanticErrorCodes.BlobStorage);
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
                    "video-segments: temporary staging failed ({ExceptionType}).", ex.GetType().Name);
                return VideoSemanticSegmenterResult.Failure(VideoSemanticErrorCodes.TemporaryStorage);
            }
        }

        return null;
    }

    // Separate tokens only — this list is handed to ProcessStartInfo
    // .ArgumentList verbatim.
    internal static IReadOnlyList<string> BuildArguments(string inputPath, double sceneThreshold)
    {
        var threshold = sceneThreshold.ToString("0.####", CultureInfo.InvariantCulture);
        return
        [
            // Never consume the parent's stdin: a detector that blocks waiting
            // for input would burn the whole timeout.
            "-nostdin",
            "-v", "error",
            "-i", inputPath,
            // Video only: audio/subtitle/data streams cannot affect scene scores
            // and decoding them is wasted work.
            "-an", "-sn", "-dn",
            "-filter:v", $"select='gt(scene,{threshold})',metadata=print:file=-",
            // Discard the frames — we want the metadata, not an output file.
            "-f", "null", "-",
        ];
    }

    // FFmpeg's `metadata=print` emits, per selected frame:
    //   frame:12   pts:98304   pts_time:2.048
    //   lavfi.scene_score=0.512345
    // Only pts_time matters here. Anything unparseable is ignored — a detector
    // that emits an unexpected line must degrade to "fewer candidates", never
    // to a crash or a bogus timestamp.
    internal static IReadOnlyList<double> ParseCandidateSeconds(byte[] stdoutBytes)
    {
        if (stdoutBytes is null || stdoutBytes.Length == 0)
        {
            return Array.Empty<double>();
        }

        var text = Encoding.UTF8.GetString(stdoutBytes);
        var lines = text.Split('\n');

        // A process killed mid-write leaves a partial final line. Without a
        // trailing newline the last line is not known to be complete, so it is
        // dropped rather than half-parsed.
        var lineCount = text.EndsWith('\n') ? lines.Length : lines.Length - 1;

        var candidates = new List<double>();
        for (var i = 0; i < lineCount; i++)
        {
            var seconds = ParsePtsTime(lines[i].AsSpan().Trim());
            if (seconds is double value)
            {
                candidates.Add(value);
            }
        }

        return candidates;
    }

    private static double? ParsePtsTime(ReadOnlySpan<char> line)
    {
        const string Marker = "pts_time:";
        var markerIndex = line.IndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var rest = line[(markerIndex + Marker.Length)..];
        var end = 0;
        while (end < rest.Length && !char.IsWhiteSpace(rest[end]))
        {
            end++;
        }

        return double.TryParse(rest[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds)
            ? seconds
            : null;
    }

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
                "video-segments: temporary input file could not be deleted ({ExceptionType}).",
                ex.GetType().Name);
        }
    }
}
