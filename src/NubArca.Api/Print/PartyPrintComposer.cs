using QRCoder;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

// ImageSharp.Drawing brings its own `Path` (a geometric one), so the file
// says which it means rather than relying on which using came last.
using Path = System.IO.Path;

namespace NubArca.Api.Print;

/// <summary>The three controlled looks a guest can choose between.</summary>
public enum PartyPrintTheme
{
    /// <summary>Cloud White, wide margins, the photograph and nothing else.</summary>
    Pure,
    /// <summary>Midnight Navy, the photograph framed, a restrained cyan edge.</summary>
    Midnight,
    /// <summary>The party's own name given room, for a keepsake that says where it is from.</summary>
    Event,
    /// <summary>
    /// A single photograph to the edges, untouched, with the NubArca symbol and
    /// the party's name printed on it. Its colours are the composition's
    /// <see cref="PartyPrintOverlay"/>, chosen independently.
    /// </summary>
    Overlay,
}

/// <summary>The words on an "On the photo" print: name, host's line and number.</summary>
public enum PartyPrintOverlayText { White, Black, Red }

/// <summary>The NubArca symbol on an "On the photo" print, independent of the words.</summary>
public enum PartyPrintOverlayLogo { Light, Dark }

/// <summary>The two independent choices an "On the photo" print carries.</summary>
public sealed record PartyPrintOverlay(PartyPrintOverlayText Text, PartyPrintOverlayLogo Logo)
{
    public static readonly PartyPrintOverlay Default = new(PartyPrintOverlayText.White, PartyPrintOverlayLogo.Light);
}

/// <summary>
/// One photograph and how it is framed, already validated: by its
/// <see cref="Placement"/> when the studio sent one, else by the legacy crop.
/// </summary>
public sealed record PartyPrintPhoto(
    byte[] Bytes, double CropX, double CropY, double CropWidth, double CropHeight,
    PhotoPlacement? Placement = null)
{
    public static PartyPrintPhoto Placed(byte[] bytes, PhotoPlacement placement) =>
        new(bytes, 0, 0, 1, 1, placement);
}

/// <summary>Which way round the sheet goes for a single photograph.</summary>
public enum PartyPrintOrientation
{
    /// <summary>The sheet follows the photograph — the default, and usually right.</summary>
    FollowPhoto = 0,
    Portrait = 1,
    Landscape = 2,
}

/// <summary>Everything the composer needs, and nothing about who asked.</summary>
public sealed record PartyPrintComposition(
    string Product,
    PartyPrintTheme Theme,
    IReadOnlyList<PartyPrintPhoto> Photos,
    string PartyName,
    string? FooterText,
    /// <summary>
    /// The guest's queue number, printed as #n so a sheet on the collection
    /// table can be matched to the person holding that number on their phone.
    /// Zero prints nothing, which is what a preview or a test page wants.
    /// </summary>
    long PublicSequence = 0,
    /// <summary>
    /// Only meaningful for a single photograph. The four-photo strip is a fixed
    /// composition on a portrait sheet — two strips side by side is what makes
    /// it a strip — so turning that sheet would not turn a picture, it would
    /// destroy the product.
    /// </summary>
    PartyPrintOrientation Orientation = PartyPrintOrientation.FollowPhoto,
    /// <summary>The printer's tone compensation, applied to the whole sheet. Null is neutral.</summary>
    PrintCalibration? Calibration = null,
    /// <summary>Text and symbol colours of the "On the photo" look. Null is its default.</summary>
    PartyPrintOverlay? Overlay = null,
    /// <summary>The paper the sheet is for (a PrintPapers id). The twin strip is always 10x15.</summary>
    string Paper = Domain.Print.PrintPapers.Photo10x15);

/// <summary>
/// Draws the sheet that is actually printed.
///
/// The browser's preview is a preview. This is the artifact: rendered from the
/// validated originals, the normalised crops and the shared geometry, so what
/// the guest composed is what comes out of the printer.
///
/// Two rules the output must keep. Photographs are never filtered — a theme
/// decides the paper around a picture, never the picture; the one adjustment
/// that touches it is the PRINTER's calibration, which compensates that device
/// on the whole sheet so it prints what was composed. And the sheet carries
/// no metadata: the JPEG is written without the EXIF, GPS and camera data the
/// sources came with, because a print handed to a stranger should not carry
/// where it was taken.
/// </summary>
public sealed class PartyPrintComposer
{
    private static readonly Rgba32 CloudWhite = new(0xF5, 0xF7, 0xFB);
    private static readonly Rgba32 White = new(0xFF, 0xFF, 0xFF);
    private static readonly Rgba32 MidnightNavy = new(0x0A, 0x0F, 0x1A);
    private static readonly Rgba32 DeepBlue = new(0x0F, 0x1E, 0x3A);
    private static readonly Rgba32 CyanGlow = new(0x00, 0xD4, 0xFF);
    private static readonly Rgba32 Ink = new(0x0A, 0x0F, 0x1A);

    /// <summary>
    /// The red a guest may choose for the words on a photograph: decided and
    /// printable, neither fluorescent nor brown. A print colour, not a brand one.
    /// </summary>
    private static readonly Rgba32 OverlayTextRed = new(0xD1, 0x1F, 0x2E);

    /// <summary>Minimum rendered wordmark width, from the brand guidelines.</summary>
    private const int BrandMinWordmarkWidth = 120;

    /// <summary>How much of the signature row the wordmark may stand in (it was 0.80).</summary>
    private const double WordmarkRowShare = 0.92;

