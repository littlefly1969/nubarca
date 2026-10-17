using NubArca.Api.Domain.Print;

namespace NubArca.Api.Print;

/// <summary>
/// THE CATALOGUE OF PRINT FORMATS: one, for every surface that prints.
///
/// A FORMAT (layout) is where photographs land on a sheet — one photograph,
/// four two by two, the twin strip. A STYLE is how much of the sheet a single
/// photograph takes: <see cref="Framed"/>, with a border and a band at its foot
/// (the party's look, and an owner's "Cornice"), or <see cref="FullBleed"/>, to
/// the edges. What is PRINTED in a band — a party's name and number, an owner's
/// line and date — is the caller's; where the band is, is this catalogue's.
///
/// The browser lays out the preview and the server draws the sheet, and the
/// two must agree or somebody composes one thing and collects another. So this
/// file is mirrored by <c>packages/contracts/src/printLayouts.ts</c>, and
/// <c>printLayouts.cases.json</c> — produced by an independent implementation —
/// holds both to the same numbers for every format, style, paper and way up.
///
/// Everything is a fraction of the sheet, and margins, gutters and bands are
/// fractions of its SHORT edge, so a composition keeps its proportions on every
/// paper and the band under a photograph is the same strip of paper whichever
/// way the sheet stands. The sheet is the loaded paper at 300dpi (PrintPapers).
/// </summary>
public static class PrintLayouts
{
    // --- Formats and styles ---------------------------------------------------

    /// <summary>One photograph on the sheet.</summary>
    public const string Photo = "photo";
    /// <summary>Four photographs on one sheet, two by two.</summary>
    public const string Grid4 = "grid4";
    /// <summary>Two strips of four on one 10x15, which the printer cuts apart.</summary>
    public const string TwinStrip4 = "twinStrip4";
    /// <summary>Two photographs on one 20x15, which the printer cuts into two 10x15.</summary>
    public const string Photo2 = "photo2";
    /// <summary>Four strips of four on one 20x15, which the printer cuts apart.</summary>
    public const string QuadStrip4 = "quadStrip4";

    public static readonly IReadOnlyList<string> All = [Photo, Grid4, TwinStrip4, Photo2, QuadStrip4];

    /// <summary>A border, and a band at the foot for words.</summary>
    public const string Framed = "framed";
    /// <summary>The photograph to the edges of the sheet. A single photograph only.</summary>
    public const string FullBleed = "fullBleed";
    /// <summary>
    /// "Esce dalla foto": a framed photograph with deep paper above it, where
    /// the subject — lifted off its background — breaks out over the frame's
    /// top edge. A single photograph only, and the owner's own.
    /// </summary>
    public const string PopOut = "popOut";

    public static bool IsKnown(string layout) => layout is Photo or Grid4 or TwinStrip4 or Photo2 or QuadStrip4;

    /// <summary>
    /// Which format each paper can make. A photo and four photos on any known
    /// paper; the twin strip only on 10x15, the sheet the printer cuts in two;
    /// two photographs and four strips only on 20x15, which it cuts into two
    /// 10x15 or four 2x6.
    /// </summary>
    public static bool Allowed(string paper, string layout) => layout switch
    {
        Photo or Grid4 => PrintPapers.IsKnown(paper),
        TwinStrip4 => paper == PrintPapers.Photo10x15,
        Photo2 or QuadStrip4 => paper == PrintPapers.Photo20x15,
        _ => false,
    };

    /// <summary>
    /// The job format the Print Agent is sent for a sheet: the paper's own
    /// name, or the cut that makes it more than one print.
    /// </summary>
    public static string JobFormat(string paper, string layout) => layout switch
    {
        TwinStrip4 => PrintFormats.Strip2x6Pair,
        Photo2 => PrintFormats.Photo4x6Pair,
        QuadStrip4 => PrintFormats.Strip2x6Quad,
        _ => paper,
    };

    /// <summary>
    /// How many DIFFERENT photographs a format takes. The twin strip takes four
    /// — the same strip twice, one to keep and one to give — or eight, two
    /// different strips. The four strips take four, the same strip four
    /// times, or sixteen, four different strips.
    /// </summary>
    public static IReadOnlyList<int> PhotoCounts(string layout) => layout switch
    {
        Grid4 => [4],
        TwinStrip4 => [SlotsPerStrip, SlotsPerStrip * StripsPerSheet],
        Photo2 => [HalvesPerSheet],
        QuadStrip4 => [SlotsPerStrip, SlotsPerStrip * StripsPerSheet * HalvesPerSheet],
        Photo => [1],
        _ => [],
    };

    /// <summary>The most photographs a format takes.</summary>
    public static int MaxPhotos(string layout) => PhotoCounts(layout).DefaultIfEmpty(0).Max();

