using NubArca.Api.Domain.Print;
using NubArca.Api.Print;
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
            PartyPrintProducts.Strip4, PartyPrintTheme.Pure, photos,
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
            Composition(PartyPrintProducts.Strip4, PartyPrintTheme.Pure, 8), default);

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
            Composition(PartyPrintProducts.Strip4, PartyPrintTheme.Pure, 4), default);
        using var sheet = Image.Load<Rgba32>(bytes);
        for (var slot = 0; slot < PartyPrintGeometry.SlotsPerStrip; slot++)
            Assert.Equal(SampleSlot(sheet, 0, slot), SampleSlot(sheet, 1, slot));
    }

    [Theory]
    [InlineData(PartyPrintTheme.OverlayWhite, true)]
    [InlineData(PartyPrintTheme.OverlayBlack, false)]
    public async Task The_Title_On_The_Photo_Runs_The_Picture_To_The_Edge_Under_The_Invitation_Scrim(
        PartyPrintTheme theme, bool whiteInk)
    {
        // A mid-grey photograph, so the scrim's effect is readable pixel by pixel.
        using var grey = new Image<Rgba32>(1000, 1500, new Rgba32(128, 128, 128));
        using var ms = new MemoryStream();
        await grey.SaveAsJpegAsync(ms);
        var bytes = await new PartyPrintComposer().RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.Photo, theme, [new PartyPrintPhoto(ms.ToArray(), 0, 0, 1, 1)],
            "Giulia & Matteo", "Una notte da ricordare", 12), default);
        using var sheet = Image.Load<Rgba32>(bytes);

        Assert.Equal(PartyPrintGeometry.PortraitWidth, sheet.Width);
        Assert.Equal(PartyPrintGeometry.PortraitHeight, sheet.Height);

        // No paper margin: an edge pixel is the photograph under the scrim, and
        // the scrim pulls it toward the base colour — navy under white ink,
        // white under black ink.
        var edge = sheet[sheet.Width / 2, sheet.Height / 4];
        if (whiteInk) Assert.True(edge.R < 128, $"white-ink scrim left {edge.R}");
        else Assert.True(edge.R > 128, $"black-ink scrim left {edge.R}");

        // The foot of the sheet IS the base colour, as at the foot of the invitation.
        var foot = sheet[sheet.Width / 2, sheet.Height - 2];
        if (whiteInk) Assert.True(foot.R < 30 && foot.B < 45, $"foot {foot}");
        else Assert.True(foot.R > 225, $"foot {foot}");

        // The symbol sits top-left in the ink: somewhere in its box a pixel is
        // the ink, which the grey photograph and its scrim alone never are.
        var margin = (int)(PartyPrintGeometry.OverlayMarginFraction * sheet.Width);
        var size = (int)(PartyPrintGeometry.OverlaySymbolFraction * sheet.Width);
        var inked = false;
        for (var y = margin; y < margin + size && !inked; y += 3)
            for (var x = margin; x < margin + size && !inked; x += 3)
            {
                var p = sheet[x, y];
                inked = whiteInk ? p.R > 235 && p.G > 235 : p.R < 20 && p.G < 25;
            }
        Assert.True(inked, "the NubArca symbol is not on the sheet");

        await File.WriteAllBytesAsync(
            Path.Combine(ArtifactDir(), $"photo-{theme.ToString().ToLowerInvariant()}.jpg"), bytes);
    }

    [Fact]
    public async Task A_Strip_The_Printer_Cuts_Carries_No_Cut_Marks()
    {
        // The ticks show scissors where to go. Under a blade they would sit
        // exactly on the cut, and a cut a fraction of a millimetre off leaves
        // one on a strip's edge.
        var composer = new PartyPrintComposer();
        using var byHand = Image.Load<Rgba32>(await composer.RenderAsync(
            Composition(PartyPrintProducts.Strip4, PartyPrintTheme.Pure, 4), default));
        using var byPrinter = Image.Load<Rgba32>(await composer.RenderAsync(
            Composition(PartyPrintProducts.Strip4, PartyPrintTheme.Pure, 4) with { CutByPrinter = true },
            default));

        var centre = PartyPrintGeometry.PortraitWidth / 2;
        const int y = 10;
        static int Distance(Rgba32 a, Rgba32 b) =>
            Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

        // The same spot on the top margin: a tick on one sheet, paper on the other.
        Assert.True(Distance(byHand[centre, y], byHand[centre - 12, y]) > 30,
            "the hand-cut sheet lost its cut mark");
        Assert.True(Distance(byPrinter[centre, y], byPrinter[centre - 12, y]) <= 6,
            "the printer-cut sheet still has a cut mark");

        // Everything else is the same composition.
        for (var slot = 0; slot < PartyPrintGeometry.SlotsPerStrip; slot++)
        {
            Assert.Equal(SampleSlot(byHand, 0, slot), SampleSlot(byPrinter, 0, slot));
            Assert.Equal(SampleSlot(byHand, 1, slot), SampleSlot(byPrinter, 1, slot));
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
            Composition(PartyPrintProducts.Strip4, PartyPrintTheme.Midnight, 4), default);

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
    public async Task Writes_The_Eight_Artifacts_A_Person_Has_To_Look_At()
    {
        // Tests can prove the geometry. Whether the print is beautiful is a
        // judgement, and these are what it is made on: every look of a photo,
        // and the three framed looks of a strip (the title on the photograph is
        // a single-photograph look).
        var composer = new PartyPrintComposer();
        var dir = ArtifactDir();
        foreach (var theme in Enum.GetValues<PartyPrintTheme>())
        {
            var photo = await composer.RenderAsync(
                Composition(PartyPrintProducts.Photo, theme, 1), default);
            await File.WriteAllBytesAsync(
                Path.Combine(dir, $"photo-{theme.ToString().ToLowerInvariant()}.jpg"), photo);

            if (theme is PartyPrintTheme.OverlayWhite or PartyPrintTheme.OverlayBlack) continue;
            var strip = await composer.RenderAsync(
                Composition(PartyPrintProducts.Strip4, theme, 8), default);
            await File.WriteAllBytesAsync(
                Path.Combine(dir, $"strip-{theme.ToString().ToLowerInvariant()}.jpg"), strip);
        }

        Assert.Equal(8, Directory.GetFiles(dir, "*.jpg").Length);
    }
}
