namespace NubArca.PrintAgent.Adapters;

/// <summary>
/// Which Windows queue prints which format: the spooler adapter's decisions
/// that do not need a spooler, so they are tested on every platform.
///
/// A <c>2x6x2</c> sheet is the same 10x15 paper as a photograph. What makes it
/// two strips is a SECOND queue on the same physical printer whose driver
/// defaults have the DNP "2inch cut" enabled, so the printer cuts the sheet
/// down the middle. That queue belongs to the one configured printer: no other
/// printer is ever advertised as able to cut, and no other printer's job is
/// ever sent to it.
/// </summary>
public static class SpoolerQueueRouting
{
    public const string Photo10x15 = "10x15";
    public const string Strip2x6Pair = "2x6x2";

    /// <summary>The formats <paramref name="deviceKey"/> reports to the server.</summary>
    public static IReadOnlyList<string> Formats(string deviceKey, bool supportsPhoto10x15,
        string? configuredPrinter, string? stripPrinter, bool stripQueueSupportsPhoto10x15)
    {
        if (!supportsPhoto10x15) return [];
        return OwnsStripQueue(deviceKey, configuredPrinter, stripPrinter) && stripQueueSupportsPhoto10x15
            ? [Photo10x15, Strip2x6Pair]
            : [Photo10x15];
    }

    /// <summary>The queue a job of <paramref name="format"/> goes to, or null when this printer cannot print it.</summary>
    public static string? TargetQueue(string format, string deviceKey,
        string? configuredPrinter, string? stripPrinter) => format.ToLowerInvariant() switch
    {
        Photo10x15 => deviceKey,
        Strip2x6Pair when OwnsStripQueue(deviceKey, configuredPrinter, stripPrinter) => stripPrinter,
        _ => null,
    };

    private static bool OwnsStripQueue(string deviceKey, string? configuredPrinter, string? stripPrinter) =>
        !string.IsNullOrWhiteSpace(stripPrinter)
        && !string.IsNullOrWhiteSpace(configuredPrinter)
        && string.Equals(deviceKey, configuredPrinter, StringComparison.OrdinalIgnoreCase);
}
