using NubArca.Api.Domain.Print;

namespace NubArca.Api.Print;

/// <summary>
/// What a PARTY prints in the bands and on the photograph — its name, the
/// host's line, the guest's number, the NubArca mark — and the party's QR card.
///
/// Where photographs and bands sit is not here: that is the catalogue every
/// surface shares (<see cref="PrintLayouts"/>). This is the party's decoration
/// on top of it, as fractions of the sheet's short edge, mirrored value for
/// value by the browser's preview (partyPrintGeometry.ts), whose parity test
/// reads these numbers from this file.
/// </summary>
public static class PartyPrintGeometry
{
    // --- Single photograph, title on the photograph ---------------------------

    /// <summary>
    /// Inset of the symbol and the text from the edges of the full-bleed sheet,
    /// as a fraction of the short edge. The photograph itself runs to the edge.
    /// </summary>
    public const double OverlayMarginFraction = 0.06;

    /// <summary>
    /// Height of the NubArca symbol, short-edge fraction. It stands on the
    /// party's name line, just before the name, centred on the name's capitals
    /// — one signature with the words rather than a mark in the corner (it was
    /// 0.105, alone at the top left).
    /// </summary>
    public const double OverlaySymbolFraction = 0.085;

    /// <summary>The space between the symbol and the party's name, short-edge fraction.</summary>
    public const double OverlaySymbolGapFraction = 0.02;

    /// <summary>Type size of the party's name, bottom-left, short-edge fraction (it was 0.077, before that 0.085).</summary>
    public const double OverlayTitleFraction = 0.069;

    /// <summary>Type size of the host's line under the name (it was 0.036).</summary>
    public const double OverlayLineFraction = 0.0325;

    /// <summary>
    /// Type size of the guest's number: a touch above the host's line, because
    /// the number is what the desk reads to hand the right print over.
    /// </summary>
    public const double OverlayNumberFraction = 0.044;

    /// <summary>
    /// How far above the words their legibility support begins, short-edge
    /// fraction. The support starts just over the real block of text — the
    /// name, the host's line and the number as laid out — and runs to the foot
    /// of the sheet, so it is as tall as the words need and no taller. The
    /// photograph above it is untouched.
    /// </summary>
    public const double OverlayTextSupportPaddingFraction = 0.025;

    /// <summary>
    /// The support's strongest point, at the very foot of the sheet — a whisper
    /// of black under light words or of white under dark ones, never a tint.
    /// </summary>
    public const double OverlayTextSupportMaxOpacity = 0.22;

    /// <summary>
    /// The halo round the letters: the words in the support colour, blurred by
    /// a Gaussian of this sigma (short-edge fraction), laid under them at
    /// <see cref="OverlayHaloOpacity"/>. The preview's text-shadow is the same
    /// thing — a CSS blur radius is two sigmas.
    /// </summary>
    public const double OverlayHaloBlurFraction = 0.006;

    public const double OverlayHaloOpacity = 0.33;

    // --- Words on any sheet ----------------------------------------------------

    /// <summary>
    /// The longest party name a sheet prints; a longer one is cut with an
    /// ellipsis, after line breaks become spaces. The preview applies the same
    /// cut, so it never shows a name longer than the one on the paper.
    /// </summary>
    public const int PartyNameMaxLength = 42;

    // --- The wordmark in a band ------------------------------------------------

    /// <summary>
    /// Width of the wordmark on a strip, as a fraction of the strip's width —
    /// as large as its signature row lets it stand. At the brand's 120px
    /// minimum the symbol inside the lockup was ~37px, too small for the ark to
    /// resolve on paper, and the mark printed as a smudge (0.27, then a touch
    /// larger with the row's height share).
    /// </summary>
    public const double StripWordmarkWidthFraction = 0.31;

    /// <summary>
    /// Width of the wordmark under a single photograph or four, as a fraction
    /// of the footer's width: a signature a little more present than the quiet
    /// 0.20 it was, still the smallest thing on the sheet beside the number.
    /// </summary>
    public const double FooterWordmarkWidthFraction = 0.23;

    // --- The party's QR card, on the twin strip's sheet ------------------------

    /// <summary>
    /// The party's public QR on paper, for the tables. It is the twin strip's
    /// sheet — its margins, gutter, footer and the printer's cut — with two
    /// cells in each strip where the twin strip has four: the photograph in the
    /// top one and the QR in the bottom one, two over two as the four-photo
    /// sheet sets them. Both strips are the same card: one sheet, two cards.
    /// </summary>
    public const int QrCardCellsPerStrip = 2;

    /// <summary>
    /// The code's width, quiet zone included, as a fraction of the strip's
    /// width: about 3.8cm on a 10x15, a module near a millimetre for a party
    /// address — read at arm's length by any phone.
    /// </summary>
    public const double QrCardCodeWidthFraction = 0.84;

    /// <summary>Type size of the line saying what the code is for, short-edge fraction.</summary>
    public const double QrCardLineFraction = 0.034;

    /// <summary>One cell of a QR card — 0 the photograph, 1 the code — in fractions of the sheet.</summary>
    public static (double X, double Y, double Width, double Height) QrCardCell(int stripIndex, int cellIndex)
    {
        var stripW = PrintLayouts.StripWidthFraction;
        var x = PrintLayouts.StripMarginFraction + (stripIndex * (stripW + PrintLayouts.StripGutterFraction));
        var contentHeight = 1.0 - (2 * PrintLayouts.StripMarginFraction) - PrintLayouts.StripFooterFraction;
        var cellH = (contentHeight - (PrintLayouts.StripSlotGapFraction * (QrCardCellsPerStrip - 1))) / QrCardCellsPerStrip;
        var y = PrintLayouts.StripMarginFraction + (cellIndex * (cellH + PrintLayouts.StripSlotGapFraction));
        return (x, y, stripW, cellH);
    }

    /// <summary>The shape the card's photograph is placed in.</summary>
    public static double QrCardPhotoAspect()
    {
        var (_, _, w, h) = QrCardCell(0, 0);
        return (w * PrintLayouts.PortraitWidth) / (h * PrintLayouts.PortraitHeight);
    }
}