    /// <summary>The most photographs any format takes: four different strips.</summary>
    public static int MostPhotos { get; } = All.Max(MaxPhotos);

    /// <summary>A style is a single photograph's choice; four and the strips are always framed.</summary>
    public static bool SupportsStyle(string layout, string style) =>
        style == Framed ? IsKnown(layout) : style is (FullBleed or PopOut) && layout == Photo;

    /// <summary>
    /// Which way the sheet stands: a single photograph's as it is placed; four
    /// as the paper is named; the twin strip always standing. A halved 20x15
    /// lies when its halves stand — two standing photographs, or the four
    /// strips — and stands when they lie, two landscape photographs one above
    /// the other.
    /// </summary>
    public static bool SheetPortrait(string layout, string paper, bool photoPortrait) => layout switch
    {
        Grid4 => GridPortrait(paper),
        TwinStrip4 => true,
        Photo2 => !photoPortrait,
        QuadStrip4 => false,
        _ => photoPortrait,
    };

    /// <summary>
    /// Every rectangle of one sheet: the slots photographs land in — in the
    /// order they were arranged, the twin strip's first strip then its second —
    /// and the bands for words at the foot.
    /// </summary>
    public static PrintSheetLayout Arrange(string layout, string style, string paper, bool portrait)
    {
        var sheetPortrait = SheetPortrait(layout, paper, portrait);
        var (w, h) = Sheet(paper, sheetPortrait);
        return layout switch
        {
            Photo2 or QuadStrip4 => Halves(layout, w, h, portrait),
            Grid4 => new(w, h, [.. Enumerable.Range(0, 4).Select(i => GridSlot(paper, i))], [GridFooter(paper)]),
            TwinStrip4 => new(w, h,
                [.. Enumerable.Range(0, StripsPerSheet).SelectMany(strip =>
                    Enumerable.Range(0, SlotsPerStrip).Select(slot => StripSlot(strip, slot)))],
                [.. Enumerable.Range(0, StripsPerSheet).Select(StripFooter)]),
            _ when style == FullBleed => new(w, h, [(0, 0, 1, 1)], []),
            _ when style == PopOut => new(w, h, [PopOutSlot(paper, sheetPortrait)], [PopOutFooter(paper, sheetPortrait)]),
            _ => new(w, h, [PhotoSlot(paper, sheetPortrait)], [PhotoFooter(paper, sheetPortrait)]),
        };
    }

    /// <summary>The shape every slot of a sheet is locked to, width over height.</summary>
    public static double SlotAspect(string layout, string style, string paper, bool portrait)
    {
        var sheet = Arrange(layout, style, paper, portrait);
        var (_, _, width, height) = sheet.Slots[0];
        return width * sheet.Width / (height * sheet.Height);
    }

    // --- Sheets ---------------------------------------------------------------

    /// <summary>10x15cm at 300dpi, portrait. The twin strip's sheet.</summary>
    public const int PortraitWidth = 1200;
    public const int PortraitHeight = 1800;

    /// <summary>The same sheet turned, for a single landscape photo.</summary>
    public const int LandscapeWidth = 1800;
    public const int LandscapeHeight = 1200;

    /// <summary>A sheet of <paramref name="paper"/>, standing or lying, in pixels.</summary>
    public static (int Width, int Height) Sheet(string paper, bool portrait) =>
        PrintPapers.Pixels(PrintPapers.IsKnown(paper) ? paper : PrintPapers.Photo10x15, portrait);

    // --- One photograph, framed -------------------------------------------------

    /// <summary>Border around the photograph, as a fraction of the short edge.</summary>
    public const double PhotoMarginFraction = 0.055;

    /// <summary>
    /// Room under the photograph for words.
    ///
    /// A fraction of the SHORT EDGE, like the margin beside it — never of the
    /// height. The height is what flips when the sheet follows a landscape
    /// photograph, and a band measured against it came out a third shorter on
    /// exactly the sheets that are widest: 11.7mm instead of 17.5mm, with the
    /// type shrinking with the band. The short edge is 10cm on both, so this is
    /// the same strip of paper whichever way the picture faces.
    /// </summary>
    public const double PhotoFooterFraction = 0.17;

    /// <summary>Where the photograph sits on a framed single-photo sheet, in sheet fractions.</summary>
    public static (double X, double Y, double Width, double Height) PhotoSlot(string paper, bool portrait)
    {
        var (w, h) = Sheet(paper, portrait);
        var margin = PhotoMarginFraction * Math.Min(w, h);
        var footer = PhotoFooterFraction * Math.Min(w, h);
        return (margin / w, margin / h, (w - (2 * margin)) / w, (h - (2 * margin) - footer) / h);
    }

