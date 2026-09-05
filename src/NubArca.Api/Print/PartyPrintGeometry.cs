namespace NubArca.Api.Print;

/// <summary>
/// The geometry of a party print, in one place.
///
/// The browser draws a preview and the server draws the sheet that is actually
/// printed. Those two must agree, or a guest composes one thing and collects
/// another — and the sheet is the one they take home. So every number that
/// decides framing lives here, as fractions of the sheet, and the frontend
/// mirrors this file. Fractions rather than pixels: the preview is a few hundred
/// pixels wide and the print is 1200, and a fraction means the same thing at
/// both sizes.
///
/// Everything is 10x15 at 300dpi. The strip is a COMPOSITION on that sheet, not
/// a second paper size: the printer requirement never changes.
/// </summary>
public static class PartyPrintGeometry
{
    /// <summary>10x15cm at 300dpi, portrait. The strip sheet.</summary>
    public const int PortraitWidth = 1200;
    public const int PortraitHeight = 1800;

    /// <summary>The same sheet turned, for a single landscape photo.</summary>
    public const int LandscapeWidth = 1800;
    public const int LandscapeHeight = 1200;

    // --- Single photo ------------------------------------------------------

    /// <summary>Border around the photograph, as a fraction of the short edge.</summary>
    public const double PhotoMarginFraction = 0.055;

    /// <summary>
    /// Room under the photograph for the party line, the wordmark and the number.
    ///
    /// A fraction of the SHORT EDGE, like the margin beside it — never of the
    /// height. The height is what flips when the sheet follows a landscape
    /// photograph, and a footer measured against it came out a third shorter on
    /// exactly the sheets that are widest: 11.7mm instead of 17.5mm, with the
    /// type shrinking with the band, which is why a landscape print read as
    /// having no party name at all. The short edge is 10cm on both, so this is
    /// the same strip of paper whichever way the picture faces.
    /// </summary>
    public const double PhotoFooterFraction = 0.17;

    /// <summary>Aspect ratio the single-photo crop is locked to.</summary>
    public static double PhotoSlotAspect(bool portrait)
    {
        var (w, h) = portrait
            ? (PortraitWidth, PortraitHeight)
            : (LandscapeWidth, LandscapeHeight);
        var margin = PhotoMarginFraction * Math.Min(w, h);
        var slotW = w - (2 * margin);
        var slotH = h - (2 * margin) - (PhotoFooterFraction * Math.Min(w, h));
        return slotW / slotH;
    }

    // --- Four-photo strip --------------------------------------------------

    /// <summary>
    /// TWO IDENTICAL STRIPS side by side on one portrait sheet, so a single
    /// 10x15 yields two photo-booth keepsakes: one to keep, one to give away.
    /// </summary>
    public const int StripsPerSheet = 2;
    public const int SlotsPerStrip = 4;

    /// <summary>Gutter between the twin strips, where the sheet is cut.</summary>
    public const double StripGutterFraction = 0.035;

    /// <summary>Outer border of the sheet.</summary>
    public const double StripMarginFraction = 0.035;

    /// <summary>Gap between the four frames within a strip.</summary>
    public const double StripSlotGapFraction = 0.012;

    /// <summary>Room at the foot of each strip for the party line and wordmark.</summary>
    public const double StripFooterFraction = 0.075;

    /// <summary>
    /// Cut marks: short ticks at the very top and bottom of the gutter only.
    /// A dashed line down the middle of the composition would run through the
    /// photographs, which is what makes a strip look printed rather than made.
    /// </summary>
    public const double CutMarkLengthFraction = 0.022;

    /// <summary>Width of one strip, in sheet fractions.</summary>
    public static double StripWidthFraction =>
        (1.0 - (2 * StripMarginFraction) - StripGutterFraction) / StripsPerSheet;

    /// <summary>
    /// One slot's rectangle inside a strip, in fractions of the SHEET.
    /// The single place that decides where a photograph lands, shared by the
    /// renderer and mirrored by the preview.
    /// </summary>
    public static (double X, double Y, double Width, double Height) StripSlot(
        int stripIndex, int slotIndex)
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

    /// <summary>Aspect ratio every strip slot's crop is locked to.</summary>
    public static double StripSlotAspect()
    {
        var (_, _, w, h) = StripSlot(0, 0);
        return (w * PortraitWidth) / (h * PortraitHeight);
    }
}
