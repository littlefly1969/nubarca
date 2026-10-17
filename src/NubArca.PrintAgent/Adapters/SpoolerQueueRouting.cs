namespace NubArca.PrintAgent.Adapters;

/// <summary>
/// Which queue prints which format: the adapters' decisions that do not need a
/// spooler, so they are tested on every platform.
///
/// A format is a sheet: one of three papers (<c>10x15</c>, <c>13x18</c>,
/// <c>20x15</c> — DNP's 4x6, 5x7 and 6x8 inch media), printed on the photo
/// queue, or the 10x15 cut in two. Which paper is LOADED is the operator's to
/// say on the server; the agent only reports which papers the queue can print.
///
/// A <c>2x6x2</c> sheet is the same 10x15 paper as a photograph. What makes it
/// two strips is a SECOND queue on the same physical printer whose driver
/// defaults have the DNP "2inch cut" enabled, so the printer cuts the sheet
/// down the middle. That queue belongs to the one configured printer: no other
/// printer is ever advertised as able to cut, and no other printer's job is
/// ever sent to it.
///
/// A 20x15 is cut the DNP's own way: two 10x15 sheets in ONE job, which the
/// driver puts side by side on the 6x8 and the printer cuts apart. A
/// <c>4x6x2</c> is two 10x15 photographs on the photo queue; a <c>2x6x4</c> is
/// two twin-strip sheets on the strip queue, cut into four strips. Only CUPS
/// (Gutenprint's DNP backend) joins them; the Windows spooler never reports them.
/// </summary>
public static class SpoolerQueueRouting
{
    public const string Photo10x15 = "10x15";
    public const string Photo13x18 = "13x18";
    public const string Photo20x15 = "20x15";
    public const string Strip2x6Pair = "2x6x2";
    public const string Photo4x6Pair = "4x6x2";
    public const string Strip2x6Quad = "2x6x4";

    /// <summary>Every paper, in the order they are reported.</summary>
    public static readonly IReadOnlyList<string> Papers = [Photo10x15, Photo13x18, Photo20x15];

    /// <summary>A 20x15 sent as two 10x15 the printer joins and cuts — two photographs, four strips.</summary>
    public static readonly IReadOnlyList<string> LargeCuts = [Photo4x6Pair, Strip2x6Quad];

    /// <summary>A paper's short and long edge in hundredths of an inch, as the Windows spooler measures it.</summary>
    public static (int Short, int Long) Hundredths(string paper) => paper switch
    {
        Photo13x18 => (500, 700),
        Photo20x15 => (600, 800),
        _ => (400, 600),
    };

    /// <summary>Whether a spooler paper of this size, either way up, is <paramref name="paper"/>.</summary>
    public static bool IsPaper(string paper, int width, int height)
    {
        var (shortEdge, longEdge) = Hundredths(paper);
        return Math.Abs(Math.Min(width, height) - shortEdge) <= 20
            && Math.Abs(Math.Max(width, height) - longEdge) <= 25;
    }

    /// <summary>The formats <paramref name="deviceKey"/> reports to the server: 10x15 only.</summary>
    public static IReadOnlyList<string> Formats(string deviceKey, bool supportsPhoto10x15,
        string? configuredPrinter, string? stripPrinter, bool stripQueueSupportsPhoto10x15) =>
        Formats(deviceKey, supportsPhoto10x15 ? [Photo10x15] : [], configuredPrinter, stripPrinter,
            stripQueueSupportsPhoto10x15);

    /// <summary>
    /// The formats <paramref name="deviceKey"/> reports: the papers its queue
    /// prints, the cut 10x15 when it owns a ready strip queue, and — while it
    /// prints 20x15 — each joined 20x15 its driver makes (in
    /// <paramref name="papers"/>): two photographs, and four strips when the
    /// twin strip is ready too.
    /// </summary>
    public static IReadOnlyList<string> Formats(string deviceKey, IReadOnlyCollection<string> papers,
        string? configuredPrinter, string? stripPrinter, bool stripQueueSupportsPhoto10x15)
    {
        var formats = Papers.Where(papers.Contains).ToList();
        if (formats.Contains(Photo10x15)
            && OwnsStripQueue(deviceKey, configuredPrinter, stripPrinter) && stripQueueSupportsPhoto10x15)
        {
            formats.Add(Strip2x6Pair);
        }
        if (formats.Contains(Photo20x15))
        {
            if (papers.Contains(Photo4x6Pair)) formats.Add(Photo4x6Pair);
            if (papers.Contains(Strip2x6Quad) && formats.Contains(Strip2x6Pair)) formats.Add(Strip2x6Quad);
        }
        return formats;
    }

    /// <summary>The queue a job of <paramref name="format"/> goes to, or null when this printer cannot print it.</summary>
    public static string? TargetQueue(string format, string deviceKey,
        string? configuredPrinter, string? stripPrinter) => format.ToLowerInvariant() switch
    {
        Photo10x15 or Photo13x18 or Photo20x15 or Photo4x6Pair => deviceKey,
        Strip2x6Pair or Strip2x6Quad when OwnsStripQueue(deviceKey, configuredPrinter, stripPrinter) => stripPrinter,
        _ => null,
    };

    private static bool OwnsStripQueue(string deviceKey, string? configuredPrinter, string? stripPrinter) =>
        !string.IsNullOrWhiteSpace(stripPrinter)
        && !string.IsNullOrWhiteSpace(configuredPrinter)
        && string.Equals(deviceKey, configuredPrinter, StringComparison.OrdinalIgnoreCase);
}
