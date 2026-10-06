using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Print;

public sealed class PrintArtifactRenderer
{
    public const int LandscapeWidth = 1800;
    public const int LandscapeHeight = 1200;

    /// <summary>The paper a zoomed-out photograph sits on: the sheet's own white.</summary>
    public static readonly Rgba32 OwnerBand = new(0xFF, 0xFF, 0xFF);

    /// <summary>Type size of the date, a fraction of the sheet's short edge: discreet, legible.</summary>
    public const double DateTypeFraction = 0.03;

    /// <summary>Its inset from the visible photograph's bottom-right corner, short-edge fraction.</summary>
    public const double DateInsetFraction = 0.035;

    private readonly string _assetRoot;
    private readonly Lazy<FontFamily> _dateFont;

    public PrintArtifactRenderer(string? assetRoot = null)
    {
        _assetRoot = assetRoot ?? AppContext.BaseDirectory;
        _dateFont = new Lazy<FontFamily>(() =>
            new FontCollection().Add(Path.Combine(_assetRoot, "Assets", "fonts", "Exo2-Medium.ttf")));
    }

    private static readonly IReadOnlyDictionary<char, string> Glyphs = new Dictionary<char, string>
    {
        ['A']="01110100011000111111100011000110001", ['B']="11110100011000111110100011000111110",
        ['C']="01111100001000010000100001000001111", ['D']="11110100011000110001100011000111110",
        ['E']="11111100001000011110100001000011111", ['F']="11111100001000011110100001000010000",
        ['G']="01111100001000010111100011000101111", ['H']="10001100011000111111100011000110001",
        ['I']="11111001000010000100001000010011111", ['J']="00111000100001000010100101001001100",
        ['K']="10001100101010011000101001001010001", ['L']="10000100001000010000100001000011111",
        ['M']="10001110111010110101100011000110001", ['N']="10001110011010110011100011000110001",
        ['O']="01110100011000110001100011000101110", ['P']="11110100011000111110100001000010000",
        ['Q']="01110100011000110001101011001001101", ['R']="11110100011000111110101001001010001",
        ['S']="01111100001000001110000010000111110", ['T']="11111001000010000100001000010000100",
        ['U']="10001100011000110001100011000101110", ['V']="10001100011000110001100010101000100",
        ['W']="10001100011000110101101011101110001", ['X']="10001100010101000100010101000110001",
        ['Y']="10001100010101000100001000010000100", ['Z']="11111000010001000100010001000011111",
        ['0']="01110100011001110101110011000101110", ['1']="00100011000010000100001000010001110",
        ['2']="01110100010000100010001000100011111", ['3']="11110000010000101110000010000111110",
        ['4']="00010001100101010010111110001000010", ['5']="11111100001000011110000010000111110",
        ['6']="01110100001000011110100011000101110", ['7']="11111000010001000100010000100001000",
        ['8']="01110100011000101110100011000101110", ['9']="01110100011000101111000010000101110",
        ['-']="00000000000000011111000000000000000", [':']="00000001000000000000001000000000000",
        ['.']="00000000000000000000000000011000110", ['/']="00001000100001000100010001000010000",
        [' ']="00000000000000000000000000000000000",
    };

