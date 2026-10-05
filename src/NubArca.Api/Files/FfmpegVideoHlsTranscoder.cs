using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Files;

// Video-hls slice 1: produces the fMP4 HLS ladder by invoking an external
// ffmpeg process (one invocation per source — the source is decoded once even
// when both renditions are encoded). Argument shapes were validated against a
// real ffmpeg run; notes that matter:
//   - `-var_stream_map "... name:high ..."` + output pattern `%v/stream.m3u8`
//     puts each rendition in its own named subdirectory.
//   - the master playlist lands at the working-directory root.
//   - ffmpeg suffixes the fMP4 init file with the variant index
//     (high/init_0.mp4, low/init_1.mp4); the variant playlists reference the
//     suffixed names, so output validation and the serving whitelist accept
//     init(_N)?.mp4.
//
// Security: mirrors FfprobeVideoMetadataExtractor / FfmpegVideoPosterProvider.
// The source is a GUID-named temp file owned by the caller; stdout/stderr are
// discarded (never logged); only the exit code / timed-out flag / output-
// validation verdict reach the log. Never throws for tool failures — every
// failure path resolves to a sanitized error code.
public sealed class FfmpegVideoHlsTranscoder : IVideoHlsTranscoder
{
    // Bump when the ladder shape / encoder arguments change so a future
    // backfill can re-run rows produced by an older pipeline.
    // v2: aspect-aware scaling — the height caps apply to the SHORT side
    // (portrait 1080×1920 stays full-res instead of being downscaled to
    // 608×1080), matching the conventional meaning of "1080p"/"480p".
    // v3: HDR is tone-mapped and wide-gamut colour converted to BT.709 SDR
    // (VideoColorFormat); an H.264 source is copied only when it is 8-bit
    // 4:2:0 BT.709 SDR, which every player decodes.
    public const int Version = 3;

    private readonly IOptions<MediaOptions> _options;
    private readonly IDirectoryProcessRunner _runner;
    private readonly IVideoColorProbe _color;
    private readonly ILogger<FfmpegVideoHlsTranscoder> _logger;

    public FfmpegVideoHlsTranscoder(
        IOptions<MediaOptions> options,
        IDirectoryProcessRunner runner,
        IVideoColorProbe color,
        ILogger<FfmpegVideoHlsTranscoder> logger)
    {
        _options = options;
        _runner = runner;
        _color = color;
        _logger = logger;
    }

