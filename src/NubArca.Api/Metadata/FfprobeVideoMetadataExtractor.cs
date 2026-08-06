using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Domain;
using NubArca.Api.Files;

namespace NubArca.Api.Metadata;

// Probes a video blob by invoking an external ffprobe process and mapping its
// JSON report onto typed fields. Used only when Media:VideoMetadataProvider =
// "ffprobe".
//
// Security: the blob is written to a GUID-named temp file (its name has no
// relation to the storage key). ffprobe stderr is discarded (never logged) and
// the raw JSON is never persisted or logged — only the sanitized outcome (exit
// code, timed-out flag, or a parse result) appears in logs. Never throws: every
// failure path resolves to a safe status + sanitized error code.
public sealed class FfprobeVideoMetadataExtractor : IVideoMetadataExtractor
{
    // Bump when probe arguments or the JSON→field mapping change so a future
    // backfill re-probes older rows.
    public const int Version = 1;

    private readonly IOptions<MediaOptions> _options;
    private readonly IProcessRunner _runner;
    private readonly ILogger<FfprobeVideoMetadataExtractor> _logger;

    public FfprobeVideoMetadataExtractor(
        IOptions<MediaOptions> options,
        IProcessRunner runner,
        ILogger<FfprobeVideoMetadataExtractor> logger)
    {
        _options = options;
        _runner = runner;
        _logger = logger;
    }