    /// <summary>The guest's number, as a share of the signature row's height (they were 0.34 and 0.39).</summary>
    private const float FooterNumberRowShare = 0.51f;
    private const float StripNumberRowShare = 0.585f;

    /// <summary>The party's name and the host's line under a photograph or four, against their bands (a strip: 1).</summary>
    private const float FooterTextScale = 1.12f;

    private readonly FontFamily _display;
    private readonly FontFamily _ui;
    private readonly string _assetRoot;

    public PartyPrintComposer(string? assetRoot = null)
    {
        _assetRoot = assetRoot ?? AppContext.BaseDirectory;
        var fonts = new FontCollection();
        _display = fonts.Add(Path.Combine(_assetRoot, "Assets", "fonts", "SpaceGrotesk-Bold.ttf"));
        _ui = fonts.Add(Path.Combine(_assetRoot, "Assets", "fonts", "Exo2-Medium.ttf"));
    }

    public async Task<byte[]> RenderAsync(
        PartyPrintComposition composition, CancellationToken cancellationToken)
    {
        using var sheet = composition.Product switch
        {
            Domain.Print.PartyPrintProducts.TwinStrip4 => RenderStrip(composition),
            Domain.Print.PartyPrintProducts.Grid4 => RenderGrid4(composition),
            _ => RenderPhoto(composition),
        };
        return await EncodeAsync(sheet, composition.Calibration, cancellationToken);
    }

    /// <summary>The party's QR card: the host's own sheet for the tables, cut in two by the printer.</summary>
    public async Task<byte[]> RenderQrCardAsync(PartyQrCardComposition card, CancellationToken cancellationToken)
    {
        using var sheet = RenderQrCard(card);
        return await EncodeAsync(sheet, card.Calibration, cancellationToken);
    }

    /// <summary>The sheet as the printer receives it: calibrated, stripped of every source's metadata, a JPEG.</summary>
    private static async Task<byte[]> EncodeAsync(
        Image<Rgba32> sheet, PrintCalibration? calibration, CancellationToken cancellationToken)
    {
        // The printer's compensation, not a filter: the whole sheet, paper and
        // photographs, so that this printer's output matches what was composed.
        (calibration ?? PrintCalibration.Neutral).ApplyTo(sheet);

        // Strip everything the sources carried: a printed keepsake must not
        // travel with the GPS coordinates of where it was taken.
        sheet.Metadata.ExifProfile = null;
        sheet.Metadata.XmpProfile = null;
        sheet.Metadata.IptcProfile = null;
        sheet.Metadata.IccProfile = null;

        using var output = new MemoryStream();
        await sheet.SaveAsJpegAsync(
            output, new JpegEncoder { Quality = 94, ColorType = JpegEncodingColor.YCbCrRatio444 },
            cancellationToken);
        return output.ToArray();
    }

    // --- Single photograph -------------------------------------------------

    private Image<Rgba32> RenderPhoto(PartyPrintComposition composition)
    {
        if (composition.Theme == PartyPrintTheme.Overlay)
            return RenderOverlayPhoto(composition);
        var photo = composition.Photos[0];
        using var source = LoadOriented(photo.Bytes);
        // The sheet follows the photograph unless the guest said otherwise: a
        // landscape picture goes on a landscape sheet rather than a portrait one
        // with white bars beside it. But following is a good DEFAULT, not a
        // rule — a portrait subject in a landscape frame is a choice somebody
        // may want, and the crop editor is what makes it work.
        var portrait = composition.Orientation switch
        {
            PartyPrintOrientation.Portrait => true,
            PartyPrintOrientation.Landscape => false,
            _ => source.Height >= source.Width,
        };
        var (w, h) = PartyPrintGeometry.Sheet(composition.Paper, portrait);

        var sheet = new Image<Rgba32>(w, h);
        var palette = Palette(composition.Theme);
        sheet.Mutate(x => x.Fill(palette.Background));

        // Short edge, not height: see PhotoFooterFraction. The sheet turns; the
        // strip of paper under the photograph must not.
        DrawFramed(sheet, source, photo,
            ToPixels(PartyPrintGeometry.PhotoSlot(composition.Paper, portrait), w, h),
            composition.Theme, palette);
        DrawFooter(sheet, composition, palette,
            ToPixels(PartyPrintGeometry.PhotoFooter(composition.Paper, portrait), w, h));
        return sheet;
    }

    /// <summary>A rectangle of sheet fractions, in the sheet's pixels.</summary>
    private static Rectangle ToPixels((double X, double Y, double Width, double Height) fraction, int w, int h) =>
        new((int)Math.Round(fraction.X * w), (int)Math.Round(fraction.Y * h),
            (int)Math.Round(fraction.Width * w), (int)Math.Round(fraction.Height * h));

    // --- Four photographs on one sheet -------------------------------------

