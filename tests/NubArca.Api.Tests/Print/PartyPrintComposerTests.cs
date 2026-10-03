using NubArca.Api.Domain.Print;
using NubArca.Api.Print;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// The renderer is judged on two things a test can check — the sheet has the
/// geometry and carries none of the sources' metadata — and one it cannot: that
/// the print is beautiful. The artifacts this writes out are for that second
/// judgement, which is a person's to make.
/// </summary>
public sealed class PartyPrintComposerTests
{
    private static readonly (byte R, byte G, byte B)[] Fixtures =
    [
        (0xC9, 0x76, 0x2F), (0x2F, 0x5F, 0xC9), (0x8A, 0x4A, 0x7A), (0x3F, 0x7A, 0x5A),
        (0xB8, 0x3A, 0x3A), (0x3A, 0xA8, 0xB8), (0xC8, 0xB0, 0x3A), (0x5A, 0x3A, 0xB8),
    ];

    /// <summary>A recognisable stand-in photograph: a gradient plus a disc, so a
    /// crop or a flipped orientation is visible rather than plausible.</summary>
    private static byte[] Fixture(int index, int width = 1400, int height = 1000)
    {
        var (r, g, b) = Fixtures[index % Fixtures.Length];
        using var image = new Image<Rgba32>(width, height);
        image.Mutate(x =>
        {
            x.Fill(new Rgba32(r, g, b));
            x.Fill(new Rgba32((byte)(255 - r), (byte)(255 - g), (byte)(255 - b)),
                new RectangleF(width * 0.62f, height * 0.10f, width * 0.28f, height * 0.28f));
            x.Fill(new Rgba32(0xFF, 0xFF, 0xFF, 0x50),
                new RectangleF(0, height * 0.72f, width, height * 0.28f));
        });
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    [Fact]
    public void The_Footer_Is_The_Same_Strip_Of_Paper_Whichever_Way_The_Sheet_Faces()
    {
        // A landscape print came back reading as if it had no party name. The
        // footer was a fraction of the sheet HEIGHT, and the height is exactly
        // what flips when the sheet follows the photograph — so the band came
        // out a third shorter on the widest sheets, with the type shrinking
        // inside it. Both sheets are 10cm on the short edge; the strip of paper
        // under the picture must be the same on both.
        var portrait = PartyPrintGeometry.PhotoFooterFraction
            * Math.Min(PartyPrintGeometry.PortraitWidth, PartyPrintGeometry.PortraitHeight);
        var landscape = PartyPrintGeometry.PhotoFooterFraction
            * Math.Min(PartyPrintGeometry.LandscapeWidth, PartyPrintGeometry.LandscapeHeight);
        Assert.Equal(portrait, landscape);
        // And large enough to hold three things legibly: ~17mm at 300dpi.
        Assert.InRange(portrait, 190, 220);
    }

    [Fact]
    public void Both_Wordmarks_Are_The_Same_Lockup()
    {
        // The light sheets rendered a visibly different size from the dark ones
        // because `nubarca-wordmark-on-light.png` is a DIFFERENT artwork —
        // 1516x1024 against the 960x269 of the wordmark — and fitting a squarer
        // image into a wide band leaves it bounded by height instead of width.
        // The two files the renderer reaches for must be the same lockup.
        var root = AppContext.BaseDirectory;
        using var dark = Image.Load<Rgba32>(
            Path.Combine(root, "Assets", "brand", "nubarca-wordmark-on-dark-960w.png"));
        using var light = Image.Load<Rgba32>(
            Path.Combine(root, "Assets", "brand", "nubarca-wordmark-on-light-480w.png"));

        var darkAspect = (double)dark.Width / dark.Height;
        var lightAspect = (double)light.Width / light.Height;
        // Within a couple of percent: the exported PNGs round their pixel
        // dimensions slightly differently, and this is hunting for a different
        // ARTWORK (1.48 against 3.56), not for that rounding.
        Assert.InRange(lightAspect / darkAspect, 0.98, 1.02);
    }

    [Fact]
    public async Task The_Guests_Number_Is_Printed_On_The_Sheet_And_Nothing_Is_Printed_Without_One()
    {
        // The number on the paper is the number their phone showed, so a stack
        // of sheets can be matched to the people waiting without reading a name
        // off anybody's print.
        var composer = new PartyPrintComposer();
        var numbered = await composer.RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.Photo, PartyPrintTheme.Pure,
            [new PartyPrintPhoto(Fixture(0), 0, 0, 1, 1)], "Festa", null, 41), default);
        var unnumbered = await composer.RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.Photo, PartyPrintTheme.Pure,
            [new PartyPrintPhoto(Fixture(0), 0, 0, 1, 1)], "Festa", null, 0), default);

        // A preview or a test page has no queue number, and prints none rather
        // than a misleading "#0".
        Assert.NotEqual(numbered.Length, unnumbered.Length);
    }

    [Fact]
    public async Task The_Sheet_Follows_The_Photograph_Unless_The_Guest_Turns_It()
    {
        var composer = new PartyPrintComposer();

        // Default: a wide picture gets a wide sheet, rather than a portrait one
        // with white bars beside it.
        var followed = await composer.RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.Photo, PartyPrintTheme.Pure,
            [new PartyPrintPhoto(Fixture(0, 1600, 1000), 0, 0, 1, 1)],
            "Festa", null), default);
        using (var image = Image.Load(followed))
        {
            Assert.True(image.Width > image.Height);
        }

        // Turned: the guest asked for the other one, and the crop editor is what
        // makes a portrait subject work in a landscape frame.
        var turned = await composer.RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.Photo, PartyPrintTheme.Pure,
            [new PartyPrintPhoto(Fixture(0, 1600, 1000), 0, 0, 1, 1)],
            "Festa", null, 0, PartyPrintOrientation.Portrait), default);
        using (var image = Image.Load(turned))
        {
            Assert.True(image.Height > image.Width);
        }
    }

    [Fact]
    public async Task Turning_The_Sheet_Does_Nothing_To_A_Strip()
    {
        // Two strips side by side IS the product. Honouring an orientation here
        // would not turn a picture, it would destroy what a strip is.
        var composer = new PartyPrintComposer();
        var photos = Enumerable.Range(0, 4)
            .Select(i => new PartyPrintPhoto(Fixture(i), 0, 0, 1, 1)).ToList();
        var asked = await composer.RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.TwinStrip4, PartyPrintTheme.Pure, photos,
            "Festa", null, 0, PartyPrintOrientation.Landscape), default);
        using var sheet = Image.Load(asked);
        Assert.Equal(PartyPrintGeometry.PortraitWidth, sheet.Width);
        Assert.Equal(PartyPrintGeometry.PortraitHeight, sheet.Height);
    }

    private static PartyPrintComposition Composition(
        string product, PartyPrintTheme theme, int photos, string? footer = "Una notte da ricordare")
        => new(product, theme,
            Enumerable.Range(0, photos)
                .Select(i => new PartyPrintPhoto(Fixture(i), 0, 0, 1, 1))
                .ToList(),
            "Giulia & Matteo", footer);

    private static string ArtifactDir()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "print-artifacts");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Strip_Is_A_Portrait_Sheet_Carrying_Two_Different_Strips()
    {
        var composer = new PartyPrintComposer();
        var bytes = await composer.RenderAsync(
            Composition(PartyPrintProducts.TwinStrip4, PartyPrintTheme.Pure, 8), default);

        using var sheet = Image.Load<Rgba32>(bytes);
        // One 10x15 sheet, portrait — not a new paper size.
        Assert.Equal(PartyPrintGeometry.PortraitWidth, sheet.Width);
        Assert.Equal(PartyPrintGeometry.PortraitHeight, sheet.Height);

        // Eight slots, eight DIFFERENT photographs: the first strip carries
        // photographs 1–4 in order, the second 5–8. Two copies of one strip is
        // exactly what a guest who chose eight did not ask for.
        var samples = Enumerable.Range(0, PartyPrintGeometry.StripsPerSheet)
            .SelectMany(strip => Enumerable.Range(0, PartyPrintGeometry.SlotsPerStrip)
                .Select(slot => SampleSlot(sheet, strip, slot)))
            .ToList();
        Assert.Equal(8, samples.Distinct().Count());
        for (var slot = 0; slot < PartyPrintGeometry.SlotsPerStrip; slot++)
            Assert.NotEqual(SampleSlot(sheet, 0, slot), SampleSlot(sheet, 1, slot));
    }

    [Fact]
    public async Task Four_Photographs_Still_Make_A_Sheet_Of_Two_Copies()
    {
        // A client from before eight: nothing breaks, the strip is repeated.
        var bytes = await new PartyPrintComposer().RenderAsync(
            Composition(PartyPrintProducts.TwinStrip4, PartyPrintTheme.Pure, 4), default);
        using var sheet = Image.Load<Rgba32>(bytes);
        for (var slot = 0; slot < PartyPrintGeometry.SlotsPerStrip; slot++)
            Assert.Equal(SampleSlot(sheet, 0, slot), SampleSlot(sheet, 1, slot));
    }

    // --- The title on the photo ---------------------------------------------

    /// <summary>
    /// A photograph the size of the sheet, stored losslessly, so the only thing
    /// between it and the print is the renderer (and the JPEG it writes).
    /// </summary>
    private static byte[] SheetPhoto(string kind, bool portrait = true)
    {
        var (w, h) = portrait
            ? (PartyPrintGeometry.PortraitWidth, PartyPrintGeometry.PortraitHeight)
            : (PartyPrintGeometry.LandscapeWidth, PartyPrintGeometry.LandscapeHeight);
        using var image = new Image<Rgba32>(w, h);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var (u, v) = ((double)x / w, (double)y / h);
                    row[x] = kind switch
                    {
                        "grey" => new Rgba32(128, 128, 128),
                        "light" => new Rgba32((byte)(225 + (25 * u)), (byte)(228 + (20 * v)), (byte)(232 + (15 * u))),
                        "dark" => new Rgba32((byte)(12 + (30 * u)), (byte)(14 + (24 * v)), (byte)(20 + (26 * u))),
                        // Every hue across, bright to deep down: a party's lights.
                        _ => Hue(u, 0.35 + (0.6 * v)),
                    };
                }
            }
        });
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();

        static Rgba32 Hue(double h, double value)
        {
            var sector = h * 6;
            var f = sector - Math.Floor(sector);
            (double R, double G, double B) c = ((int)sector % 6) switch
            {
                0 => (1, f, 0), 1 => (1 - f, 1, 0), 2 => (0, 1, f),
                3 => (0, 1 - f, 1), 4 => (f, 0, 1), _ => (1, 0, 1 - f),
            };
            var (r, g, b) = c;
            return new Rgba32((byte)(r * 255 * value), (byte)(g * 255 * value), (byte)(b * 255 * value));
        }
    }

    private static PartyPrintComposition OnThePhoto(
        byte[] photo, PartyPrintOverlayText text, PartyPrintOverlayLogo logo,
        string? footer = "Una notte da ricordare", long number = 27,
        string name = "Giulia & Matteo")
        => new(PartyPrintProducts.Photo, PartyPrintTheme.Overlay,
            [new PartyPrintPhoto(photo, 0, 0, 1, 1)], name, footer, number,
            Overlay: new PartyPrintOverlay(text, logo));

    /// <summary>The square the symbol stands in: on the name's line, just before the name.</summary>
    private static Rectangle SymbolBox(PartyPrintComposition composition, Image<Rgba32> sheet)
    {
        var symbol = new PartyPrintComposer().LayoutOverlayText(composition, sheet.Width, sheet.Height).Symbol;
        return new Rectangle((int)symbol.X, (int)symbol.Y, (int)symbol.Width, (int)symbol.Height);
    }

    private static int Distance(Rgba32 a, Rgba32 b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    /// <summary>Whether any pixel inside <paramref name="box"/> matches <paramref name="test"/>.</summary>
    private static bool Any(Image<Rgba32> sheet, RectangleF box, Func<Rgba32, bool> test)
    {
        for (var y = (int)box.Top; y < (int)box.Bottom; y += 2)
            for (var x = (int)box.Left; x < (int)box.Right; x += 2)
                if (test(sheet[x, y])) return true;
        return false;
    }

    private static bool IsWhite(Rgba32 p) => p.R > 225 && p.G > 225 && p.B > 225;
    private static bool IsBlack(Rgba32 p) => p.R < 35 && p.G < 35 && p.B < 45;
    private static bool IsRed(Rgba32 p) => p.R > 170 && p.G < 70 && p.B < 80;
    private static bool IsElectricBlue(Rgba32 p) => p.R < 70 && p.G is > 70 and < 140 && p.B > 215;
    private static bool IsCyan(Rgba32 p) => p.R < 60 && p.G > 180 && p.B > 215;

    private const string LongLine = "Grazie a tutti di essere venuti, è stata una notte che ricorderemo";

    private static (int W, int H) Sheet(bool portrait) => portrait
        ? (PartyPrintGeometry.PortraitWidth, PartyPrintGeometry.PortraitHeight)
        : (PartyPrintGeometry.LandscapeWidth, PartyPrintGeometry.LandscapeHeight);

    [Theory]
    [InlineData(true, "Una notte da ricordare")]
    [InlineData(false, "Una notte da ricordare")]
    [InlineData(true, null)]
    [InlineData(false, null)]
    public async Task The_Title_On_The_Photo_Leaves_The_Photograph_Itself_Untouched(bool portrait, string? footer)
    {
        // The invitation's scrim once lay over the whole print, and at the foot
        // it replaced the photograph with navy. Now everything above the words'
        // support — except the symbol — IS the photograph, to the very edges,
        // which is what full bleed means.
        var source = SheetPhoto("colourful", portrait);
        using var original = Image.Load<Rgba32>(source);
        var composer = new PartyPrintComposer();
        var composition = OnThePhoto(source, PartyPrintOverlayText.White, PartyPrintOverlayLogo.Light, footer);
        using var sheet = Image.Load<Rgba32>(await composer.RenderAsync(composition, default));
        Assert.Equal((original.Width, original.Height), (sheet.Width, sheet.Height));

        var symbol = SymbolBox(composition, sheet);
        var supportTop = (int)PartyPrintComposer.OverlayTextSupportTop(
            composer.LayoutOverlayText(composition, sheet.Width, sheet.Height), sheet.Width, sheet.Height);

        var worst = 0;
        var compared = 0;
        for (var y = 0; y < supportTop; y += 3)
            for (var x = 0; x < sheet.Width; x += 3)
            {
                if (symbol.Contains(x, y)) continue;
                worst = Math.Max(worst, Distance(sheet[x, y], original[x, y]));
                compared++;
            }
        // Only the JPEG's own rounding: no tint, no fade, no scrim — and no
        // letter or halo either, which all lie below the support's top.
        Assert.True(compared > 150_000);
        Assert.True(worst <= 12, $"the photograph changed by {worst} above the words' support");

        // The edges are the photograph's own, top and sides.
        for (var x = 0; x < sheet.Width; x += 5)
            Assert.True(Distance(sheet[x, 0], original[x, 0]) <= 12, $"top edge at {x}");
        for (var y = 0; y < supportTop; y += 5)
        {
            Assert.True(Distance(sheet[0, y], original[0, y]) <= 12, $"left edge at {y}");
            Assert.True(Distance(sheet[sheet.Width - 1, y], original[sheet.Width - 1, y]) <= 12, $"right edge at {y}");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_Support_Is_As_Tall_As_The_Words_And_No_Taller(bool portrait)
    {
        // Not a fixed band of the photograph: it begins just above the real
        // block of text, so what it covers follows what is written.
        var composer = new PartyPrintComposer();
        var (w, h) = Sheet(portrait);
        var shortEdge = Math.Min(w, h);
        var padding = PartyPrintGeometry.OverlayTextSupportPaddingFraction * shortEdge;
        Assert.InRange(PartyPrintGeometry.OverlayTextSupportPaddingFraction, 0.02, 0.03);

        (float Top, PartyPrintComposer.OverlayTextLayout Layout) Support(string name, string? footer, long number)
        {
            var layout = composer.LayoutOverlayText(OnThePhoto([], PartyPrintOverlayText.White,
                PartyPrintOverlayLogo.Light, footer, number, name), w, h);
            return (PartyPrintComposer.OverlayTextSupportTop(layout, w, h), layout);
        }

        var bare = Support("Marta 50", null, 7);
        var withLine = Support("Marta 50", "Una notte da ricordare", 27);
        var longest = Support("Giulia & Matteo", LongLine, 12345);

        foreach (var (top, layout) in new[] { bare, withLine, longest })
        {
            var boxes = new[] { layout.Title, layout.Footer, layout.Number }
                .Where(x => x is not null).Select(x => x!.Box).Append(layout.Symbol).ToList();
            // Every word — and the symbol on the name's line — inside it, with
            // the padding above the highest...
            foreach (var box in boxes) Assert.True(top <= box.Top - padding + 0.5f, $"{box} is outside the support");
            // ...and not a line more: it starts exactly one padding above the text.
            Assert.Equal(boxes.Min(b => b.Top) - padding, top, 1.0);
        }

        // A short name alone takes well under the old fixed fifth.
        Assert.True((h - bare.Top) / h < 0.2, $"the bare support is {(h - bare.Top) / h:P0} of the sheet");
        // The host's line makes it taller, by the line.
        Assert.True(withLine.Top < bare.Top - (PartyPrintGeometry.OverlayLineFraction * shortEdge * 0.5));
        // And a five-digit number sits inside it.
        Assert.True(longest.Top <= longest.Layout.Number!.Box.Top - padding + 0.5f);
    }

    [Theory]
    [InlineData(PartyPrintOverlayText.White)]
    [InlineData(PartyPrintOverlayText.Red)]
    [InlineData(PartyPrintOverlayText.Black)]
    public async Task The_Words_Support_Is_A_Whisper_Behind_The_Words_Only(PartyPrintOverlayText text)
    {
        // A mid-grey photograph makes the support readable pixel by pixel. The
        // column is inside the right margin, clear of the number and its halo.
        var composer = new PartyPrintComposer();
        var composition = OnThePhoto(SheetPhoto("grey"), text, PartyPrintOverlayLogo.Light);
        using var sheet = Image.Load<Rgba32>(await composer.RenderAsync(composition, default));
        var x = sheet.Width - 4;
        var start = PartyPrintComposer.OverlayTextSupportTop(
            composer.LayoutOverlayText(composition, sheet.Width, sheet.Height), sheet.Width, sheet.Height);

        // Nothing at all above where it starts.
        for (var y = 0; y < (int)start - 2; y += 4)
            Assert.InRange(sheet[x, y].R, 125, 131);

        // Toward the foot it leans — black under white or red words, white under
        // black ones — never more than a quarter of the way, never solid.
        var foot = sheet[x, sheet.Height - 1].R;
        var strongest = PartyPrintGeometry.OverlayTextSupportMaxOpacity;
        Assert.InRange(strongest, 0.15, 0.25);
        if (text == PartyPrintOverlayText.Black)
        {
            var expected = 128 + ((245 - 128) * strongest);
            Assert.InRange(foot, expected - 4, expected + 4);
            Assert.True(foot < 128 + ((245 - 128) * 0.25) + 2, $"white support reached {foot}");
        }
        else
        {
            var expected = 128 - ((128 - 10) * strongest);
            Assert.InRange(foot, expected - 4, expected + 4);
            Assert.True(foot > 128 - ((128 - 10) * 0.25) - 2, $"dark support reached {foot}");
        }

        // It grows smoothly from nothing: halfway down it is about half.
        var middle = sheet[x, (int)((start + sheet.Height) / 2)].R;
        Assert.InRange(middle, Math.Min(128, (int)foot) - 1, Math.Max(128, (int)foot) + 1);

        // Above its top the grey is grey everywhere but the symbol: no letter,
        // and no halo, escapes the support.
        var symbol = SymbolBox(composition, sheet);
        for (var y = 0; y < (int)start - 1; y += 2)
            for (var px = 0; px < sheet.Width; px += 2)
                if (!symbol.Contains(px, y))
                    Assert.True(Math.Abs(sheet[px, y].R - 128) <= 4, $"({px},{y}) is {sheet[px, y]}");
    }

    [Theory]
    [InlineData(PartyPrintOverlayText.White, PartyPrintOverlayLogo.Light)]
    [InlineData(PartyPrintOverlayText.Black, PartyPrintOverlayLogo.Dark)]
    [InlineData(PartyPrintOverlayText.Red, PartyPrintOverlayLogo.Light)]
    [InlineData(PartyPrintOverlayText.Red, PartyPrintOverlayLogo.Dark)]
    [InlineData(PartyPrintOverlayText.White, PartyPrintOverlayLogo.Dark)]
    [InlineData(PartyPrintOverlayText.Black, PartyPrintOverlayLogo.Light)]
    public async Task The_Words_And_The_Symbol_Take_Their_Own_Colours(
        PartyPrintOverlayText text, PartyPrintOverlayLogo logo)
    {
        var composer = new PartyPrintComposer();
        var composition = OnThePhoto(SheetPhoto("grey"), text, logo);
        using var sheet = Image.Load<Rgba32>(await composer.RenderAsync(composition, default));
        var layout = composer.LayoutOverlayText(composition, sheet.Width, sheet.Height);

        Func<Rgba32, bool> ink = text switch
        {
            PartyPrintOverlayText.Black => IsBlack,
            PartyPrintOverlayText.Red => IsRed,
            _ => IsWhite,
        };
        // Name, host's line and number are all in the words' colour.
        Assert.True(Any(sheet, layout.Title.Box, ink), $"the name is not {text}");
        Assert.True(Any(sheet, layout.Footer!.Box, ink), $"the host's line is not {text}");
        Assert.True(Any(sheet, layout.Number!.Box, ink), $"the number is not {text}");

        // The symbol keeps its own treatment, whatever the words chose: the
        // brand's light mark (Cloud White, Cyan, Electric Blue) or its dark one
        // (Midnight Navy, Electric Blue) — coloured, never one flat fill — and
        // never red: red is a colour for words on a photograph, not the brand's.
        var symbol = SymbolBox(composition, sheet);
        Assert.True(Any(sheet, symbol, IsElectricBlue), $"the {logo} symbol lost its Electric Blue");
        if (logo == PartyPrintOverlayLogo.Light)
        {
            Assert.True(Any(sheet, symbol, IsWhite), "the light symbol has no Cloud White");
            Assert.True(Any(sheet, symbol, IsCyan), "the light symbol has no Cyan");
            Assert.False(Any(sheet, symbol, IsBlack), "the light symbol took the dark one's navy");
        }
        else
        {
            Assert.True(Any(sheet, symbol, IsBlack), "the dark symbol has no Midnight Navy");
            Assert.False(Any(sheet, symbol, IsWhite), "the dark symbol took the light one's white");
            Assert.False(Any(sheet, symbol, IsCyan), "the dark symbol took the light one's cyan");
        }
        Assert.False(Any(sheet, symbol, IsRed), "the symbol turned red");
    }

    [Theory]
    [InlineData(PartyPrintOverlayLogo.Light)]
    [InlineData(PartyPrintOverlayLogo.Dark)]
    public async Task The_Symbol_Is_The_Brand_Artwork_As_Shipped(PartyPrintOverlayLogo logo)
    {
        // Not the mark's outline refilled with one colour: the approved file for
        // the treatment, scaled once, laid on the photograph as it is.
        var composition = OnThePhoto(SheetPhoto("grey"), PartyPrintOverlayText.White, logo);
        using var sheet = Image.Load<Rgba32>(await new PartyPrintComposer().RenderAsync(composition, default));
        var box = SymbolBox(composition, sheet);
        using var mark = Image.Load<Rgba32>(Path.Combine(
            AppContext.BaseDirectory, "Assets", "brand", PartyPrintComposer.SymbolFile(logo)));
        mark.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(box.Width, box.Height),
            Sampler = KnownResamplers.Lanczos3,
        }));

        // The mark stands on the words' faint support now, so only its own
        // opaque pixels are compared — those are the artwork, whatever is
        // behind — and only away from its edges, where a JPEG rings on line art.
        bool Solid(int x, int y)
        {
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int px = x + dx, py = y + dy;
                    if (px < 0 || py < 0 || px >= box.Width || py >= box.Height || mark[px, py].A < 250) return false;
                }
            return true;
        }
        long total = 0;
        var compared = 0;
        for (var y = 0; y < box.Height; y++)
            for (var x = 0; x < box.Width; x++)
            {
                var artwork = mark[x, y];
                if (!Solid(x, y)) continue;
                total += Distance(sheet[box.X + x, box.Y + y], artwork);
                compared++;
            }
        Assert.True(compared > 300, $"only {compared} solid pixels in the mark");
        var mean = total / (double)compared;
        // Only the JPEG's rounding between the two — a few levels on thin
        // lines; a refilled or recoloured mark is off by tens.
        Assert.True(mean < 6, $"the {logo} symbol differs from the artwork by {mean:F1} on average");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_Number_Keeps_Its_Room_However_Long_The_Line(bool portrait)
    {
        // The host's line and the number share the bottom line. The number is
        // measured and its room reserved first, so a long line shrinks and then
        // shortens — it never runs under the number.
        var composer = new PartyPrintComposer();
        var (w, h) = portrait
            ? (PartyPrintGeometry.PortraitWidth, PartyPrintGeometry.PortraitHeight)
            : (PartyPrintGeometry.LandscapeWidth, PartyPrintGeometry.LandscapeHeight);
        var composition = OnThePhoto([], PartyPrintOverlayText.White, PartyPrintOverlayLogo.Light,
            footer: LongLine,
            number: 12345,
            name: "Il matrimonio di Giulia Rossi e Matteo Bianchi, finalmente");
        var layout = composer.LayoutOverlayText(composition, w, h);
        var (title, footer, number) = (layout.Title, layout.Footer!, layout.Number!);

        Assert.False(footer.Box.IntersectsWith(number.Box), "the host's line runs under the number");
        Assert.False(title.Box.IntersectsWith(number.Box), "the name runs under the number");
        Assert.False(title.Box.IntersectsWith(footer.Box), "the name sits on the host's line");
        // A visible gap, not a touch.
        var lineSize = PartyPrintGeometry.OverlayLineFraction * Math.Min(w, h);
        Assert.True(number.Box.Left - footer.Box.Right >= lineSize, "the line touches the number");
        // Everything inside the margins. The boxes are the ink itself, so a
        // side bearing may reach a hair past the margin, and a comma's tail
        // below the line the words stand on — never further.
        var margin = Math.Round(PartyPrintGeometry.OverlayMarginFraction * Math.Min(w, h));
        foreach (var word in new[] { title, footer, number })
        {
            var bearing = word.Font.Size * 0.06;
            Assert.True(word.Box.Left >= margin - bearing && word.Box.Right <= w - margin + bearing,
                $"{word.Text} {word.Box} runs past the side margins");
            Assert.True(word.Box.Bottom <= h - margin + (word.Font.Size * 0.3),
                $"{word.Text} {word.Box} runs past the bottom margin");
        }
        // The number is the one thing read at the collection table: it stands
        // out from the host's line, and it is printed whole.
        Assert.Equal("#12345", number.Text);
        Assert.True(number.Font.Size > footer.Font.Size, "the number is no larger than the line");
        Assert.EndsWith("…", footer.Text);
    }

    [Fact]
    public void Without_A_Line_The_Name_Leaves_The_Number_Its_Room()
    {
        var composer = new PartyPrintComposer();
        var composition = OnThePhoto([], PartyPrintOverlayText.White, PartyPrintOverlayLogo.Light,
            footer: null, number: 987,
            name: "Il matrimonio di Giulia Rossi e Matteo Bianchi, finalmente");
        var layout = composer.LayoutOverlayText(
            composition, PartyPrintGeometry.PortraitWidth, PartyPrintGeometry.PortraitHeight);
        Assert.Null(layout.Footer);
        Assert.False(layout.Title.Box.IntersectsWith(layout.Number!.Box));
        Assert.True(layout.Title.Box.Right < layout.Number.Box.Left);
    }

    [Theory]
    [InlineData(PartyPrintTheme.Pure)]
    [InlineData(PartyPrintTheme.Midnight)]
    [InlineData(PartyPrintTheme.Event)]
    public async Task A_Framed_Look_Is_Not_Touched_By_The_Overlay_Choices(PartyPrintTheme theme)
    {
        // The three framed looks draw exactly what they drew before: whatever
        // the overlay's colours say, the sheet is the same, byte for byte.
        var composer = new PartyPrintComposer();
        var plain = await composer.RenderAsync(Composition(PartyPrintProducts.Photo, theme, 1), default);
        var chosen = await composer.RenderAsync(Composition(PartyPrintProducts.Photo, theme, 1) with
        {
            Overlay = new PartyPrintOverlay(PartyPrintOverlayText.Red, PartyPrintOverlayLogo.Dark),
        }, default);
        Assert.Equal(plain, chosen);
    }

    [Fact]
    public async Task Writes_The_Title_On_The_Photo_Artifacts_A_Person_Has_To_Look_At()
    {
        // Three photographs a party produces — a bright one, a dark one, a
        // colourful one — under the three pairings worth judging side by side.
        var composer = new PartyPrintComposer();
        var dir = Path.Combine(ArtifactDir(), "on-the-photo");
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.jpg")) File.Delete(stale);
        var pairings = new[]
        {
            (PartyPrintOverlayText.White, PartyPrintOverlayLogo.Light),
            (PartyPrintOverlayText.Black, PartyPrintOverlayLogo.Dark),
            (PartyPrintOverlayText.Red, PartyPrintOverlayLogo.Light),
        };
        var written = 0;
        foreach (var kind in new[] { "light", "dark", "colourful" })
            foreach (var (text, logo) in pairings)
            {
                var bytes = await composer.RenderAsync(OnThePhoto(SheetPhoto(kind), text, logo), default);
                await File.WriteAllBytesAsync(Path.Combine(dir,
                    $"{kind}-text-{text.ToString().ToLowerInvariant()}-logo-{logo.ToString().ToLowerInvariant()}.jpg"),
                    bytes);
                written++;
            }
        // How tall the support is, which follows the words: a short name alone,
        // and the tightest line — a long host's line against a five-digit
        // number — both ways up.
        var layouts = new (string File, bool Portrait, string Name, string? Footer, long Number)[]
        {
            ("portrait-short-name-no-line", true, "Marta 50", null, 7),
            ("portrait-long-line-12345", true, "Giulia & Matteo", LongLine, 12345),
            ("landscape-long-line", false, "Giulia & Matteo", LongLine, 12345),
        };
        foreach (var (file, portrait, name, footer, number) in layouts)
        {
            var bytes = await composer.RenderAsync(OnThePhoto(SheetPhoto("colourful", portrait),
                PartyPrintOverlayText.White, PartyPrintOverlayLogo.Light, footer, number, name), default);
            await File.WriteAllBytesAsync(Path.Combine(dir, $"{file}.jpg"), bytes);
            written++;
        }

        Assert.Equal(12, written);
        Assert.Equal(12, Directory.GetFiles(dir, "*.jpg").Length);
    }

    [Theory]
    [InlineData(PartyPrintTheme.Pure)]
    [InlineData(PartyPrintTheme.Midnight)]
    [InlineData(PartyPrintTheme.Event)]
    public async Task A_Strip_Signs_With_A_Wordmark_And_A_Number_Large_Enough_To_Read(PartyPrintTheme theme)
    {
        // A strip's signature row is a third as wide as a photograph's. At the
        // photograph's proportions the wordmark shrank to the brand minimum —
        // its symbol a smudge on paper — and the number was squinted at.
        using var sheet = Image.Load<Rgba32>(await new PartyPrintComposer().RenderAsync(
            Composition(PartyPrintProducts.TwinStrip4, theme, 8) with { PublicSequence = 1000 }, default));
        const int w = PartyPrintGeometry.PortraitWidth;
        const int h = PartyPrintGeometry.PortraitHeight;
        var stripW = PartyPrintGeometry.StripWidthFraction * w;
        var footX = (int)Math.Round(PartyPrintGeometry.StripMarginFraction * w);
        var footH = PartyPrintGeometry.StripFooterFraction * h;
        var footBottom = (int)Math.Round((1.0 - PartyPrintGeometry.StripMarginFraction) * h);
        // The signature row: the lower part of the strip's foot.
        var rowTop = (int)(footBottom - (footH * 0.38));
        var paper = sheet[footX + (int)(stripW / 2), footBottom - 2];

        // Which columns of the row carry ink, grouped into runs.
        var ink = new List<(int Left, int Right, int Top, int Bottom)>();
        for (var x = footX; x < footX + (int)stripW; x++)
        {
            int top = int.MaxValue, bottom = -1;
            for (var y = rowTop; y < footBottom; y++)
                if (Distance(sheet[x, y], paper) > 48) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
            if (bottom < 0) continue;
            if (ink.Count > 0 && x - ink[^1].Right <= 14)
            {
                var last = ink[^1];
                ink[^1] = (last.Left, x, Math.Min(last.Top, top), Math.Max(last.Bottom, bottom));
            }
            else ink.Add((x, x, top, bottom));
        }
        Assert.True(ink.Count >= 2, $"expected the wordmark and the number, found {ink.Count} marks");
        var (wordmark, number) = (ink[0], ink[^1]);

        // The wordmark stands as wide as the row lets it: past the old 120px,
        // and a touch past the 0.27 it once was.
        Assert.InRange(wordmark.Right - wordmark.Left, 0.29 * stripW, 0.32 * stripW);
        // The number is half again its old size (0.39 → 0.585 of the row): its
        // digits stand about 21px of a 51px row, where they stood about 13 —
        // and well clear of the wordmark.
        Assert.True(number.Bottom - number.Top >= 0.36 * footH * 0.38,
            $"the number's digits are {number.Bottom - number.Top}px in a {footH * 0.38:F0}px row");
        Assert.True(number.Left - wordmark.Right > 100, "the number crowds the wordmark");
        // Both inside the strip's own foot.
        Assert.True(number.Right < footX + stripW && wordmark.Left >= footX);
        Assert.True(Math.Max(number.Bottom, wordmark.Bottom) < footBottom);
    }

    [Theory]
    [InlineData(PartyPrintTheme.Pure, true)]
    [InlineData(PartyPrintTheme.Event, true)]
    [InlineData(PartyPrintTheme.Midnight, false)]
    public void Under_A_Photograph_The_Number_Is_Half_Again_And_The_Words_A_Touch_Larger(
        PartyPrintTheme theme, bool hasFooter)
    {
        // The same band measured as a photograph's or four photographs' foot,
        // and as a strip's.
        const int band = 204;
        var photo = PartyPrintComposer.MeasureFooter(band, theme, hasFooter, strip: false);
        var strip = PartyPrintComposer.MeasureFooter(band, theme, hasFooter, strip: true);
        var markRow = band * 0.38f;
        var nameShare = theme == PartyPrintTheme.Event ? 0.78f : 0.62f;

        // The number: half again what it was, on every sheet (0.34 and 0.39 of the row).
        Assert.Equal(markRow * 0.34f * 1.5f, photo.NumberSize, 0.01f);
        Assert.Equal(markRow * 0.39f * 1.5f, strip.NumberSize, 0.01f);
        // The words: a touch larger under a photograph, unchanged on a strip.
        Assert.Equal(photo.NameBand * nameShare * 1.12f, photo.NameSize, 0.01f);
        Assert.Equal(strip.NameBand * nameShare, strip.NameSize, 0.01f);
        if (hasFooter)
        {
            Assert.Equal((photo.TextBand - photo.NameBand) * 0.52f * 1.12f, photo.LineSize, 0.01f);
            Assert.Equal((strip.TextBand - strip.NameBand) * 0.52f, strip.LineSize, 0.01f);
        }
        // Still inside their bands.
        Assert.True(photo.NameSize < photo.NameBand && photo.NumberSize < photo.MarkBand);
        // The wordmark a little larger everywhere.
        Assert.Equal(0.23, photo.WordmarkWidthFraction);
        Assert.Equal(0.31, strip.WordmarkWidthFraction);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Under_A_Photograph_The_Wordmark_And_The_Number_Sign_The_Foot_Larger(bool portrait)
    {
        using var sheet = Image.Load<Rgba32>(await new PartyPrintComposer().RenderAsync(
            Composition(PartyPrintProducts.Photo, PartyPrintTheme.Pure, 1) with
            {
                PublicSequence = 1000,
                Orientation = portrait ? PartyPrintOrientation.Portrait : PartyPrintOrientation.Landscape,
            }, default));
        var (w, h) = (sheet.Width, sheet.Height);
        var (fx, fy, fw, fh) = PartyPrintGeometry.PhotoFooter(PrintPapers.Photo10x15, portrait);
        var left = (int)(fx * w);
        var right = (int)((fx + fw) * w);
        var bottom = (int)((fy + fh) * h);
        var rowTop = (int)(bottom - (fh * h * 0.38));
        var paper = sheet[left + ((right - left) / 2), bottom - 2];

        // Runs of ink a lockup's own gap apart are one mark: the symbol and
        // "NubArca" stand further apart here than on a strip.
        var ink = new List<(int Left, int Right, int Top, int Bottom)>();
        for (var x = left; x < right; x++)
        {
            int top = int.MaxValue, low = -1;
            for (var y = rowTop; y < bottom; y++)
                if (Distance(sheet[x, y], paper) > 48) { top = Math.Min(top, y); low = Math.Max(low, y); }
            if (low < 0) continue;
            if (ink.Count > 0 && x - ink[^1].Right <= 30)
            {
                var last = ink[^1];
                ink[^1] = (last.Left, x, Math.Min(last.Top, top), Math.Max(last.Bottom, low));
            }
            else ink.Add((x, x, top, low));
        }
        var (wordmark, number) = (ink[0], ink[^1]);
        var row = fh * h * 0.38;
        // The wordmark: past the quiet 0.20 of the foot it was (or, lying down,
        // the old 80% of the row's height).
        Assert.True(wordmark.Right - wordmark.Left > (portrait ? 0.21 * (right - left) : 230),
            $"the wordmark is {wordmark.Right - wordmark.Left}px wide");
        // The number's digits: about a third of the row, where they were about a quarter.
        Assert.True(number.Bottom - number.Top >= 0.33 * row,
            $"the number's digits are {number.Bottom - number.Top}px in a {row:F0}px row");
        Assert.True(number.Left - wordmark.Right > 300, "the number crowds the wordmark");
    }

    /// <summary>Where the ink of a shipped mark sits vertically in its square, top to bottom, 0 to 1.</summary>
    private static double InkMiddle(string file)
    {
        using var mark = Image.Load<Rgba32>(file);
        int top = -1, bottom = -1;
        for (var y = 0; y < mark.Height; y++)
            for (var x = 0; x < mark.Width; x++)
                if (mark[x, y].A > 128)
                {
                    if (top < 0) top = y;
                    bottom = y;
                    break;
                }
        return (top + bottom + 1) / 2.0 / mark.Height;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NubArca.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("NubArca.sln not found");
    }

    [Fact]
    public void Every_Shipped_Mark_Has_Its_Boat_Centred_In_Its_Square()
    {
        // The renderer and the studio centre the mark's SQUARE on the name's
        // capitals; that centres the boat only because the artwork leaves as
        // much room above it as below. A re-exported mark that did not would
        // tilt the signature again, so the files themselves are held to it.
        var files = new[] { PartyPrintOverlayLogo.Light, PartyPrintOverlayLogo.Dark }
            .Select(logo => Path.Combine(AppContext.BaseDirectory, "Assets", "brand", PartyPrintComposer.SymbolFile(logo)))
            .Concat(new[] { "nubarca-mark-flat-on-dark-256.png", "nubarca-mark-flat-on-light-256.png" }
                .Select(name => Path.Combine(RepositoryRoot(), "frontend", "public", "brand", name)));
        foreach (var file in files)
            Assert.InRange(InkMiddle(file), 0.49, 0.51);
    }

    [Theory]
    [InlineData(true, "MARE")]
    [InlineData(false, "MARE")]
    [InlineData(true, "FESTA DI FINE ESTATE AL MARE CON TUTTI NOI")]
    public void The_Boat_Is_Level_With_The_Name_Printed_Beside_It(bool portrait, string name)
    {
        // What a guest saw: the boat a little higher than the name, by about
        // 2mm on a 10x15 — its square stood on the baseline, and the artwork
        // is both taller than the capitals and padded below. Now the middle of
        // the boat's ink is the middle of the name's ink (capitals only, so
        // that ink IS the capitals), including a long name fitted smaller.
        var composer = new PartyPrintComposer();
        var (w, h) = Sheet(portrait);
        var shortEdge = Math.Min(w, h);
        var layout = composer.LayoutOverlayText(OnThePhoto([], PartyPrintOverlayText.White,
            PartyPrintOverlayLogo.Light, "Una notte da ricordare", 27, name), w, h);
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "brand",
            PartyPrintComposer.SymbolFile(PartyPrintOverlayLogo.Light));
        var boat = layout.Symbol.Top + (layout.Symbol.Height * InkMiddle(file));
        var words = (layout.Title.Box.Top + layout.Title.Box.Bottom) / 2;
        Assert.True(Math.Abs(boat - words) <= 0.004 * shortEdge,
            $"the boat's middle is {words - boat:F1}px above the name's");
    }

    [Theory]
    [InlineData(true, "Una notte da ricordare")]
    [InlineData(false, null)]
    public void On_The_Photo_The_Symbol_Stands_Just_Before_The_Name_On_Its_Line(bool portrait, string? footer)
    {
        var composer = new PartyPrintComposer();
        var (w, h) = Sheet(portrait);
        var shortEdge = Math.Min(w, h);
        var layout = composer.LayoutOverlayText(OnThePhoto([], PartyPrintOverlayText.White,
            PartyPrintOverlayLogo.Light, footer, 27, "Giulia & Matteo"), w, h);
        var margin = PartyPrintGeometry.OverlayMarginFraction * shortEdge;
        var symbol = layout.Symbol;
        var name = layout.Title.Box;

        // Not in the corner any more: at the left margin, down on the name's line.
        Assert.Equal(margin, symbol.Left, 1.0);
        Assert.True(symbol.Top > h / 2f, "the symbol is still at the top of the sheet");
        // Just before the name, by the gap, and never touching it.
        Assert.InRange(name.Left - symbol.Right, 0.5 * PartyPrintGeometry.OverlaySymbolGapFraction * shortEdge,
            1.5 * PartyPrintGeometry.OverlaySymbolGapFraction * shortEdge);
        // On the name's line: centred on its capitals, and taller than they are.
        var caps = TextMeasurer.MeasureBounds("H", layout.Title.Options());
        Assert.Equal((caps.Top + caps.Bottom) / 2f, symbol.Top + (symbol.Height / 2f), 1.0);
        Assert.True(symbol.Top < name.Top, "the symbol is shorter than the name's capitals");
        // Never into the host's line under it, nor into the number.
        if (layout.Footer is { } line) Assert.True(symbol.Bottom < line.Box.Top);
        Assert.True(symbol.Right < layout.Number!.Box.Left);
        // The words a touch smaller than they were (0.077 and 0.036); the number as it was.
        Assert.Equal(0.069, PartyPrintGeometry.OverlayTitleFraction);
        Assert.Equal(0.0325, PartyPrintGeometry.OverlayLineFraction);
        Assert.Equal(0.044, PartyPrintGeometry.OverlayNumberFraction);
    }

    // --- Papers and four photographs -------------------------------------------

    [Theory]
    [InlineData(PrintPapers.Photo10x15, true, 1200, 1800)]
    [InlineData(PrintPapers.Photo10x15, false, 1800, 1200)]
    [InlineData(PrintPapers.Photo13x18, true, 1500, 2100)]
    [InlineData(PrintPapers.Photo13x18, false, 2100, 1500)]
    [InlineData(PrintPapers.Photo20x15, true, 1800, 2400)]
    [InlineData(PrintPapers.Photo20x15, false, 2400, 1800)]
    public async Task A_Photo_Is_A_Sheet_Of_Its_Paper_Either_Way_Up(string paper, bool portrait, int w, int h)
    {
        // 4x6, 5x7 and 6x8 inches at 300dpi: the media DNP ships under those names.
        var orientation = portrait ? PartyPrintOrientation.Portrait : PartyPrintOrientation.Landscape;
        foreach (var theme in new[] { PartyPrintTheme.Pure, PartyPrintTheme.Overlay })
        {
            using var sheet = Image.Load<Rgba32>(await new PartyPrintComposer().RenderAsync(
                Composition(PartyPrintProducts.Photo, theme, 1) with { Paper = paper, Orientation = orientation },
                default));
            Assert.Equal((w, h), (sheet.Width, sheet.Height));
        }
    }

    private static Rgba32 SampleGrid(Image<Rgba32> sheet, string paper, int index)
    {
        var (x, y, w, h) = PartyPrintGeometry.GridSlot(paper, index);
        return sheet[(int)((x + (w / 2)) * sheet.Width), (int)((y + (h / 2)) * sheet.Height)];
    }

    [Theory]
    [InlineData(PrintPapers.Photo10x15, 1200, 1800)]
    [InlineData(PrintPapers.Photo13x18, 1500, 2100)]
    [InlineData(PrintPapers.Photo20x15, 2400, 1800)]
    public async Task Four_Photographs_Sit_Two_By_Two_In_The_Order_Chosen(string paper, int w, int h)
    {
        using var sheet = Image.Load<Rgba32>(await new PartyPrintComposer().RenderAsync(
            Composition(PartyPrintProducts.Grid4, PartyPrintTheme.Pure, 4) with { Paper = paper }, default));
        // Standing as 10x15 and 13x18 are named, lying as 20x15 is.
        Assert.Equal((w, h), (sheet.Width, sheet.Height));

        // 1 top left, 2 top right, 3 bottom left, 4 bottom right: each frame is
        // its own photograph, in the order the guest arranged them.
        for (var i = 0; i < 4; i++)
        {
            var (r, g, b) = Fixtures[i];
            Assert.True(Distance(SampleGrid(sheet, paper, i), new Rgba32(r, g, b)) <= 12,
                $"frame {i + 1} is not photograph {i + 1}");
        }

        // The four never overlap, and stay clear of the footer and the edges.
        var slots = Enumerable.Range(0, 4).Select(i => PartyPrintGeometry.GridSlot(paper, i)).ToList();
        var footer = PartyPrintGeometry.GridFooter(paper);
        for (var a = 0; a < 4; a++)
        {
            var sa = slots[a];
            Assert.True(sa.X > 0 && sa.Y > 0 && sa.X + sa.Width < 1 && sa.Y + sa.Height <= footer.Y + 1e-9);
            for (var b = a + 1; b < 4; b++)
            {
                var sb = slots[b];
                var apart = sa.X + sa.Width <= sb.X + 1e-9 || sb.X + sb.Width <= sa.X + 1e-9
                    || sa.Y + sa.Height <= sb.Y + 1e-9 || sb.Y + sb.Height <= sa.Y + 1e-9;
                Assert.True(apart, $"frames {a + 1} and {b + 1} overlap");
            }
        }

        // A sheet of four is not cut: the gutter's ends carry no marks.
        var centre = sheet.Width / 2;
        Assert.True(Distance(sheet[centre, 3], sheet[centre - 12, 3]) <= 6);
    }

    [Fact]
    public async Task Each_Of_The_Four_Keeps_Its_Own_Crop()
    {
        var composer = new PartyPrintComposer();
        var plain = Composition(PartyPrintProducts.Grid4, PartyPrintTheme.Pure, 4) with { Paper = PrintPapers.Photo20x15 };
        // Only the third photograph is framed on its complementary square.
        var photos = plain.Photos.ToList();
        photos[2] = photos[2] with { CropX = 0.64, CropY = 0.14, CropWidth = 0.22, CropHeight = 0.2 };
        using var a = Image.Load<Rgba32>(await composer.RenderAsync(plain, default));
        using var b = Image.Load<Rgba32>(await composer.RenderAsync(plain with { Photos = photos }, default));
        for (var i = 0; i < 4; i++)
        {
            var moved = Distance(SampleGrid(a, PrintPapers.Photo20x15, i), SampleGrid(b, PrintPapers.Photo20x15, i)) > 60;
            Assert.Equal(i == 2, moved);
        }
    }

    [Fact]
    public void The_Layouts_The_Preview_Mirrors_Are_The_Renderers()
    {
        // The preview is a different program. It reads THIS file, written from
        // the geometry the renderer uses, for every paper and product that
        // exists — so a layout that changes here and not there fails a test on
        // one side or the other. NUBARCA_WRITE_PRINT_LAYOUTS=1 rewrites it.
        static double[] R((double X, double Y, double Width, double Height) r) =>
            [Math.Round(r.X, 6), Math.Round(r.Y, 6), Math.Round(r.Width, 6), Math.Round(r.Height, 6)];
        var layouts = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var paper in PrintPapers.All)
        {
            var products = new SortedDictionary<string, object>(StringComparer.Ordinal);
            var photo = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var portrait in new[] { true, false })
            {
                var (w, h) = PartyPrintGeometry.Sheet(paper, portrait);
                photo[portrait ? "portrait" : "landscape"] = new
                {
                    sheet = new[] { w, h },
                    slot = R(PartyPrintGeometry.PhotoSlot(paper, portrait)),
                    footer = R(PartyPrintGeometry.PhotoFooter(paper, portrait)),
                };
            }
            products[PartyPrintProducts.Photo] = photo;
            var (gw, gh) = PartyPrintGeometry.Sheet(paper, PartyPrintGeometry.GridPortrait(paper));
            products[PartyPrintProducts.Grid4] = new
            {
                sheet = new[] { gw, gh },
                slots = Enumerable.Range(0, 4).Select(i => R(PartyPrintGeometry.GridSlot(paper, i))).ToArray(),
                footer = R(PartyPrintGeometry.GridFooter(paper)),
            };
            if (PartyPrintProducts.Allowed(paper, PartyPrintProducts.TwinStrip4))
            {
                products[PartyPrintProducts.TwinStrip4] = new
                {
                    sheet = new[] { PartyPrintGeometry.PortraitWidth, PartyPrintGeometry.PortraitHeight },
                    slots = Enumerable.Range(0, PartyPrintGeometry.StripsPerSheet)
                        .SelectMany(strip => Enumerable.Range(0, PartyPrintGeometry.SlotsPerStrip)
                            .Select(slot => R(PartyPrintGeometry.StripSlot(strip, slot))))
                        .ToArray(),
                };
            }
            // Nothing the matrix forbids has a layout.
            Assert.Equal(PartyPrintProducts.All.Where(x => PartyPrintProducts.Allowed(paper, x)).OrderBy(x => x, StringComparer.Ordinal),
                products.Keys);
            layouts[paper] = products;
        }
        var json = System.Text.Json.JsonSerializer.Serialize(layouts,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";

        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "NubArca.sln"))) root = Path.GetDirectoryName(root)!;
        var file = Path.Combine(root, "frontend", "src", "pages", "partyPrintLayouts.json");
        if (Environment.GetEnvironmentVariable("NUBARCA_WRITE_PRINT_LAYOUTS") == "1")
            File.WriteAllText(file, json);
        Assert.True(File.Exists(file), $"{file} is missing: run with NUBARCA_WRITE_PRINT_LAYOUTS=1");
        Assert.Equal(json, File.ReadAllText(file).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Writes_The_Paper_Artifacts_A_Person_Has_To_Look_At()
    {
        // Four photographs on each paper, and a photograph on the two new ones.
        var composer = new PartyPrintComposer();
        var dir = Path.Combine(ArtifactDir(), "papers");
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.jpg")) File.Delete(stale);
        foreach (var paper in PrintPapers.All)
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, $"grid4-{paper}.jpg"), await composer.RenderAsync(
                Composition(PartyPrintProducts.Grid4, PartyPrintTheme.Pure, 4) with { Paper = paper, PublicSequence = 27 },
                default));
        }
        foreach (var (paper, portrait) in new[] { (PrintPapers.Photo13x18, true), (PrintPapers.Photo20x15, false) })
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, $"photo-{paper}.jpg"), await composer.RenderAsync(
                Composition(PartyPrintProducts.Photo, PartyPrintTheme.Midnight, 1) with
                {
                    Paper = paper, PublicSequence = 27,
                    Orientation = portrait ? PartyPrintOrientation.Portrait : PartyPrintOrientation.Landscape,
                }, default));
        }
        Assert.Equal(5, Directory.GetFiles(dir, "*.jpg").Length);
    }

    [Fact]
    public async Task A_Twin_Strip_Carries_No_Cut_Marks_In_Any_Look()
    {
        // The printer cuts the twin strip. A tick showing scissors where to go
        // would sit exactly under the blade, and a cut a fraction of a
        // millimetre off leaves it on a strip's edge — so there is none, ever.
        var composer = new PartyPrintComposer();
        var centre = PartyPrintGeometry.PortraitWidth / 2;
        foreach (var theme in new[] { PartyPrintTheme.Pure, PartyPrintTheme.Midnight, PartyPrintTheme.Event })
        {
            using var sheet = Image.Load<Rgba32>(await composer.RenderAsync(
                Composition(PartyPrintProducts.TwinStrip4, theme, 8), default));
            // The gutter's ends, top and bottom, are the paper's colour.
            foreach (var y in new[] { 10, PartyPrintGeometry.PortraitHeight - 10 })
                Assert.True(Distance(sheet[centre, y], sheet[centre - 12, y]) <= 6,
                    $"{theme}: a cut mark at ({centre},{y})");
        }
    }

    private static Rgba32 SampleSlot(Image<Rgba32> sheet, int strip, int slot)
    {
        var (x, y, w, h) = PartyPrintGeometry.StripSlot(strip, slot);
        return sheet[
            (int)((x + (w / 2)) * sheet.Width),
            (int)((y + (h / 2)) * sheet.Height)];
    }

    [Fact]
    public async Task Photo_Sheet_Follows_The_Photograph_Orientation()
    {
        var composer = new PartyPrintComposer();

        var landscape = await composer.RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.Photo, PartyPrintTheme.Pure,
            [new PartyPrintPhoto(Fixture(0, 1600, 1000), 0, 0, 1, 1)],
            "Giulia & Matteo", null), default);
        using (var sheet = Image.Load<Rgba32>(landscape))
        {
            Assert.Equal(PartyPrintGeometry.LandscapeWidth, sheet.Width);
            Assert.Equal(PartyPrintGeometry.LandscapeHeight, sheet.Height);
        }

        // A portrait photograph gets a portrait sheet rather than white bars.
        var portrait = await composer.RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.Photo, PartyPrintTheme.Pure,
            [new PartyPrintPhoto(Fixture(1, 1000, 1600), 0, 0, 1, 1)],
            "Giulia & Matteo", null), default);
        using (var sheet = Image.Load<Rgba32>(portrait))
        {
            Assert.Equal(PartyPrintGeometry.PortraitWidth, sheet.Width);
            Assert.Equal(PartyPrintGeometry.PortraitHeight, sheet.Height);
        }
    }

    [Fact]
    public async Task Crop_Is_Deterministic_And_Actually_Changes_The_Framing()
    {
        var composer = new PartyPrintComposer();
        var full = await composer.RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.Photo, PartyPrintTheme.Pure,
            [new PartyPrintPhoto(Fixture(0), 0, 0, 1, 1)], "Festa", null), default);
        var cropped = await composer.RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.Photo, PartyPrintTheme.Pure,
            [new PartyPrintPhoto(Fixture(0), 0.55, 0.05, 0.35, 0.35)], "Festa", null), default);
        var again = await composer.RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.Photo, PartyPrintTheme.Pure,
            [new PartyPrintPhoto(Fixture(0), 0.55, 0.05, 0.35, 0.35)], "Festa", null), default);

        // The crop reaches the sheet...
        Assert.NotEqual(Convert.ToHexString(full), Convert.ToHexString(cropped));
        // ...and the same composition renders the same bytes every time, which is
        // what lets a preview promise anything about the print.
        Assert.Equal(Convert.ToHexString(cropped), Convert.ToHexString(again));
    }

    [Fact]
    public async Task Sheet_Carries_No_Metadata_From_Its_Sources()
    {
        var composer = new PartyPrintComposer();
        var bytes = await composer.RenderAsync(
            Composition(PartyPrintProducts.TwinStrip4, PartyPrintTheme.Midnight, 4), default);

        using var sheet = Image.Load<Rgba32>(bytes);
        // A print handed to a stranger must not travel with where it was taken.
        Assert.Null(sheet.Metadata.ExifProfile);
        Assert.Null(sheet.Metadata.XmpProfile);
        Assert.Null(sheet.Metadata.IptcProfile);
    }

    [Fact]
    public async Task Footer_Text_Is_Bounded_And_Single_Line()
    {
        var composer = new PartyPrintComposer();
        // A host who pastes an essay with newlines still gets a sheet, not a
        // composition overflowing off the paper.
        var bytes = await composer.RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.Photo, PartyPrintTheme.Event,
            [new PartyPrintPhoto(Fixture(2), 0, 0, 1, 1)],
            new string('A', 200), "riga uno\nriga due\r\nriga tre " + new string('B', 300)),
            default);

        using var sheet = Image.Load<Rgba32>(bytes);
        Assert.True(sheet.Width > 0 && sheet.Height > 0);
    }

    [Fact]
    public void Geometry_Keeps_The_Twin_Strips_Inside_The_Sheet()
    {
        // The numbers the preview mirrors: if these stop adding up, a strip runs
        // off the paper, so they are asserted rather than assumed.
        for (var strip = 0; strip < PartyPrintGeometry.StripsPerSheet; strip++)
        {
            for (var slot = 0; slot < PartyPrintGeometry.SlotsPerStrip; slot++)
            {
                var (x, y, w, h) = PartyPrintGeometry.StripSlot(strip, slot);
                Assert.True(x >= 0 && y >= 0, $"slot {strip}/{slot} starts off the sheet");
                Assert.True(x + w <= 1.0001, $"slot {strip}/{slot} runs off the right edge");
                Assert.True(y + h <= 1.0001, $"slot {strip}/{slot} runs off the bottom");
                Assert.True(w > 0 && h > 0);
            }
        }

        // The two strips do not overlap, and the gutter between them is real.
        var (leftX, _, leftW, _) = PartyPrintGeometry.StripSlot(0, 0);
        var (rightX, _, _, _) = PartyPrintGeometry.StripSlot(1, 0);
        Assert.True(rightX >= leftX + leftW, "the twin strips overlap");
        Assert.Equal(PartyPrintGeometry.StripGutterFraction, rightX - (leftX + leftW), 3);
    }

    [Fact]
    public async Task Writes_The_Seven_Artifacts_A_Person_Has_To_Look_At()
    {
        // Tests can prove the geometry. Whether the print is beautiful is a
        // judgement, and these are what it is made on: every look of a photo,
        // and the three framed looks of a strip (the title on the photograph is
        // a single-photograph look, with its own set under on-the-photo/).
        var composer = new PartyPrintComposer();
        var dir = ArtifactDir();
        // A sheet left by an earlier run of an older renderer is not evidence.
        foreach (var stale in Directory.GetFiles(dir, "*.jpg")) File.Delete(stale);
        foreach (var theme in Enum.GetValues<PartyPrintTheme>())
        {
            // Numbered, as a guest's sheet is, so the number is judged too.
            var photo = await composer.RenderAsync(
                Composition(PartyPrintProducts.Photo, theme, 1) with { PublicSequence = 27 }, default);
            await File.WriteAllBytesAsync(
                Path.Combine(dir, $"photo-{theme.ToString().ToLowerInvariant()}.jpg"), photo);

            if (theme is PartyPrintTheme.Overlay) continue;
            var strip = await composer.RenderAsync(
                Composition(PartyPrintProducts.TwinStrip4, theme, 8) with { PublicSequence = 27 }, default);
            await File.WriteAllBytesAsync(
                Path.Combine(dir, $"strip-{theme.ToString().ToLowerInvariant()}.jpg"), strip);
        }

        Assert.Equal(7, Directory.GetFiles(dir, "*.jpg").Length);
    }
}
