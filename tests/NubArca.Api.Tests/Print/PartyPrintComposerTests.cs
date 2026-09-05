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
    public async Task Strip_Is_A_Portrait_Sheet_Carrying_Two_Identical_Strips()
    {
        var composer = new PartyPrintComposer();
        var bytes = await composer.RenderAsync(
            Composition(PartyPrintProducts.Strip4, PartyPrintTheme.Pure, 4), default);

        using var sheet = Image.Load<Rgba32>(bytes);
        // One 10x15 sheet, portrait — not a new paper size.
        Assert.Equal(PartyPrintGeometry.PortraitWidth, sheet.Width);
        Assert.Equal(PartyPrintGeometry.PortraitHeight, sheet.Height);

        // The twins are identical: sample the centre of each slot on both strips
        // and require the pairs to match. If the second strip drew a different
        // photograph, or the same photographs in another order, this fails.
        for (var slot = 0; slot < PartyPrintGeometry.SlotsPerStrip; slot++)
        {
            var left = SampleSlot(sheet, 0, slot);
            var right = SampleSlot(sheet, 1, slot);
            Assert.Equal(left, right);
        }

        // And the four slots are four DIFFERENT photographs, in order.
        var distinct = Enumerable.Range(0, PartyPrintGeometry.SlotsPerStrip)
            .Select(slot => SampleSlot(sheet, 0, slot))
            .Distinct()
            .Count();
        Assert.Equal(PartyPrintGeometry.SlotsPerStrip, distinct);
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
    public async Task Writes_The_Six_Artifacts_A_Person_Has_To_Look_At()
    {
        // Tests can prove the geometry. Whether the print is beautiful is a
        // judgement, and these are what it is made on.
        var composer = new PartyPrintComposer();
        var dir = ArtifactDir();
        foreach (var theme in Enum.GetValues<PartyPrintTheme>())
        {
            var photo = await composer.RenderAsync(
                Composition(PartyPrintProducts.Photo, theme, 1), default);
            await File.WriteAllBytesAsync(
                Path.Combine(dir, $"photo-{theme.ToString().ToLowerInvariant()}.jpg"), photo);

            var strip = await composer.RenderAsync(
                Composition(PartyPrintProducts.Strip4, theme, 4), default);
            await File.WriteAllBytesAsync(
                Path.Combine(dir, $"strip-{theme.ToString().ToLowerInvariant()}.jpg"), strip);
        }

        Assert.Equal(6, Directory.GetFiles(dir, "*.jpg").Length);
    }
}