    /// <summary>
    /// Four photographs, two by two, on the loaded paper as it is named: the
    /// first top left, the second top right, the third and fourth under them —
    /// the order the guest arranged. Each keeps its own crop. One footer signs
    /// the sheet. No cut marks: nothing here is meant to be cut.
    /// </summary>
    private Image<Rgba32> RenderGrid4(PartyPrintComposition composition)
    {
        var paper = composition.Paper;
        var (w, h) = PartyPrintGeometry.Sheet(paper, PartyPrintGeometry.GridPortrait(paper));
        var sheet = new Image<Rgba32>(w, h);
        var palette = Palette(composition.Theme);
        sheet.Mutate(x => x.Fill(palette.Background));

        var sources = composition.Photos
            .Select(p => (Photo: p, Image: LoadOriented(p.Bytes)))
            .ToList();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var (photo, image) = sources[index % sources.Count];
                DrawFramed(sheet, image, photo,
                    ToPixels(PartyPrintGeometry.GridSlot(paper, index), w, h), composition.Theme, palette);
            }
            DrawFooter(sheet, composition, palette, ToPixels(PartyPrintGeometry.GridFooter(paper), w, h));
            return sheet;
        }
        finally
        {
            foreach (var (_, image) in sources) image.Dispose();
        }
    }

    // --- Two strips of four, cut apart by the printer -------------------------

    /// <summary>
    /// The twin strip: two strips of four on one portrait 10x15, photographs
    /// 1–4 on the left and 5–8 on the right. The printer cuts the sheet in two,
    /// so it carries no marks to cut along — a tick would sit exactly under the
    /// blade, and a cut a fraction of a millimetre off would leave it on a strip.
    /// </summary>
    private Image<Rgba32> RenderStrip(PartyPrintComposition composition)
    {
        const int w = PartyPrintGeometry.PortraitWidth;
        const int h = PartyPrintGeometry.PortraitHeight;
        var sheet = new Image<Rgba32>(w, h);
        var palette = Palette(composition.Theme);
        sheet.Mutate(x => x.Fill(palette.Background));

        var sources = composition.Photos
            .Select(p => (Photo: p, Image: LoadOriented(p.Bytes)))
            .ToList();
        try
        {
            // Eight photographs, four per strip: one sheet, two keepsakes.
            for (var strip = 0; strip < PartyPrintGeometry.StripsPerSheet; strip++)
            {
                for (var slotIndex = 0; slotIndex < PartyPrintGeometry.SlotsPerStrip; slotIndex++)
                {
                    var (fx, fy, fw, fh) = PartyPrintGeometry.StripSlot(strip, slotIndex);
                    var rect = new Rectangle(
                        (int)Math.Round(fx * w), (int)Math.Round(fy * h),
                        (int)Math.Round(fw * w), (int)Math.Round(fh * h));
                    // Strip 0 takes photographs 1–4, strip 1 takes 5–8. A
                    // composition of only four (an older client) repeats them.
                    var (photo, image) = sources[
                        ((strip * PartyPrintGeometry.SlotsPerStrip) + slotIndex) % sources.Count];
                    DrawFramed(sheet, image, photo, rect, composition.Theme, palette);
                }

                DrawFooter(sheet, composition, palette,
                    ToPixels(PartyPrintGeometry.StripFooter(strip), w, h), strip: true);
            }

            return sheet;
        }
        finally
        {
            foreach (var (_, image) in sources) image.Dispose();
        }
    }

    // --- The party's QR card, on the twin strip's sheet -------------------------

    /// <summary>
    /// Two identical cards on the twin strip's sheet, which the printer cuts
    /// apart: in each strip the host's photograph over the party's QR, the
    /// line saying what it opens, and the strip's own foot — the party's name
    /// and the wordmark, and no queue number, since no guest is waiting for it.
    /// On white paper, because a code is read by its contrast.
    /// </summary>
    private Image<Rgba32> RenderQrCard(PartyQrCardComposition card)
    {
        const int w = PartyPrintGeometry.PortraitWidth;
        const int h = PartyPrintGeometry.PortraitHeight;
        var sheet = new Image<Rgba32>(w, h);
        var palette = Palette(PartyPrintTheme.Pure);
        sheet.Mutate(x => x.Fill(palette.Background));

        using var source = LoadOriented(card.Photo);
        using var code = QrModules(card.Url);
        var photo = PartyPrintPhoto.Placed(card.Photo, card.Placement);
        // The foot is the strip's, drawn by the strip's own code: the name, no
        // host's line, no number.
        var foot = new PartyPrintComposition(
            Domain.Print.PartyPrintProducts.TwinStrip4, PartyPrintTheme.Pure, [photo], card.PartyName, FooterText: null);
        for (var strip = 0; strip < PartyPrintGeometry.StripsPerSheet; strip++)
        {
            DrawFramed(sheet, source, photo, ToPixels(PartyPrintGeometry.QrCardCell(strip, 0), w, h),
                PartyPrintTheme.Pure, palette);
            DrawQrCell(sheet, code, card.Line, ToPixels(PartyPrintGeometry.QrCardCell(strip, 1), w, h), palette);
            DrawFooter(sheet, foot, palette, ToPixels(PartyPrintGeometry.StripFooter(strip), w, h), strip: true);
        }
        return sheet;
    }

    /// <summary>
    /// The code as one pixel per module, its quiet zone included — so that it
    /// is scaled once, by a whole number, with nothing blurred between modules.
    /// </summary>
    private static Image<Rgba32> QrModules(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        var image = new Image<Rgba32>(size, size, White);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (matrix[y][x]) image[x, y] = Ink;
        return image;
    }

    /// <summary>
    /// The code's cell: the line that says what it opens, then the code — as
    /// large a whole number of pixels per module as its width allows — the two
    /// centred together in the cell.
    /// </summary>
    private void DrawQrCell(Image<Rgba32> sheet, Image<Rgba32> modules, string line, Rectangle cell, ThemePalette palette)
    {
        var area = QrCodeArea(cell, modules.Width, Math.Min(sheet.Width, sheet.Height));
        // A little air at the strip's edges: the line is fitted to most of its width.
        var (text, font) = FitLine(line, _ui, FontStyle.Regular, area.LineSize, area.LineSize * 0.75f, cell.Width * 0.92f);
        sheet.Mutate(x => x.DrawText(
            new RichTextOptions(font)
            {
                Origin = new PointF(cell.X + (cell.Width / 2f), area.LineMiddle),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            text, palette.Foreground));

        using var code = modules.Clone(x => x.Resize(new ResizeOptions
        {
            Size = new Size(area.Code.Width, area.Code.Height),
            Sampler = KnownResamplers.NearestNeighbor,
        }));
        sheet.Mutate(x => x.DrawImage(code, new Point(area.Code.X, area.Code.Y), 1f));
    }

    /// <summary>Where the code and the line over it go in a card's code cell.</summary>
    internal sealed record QrCellArea(Rectangle Code, int Module, float LineSize, float LineMiddle);

    /// <summary>
    /// The code's square — <paramref name="modules"/> modules a side, quiet zone
    /// included, each a whole number of pixels — and the line over it, the two
    /// centred together in <paramref name="cell"/>.
    /// </summary>
    internal static QrCellArea QrCodeArea(Rectangle cell, int modules, int shortEdge)
    {
        var module = Math.Max(1, (int)(cell.Width * PartyPrintGeometry.QrCardCodeWidthFraction / modules));
        var codeSize = module * modules;
        var lineSize = (float)(PartyPrintGeometry.QrCardLineFraction * shortEdge);
        var textHeight = lineSize * 1.3f;
        var gap = lineSize * 0.6f;
        var top = cell.Y + ((cell.Height - (textHeight + gap + codeSize)) / 2f);
        var code = new Rectangle(
            cell.X + ((cell.Width - codeSize) / 2), (int)Math.Round(top + textHeight + gap), codeSize, codeSize);
        return new QrCellArea(code, module, lineSize, top + (textHeight / 2f));
    }

    // --- Single photograph, title on it ---------------------------------------

    /// <summary>
    /// The photograph to the edges of the sheet, exactly as cropped — no tint,
    /// no scrim, no fade — with the party's name bottom-left printed ON it and
    /// the NubArca symbol just before the name, on its line. Legibility is helped only where the words are:
    /// a faint gradient across the last fifth of the sheet and a soft halo round
    /// the letters. The photograph stays the subject.
    /// </summary>
    private Image<Rgba32> RenderOverlayPhoto(PartyPrintComposition composition)
    {
        var photo = composition.Photos[0];
        using var source = LoadOriented(photo.Bytes);
        var portrait = composition.Orientation switch
        {
            PartyPrintOrientation.Portrait => true,
            PartyPrintOrientation.Landscape => false,
            _ => source.Height >= source.Width,
        };
        var (w, h) = PartyPrintGeometry.Sheet(composition.Paper, portrait);

        var overlay = composition.Overlay ?? PartyPrintOverlay.Default;
        var ink = TextInk(overlay.Text);
        // Dark words get a whisper of white under them; light and red words, of black.
        var support = overlay.Text == PartyPrintOverlayText.Black ? CloudWhite : Ink;

        // The words are laid out first: their support is drawn behind them, as
        // tall as they turned out to be.
        var layout = LayoutOverlayText(composition, w, h);
        var sheet = new Image<Rgba32>(w, h);
        // Full bleed has no paper of its own: a photograph zoomed out sits on
        // white, the colour of the sheet it is printed on.
        DrawFramed(sheet, source, photo, new Rectangle(0, 0, w, h), composition.Theme,
            Palette(PartyPrintTheme.Pure), band: White);
        DrawOverlayTextSupport(sheet, OverlayTextSupportTop(layout, w, h), support);

        DrawOverlayText(sheet, layout, ink, support);
        // After the words: the name's halo reaches toward its neighbour, and
        // the brand mark is laid over it exactly as shipped, never veiled.
        DrawSymbol(sheet, overlay.Logo, new Point((int)layout.Symbol.X, (int)layout.Symbol.Y), (int)layout.Symbol.Width);
        return sheet;
    }

    private static Rgba32 TextInk(PartyPrintOverlayText text) => text switch
    {
        PartyPrintOverlayText.Black => Ink,
        PartyPrintOverlayText.Red => OverlayTextRed,
        _ => CloudWhite,
    };

    /// <summary>
    /// Where the words' support begins: just above the highest of the name, the
    /// host's line and the number, by
    /// <see cref="PartyPrintGeometry.OverlayTextSupportPaddingFraction"/> of the
    /// short edge.
    /// </summary>
    internal static float OverlayTextSupportTop(OverlayTextLayout layout, int w, int h)
    {
        // The symbol stands on the name's line now: the support begins over
        // whichever is higher, the words or the mark beside them.
        var textTop = Math.Min(layout.Symbol.Top, new[] { layout.Title, layout.Footer, layout.Number }
            .Where(x => x is not null).Min(x => x!.Box.Top));
        var padding = (float)(PartyPrintGeometry.OverlayTextSupportPaddingFraction * Math.Min(w, h));
        return Math.Max(0f, textTop - padding);
    }

    /// <summary>
    /// Transparent at <paramref name="top"/>, at most
    /// <see cref="PartyPrintGeometry.OverlayTextSupportMaxOpacity"/> of
    /// <paramref name="colour"/> at the foot, and nothing above it.
    /// </summary>
    private static void DrawOverlayTextSupport(Image<Rgba32> sheet, float top, Rgba32 colour)
    {
        var strongest = (byte)Math.Round(PartyPrintGeometry.OverlayTextSupportMaxOpacity * 255);
        var brush = new LinearGradientBrush(
            new PointF(0, top), new PointF(0, sheet.Height), GradientRepetitionMode.None,
            new ColorStop(0, Color.FromPixel(new Rgba32(colour.R, colour.G, colour.B, 0))),
            new ColorStop(1, Color.FromPixel(new Rgba32(colour.R, colour.G, colour.B, strongest))));
        sheet.Mutate(x => x.Fill(brush, new RectangleF(0, top, sheet.Width, sheet.Height - top)));
    }

    /// <summary>The approved flat mark for each treatment, exactly as the brand ships it.</summary>
    internal static string SymbolFile(PartyPrintOverlayLogo logo) => logo == PartyPrintOverlayLogo.Dark
        // Midnight Navy and Electric Blue, the brand's mark for light grounds.
        ? "nubarca-mark-flat-on-light-512.png"
        // Cloud White, Cyan and Electric Blue, the brand's mark for dark grounds.
        : "nubarca-mark-flat-on-dark-512.png";

    /// <summary>
    /// The approved flat mark in the chosen treatment, in its own colours —
    /// never refilled — scaled once, with a sharp filter, to
    /// <paramref name="size"/>. Nothing is drawn under it: the photograph is
    /// not altered for the mark.
    /// </summary>
    private void DrawSymbol(Image<Rgba32> sheet, PartyPrintOverlayLogo logo, Point at, int size)
    {
        using var mark = Image.Load<Rgba32>(Path.Combine(_assetRoot, "Assets", "brand", SymbolFile(logo)));
        mark.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(size, size),
            Sampler = KnownResamplers.Lanczos3,
        }));
        sheet.Mutate(x => x.DrawImage(mark, at, 1f));
    }

    /// <summary>
    /// Where the words go and how big they are — measured, so nothing meets —
    /// and the square the symbol stands in, just before the name.
    /// </summary>
    internal sealed record OverlayTextLayout(
        OverlayWord Title, OverlayWord? Footer, OverlayWord? Number, RectangleF Symbol);

    /// <summary>
    /// One run of text: its font, the bottom origin it is drawn from (right
    /// edge for a right-aligned run), and <paramref name="Box"/>, the ink it
    /// actually puts on the paper — measured with the very options it is drawn
    /// with, so a box that does not touch another is letters that do not touch.
    /// </summary>
    internal sealed record OverlayWord(string Text, Font Font, PointF Origin, bool AlignRight, RectangleF Box)
    {
        public RichTextOptions Options(float dy = 0) => new(Font)
        {
            Origin = new PointF(Origin.X, Origin.Y + dy),
            HorizontalAlignment = AlignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        public static OverlayWord At(string text, Font font, PointF origin, bool alignRight)
        {
            var word = new OverlayWord(text, font, origin, alignRight, RectangleF.Empty);
            var ink = TextMeasurer.MeasureBounds(text, word.Options());
            return word with { Box = new RectangleF(ink.X, ink.Y, ink.Width, ink.Height) };
        }
    }

    /// <summary>
    /// The name bottom-left with the NubArca symbol just before it on the same
    /// line, the host's line under them, and the number bottom-right on the
    /// same line as the host's line. The number's measured width — plus a gap —
    /// is RESERVED before the line is laid out, and the line shrinks, then
    /// shortens, to fit what is left, so the two can never touch; the name does
    /// the same in what the symbol leaves it.
    /// </summary>
    internal OverlayTextLayout LayoutOverlayText(PartyPrintComposition composition, int w, int h)
    {
        var shortEdge = Math.Min(w, h);
        var margin = (float)Math.Round(PartyPrintGeometry.OverlayMarginFraction * shortEdge);
        var bottom = h - margin;
        var lineSize = (float)(PartyPrintGeometry.OverlayLineFraction * shortEdge);
        var gap = lineSize * 1.2f;

        OverlayWord? number = null;
        var reserved = 0f;
        if (composition.PublicSequence > 0)
        {
            var font = _display.CreateFont((float)(PartyPrintGeometry.OverlayNumberFraction * shortEdge), FontStyle.Bold);
            var text = $"#{composition.PublicSequence}";
            number = OverlayWord.At(text, font, new PointF(w - margin, bottom), alignRight: true);
            reserved = TextMeasurer.MeasureSize(text, new TextOptions(font)).Width + gap;
        }

        OverlayWord? footer = null;
        var footerText = Truncate(composition.FooterText ?? string.Empty, Domain.Print.PartyPrintLimits.FooterMaxLength);
        if (footerText.Length > 0)
        {
            var available = w - (2 * margin) - reserved;
            var (text, font) = FitLine(footerText, _ui, FontStyle.Regular, lineSize, lineSize * 0.75f, available);
            footer = OverlayWord.At(text, font, new PointF(margin, bottom), alignRight: false);
        }

        // The name takes the full width above the host's line — clear of the
        // tallest ink on that line, the number's included — or the width the
        // number leaves when it shares the bottom line.
        var lineTop = Math.Min(footer?.Box.Top ?? bottom, number?.Box.Top ?? bottom);
        var nameBottom = footer is null ? bottom : lineTop - (lineSize * 0.45f);
        // The symbol takes the head of the name's line; the name starts after it.
        var symbolSize = (float)Math.Round(PartyPrintGeometry.OverlaySymbolFraction * shortEdge);
        var nameLeft = margin + symbolSize + (float)Math.Round(PartyPrintGeometry.OverlaySymbolGapFraction * shortEdge);
        var nameAvailable = w - margin - nameLeft - (footer is null ? reserved : 0f);
        var titleSize = (float)(PartyPrintGeometry.OverlayTitleFraction * shortEdge);
        var (name, titleFont) = FitLine(
            Truncate(composition.PartyName, PartyPrintGeometry.PartyNameMaxLength), _display, FontStyle.Bold,
            titleSize, 12f, nameAvailable);
        var title = OverlayWord.At(name, titleFont, new PointF(nameLeft, nameBottom), alignRight: false);
        // Centred on the name's capitals — measured on a capital in the name's
        // own font, so a name with descenders does not move it, and a name
        // fitted smaller takes the symbol with it. The boat is taller than the
        // capitals, so it reaches as far above them as below the baseline, the
        // way a mark sits beside a word; standing on the baseline, it read as
        // set too high. The artwork's ink is centred in its square (asserted on
        // the shipped files), so centring the square centres the boat.
        var caps = TextMeasurer.MeasureBounds("H", title.Options());
        var capsMiddle = (caps.Top + caps.Bottom) / 2f;
        var symbol = new RectangleF(margin, capsMiddle - (symbolSize / 2f), symbolSize, symbolSize);
        return new OverlayTextLayout(title, footer, number, symbol);
    }

    /// <summary>
    /// Shrinks a line toward <paramref name="minSize"/>, then shortens it with
    /// an ellipsis, until its INK — not just its advance — ends within
    /// <paramref name="available"/> of where it starts.
    /// </summary>
    private static (string Text, Font Font) FitLine(
        string text, FontFamily family, FontStyle style, float size, float minSize, float available)
    {
        static float Reach(string value, Font font) => TextMeasurer.MeasureBounds(value, new TextOptions(font)).Right;
        var font = family.CreateFont(size, style);
        var width = Reach(text, font);
        if (width <= available || width <= 0) return (text, font);
        font = family.CreateFont(Math.Max(minSize, size * available / width), style);
        while (text.Length > 1 && Reach(text, font) > available)
            text = text[..^2].TrimEnd() + "…";
        return (text, font);
    }

    /// <summary>
    /// The words, over a soft halo of the support colour — blurred, faint, only
    /// round the letters — so they read on any photograph without a badge.
    /// </summary>
    private static void DrawOverlayText(Image<Rgba32> sheet, OverlayTextLayout layout, Rgba32 ink, Rgba32 halo)
    {
        var words = new[] { layout.Title, layout.Footer, layout.Number }.Where(x => x is not null).Cast<OverlayWord>().ToList();
        var shortEdge = Math.Min(sheet.Width, sheet.Height);
        var pad = (int)Math.Ceiling(shortEdge * 0.03);
        var top = Math.Max(0, (int)words.Min(x => x.Box.Top) - pad);
        // The band is the halo colour throughout, transparent: only its alpha
        // carries the letters, so blurring it cannot drag the colour anywhere.
        using (var band = new Image<Rgba32>(sheet.Width, sheet.Height - top, new Rgba32(halo.R, halo.G, halo.B, 0)))
        {
            foreach (var word in words) Draw(band, word, new Rgba32(halo.R, halo.G, halo.B, 255), -top);
            band.Mutate(x => x.GaussianBlur((float)(shortEdge * PartyPrintGeometry.OverlayHaloBlurFraction)));
            sheet.Mutate(x => x.DrawImage(band, new Point(0, top), (float)PartyPrintGeometry.OverlayHaloOpacity));
        }
        foreach (var word in words) Draw(sheet, word, ink, 0);

        static void Draw(Image<Rgba32> target, OverlayWord word, Rgba32 colour, int dy) =>
            target.Mutate(x => x.DrawText(word.Options(dy), word.Text, colour));
    }

    // --- Shared drawing ----------------------------------------------------

    /// <summary>
    /// The photograph, cropped as composed and filled into its slot.
    ///
    /// The crop arrives as fractions of the auto-oriented source, so it means
    /// the same thing here as it did in the browser that produced it. Nothing
    /// about the picture itself changes: no filter, no saturation, no rotation.
    /// </summary>
    private static void DrawFramed(
        Image<Rgba32> sheet, Image<Rgba32> source, PartyPrintPhoto photo,
        Rectangle slot, PartyPrintTheme theme, ThemePalette palette, Rgba32? band = null)
    {
        if (photo.Placement is PhotoPlacement placement)
        {
            // The frame's own paper first: where a zoomed-out photograph leaves
            // room, the theme's background shows — never a black bar.
            DrawPlaced(sheet, source, placement, slot, band ?? palette.Background);
        }
        else
        {
            DrawCropped(sheet, source, photo, slot);
        }

        if (theme == PartyPrintTheme.Midnight)
        {
            // A HAIRLINE where the photograph meets the dark paper. Eight bright
            // cyan frames on one sheet read as neon; one thin, low-contrast edge
            // per picture reads as a deliberate mount, which is the intent.
            var edge = new RectangularPolygon(
                slot.X - 1, slot.Y - 1, slot.Width + 2, slot.Height + 2);
            sheet.Mutate(x => x.Draw(palette.Edge, 2f, edge));
        }
    }

    /// <summary>
    /// A photograph placed in a frame by the shared geometry, on the frame's
    /// <paramref name="band"/>: covered, zoomed in, or zoomed out with the band
    /// showing — never stretched, never panned into a gap.
    /// </summary>
    internal static void DrawPlaced(
        Image<Rgba32> target, Image<Rgba32> source, PhotoPlacement placement, Rectangle slot, Rgba32 band)
    {
        target.Mutate(x => x.Fill(band, new RectangularPolygon(slot.X, slot.Y, slot.Width, slot.Height)));
        var placed = PhotoPlacementGeometry.Place(
            (double)source.Width / source.Height, (double)slot.Width / slot.Height, placement);
        var visible = PhotoPlacementGeometry.Visible(placed);
        var (cx, cy, cw, ch) = PhotoPlacementGeometry.LegacyCrop(placed);
        var from = Rectangle.Intersect(new Rectangle(
            (int)Math.Round(cx * source.Width), (int)Math.Round(cy * source.Height),
            Math.Max(1, (int)Math.Round(cw * source.Width)), Math.Max(1, (int)Math.Round(ch * source.Height))),
            source.Bounds);
        var to = new Rectangle(
            slot.X + (int)Math.Round(visible.Left * slot.Width), slot.Y + (int)Math.Round(visible.Top * slot.Height),
            Math.Max(1, (int)Math.Round(visible.Width * slot.Width)),
            Math.Max(1, (int)Math.Round(visible.Height * slot.Height)));
        using var drawn = source.Clone(x => x.Crop(from).Resize(to.Width, to.Height));
        target.Mutate(x => x.DrawImage(drawn, new Point(to.X, to.Y), 1f));
    }

    /// <summary>A job from before placements: its crop, filled edge to edge, as it always printed.</summary>
    private static void DrawCropped(Image<Rgba32> sheet, Image<Rgba32> source, PartyPrintPhoto photo, Rectangle slot)
    {
        var cropRect = new Rectangle(
            (int)Math.Round(photo.CropX * source.Width),
            (int)Math.Round(photo.CropY * source.Height),
            Math.Max(1, (int)Math.Round(photo.CropWidth * source.Width)),
            Math.Max(1, (int)Math.Round(photo.CropHeight * source.Height)));
        cropRect = Rectangle.Intersect(cropRect, source.Bounds);

        using var framed = source.Clone(x => x
            .Crop(cropRect)
            // Crop, not Pad: the slot is filled edge to edge, so a print never
            // arrives with white bars where a photograph should be.
            .Resize(new ResizeOptions
            {
                Size = new Size(slot.Width, slot.Height),
                Mode = ResizeMode.Crop,
                Position = AnchorPositionMode.Center,
            }));

        sheet.Mutate(x => x.DrawImage(framed, new Point(slot.X, slot.Y), 1f));
    }

    private void DrawFooter(
        Image<Rgba32> sheet, PartyPrintComposition composition,
        ThemePalette palette, Rectangle area, bool strip = false)
    {
        // Only three things may ever appear on the paper: the party's name, the
        // line the HOST configured, and the wordmark. A guest writes nothing —
        // which is what keeps a physical print free of arbitrary text.
        // Three things share this strip of paper — the party's name, the host's
        // line, the wordmark — so the area is DIVIDED between them rather than
        // each being placed at its own fraction, which is how the footer and the
        // wordmark ended up drawn on top of each other.
        var footer = Truncate(composition.FooterText ?? string.Empty, Domain.Print.PartyPrintLimits.FooterMaxLength);
        var hasFooter = footer.Length > 0;
        var sizes = MeasureFooter(area.Height, composition.Theme, hasFooter, strip);

        var nameFont = _display.CreateFont(sizes.NameSize, FontStyle.Bold);
        sheet.Mutate(x => x.DrawText(
            new RichTextOptions(nameFont)
            {
                Origin = new PointF(area.X + (area.Width / 2f), area.Y + (sizes.NameBand / 2f)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Truncate(composition.PartyName, PartyPrintGeometry.PartyNameMaxLength), palette.Foreground));

        if (hasFooter)
        {
            var footBand = sizes.TextBand - sizes.NameBand;
            var footFont = _ui.CreateFont(sizes.LineSize, FontStyle.Regular);
            sheet.Mutate(x => x.DrawText(
                new RichTextOptions(footFont)
                {
                    Origin = new PointF(
                        area.X + (area.Width / 2f), area.Y + sizes.NameBand + (footBand / 2f)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                footer, palette.Muted));
        }

        // The signature row: wordmark left, the guest's number right, sharing a
        // baseline so the foot of the sheet reads as one line rather than two
        // things that happen to be near each other.
        var markRow = new Rectangle(
            area.X, area.Y + (int)sizes.TextBand, area.Width, (int)sizes.MarkBand);
        DrawWordmark(sheet, palette, markRow, sizes.WordmarkWidthFraction);
        DrawSequence(sheet, palette, markRow, composition.PublicSequence, sizes.NumberSize);
    }

    /// <summary>How the footer band is shared out, and how large each thing in it is drawn, in pixels.</summary>
    internal sealed record FooterSizes(
        float TextBand, float NameBand, float MarkBand, float NameSize, float LineSize,
        double WordmarkWidthFraction, float NumberSize);

    /// <summary>
    /// The footer under the photographs, for a band <paramref name="height"/>
    /// tall: the words above, the signature row below (38%). A strip's row is a
    /// third as wide as a photograph's, so there the wordmark stands as tall as
    /// the row allows and the number is set a size up: at the photograph's
    /// proportions both came out too small to read — the symbol a smudge, the
    /// number squinted at. The number is what the collection desk reads, so on
    /// every sheet it is half again the size it was (0.34 and 0.39 of the row);
    /// the words are a touch larger except on a strip's narrow foot.
    /// </summary>
    internal static FooterSizes MeasureFooter(int height, PartyPrintTheme theme, bool hasFooter, bool strip)
    {
        var markBand = height * 0.38f;
        var textBand = height - markBand;
        var nameBand = hasFooter ? textBand * 0.58f : textBand;
        var textScale = strip ? 1f : FooterTextScale;
        var nameSize = Math.Max(12f, nameBand * (theme == PartyPrintTheme.Event ? 0.78f : 0.62f) * textScale);
        var lineSize = Math.Max(9f, (textBand - nameBand) * 0.52f * textScale);
        var numberSize = Math.Max(10f, markBand * (strip ? StripNumberRowShare : FooterNumberRowShare));
        return new FooterSizes(textBand, nameBand, markBand, nameSize, lineSize,
            strip ? PartyPrintGeometry.StripWordmarkWidthFraction : PartyPrintGeometry.FooterWordmarkWidthFraction,
            numberSize);
    }

    /// <summary>
    /// The approved wordmark, scaled and placed — never redrawn, recoloured or
    /// stretched. The on-light artwork goes on light paper and the on-dark on
    /// dark, which is the whole reason both are shipped.
    /// </summary>
    private void DrawWordmark(Image<Rgba32> sheet, ThemePalette palette, Rectangle area, double widthFraction)
    {
        // BOTH files are the same lockup at the same proportions. That matters:
        // `nubarca-wordmark-on-light.png` is a DIFFERENT artwork — 1516x1024,
        // aspect 1.48 against the 3.56 of every wordmark — so fitting it into a
        // band made the light sheets render a visibly different size from the
        // dark ones. The `-480w` variant is the true counterpart.
        var file = Path.Combine(_assetRoot, "Assets", "brand",
            palette.DarkSurface
                ? "nubarca-wordmark-on-dark-960w.png"
                : "nubarca-wordmark-on-light-480w.png");
        // A missing brand asset is a broken build, not a sheet to print without
        // the mark: staying silent here is how the wrong artwork went unnoticed.
        if (!File.Exists(file))
        {
            throw new FileNotFoundException(
                "The approved wordmark is not published with the application; " +
                "the print renderer cannot compose a sheet without it.", file);
        }

        using var wordmark = Image.Load<Rgba32>(file);
        // Fit the band on BOTH axes and keep the artwork's proportions: sizing
        // by width alone put a 141px-tall lockup in a 47px band, and the sheet
        // edge cut it in half. The brand is never stretched to fit — it is
        // scaled until it fits.
        //
        // A quiet signature, not a headline. At 0.42 of the band it was the
        // loudest thing on a keepsake whose subject is the photograph; the
        // brand's 120px minimum rendered width is the floor it never goes below.
        var maxWidth = Math.Max(BrandMinWordmarkWidth, area.Width * widthFraction);
        var maxHeight = Math.Max(24, area.Height * WordmarkRowShare);
        var scale = Math.Min(maxWidth / wordmark.Width, maxHeight / wordmark.Height);
        var targetWidth = Math.Max(1, (int)Math.Round(wordmark.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(wordmark.Height * scale));
        // One scale, with a sharp filter: the lockup's small symbol is where a
        // softer one shows first.
        wordmark.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(targetWidth, targetHeight),
            Sampler = KnownResamplers.Lanczos3,
        }));

        // Bottom-left, on the same baseline the number sits on at the right.
        var y0 = area.Y + area.Height - targetHeight;
        sheet.Mutate(x => x.DrawImage(wordmark, new Point(area.X, Math.Max(area.Y, y0)), 1f));
    }

    /// <summary>
    /// The guest's queue number, bottom-right, opposite the wordmark.
    ///
    /// This is the same number their phone showed when the print was accepted,
    /// so a stack of sheets on the collection table can be matched to the people
    /// waiting for them without anybody reading a name off the paper.
    /// </summary>
    private void DrawSequence(
        Image<Rgba32> sheet, ThemePalette palette, Rectangle area, long sequence, float size)
    {
        if (sequence <= 0) return;
        var font = _display.CreateFont(size, FontStyle.Bold);
        sheet.Mutate(x => x.DrawText(
            new RichTextOptions(font)
            {
                Origin = new PointF(area.X + area.Width, area.Y + area.Height),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
            },
            $"#{sequence}", palette.Muted));
    }


    private static Image<Rgba32> LoadOriented(byte[] bytes)
    {
        var image = Image.Load<Rgba32>(bytes);
        // Honour the camera's orientation before anything measures the picture,
        // so a crop composed against what the guest saw means the same here.
        image.Mutate(x => x.AutoOrient());
        return image;
    }

    private static string Truncate(string value, int max)
    {
        var flat = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (flat.Length <= max) return flat;
        return flat[..(max - 1)].TrimEnd() + "…";
    }

    private sealed record ThemePalette(
        Rgba32 Background, Rgba32 Foreground, Rgba32 Muted, Rgba32 Accent,
        Rgba32 Edge, bool DarkSurface);

    private static ThemePalette Palette(PartyPrintTheme theme) => theme switch
    {
        PartyPrintTheme.Midnight => new ThemePalette(
            MidnightNavy, CloudWhite, new Rgba32(0xA9, 0xB4, 0xC8), CyanGlow,
            Edge: new Rgba32(0x00, 0xD4, 0xFF, 0x66), DarkSurface: true),
        PartyPrintTheme.Event => new ThemePalette(
            DeepBlue, CloudWhite, new Rgba32(0xA9, 0xB4, 0xC8), CyanGlow,
            Edge: new Rgba32(0x00, 0xD4, 0xFF, 0x4D), DarkSurface: true),
        _ => new ThemePalette(
            CloudWhite, Ink, new Rgba32(0x5A, 0x63, 0x74), CyanGlow,
            Edge: new Rgba32(0x0A, 0x0F, 0x1A, 0x1A), DarkSurface: false),
    };
}