    /// <summary>The band under it for words.</summary>
    public static (double X, double Y, double Width, double Height) PhotoFooter(string paper, bool portrait)
    {
        var (w, h) = Sheet(paper, portrait);
        var (x, y, width, height) = PhotoSlot(paper, portrait);
        return (x, y + height, width, PhotoFooterFraction * Math.Min(w, h) / h);
    }

    // --- One photograph that its subject breaks out of ---------------------------

    /// <summary>Paper above the photograph for the subject to break out into, short-edge fraction.</summary>
    public const double PopOutTopFraction = 0.22;

    /// <summary>Border beside the photograph, short-edge fraction: a little wider than a framed one's.</summary>
    public const double PopOutSideFraction = 0.08;

    /// <summary>Paper under the band, short-edge fraction: the framed border.</summary>
    public const double PopOutBottomFraction = PhotoMarginFraction;

    /// <summary>
    /// How close to the sheet's edge the subject may reach, short-edge
    /// fraction: printers do not print to the very edge reliably.
    /// </summary>
    public const double PopOutEdgeFraction = 0.02;

    /// <summary>Where the photograph sits on a pop-out sheet, in sheet fractions.</summary>
    public static (double X, double Y, double Width, double Height) PopOutSlot(string paper, bool portrait)
    {
        var (w, h) = Sheet(paper, portrait);
        var shortEdge = Math.Min(w, h);
        var side = PopOutSideFraction * shortEdge;
        var top = PopOutTopFraction * shortEdge;
        var bottom = PopOutBottomFraction * shortEdge;
        var footer = PhotoFooterFraction * shortEdge;
        return (side / w, top / h, (w - (2 * side)) / w, (h - top - footer - bottom) / h);
    }

    /// <summary>The band under it for words: as tall as a framed sheet's.</summary>
    public static (double X, double Y, double Width, double Height) PopOutFooter(string paper, bool portrait)
    {
        var (w, h) = Sheet(paper, portrait);
        var (x, y, width, height) = PopOutSlot(paper, portrait);
        return (x, y + height, width, PhotoFooterFraction * Math.Min(w, h) / h);
    }

    /// <summary>
    /// Where the subject may break out, in sheet fractions: the paper above the
    /// photograph, edge to edge but for the printer's safe edge. Never beside
    /// or below the photograph — a subject reads as stepping OUT of a frame
    /// only over its top.
    /// </summary>
    public static (double X, double Y, double Width, double Height) PopOutArea(string paper, bool portrait)
    {
        var (w, h) = Sheet(paper, portrait);
        var edge = PopOutEdgeFraction * Math.Min(w, h);
        var (_, slotY, _, _) = PopOutSlot(paper, portrait);
        return (edge / w, edge / h, (w - (2 * edge)) / w, slotY - (edge / h));
    }

    // --- A 20x15 cut in two -------------------------------------------------------

    /// <summary>
    /// The halves a 20x15 is cut into by the printer: each exactly a 10x15
    /// sheet (6x8 inches is two 4x6), so each half is laid out as that sheet
    /// would be.
    /// </summary>
    public const int HalvesPerSheet = 2;

    /// <summary>What each half of a halved 20x15 is: one framed 10x15 photograph, or the twin strip.</summary>
    public static string HalfLayout(string layout) => layout == QuadStrip4 ? TwinStrip4 : Photo;

    /// <summary>
    /// Both halves, each the 10x15 sheet of <see cref="HalfLayout"/> placed in
    /// its half: side by side on a lying sheet, one above the other on a
    /// standing one. The slots are the first half's, then the second's — the
    /// four strips left to right.
    /// </summary>
    private static PrintSheetLayout Halves(string layout, int w, int h, bool photoPortrait)
    {
        var half = Arrange(HalfLayout(layout), Framed, PrintPapers.Photo10x15, photoPortrait);
        var sideBySide = w > h;
        (double X, double Y, double Width, double Height) Place(
            (double X, double Y, double Width, double Height) r, int index) => sideBySide
            ? ((index + r.X) / HalvesPerSheet, r.Y, r.Width / HalvesPerSheet, r.Height)
            : (r.X, (index + r.Y) / HalvesPerSheet, r.Width, r.Height / HalvesPerSheet);
        var halves = Enumerable.Range(0, HalvesPerSheet).ToList();
        return new(w, h,
            [.. halves.SelectMany(i => half.Slots.Select(r => Place(r, i)))],
            [.. halves.SelectMany(i => half.Bands.Select(r => Place(r, i)))]);
    }

    // --- Four photographs, two by two ----------------------------------------