    public async Task<VideoHlsTranscodeResult> TranscodeAsync(
        VideoHlsTranscodeRequest request, CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        try
        {
            var color = await _color.ProbeAsync(request.SourceFilePath, cancellationToken);
            var args = BuildArguments(request, opts, color);
            var result = await _runner.RunAsync(
                new ProcessDirectoryRunRequest(
                    opts.FfmpegPath,
                    args,
                    request.OutputDirectory,
                    opts.VideoHlsTimeoutSeconds),
                cancellationToken);

            if (result.TimedOut)
            {
                _logger.LogWarning(
                    "ffmpeg HLS transcode timed out after {TimeoutSeconds}s.",
                    opts.VideoHlsTimeoutSeconds);
                return VideoHlsTranscodeResult.Fail(VideoHlsErrorCodes.Timeout);
            }

            if (result.ExitCode != 0)
            {
                _logger.LogWarning(
                    "ffmpeg HLS transcode exited with code {ExitCode}.", result.ExitCode);
                return VideoHlsTranscodeResult.Fail(VideoHlsErrorCodes.TranscodeFailed);
            }

            if (!IsValidLadder(request.OutputDirectory, request.IncludeLowRendition))
            {
                _logger.LogWarning(
                    "ffmpeg HLS transcode exited 0 but the staging output is incomplete.");
                return VideoHlsTranscodeResult.Fail(VideoHlsErrorCodes.InvalidOutput);
            }

            return VideoHlsTranscodeResult.Ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "ffmpeg HLS transcode raised an exception ({ExceptionType}).",
                ex.GetType().Name);
            return VideoHlsTranscodeResult.Fail(VideoHlsErrorCodes.IoError);
        }
    }

    // Pure and internal for tests. Args are a list — no shell interpolation.
    internal static IReadOnlyList<string> BuildArguments(
        VideoHlsTranscodeRequest r, MediaOptions o, VideoColorFormat color)
    {
        var inv = CultureInfo.InvariantCulture;
        // HDR and wide-gamut sources become BT.709 SDR after the scale — at
        // the rendition's size, not the source's, and on the even dimensions
        // the scale below always produces. Such a source is never copied: its
        // high rung would disagree with the converted low one.
        var toSdr = color.ToBt709Filter() is string filter ? "," + filter : "";
        // Fail-closed: only a stream KNOWN to be plain 8-bit 4:2:0 SDR is copied.
        var copyVideo = r.CopyVideo && color.CanStreamCopy;
        var args = new List<string> { "-y", "-i", r.SourceFilePath };

        // One -map pair (video[, audio]) PER RENDITION, in var_stream_map order.
        var renditions = r.IncludeLowRendition ? 2 : 1;
        for (var v = 0; v < renditions; v++)
        {
            args.AddRange(["-map", "0:v:0"]);
            if (r.HasAudio)
            {
                args.AddRange(["-map", "0:a:0"]);
            }
        }

        // Keyframes on the segment cadence for encoded renditions, so segment
        // boundaries align regardless of source fps.
        var keyframeExpr = string.Create(
            inv, $"expr:gte(t,n_forced*{o.VideoHlsSegmentSeconds})");

        // ---- high (v:0) ----
        if (copyVideo)
        {
            args.AddRange(["-c:v:0", "copy"]);
        }
        else
        {
            // Aspect-aware cap on the SHORT side (a = iw/ih): landscape caps
            // the height, portrait caps the width, so a 1080×1920 phone video
            // keeps its full resolution. The inner single quotes are ffmpeg
            // filter-level quoting for the expressions (validated against a
            // real run). Both sides come out even, as 4:2:0 — x264, zscale —
            // requires: -2 for the scaled side, and the capped side rounded
            // down, for a phone video narrower than the cap is often odd
            // (465×892), which the stream copy used to carry untouched.
            args.AddRange([
                "-filter:v:0", string.Create(inv,
                    $"scale=w='if(gt(a,1),-2,min({o.VideoHlsHighMaxHeight},trunc(iw/2)*2))':h='if(gt(a,1),min({o.VideoHlsHighMaxHeight},trunc(ih/2)*2),-2)'{toSdr}"),
                "-c:v:0", "libx264", "-preset:v:0", "veryfast", "-pix_fmt:v:0", "yuv420p",
                "-crf:v:0", o.VideoHlsHighCrf.ToString(inv),
                "-maxrate:v:0", string.Create(inv, $"{o.VideoHlsHighMaxRateKbps}k"),
                "-bufsize:v:0", string.Create(inv, $"{o.VideoHlsHighMaxRateKbps * 2}k"),
                "-force_key_frames:v:0", keyframeExpr,
            ]);
        }
        if (r.HasAudio)
        {
            if (r.CopyAudio)
            {
                args.AddRange(["-c:a:0", "copy"]);
            }
            else
            {
                args.AddRange([
                    "-c:a:0", "aac",
                    "-b:a:0", string.Create(inv, $"{o.VideoHlsAudioBitrateKbps}k"),
                    "-ac:a:0", "2",
                ]);
            }
        }

        // ---- low (v:1), always encoded ----
        if (r.IncludeLowRendition)
        {
            args.AddRange([
                "-filter:v:1", string.Create(inv,
                    $"scale=w='if(gt(a,1),-2,{o.VideoHlsLowHeight})':h='if(gt(a,1),{o.VideoHlsLowHeight},-2)'{toSdr}"),
                "-c:v:1", "libx264", "-preset:v:1", "veryfast", "-pix_fmt:v:1", "yuv420p",
                "-crf:v:1", o.VideoHlsLowCrf.ToString(inv),
                "-maxrate:v:1", string.Create(inv, $"{o.VideoHlsLowMaxRateKbps}k"),
                "-bufsize:v:1", string.Create(inv, $"{o.VideoHlsLowMaxRateKbps * 2}k"),
                "-force_key_frames:v:1", keyframeExpr,
            ]);
            if (r.HasAudio)
            {
                args.AddRange([
                    "-c:a:1", "aac",
                    "-b:a:1", string.Create(inv, $"{o.VideoHlsAudioBitrateKbps}k"),
                    "-ac:a:1", "2",
                ]);
            }
        }

        // ---- HLS muxer ----
        var streamMap = (r.HasAudio, r.IncludeLowRendition) switch
        {
            (true, true) => "v:0,a:0,name:high v:1,a:1,name:low",
            (true, false) => "v:0,a:0,name:high",
            (false, true) => "v:0,name:high v:1,name:low",
            (false, false) => "v:0,name:high",
        };
        args.AddRange([
            "-f", "hls",
            "-hls_time", o.VideoHlsSegmentSeconds.ToString(inv),
            "-hls_playlist_type", "vod",
            "-hls_segment_type", "fmp4",
            "-hls_flags", "independent_segments",
            "-master_pl_name", "master.m3u8",
            "-var_stream_map", streamMap,
            "-hls_segment_filename", "%v/seg-%d.m4s",
            "-hls_fmp4_init_filename", "init.mp4",
            "%v/stream.m3u8",
        ]);

        return args;
    }

    // The exit code alone is not proof of a complete ladder (ffmpeg can exit 0
    // on some malformed inputs after producing nothing useful). Require the
    // master playlist plus, per rendition, the variant playlist, an init
    // segment and at least one media segment.
    internal static bool IsValidLadder(string outputDirectory, bool includeLowRendition)
    {
        if (!File.Exists(Path.Combine(outputDirectory, "master.m3u8")))
        {
            return false;
        }

        var renditions = includeLowRendition ? new[] { "high", "low" } : ["high"];
        foreach (var name in renditions)
        {
            var dir = Path.Combine(outputDirectory, name);
            if (!File.Exists(Path.Combine(dir, "stream.m3u8"))
                || !Directory.Exists(dir)
                || Directory.EnumerateFiles(dir, "init*.mp4").FirstOrDefault() is null
                || Directory.EnumerateFiles(dir, "seg-*.m4s").FirstOrDefault() is null)
            {
                return false;
            }
        }
        return true;
    }
}
