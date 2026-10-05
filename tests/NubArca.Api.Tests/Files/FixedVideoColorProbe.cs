using NubArca.Api.Files;

namespace NubArca.Api.Tests.Files;

/// <summary>A colour probe that answers one format for every file, and counts the questions.</summary>
public sealed class FixedVideoColorProbe(VideoColorFormat format) : IVideoColorProbe
{
    public static FixedVideoColorProbe Sdr() => new(VideoColorFormat.Unknown);

    public static FixedVideoColorProbe IphoneHdr() =>
        new(new VideoColorFormat("yuv420p10le", "tv", "bt2020nc", "arib-std-b67", "bt2020"));

    public int Calls { get; private set; }

    public Task<VideoColorFormat> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(format);
    }
}