    /// <summary>
    /// The test page: who printed it, and a calibration strip — an 11-step grey
    /// wedge from black to white and eight colour and skin patches — so the
    /// printer's tone can be judged at a glance and compared after each change.
    /// The printer's calibration is applied, exactly as to a guest's sheet.
    /// </summary>
    public async Task<byte[]> RenderDiagnosticAsync(
        string stationName, string printerModel, DateTime now, string format,
        string shortCode, CancellationToken cancellationToken, PrintCalibration? calibration = null)
    {
        using var image = new Image<Rgb24>(LandscapeWidth, LandscapeHeight, new Rgb24(248, 250, 252));
        DrawBand(image, 0, 0, LandscapeWidth, 210, new Rgb24(8, 46, 73));
        DrawText(image, "NUBARCA PRINT STATION", 100, 70, 14, new Rgb24(255, 255, 255));
        DrawText(image, stationName.ToUpperInvariant(), 110, 330, 11, new Rgb24(8, 46, 73));
        DrawText(image, $"PRINTER {printerModel}".ToUpperInvariant(), 110, 500, 8, new Rgb24(30, 64, 87));
        DrawText(image, $"DATE {now:yyyy-MM-dd HH:mm} UTC".ToUpperInvariant(), 110, 635, 7, new Rgb24(30, 64, 87));
        DrawText(image, $"FORMAT {format}".ToUpperInvariant(), 110, 760, 7, new Rgb24(30, 64, 87));
        DrawText(image, $"JOB {shortCode}".ToUpperInvariant(), 110, 885, 9, new Rgb24(8, 104, 147));
        DrawCalibrationStrip(image);
        (calibration ?? PrintCalibration.Neutral).ApplyTo(image);
        using var output = new MemoryStream();
        await image.SaveAsPngAsync(output, cancellationToken);
        return output.ToArray();
    }

    /// <summary>Where the grey wedge and the patches sit, for the test page and its tests.</summary>
    public const int WedgeTop = 980, WedgeHeight = 90, PatchTop = 1090, PatchHeight = 90;
    public const int StripLeft = 110, StripWidth = LandscapeWidth - 220;
    public const int WedgeSteps = 11;

    public static readonly Rgb24[] Patches =
    [
        new(220, 40, 40), new(40, 170, 70), new(40, 80, 200), new(0, 170, 220),
        new(210, 40, 150), new(245, 210, 40), new(240, 200, 175), new(190, 140, 105),
    ];

    private static void DrawCalibrationStrip(Image<Rgb24> image)
    {
        var step = StripWidth / WedgeSteps;
        for (var i = 0; i < WedgeSteps; i++)
        {
            var level = (byte)Math.Round(255.0 * i / (WedgeSteps - 1));
            DrawBand(image, StripLeft + (i * step), WedgeTop, step, WedgeHeight, new Rgb24(level, level, level));
        }
        var patch = StripWidth / Patches.Length;
        for (var i = 0; i < Patches.Length; i++)
            DrawBand(image, StripLeft + (i * patch), PatchTop, patch - 8, PatchHeight, Patches[i]);
    }

    /// <summary>
    /// A whole photograph on a 10x15 that follows its shape, on white — the
    /// owner print at its most basic, kept for its callers and drawn by it.
    /// </summary>
    public async Task<byte[]> RenderPhoto10x15Async(
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        var info = Image.Identify(source.Span);
        var (w, h) = Oriented(info);
        var portrait = h > w;
        var (sheetW, sheetH) = PrintLayouts.Sheet(Domain.Print.PrintPapers.Photo10x15, portrait);
        return await RenderOwnerPhotoAsync(new OwnerPhotoComposition(
            source.ToArray(), Domain.Print.PrintPapers.Photo10x15, portrait,
            new PhotoPlacement(0.5, 0.5,
                PhotoPlacementGeometry.ContainZoom((double)w / h, (double)sheetW / sheetH))), cancellationToken);
    }

    /// <summary>
    /// An owner's own photograph printed on its own: the loaded paper at 300dpi,
    /// standing or lying, the photograph placed by the shared framing (filled,
    /// zoomed in, or zoomed out onto white), and — only when asked — the
    /// photograph's date, small, bottom-right ON the photograph, never on a
    /// band. No brand, no frame, no footer, no number. The printer's
    /// calibration is applied as to every sheet; no metadata leaves with it.
    /// </summary>
    public async Task<byte[]> RenderOwnerPhotoAsync(
        OwnerPhotoComposition composition, CancellationToken cancellationToken)
    {
        using var source = Image.Load<Rgba32>(composition.Bytes);
        source.Mutate(x => x.AutoOrient());
        var (w, h) = PrintLayouts.Sheet(composition.Paper, composition.Portrait);
        using var sheet = new Image<Rgba32>(w, h, OwnerBand);
        var frame = new Rectangle(0, 0, w, h);
        PartyPrintComposer.DrawPlaced(sheet, source, composition.Placement, frame, OwnerBand);

        if (composition.DateText is { Length: > 0 } text)
        {
            var placed = PhotoPlacementGeometry.Place(
                (double)source.Width / source.Height, (double)w / h, composition.Placement);
            DrawDate(sheet, text, PhotoPlacementGeometry.Visible(placed));
        }

        (composition.Calibration ?? PrintCalibration.Neutral).ApplyTo(sheet);
        sheet.Metadata.ExifProfile = null;
        sheet.Metadata.XmpProfile = null;
        sheet.Metadata.IptcProfile = null;
        sheet.Metadata.IccProfile = null;
        using var output = new MemoryStream();
        await sheet.SaveAsJpegAsync(
            output, new JpegEncoder { Quality = 94, ColorType = JpegEncodingColor.YCbCrRatio444 }, cancellationToken);
        return output.ToArray();
    }

