using NubArca.Api.Files;

namespace NubArca.Api.Tests.Files;

/// <summary>A colour probe that answers one format for every file, and counts the questions.</summary>
public sealed class FixedVideoColorProbe(VideoColorFormat format) : IVideoColorProbe
{
    /// <summary>What a phone's ordinary video is: 8-bit 4:2:0 BT.709 SDR.</summary>
    public static readonly VideoColorFormat PlainSdr = new("yuv420p", "tv", "bt709", "bt709", "bt709");

    public static FixedVideoColorProbe Sdr() => new(PlainSdr);

    public static FixedVideoColorProbe IphoneHdr() =>
        new(new VideoColorFormat("yuv420p10le", "tv", "bt2020nc", "arib-std-b67", "bt2020"));

    public int Calls { get; private set; }

    public Task<VideoColorFormat> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(format);
    }
}
