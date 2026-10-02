using NubArca.Api.Domain.Print;
using NubArca.Api.Print;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// The party sheet drawn from placements: a photograph zoomed out sits on the
/// theme's own paper, a framing at zoom 1 or more prints exactly what its crop
/// always printed, and every slot keeps its own framing. Asserted on pixels at
/// known points, not on whole JPEG bytes.
/// </summary>
public sealed class PartyPrintPlacementRenderTests
{
    private static readonly Rgba32 Photo = new(0xC9, 0x20, 0x20);

    /// <summary>A flat red photograph of the given shape, so any pixel is either it or paper.</summary>
    private static byte[] Flat(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, Photo);
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 100 });
        return ms.ToArray();
    }

    private static async Task<Image<Rgba32>> RenderAsync(PartyPrintComposition composition)
    {
        var bytes = await new PartyPrintComposer().RenderAsync(composition, default);
        return Image.Load<Rgba32>(bytes);
    }

    private static bool Near(Rgba32 a, Rgba32 b, int tolerance = 14) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static Point In(int sheetW, int sheetH, (double X, double Y, double Width, double Height) slot,
        double fx, double fy) =>
        new((int)((slot.X + slot.Width * fx) * sheetW), (int)((slot.Y + slot.Height * fy) * sheetH));

    [Theory]
    [InlineData(PartyPrintTheme.Pure, 0xF5, 0xF7, 0xFB)]
    [InlineData(PartyPrintTheme.Midnight, 0x0A, 0x0F, 0x1A)]
    [InlineData(PartyPrintTheme.Event, 0x0F, 0x1E, 0x3A)]
    public async Task Zoomed_Out_The_Bands_Are_The_Themes_Own_Paper(PartyPrintTheme theme, byte r, byte g, byte b)
    {
        // A wide photograph whole on a standing sheet: paper above and below it.
        var contain = PhotoPlacementGeometry.ContainZoom(1.5, PartyPrintGeometry.PhotoSlotAspect(portrait: true));
        using var sheet = await RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.Photo, theme, [PartyPrintPhoto.Placed(Flat(1500, 1000), new PhotoPlacement(0.5, 0.5, contain))],
            "Festa", null, Orientation: PartyPrintOrientation.Portrait));
        var slot = PartyPrintGeometry.PhotoSlot(PrintPapers.Photo10x15, portrait: true);
        var band = In(sheet.Width, sheet.Height, slot, 0.5, 0.04);
        var middle = In(sheet.Width, sheet.Height, slot, 0.5, 0.5);
        Assert.True(Near(sheet[band.X, band.Y], new Rgba32(r, g, b)), $"band {sheet[band.X, band.Y]}");
        Assert.True(Near(sheet[middle.X, middle.Y], Photo), $"photograph {sheet[middle.X, middle.Y]}");
        // Never black where a theme is light.
        if (theme == PartyPrintTheme.Pure) Assert.False(Near(sheet[band.X, band.Y], new Rgba32(0, 0, 0)));
    }

    [Fact]
    public async Task On_The_Photo_With_No_Paper_Of_Its_Own_The_Bands_Are_White()
    {
        var contain = PhotoPlacementGeometry.ContainZoom(1.5, PartyPrintGeometry.OverlaySlotAspect(portrait: true));
        using var sheet = await RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.Photo, PartyPrintTheme.Overlay,
            [PartyPrintPhoto.Placed(Flat(1500, 1000), new PhotoPlacement(0.5, 0.5, contain))],
            "Festa", null, Orientation: PartyPrintOrientation.Portrait));
        // Near the top edge, right of the symbol: band, white.
        Assert.True(Near(sheet[sheet.Width - 40, 20], new Rgba32(0xFF, 0xFF, 0xFF)), $"{sheet[sheet.Width - 40, 20]}");
    }

    [Fact]
    public async Task At_Zoom_One_And_In_A_Placement_Prints_Exactly_What_Its_Crop_Printed()
    {
        // A two-coloured photograph so a shifted framing would show.
        using var source = new Image<Rgba32>(1500, 1000, new Rgba32(20, 120, 220));
        source.Mutate(x => x.Fill(Photo, new RectangleF(0, 0, 600, 1000)));
        using var ms = new MemoryStream();
        source.SaveAsPng(ms);
        var bytes = ms.ToArray();
        var frame = PartyPrintGeometry.PhotoSlotAspect(portrait: false);
        foreach (var placement in new[] { new PhotoPlacement(0.5, 0.5, 1), new PhotoPlacement(0.2, 0.7, 2.5) })
        {
            var (cx, cy, cw, ch) = PhotoPlacementGeometry.LegacyCrop(PhotoPlacementGeometry.Place(1.5, frame, placement));
            using var placed = await RenderAsync(new PartyPrintComposition(
                PartyPrintProducts.Photo, PartyPrintTheme.Pure, [PartyPrintPhoto.Placed(bytes, placement)],
                "Festa", null, Orientation: PartyPrintOrientation.Landscape));
            using var cropped = await RenderAsync(new PartyPrintComposition(
                PartyPrintProducts.Photo, PartyPrintTheme.Pure, [new PartyPrintPhoto(bytes, cx, cy, cw, ch)],
                "Festa", null, Orientation: PartyPrintOrientation.Landscape));
            var slot = PartyPrintGeometry.PhotoSlot(PrintPapers.Photo10x15, portrait: false);
            for (var fx = 0.05; fx < 1; fx += 0.15)
            {
                for (var fy = 0.05; fy < 1; fy += 0.15)
                {
                    var p = In(placed.Width, placed.Height, slot, fx, fy);
                    Assert.True(Near(placed[p.X, p.Y], cropped[p.X, p.Y], 24),
                        $"{placement} at ({fx:0.00},{fy:0.00}): {placed[p.X, p.Y]} vs {cropped[p.X, p.Y]}");
                }
            }
        }
    }

    [Fact]
    public async Task Each_Of_Four_Keeps_Its_Own_Framing()
    {
        var paper = PrintPapers.Photo10x15;
        var frame = PartyPrintGeometry.GridSlotAspect(paper);
        // Slot 0 zoomed out on a wide photograph; slots 1–3 filled.
        var photos = Enumerable.Range(0, 4).Select(i => PartyPrintPhoto.Placed(Flat(1500, 1000),
            new PhotoPlacement(0.5, 0.5, i == 0 ? PhotoPlacementGeometry.ContainZoom(1.5, frame) : 1))).ToList();
        using var sheet = await RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.Grid4, PartyPrintTheme.Midnight, photos, "Festa", null, Paper: paper));
        var paperColour = new Rgba32(0x0A, 0x0F, 0x1A);
        var zero = In(sheet.Width, sheet.Height, PartyPrintGeometry.GridSlot(paper, 0), 0.5, 0.03);
        var one = In(sheet.Width, sheet.Height, PartyPrintGeometry.GridSlot(paper, 1), 0.5, 0.03);
        Assert.True(Near(sheet[zero.X, zero.Y], paperColour), $"slot 0 band {sheet[zero.X, zero.Y]}");
        Assert.True(Near(sheet[one.X, one.Y], Photo), $"slot 1 filled {sheet[one.X, one.Y]}");
    }

    [Fact]
    public async Task Each_Of_Eight_On_The_Twin_Strip_Keeps_Its_Own_Framing()
    {
        var frame = PartyPrintGeometry.StripSlotAspect();
        // A tall photograph in the wide strip frames: zoomed out on odd slots,
        // so paper shows beside them; filled on even ones.
        var photos = Enumerable.Range(0, 8).Select(i => PartyPrintPhoto.Placed(Flat(800, 1200),
            new PhotoPlacement(0.5, 0.5, i % 2 == 1 ? PhotoPlacementGeometry.ContainZoom(800.0 / 1200, frame) : 1))).ToList();
        using var sheet = await RenderAsync(new PartyPrintComposition(
            PartyPrintProducts.TwinStrip4, PartyPrintTheme.Event, photos, "Festa", null));
        var paperColour = new Rgba32(0x0F, 0x1E, 0x3A);
        for (var i = 0; i < 8; i++)
        {
            var p = In(sheet.Width, sheet.Height, PartyPrintGeometry.StripSlot(i / 4, i % 4), 0.04, 0.5);
            Assert.True(Near(sheet[p.X, p.Y], i % 2 == 1 ? paperColour : Photo), $"slot {i}: {sheet[p.X, p.Y]}");
        }
    }
}