    /// <summary>
    /// Where the date's text box ends, in sheet pixels: inset from the visible
    /// photograph's bottom-right corner. Public so a test can find it.
    /// </summary>
    public static PointF DateAnchor(int sheetWidth, int sheetHeight, FrameRect visible)
    {
        var inset = DateInsetFraction * Math.Min(sheetWidth, sheetHeight);
        return new PointF(
            (float)((visible.Right * sheetWidth) - inset),
            (float)((visible.Bottom * sheetHeight) - inset));
    }

    /// <summary>White type with a soft dark halo — no box, nothing opaque behind it.</summary>
    private void DrawDate(Image<Rgba32> sheet, string text, FrameRect visible)
    {
        var shortEdge = Math.Min(sheet.Width, sheet.Height);
        var font = _dateFont.Value.CreateFont((float)(DateTypeFraction * shortEdge));
        var anchor = DateAnchor(sheet.Width, sheet.Height, visible);
        RichTextOptions Options() => new(font)
        {
            Origin = anchor,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        using (var halo = new Image<Rgba32>(sheet.Width, sheet.Height, new Rgba32(0, 0, 0, 0)))
        {
            halo.Mutate(x => x.DrawText(Options(), text, new Rgba32(0, 0, 0, 255))
                .GaussianBlur((float)(0.004 * shortEdge)));
            sheet.Mutate(x => x.DrawImage(halo, new Point(0, 0), 0.55f));
        }
        sheet.Mutate(x => x.DrawText(Options(), text, new Rgba32(0xFF, 0xFF, 0xFF, 0xFF)));
    }

    /// <summary>A photograph's displayed size: its coded size, turned for a quarter-turn EXIF orientation.</summary>
    private static (int Width, int Height) Oriented(ImageInfo info)
    {
        var orientation = info.Metadata.ExifProfile?.TryGetValue(
            SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, out var value) == true
            ? value!.Value : (ushort)1;
        return orientation is >= 5 and <= 8 ? (info.Height, info.Width) : (info.Width, info.Height);
    }

    private static void DrawText(Image<Rgb24> image, string text, int x, int y, int scale, Rgb24 color)
    {
        var cursor = x;
        foreach (var raw in text)
        {
            var ch = char.ToUpperInvariant(raw);
            var glyph = Glyphs.GetValueOrDefault(ch, Glyphs[' ']);
            for (var row = 0; row < 7; row++)
            for (var column = 0; column < 5; column++)
            {
                if (glyph[row * 5 + column] != '1') continue;
                DrawBand(image, cursor + column * scale, y + row * scale, scale, scale, color);
            }
            cursor += 6 * scale;
            if (cursor >= image.Width - 6 * scale) break;
        }
    }

    private static void DrawBand(Image<Rgb24> image, int x, int y, int width, int height, Rgb24 color)
    {
        var maxX = Math.Min(image.Width, x + width);
        var maxY = Math.Min(image.Height, y + height);
        image.ProcessPixelRows(accessor =>
        {
            for (var py = Math.Max(0, y); py < maxY; py++)
            {
                var row = accessor.GetRowSpan(py);
                for (var px = Math.Max(0, x); px < maxX; px++) row[px] = color;
            }
        });
    }
}