    public async Task<VideoMetadataExtractionResult> ExtractAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        var tempFile = Path.Combine(Path.GetTempPath(), $"nc-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var src = await openBlobContent(cancellationToken))
            await using (var dst = new FileStream(tempFile, FileMode.Create, FileAccess.Write,
                             FileShare.None, 65536, useAsync: true))
            {
                await src.CopyToAsync(dst, cancellationToken);
            }

            // -v error: quiet; -print_format json: machine-readable; -show_format
            // + -show_streams: containers + per-stream detail. Args are a list —
            // no shell interpolation. Output goes to stdout.
            var args = new[]
            {
                "-v", "error",
                "-print_format", "json",
                "-show_format",
                "-show_streams",
                tempFile,
            };

            var result = await _runner.RunAsync(
                new ProcessRunRequest(
                    opts.FfprobePath,
                    args,
                    opts.VideoProbeTimeoutSeconds,
                    opts.VideoProbeMaxOutputBytes),
                cancellationToken);

            if (result.TimedOut)
            {
                _logger.LogWarning(
                    "ffprobe timed out after {TimeoutSeconds}s; recording a transient failure.",
                    opts.VideoProbeTimeoutSeconds);
                return VideoMetadataExtractionResult.ForStatus(
                    MetadataStatuses.Failed, MetadataErrorCodes.Timeout, Version);
            }

            if (result.ExitCode != 0)
            {
                _logger.LogWarning(
                    "ffprobe exited with code {ExitCode}; treating input as not probe-able.",
                    result.ExitCode);
                return VideoMetadataExtractionResult.ForStatus(
                    MetadataStatuses.Failed, MetadataErrorCodes.ProbeFailed, Version);
            }

            return Parse(result.StdoutBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "ffprobe extraction raised an exception ({ExceptionType}).", ex.GetType().Name);
            return VideoMetadataExtractionResult.ForStatus(
                MetadataStatuses.Failed, MetadataErrorCodes.IoError, Version);
        }
        finally
        {
            TryDeleteTempFile(tempFile);
        }
    }

    // Maps ffprobe JSON to typed fields. Tolerant of missing/extra fields and
    // of the string-typed numerics ffprobe emits. Never throws — malformed JSON
    // yields a Failed result.
    internal static VideoMetadataExtractionResult Parse(byte[] jsonBytes)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonBytes);
        }
        catch (JsonException)
        {
            return VideoMetadataExtractionResult.ForStatus(
                MetadataStatuses.Failed, MetadataErrorCodes.ProbeFailed, Version);
        }

        using (doc)
        {
            var root = doc.RootElement;
            JsonElement? video = null;
            JsonElement? audio = null;

            if (root.TryGetProperty("streams", out var streams)
                && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = GetString(s, "codec_type");
                    if (video is null && type == "video" && !IsAttachedPicture(s))
                    {
                        video = s;
                    }
                    else if (audio is null && type == "audio")
                    {
                        audio = s;
                    }
                }
            }

            root.TryGetProperty("format", out var format);
            var hasFormat = format.ValueKind == JsonValueKind.Object;

            // A blob that ffprobe read but that has no video stream is not a
            // video we probe for (e.g. an audio-only or image container).
            if (video is null)
            {
                return VideoMetadataExtractionResult.ForStatus(
                    MetadataStatuses.Skipped, MetadataErrorCodes.UnsupportedFormat, Version);
            }

            var v = video.Value;
            var (width, height) = ReadDimensions(v);

            double? duration = hasFormat ? ParseDouble(GetString(format, "duration")) : null;
            duration ??= ParseDouble(GetString(v, "duration"));

            long? videoBitrate = ParseLong(GetString(v, "bit_rate"))
                ?? (hasFormat ? ParseLong(GetString(format, "bit_rate")) : null);

            DateTime? creationTime = ReadCreationTime(v)
                ?? (hasFormat ? ReadCreationTime(format) : null);

            var result = new VideoMetadataExtractionResult
            {
                Status = MetadataStatuses.Completed,
                ErrorCode = null,
                Version = Version,
                Width = width,
                Height = height,
                DurationSeconds = duration is > 0 ? duration : null,
                VideoCodec = Trim(GetString(v, "codec_name")),
                FrameRate = ParseRational(GetString(v, "avg_frame_rate")),
                VideoBitrate = videoBitrate is > 0 ? videoBitrate : null,
                Rotation = ReadRotation(v),
                CreationTime = creationTime,
                HasAudio = audio is not null,
                AudioCodec = audio is { } a ? Trim(GetString(a, "codec_name")) : null,
                AudioChannels = audio is { } a2 ? ParseInt(a2, "channels") : null,
                AudioSampleRate = audio is { } a3 ? ParseIntString(GetString(a3, "sample_rate")) : null,
            };
            return result;
        }
    }

    // Cover art / thumbnails embedded in audio files surface as a video stream
    // with the attached_pic disposition — not a real video track.
    private static bool IsAttachedPicture(JsonElement stream)
        => stream.TryGetProperty("disposition", out var d)
            && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty("attached_pic", out var ap)
            && ap.ValueKind == JsonValueKind.Number
            && ap.TryGetInt32(out var n) && n == 1;

    private static (int? Width, int? Height) ReadDimensions(JsonElement v)
    {
        int? w = ParseInt(v, "width");
        int? h = ParseInt(v, "height");
        return (w is > 0 ? w : null, h is > 0 ? h : null);
    }

    // Rotation: prefer the side_data_list "rotation" (can be negative, e.g.
    // -90), fall back to the legacy tags.rotate. Normalized to [0,360).
    private static int? ReadRotation(JsonElement v)
    {
        if (v.TryGetProperty("side_data_list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var sd in list.EnumerateArray())
            {
                if (sd.TryGetProperty("rotation", out var rot))
                {
                    int? val = rot.ValueKind == JsonValueKind.Number && rot.TryGetInt32(out var ri)
                        ? ri
                        : ParseIntString(rot.ValueKind == JsonValueKind.String ? rot.GetString() : null);
                    if (val is int r)
                    {
                        return Normalize(r);
                    }
                }
            }
        }

        if (v.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
        {
            var rotate = ParseIntString(GetString(tags, "rotate"));
            if (rotate is int r)
            {
                return Normalize(r);
            }
        }
        return null;

        static int Normalize(int deg) => ((deg % 360) + 360) % 360;
    }

    private static DateTime? ReadCreationTime(JsonElement el)
    {
        if (!el.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var raw = GetString(tags, "creation_time");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
        {
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }
        return null;
    }

    private static string? GetString(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static int? ParseInt(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String) return ParseIntString(v.GetString());
        }
        return null;
    }

    private static int? ParseIntString(string? s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static long? ParseLong(string? s)
        => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static double? ParseDouble(string? s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    // ffprobe reports frame rates as a rational string "num/den" (e.g.
    // "30000/1001"). Returns null for "0/0" (no defined rate).
    private static double? ParseRational(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var slash = s.IndexOf('/');
        if (slash < 0)
        {
            return ParseDouble(s);
        }
        var num = ParseDouble(s[..slash]);
        var den = ParseDouble(s[(slash + 1)..]);
        if (num is double n && den is double d && d != 0)
        {
            return n / d;
        }
        return null;
    }

    private void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete ffprobe temp file (type: {Type}).", ex.GetType().Name);
        }
    }
}
