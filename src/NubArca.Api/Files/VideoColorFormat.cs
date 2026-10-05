using System.Text;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Files;

/// <summary>
/// How a video's pictures are coded, as far as turning them into an ordinary
/// BT.709 SDR picture needs to know: a poster, a preview strip, an H.264
/// rendition, a frame for the AI. Read from the video's own stream by ffprobe
/// when it is converted; every value is ffprobe's name for it, or null.
/// </summary>
public sealed record VideoColorFormat(
    string? PixelFormat, string? Range, string? Matrix, string? Transfer, string? Primaries)
{
    public static readonly VideoColorFormat Unknown = new(null, null, null, null, null);

    // ffprobe's names, which zscale also takes. Only these ever reach a filter
    // graph: a value outside them leaves the picture as it is.
    private static readonly HashSet<string> HdrTransfers = ["arib-std-b67", "smpte2084"];
    private static readonly HashSet<string> SdrTransfers =
        ["bt709", "smpte170m", "bt470bg", "bt470m", "smpte240m", "bt2020-10", "bt2020-12", "iec61966-2-1", "iec61966-2-4"];
    private static readonly HashSet<string> WidePrimaries = ["bt2020", "smpte432", "smpte431"];
    private static readonly HashSet<string> KnownPrimaries =
        ["bt709", "bt2020", "smpte432", "smpte431", "smpte170m", "bt470bg", "bt470m", "smpte240m", "film"];
    private static readonly HashSet<string> KnownMatrices =
        ["bt709", "bt2020nc", "bt2020c", "smpte170m", "bt470bg", "fcc", "smpte240m", "ycgco", "gbr"];

    /// <summary>
    /// HLG — what phones record, the iPhone included: its Dolby Vision 8.4 is an
    /// HLG picture plus metadata — or PQ (HDR10, HDR10+).
    /// </summary>
    public bool IsHdr => Transfer is not null && HdrTransfers.Contains(Transfer);

    /// <summary>SDR in colours wider than BT.709: Display P3 (iPhone, recent Android) or BT.2020.</summary>
    public bool IsWideGamut =>
        Transfer is not null && SdrTransfers.Contains(Transfer)
        && Primaries is not null && WidePrimaries.Contains(Primaries);

    /// <summary>
    /// True only when an H.264 stream is KNOWN to be what every player decodes
    /// as it is — 8-bit 4:2:0, neither HDR nor wide-gamut — and may be copied
    /// into a rendition. Fail-closed: a probe that could not answer, or a pixel
    /// format it did not name, means a conservative re-encode, never a copy of
    /// something unverified.
    /// </summary>
    public bool CanStreamCopy =>
        !IsHdr && !IsWideGamut && PixelFormat is "yuv420p" or "yuvj420p";

    /// <summary>
    /// The filters that turn this picture into BT.709 SDR, or null when it is
    /// that already. The picture's dimensions must be even: zscale refuses
    /// odd ones for 4:2:0.
    ///
    /// HDR is tone-mapped with SDR white at 203 nits, the reference white of
    /// BT.2408: shadows and mid-tones come out exactly as that standard maps
    /// them to SDR, and the highlights above — skies, lamps — roll off
    /// smoothly (mobius) instead of being clipped to white.
    /// </summary>
    public string? ToBt709Filter()
    {
        if (IsHdr)
        {
            return string.Concat(
                Input(Primaries is not null && KnownPrimaries.Contains(Primaries) ? Primaries : "bt2020", "bt2020nc"),
                ":npl=203,format=gbrpf32le,zscale=p=bt709,",
                "tonemap=tonemap=mobius:param=0.5:desat=0,",
                Output);
        }
        if (IsWideGamut)
        {
            // An unsaid matrix is the one the primaries imply: BT.2020's own
            // non-constant-luminance for BT.2020 colours, BT.709 for Display P3
            // (which phones code with the BT.709 matrix).
            var matrix = Primaries == "bt2020" ? "bt2020nc" : "bt709";
            return string.Concat(Input(Primaries!, matrix), ",format=gbrpf32le,zscale=p=bt709,", Output);
        }
        return null;
    }

    /// <summary>
    /// <paramref name="then"/> preceded by the conversion to BT.709 SDR, run on
    /// the full picture once its dimensions are made even; just
    /// <paramref name="then"/> when no conversion is needed. For single frames —
    /// a poster, a strip, the AI's samples — where converting at full size costs
    /// a fraction of a second.
    /// </summary>
    public string ToBt709Then(string then) => ToBt709Filter() is string filter
        ? $"scale=trunc(iw/2)*2:trunc(ih/2)*2,{filter},{then}"
        : then;

    private const string Output = "zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    // To linear light in the input's own primaries. Every property is stated,
    // the output's primaries too: zscale otherwise takes what the frame says,
    // and a stream that leaves them unsaid would fail the whole run.
    private string Input(string primaries, string defaultMatrix)
    {
        var matrix = Matrix is not null && KnownMatrices.Contains(Matrix) ? Matrix : defaultMatrix;
        var range = Range == "pc" ? "pc" : "tv";
        return $"zscale=tin={Transfer}:pin={primaries}:min={matrix}:rin={range}:t=linear:p={primaries}";
    }

    /// <summary>Reads ffprobe's <c>default=noprint_wrappers=1</c> output (<c>key=value</c> lines).</summary>
    internal static VideoColorFormat Parse(string output)
    {
        string? pixelFormat = null, range = null, matrix = null, transfer = null, primaries = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            string? value = line[(equals + 1)..];
            if (value is "" or "unknown" or "unspecified" or "reserved") value = null;
            switch (line[..equals])
            {
                case "pix_fmt": pixelFormat ??= value; break;
                case "color_range": range ??= value; break;
                case "color_space": matrix ??= value; break;
                case "color_transfer": transfer ??= value; break;
                case "color_primaries": primaries ??= value; break;
            }
        }
        return new VideoColorFormat(pixelFormat, range, matrix, transfer, primaries);
    }
}

