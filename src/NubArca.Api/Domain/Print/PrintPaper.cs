namespace NubArca.Api.Domain.Print;

/// <summary>
/// The papers a party printer can have loaded — ONE at a time, the one the
/// operator says is in it (<see cref="PrinterDevice.LoadedPaperSize"/>): a dye
/// sublimation printer takes one roll, and neither the DNP driver on Windows
/// nor Gutenprint on CUPS reports which in a form worth trusting.
///
/// Named as the photo trade names them. The physical media are DNP's 4x6, 5x7
/// and 6x8 inch sets, so every pixel is derived from those inches at
/// <see cref="Dpi"/>: 10x15 is the 1200x1800 sheet the studio has always
/// printed. The name's order is the paper's own orientation — 10x15 and 13x18
/// stand, 20x15 lies — and one paper is one paper however it is turned.
///
/// The id doubles as the job format the Print Agent is sent for a sheet of
/// that paper (see <see cref="PrintFormats"/>).
/// </summary>
public static class PrintPapers
{
    public const string Photo10x15 = "10x15";
    public const string Photo13x18 = "13x18";
    public const string Photo20x15 = "20x15";

    public const int Dpi = 300;

    public static readonly IReadOnlyList<string> All = [Photo10x15, Photo13x18, Photo20x15];

    public static bool IsKnown(string? value) => value is Photo10x15 or Photo13x18 or Photo20x15;

    /// <summary>Short and long edge of the physical sheet, in inches.</summary>
    public static (int Short, int Long) Inches(string paper) => paper switch
    {
        Photo13x18 => (5, 7),
        Photo20x15 => (6, 8),
        _ => (4, 6),
    };

    /// <summary>True for a paper whose name reads lying down: 20x15.</summary>
    public static bool NamedLandscape(string paper) => paper == Photo20x15;

    /// <summary>The sheet in pixels, standing or lying.</summary>
    public static (int Width, int Height) Pixels(string paper, bool portrait)
    {
        var (shortEdge, longEdge) = Inches(paper);
        return portrait
            ? (shortEdge * Dpi, longEdge * Dpi)
            : (longEdge * Dpi, shortEdge * Dpi);
    }
}
