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
    /// A single photograph to the edges, the party's name and the NubArca symbol
    /// on it in white, over the invitation's scrim fading into Midnight Navy.
    /// </summary>
    OverlayWhite,
    /// <summary>The same in black, over a scrim fading into Cloud White.</summary>
    OverlayBlack,
}

/// <summary>One photograph and how it is framed, already validated.</summary>
public sealed record PartyPrintPhoto(
    byte[] Bytes, double CropX, double CropY, double CropWidth, double CropHeight);

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
    /// <summary>
    /// The printer cuts the strip sheet itself. The ticks that show a pair of
    /// scissors where to go would then sit exactly under the blade, and a cut a
    /// fraction of a millimetre off leaves one of them on a strip's edge.
    /// </summary>
    bool CutByPrinter = false,
    /// <summary>The printer's tone compensation, applied to the whole sheet. Null is neutral.</summary>
    PrintCalibration? Calibration = null);

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
    private static readonly Rgba32 MidnightNavy = new(0x0A, 0x0F, 0x1A);
    private static readonly Rgba32 DeepBlue = new(0x0F, 0x1E, 0x3A);
    private static readonly Rgba32 CyanGlow = new(0x00, 0xD4, 0xFF);
    private static readonly Rgba32 Ink = new(0x0A, 0x0F, 0x1A);

    /// <summary>Minimum rendered wordmark width, from the brand guidelines.</summary>
    private const int BrandMinWordmarkWidth = 120;

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
        using var sheet = composition.Product == Domain.Print.PartyPrintProducts.Strip4
            ? RenderStrip(composition)
            : RenderPhoto(composition);

        // The printer's compensation, not a filter: the whole sheet, paper and
        // photographs, so that this printer's output matches what was composed.
        (composition.Calibration ?? PrintCalibration.Neutral).ApplyTo(sheet);

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
        if (composition.Theme is PartyPrintTheme.OverlayWhite or PartyPrintTheme.OverlayBlack)
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
        var (w, h) = portrait
            ? (PartyPrintGeometry.PortraitWidth, PartyPrintGeometry.PortraitHeight)
            : (PartyPrintGeometry.LandscapeWidth, PartyPrintGeometry.LandscapeHeight);

        var sheet = new Image<Rgba32>(w, h);
        var palette = Palette(composition.Theme);
        sheet.Mutate(x => x.Fill(palette.Background));

        var margin = (int)Math.Round(PartyPrintGeometry.PhotoMarginFraction * Math.Min(w, h));
        // Short edge, not height: see PhotoFooterFraction. The sheet turns; the
        // strip of paper under the photograph must not.
        var footer = (int)Math.Round(PartyPrintGeometry.PhotoFooterFraction * Math.Min(w, h));
        var slot = new Rectangle(margin, margin, w - (2 * margin), h - (2 * margin) - footer);

        DrawFramed(sheet, source, photo, slot, composition.Theme, palette);
        DrawFooter(sheet, composition, palette,
            new Rectangle(margin, slot.Bottom, slot.Width, footer));
        return sheet;
    }

    // --- Four-photo strip, twice ------------------------------------------

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

                var stripW = PartyPrintGeometry.StripWidthFraction;
                var footTop = 1.0 - PartyPrintGeometry.StripMarginFraction
                    - PartyPrintGeometry.StripFooterFraction;
                var footX = PartyPrintGeometry.StripMarginFraction
                    + (strip * (stripW + PartyPrintGeometry.StripGutterFraction));
                DrawFooter(sheet, composition, palette, new Rectangle(
                    (int)Math.Round(footX * w), (int)Math.Round(footTop * h),
                    (int)Math.Round(stripW * w),
                    (int)Math.Round(PartyPrintGeometry.StripFooterFraction * h)));
            }

            if (!composition.CutByPrinter) DrawCutMarks(sheet, palette);
            return sheet;
        }
        finally
        {
            foreach (var (_, image) in sources) image.Dispose();
        }
    }

    // --- Single photograph, title on it ---------------------------------------

    /// <summary>
    /// The photograph to the edges of the sheet, and on it — never beside it —
    /// the NubArca symbol top-left and the party's name bottom-left, in white or
    /// black, over the scrim the party invitation lays over its cover. The
    /// scrim fades into the base colour opposite the ink, so either one reads.
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
        var (w, h) = portrait
            ? (PartyPrintGeometry.PortraitWidth, PartyPrintGeometry.PortraitHeight)
            : (PartyPrintGeometry.LandscapeWidth, PartyPrintGeometry.LandscapeHeight);

        var white = composition.Theme == PartyPrintTheme.OverlayWhite;
        var ink = white ? CloudWhite : Ink;
        var baseColour = white ? MidnightNavy : CloudWhite;

        var sheet = new Image<Rgba32>(w, h);
        DrawFramed(sheet, source, photo, new Rectangle(0, 0, w, h), composition.Theme,
            Palette(PartyPrintTheme.Pure));
        DrawScrim(sheet, baseColour);

        var shortEdge = Math.Min(w, h);
        var margin = (int)Math.Round(PartyPrintGeometry.OverlayMarginFraction * shortEdge);
        DrawSymbol(sheet, ink, new Point(margin, margin),
            (int)Math.Round(PartyPrintGeometry.OverlaySymbolFraction * shortEdge));
        DrawOverlayText(sheet, composition, ink, margin, shortEdge);
        return sheet;
    }

    private static void DrawScrim(Image<Rgba32> sheet, Rgba32 baseColour)
    {
        var values = PartyPrintGeometry.OverlayScrimStops;
        var stops = new ColorStop[values.Length / 2];
        for (var i = 0; i < stops.Length; i++)
        {
            var alpha = (byte)Math.Round(values[(i * 2) + 1] * 255);
            stops[i] = new ColorStop((float)values[i * 2],
                Color.FromPixel(new Rgba32(baseColour.R, baseColour.G, baseColour.B, alpha)));
        }
        var brush = new LinearGradientBrush(
            new PointF(0, 0), new PointF(0, sheet.Height), GradientRepetitionMode.None, stops);
        sheet.Mutate(x => x.Fill(brush));
    }

    /// <summary>
    /// The approved flat symbol in ONE solid colour — the brand's flat mark is
    /// exactly that geometry in solid colours — sized to <paramref name="size"/>.
    /// </summary>
    private void DrawSymbol(Image<Rgba32> sheet, Rgba32 ink, Point at, int size)
    {
        using var mark = Image.Load<Rgba32>(
            Path.Combine(_assetRoot, "Assets", "brand", "nubarca-mark-flat-on-dark-512.png"));
        mark.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgba32(ink.R, ink.G, ink.B, row[x].A);
            }
        });
        mark.Mutate(x => x.Resize(size, size));
        sheet.Mutate(x => x.DrawImage(mark, at, 1f));
    }

    private void DrawOverlayText(Image<Rgba32> sheet, PartyPrintComposition composition, Rgba32 ink,
        int margin, int shortEdge)
    {
        var (w, h) = (sheet.Width, sheet.Height);
        var soft = new Rgba32(ink.R, ink.G, ink.B, 0xD9);
        var lineSize = (float)(PartyPrintGeometry.OverlayLineFraction * shortEdge);
        var footer = Truncate(composition.FooterText ?? string.Empty, 60);
        var hasFooter = footer.Length > 0;
        var bottom = h - margin;

        // The guest's number, bottom-right, as on every other sheet.
        var numberWidth = 0f;
        if (composition.PublicSequence > 0)
        {
            var numberFont = _display.CreateFont(lineSize * 1.15f, FontStyle.Bold);
            var number = $"#{composition.PublicSequence}";
            numberWidth = TextMeasurer.MeasureSize(number, new TextOptions(numberFont)).Width;
            sheet.Mutate(x => x.DrawText(
                new RichTextOptions(numberFont)
                {
                    Origin = new PointF(w - margin, bottom),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                },
                number, soft));
        }

        if (hasFooter)
        {
            var lineFont = _ui.CreateFont(lineSize, FontStyle.Regular);
            sheet.Mutate(x => x.DrawText(
                new RichTextOptions(lineFont)
                {
                    Origin = new PointF(margin, bottom),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Bottom,
                },
                footer, soft));
            bottom -= (int)Math.Round(lineSize * 1.5f);
        }

        // The name takes what is left of the width, and shrinks rather than
        // running under the number or off the paper.
        var name = Truncate(composition.PartyName, 42);
        var available = w - (2 * margin) - (hasFooter ? 0 : numberWidth + (lineSize * 1.5f));
        var titleSize = (float)(PartyPrintGeometry.OverlayTitleFraction * shortEdge);
        var measured = TextMeasurer.MeasureSize(name, new TextOptions(_display.CreateFont(titleSize, FontStyle.Bold))).Width;
        if (measured > available && measured > 0) titleSize *= available / measured;
        var titleFont = _display.CreateFont(Math.Max(12f, titleSize), FontStyle.Bold);
        sheet.Mutate(x => x.DrawText(
            new RichTextOptions(titleFont)
            {
                Origin = new PointF(margin, bottom),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
            },
            name, ink));
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
        Rectangle slot, PartyPrintTheme theme, ThemePalette palette)
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

    private void DrawFooter(
        Image<Rgba32> sheet, PartyPrintComposition composition,
        ThemePalette palette, Rectangle area)
    {
        // Only three things may ever appear on the paper: the party's name, the
        // line the HOST configured, and the wordmark. A guest writes nothing —
        // which is what keeps a physical print free of arbitrary text.
        // Three things share this strip of paper — the party's name, the host's
        // line, the wordmark — so the area is DIVIDED between them rather than
        // each being placed at its own fraction, which is how the footer and the
        // wordmark ended up drawn on top of each other.
        var footer = Truncate(composition.FooterText ?? string.Empty, 60);
        var hasFooter = footer.Length > 0;

        var markBand = area.Height * 0.38f;
        var textBand = area.Height - markBand;
        var nameBand = hasFooter ? textBand * 0.58f : textBand;

        var nameSize = Math.Max(
            12f, nameBand * (composition.Theme == PartyPrintTheme.Event ? 0.78f : 0.62f));
        var nameFont = _display.CreateFont(nameSize, FontStyle.Bold);
        sheet.Mutate(x => x.DrawText(
            new RichTextOptions(nameFont)
            {
                Origin = new PointF(area.X + (area.Width / 2f), area.Y + (nameBand / 2f)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Truncate(composition.PartyName, 42), palette.Foreground));

        if (hasFooter)
        {
            var footBand = textBand - nameBand;
            var footFont = _ui.CreateFont(Math.Max(9f, footBand * 0.52f), FontStyle.Regular);
            sheet.Mutate(x => x.DrawText(
                new RichTextOptions(footFont)
                {
                    Origin = new PointF(
                        area.X + (area.Width / 2f), area.Y + nameBand + (footBand / 2f)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                footer, palette.Muted));
        }

        // The signature row: wordmark left, the guest's number right, sharing a
        // baseline so the foot of the sheet reads as one line rather than two
        // things that happen to be near each other.
        var markRow = new Rectangle(
            area.X, area.Y + (int)textBand, area.Width, (int)markBand);
        DrawWordmark(sheet, palette, markRow);
        DrawSequence(sheet, palette, markRow, composition.PublicSequence);
    }

    /// <summary>
    /// The approved wordmark, scaled and placed — never redrawn, recoloured or
    /// stretched. The on-light artwork goes on light paper and the on-dark on
    /// dark, which is the whole reason both are shipped.
    /// </summary>
    private void DrawWordmark(Image<Rgba32> sheet, ThemePalette palette, Rectangle area)
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
        var maxWidth = Math.Max(BrandMinWordmarkWidth, area.Width * 0.20);
        var maxHeight = Math.Max(24, area.Height * 0.80);
        var scale = Math.Min(maxWidth / wordmark.Width, maxHeight / wordmark.Height);
        var targetWidth = Math.Max(1, (int)Math.Round(wordmark.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(wordmark.Height * scale));
        wordmark.Mutate(x => x.Resize(targetWidth, targetHeight));

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
        Image<Rgba32> sheet, ThemePalette palette, Rectangle area, long sequence)
    {
        if (sequence <= 0) return;
        var font = _display.CreateFont(Math.Max(10f, area.Height * 0.34f), FontStyle.Bold);
        sheet.Mutate(x => x.DrawText(
            new RichTextOptions(font)
            {
                Origin = new PointF(area.X + area.Width, area.Y + area.Height),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
            },
            $"#{sequence}", palette.Muted));
    }

    /// <summary>
    /// Ticks at the ends of the gutter only. A dashed line down the middle
    /// would cross the photographs, which is what makes a strip look printed
    /// rather than made.
    /// </summary>
    private static void DrawCutMarks(Image<Rgba32> sheet, ThemePalette palette)
    {
        const int w = PartyPrintGeometry.PortraitWidth;
        const int h = PartyPrintGeometry.PortraitHeight;
        var centre = w / 2f;
        var length = (float)(PartyPrintGeometry.CutMarkLengthFraction * h);

        sheet.Mutate(x =>
        {
            x.DrawLine(palette.Muted, 2f, new PointF(centre, 0), new PointF(centre, length));
            x.DrawLine(palette.Muted, 2f, new PointF(centre, h - length), new PointF(centre, h));
        });
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