    /// <summary>Border around the four, short-edge fraction: little, the paper is for photographs.</summary>
    public const double GridMarginFraction = 0.035;

    /// <summary>Space between the four, short-edge fraction.</summary>
    public const double GridGutterFraction = 0.02;

    /// <summary>
    /// The band at the foot — slimmer than a single photograph's, because four
    /// photographs want the room. Short-edge fraction, like everything here.
    /// </summary>
    public const double GridFooterFraction = 0.12;

    /// <summary>
    /// The four sit as the paper is named, never turned: standing on 10x15 and
    /// 13x18 (four 5x7.5 and 6.5x9 cm frames), lying on 20x15 (four 10x7.5).
    /// </summary>
    public static bool GridPortrait(string paper) => !PrintPapers.NamedLandscape(paper);

    /// <summary>
    /// One of the four, in sheet fractions: 0 top left, 1 top right, 2 bottom
    /// left, 3 bottom right — the order they were arranged in.
    /// </summary>
    public static (double X, double Y, double Width, double Height) GridSlot(string paper, int index)
    {
        var (w, h) = Sheet(paper, GridPortrait(paper));
        var shortEdge = Math.Min(w, h);
        var margin = GridMarginFraction * shortEdge;
        var gutter = GridGutterFraction * shortEdge;
        var footer = GridFooterFraction * shortEdge;
        var cellW = (w - (2 * margin) - gutter) / 2;
        var cellH = (h - (2 * margin) - gutter - footer) / 2;
        var (column, row) = (index % 2, index / 2);
        return ((margin + (column * (cellW + gutter))) / w, (margin + (row * (cellH + gutter))) / h,
            cellW / w, cellH / h);
    }

    /// <summary>The band at the foot of a four-photo sheet, in sheet fractions.</summary>
    public static (double X, double Y, double Width, double Height) GridFooter(string paper)
    {
        var (w, h) = Sheet(paper, GridPortrait(paper));
        var shortEdge = Math.Min(w, h);
        var margin = GridMarginFraction * shortEdge;
        var footer = GridFooterFraction * shortEdge;
        return (margin / w, (h - margin - footer) / h, (w - (2 * margin)) / w, footer / h);
    }

    // --- Four-photo strips ------------------------------------------------------

    /// <summary>
    /// TWO STRIPS side by side on one portrait sheet, so a single 10x15 yields
    /// two photo-booth keepsakes — the same strip twice, or two different ones.
    /// </summary>
    public const int StripsPerSheet = 2;
    public const int SlotsPerStrip = 4;

    /// <summary>Gutter between the twin strips, where the sheet is cut.</summary>
    public const double StripGutterFraction = 0.035;

    /// <summary>Outer border of the sheet.</summary>
    public const double StripMarginFraction = 0.035;

    /// <summary>Gap between the four frames within a strip.</summary>
    public const double StripSlotGapFraction = 0.012;

    /// <summary>Room at the foot of each strip for words.</summary>
    public const double StripFooterFraction = 0.075;

    /// <summary>Width of one strip, in sheet fractions.</summary>
    public static double StripWidthFraction =>
        (1.0 - (2 * StripMarginFraction) - StripGutterFraction) / StripsPerSheet;

    /// <summary>One slot's rectangle inside a strip, in fractions of the SHEET.</summary>
    public static (double X, double Y, double Width, double Height) StripSlot(int stripIndex, int slotIndex)
    {
        var stripW = StripWidthFraction;
        var x = StripMarginFraction + (stripIndex * (stripW + StripGutterFraction));

        var contentTop = StripMarginFraction;
        var contentHeight = 1.0 - (2 * StripMarginFraction) - StripFooterFraction;
        var totalGap = StripSlotGapFraction * (SlotsPerStrip - 1);
        var slotH = (contentHeight - totalGap) / SlotsPerStrip;
        var y = contentTop + (slotIndex * (slotH + StripSlotGapFraction));

        return (x, y, stripW, slotH);
    }

    /// <summary>The band at the foot of one strip, in sheet fractions.</summary>
    public static (double X, double Y, double Width, double Height) StripFooter(int stripIndex)
    {
        var stripW = StripWidthFraction;
        return (StripMarginFraction + (stripIndex * (stripW + StripGutterFraction)),
            1.0 - StripMarginFraction - StripFooterFraction, stripW, StripFooterFraction);
    }
}

/// <summary>One sheet's rectangles, in fractions of a sheet of <see cref="Width"/> x <see cref="Height"/> pixels.</summary>
public sealed record PrintSheetLayout(
    int Width,
    int Height,
    IReadOnlyList<(double X, double Y, double Width, double Height)> Slots,
    IReadOnlyList<(double X, double Y, double Width, double Height)> Bands);