/// <summary>Reads a video file's <see cref="VideoColorFormat"/>.</summary>
public interface IVideoColorProbe
{
    /// <summary>The format of the first video stream; <see cref="VideoColorFormat.Unknown"/> when it cannot be read. Never throws but for cancellation.</summary>
    Task<VideoColorFormat> ProbeAsync(string inputPath, CancellationToken cancellationToken);
}

public sealed class FfprobeVideoColorProbe : IVideoColorProbe
{
    private readonly IOptions<MediaOptions> _options;
    private readonly IProcessRunner _runner;
    private readonly ILogger<FfprobeVideoColorProbe> _logger;

    public FfprobeVideoColorProbe(
        IOptions<MediaOptions> options, IProcessRunner runner, ILogger<FfprobeVideoColorProbe> logger)
    {
        _options = options;
        _runner = runner;
        _logger = logger;
    }

    public async Task<VideoColorFormat> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        try
        {
            var result = await _runner.RunAsync(new ProcessRunRequest(
                options.FfprobePath,
                [
                    "-v", "error", "-select_streams", "v:0",
                    "-show_entries", "stream=pix_fmt,color_range,color_space,color_transfer,color_primaries",
                    "-of", "default=noprint_wrappers=1", inputPath,
                ],
                options.VideoProbeTimeoutSeconds,
                options.VideoProbeMaxOutputBytes), cancellationToken);
            if (result.ExitCode != 0 || result.TimedOut)
            {
                // Never the path: only what happened.
                _logger.LogWarning(
                    "Video colour probe failed (exit {ExitCode}, timed out {TimedOut}).", result.ExitCode, result.TimedOut);
                return VideoColorFormat.Unknown;
            }
            return VideoColorFormat.Parse(Encoding.UTF8.GetString(result.StdoutBytes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Video colour probe could not run ({ExceptionType}).", ex.GetType().Name);
            return VideoColorFormat.Unknown;
        }
    }
}
