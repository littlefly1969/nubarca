using NubArca.Api.Plates.Redaction;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Plates;

// Pure-unit coverage for the privacy redaction renderer: dimension preservation,
// box expansion/clamping (including edge/out-of-range boxes), and that the
// redacted region actually changes pixels. No DB, no HTTP, no detector.
public sealed class ImageRedactionRendererTests
{
    private readonly ImageRedactionRenderer _renderer = new();

    // A high-frequency checkerboard so pixelation of ANY region changes bytes
    // (a flat color would pixelate to itself).
    private static byte[] Checkerboard(int width, int height)
    {
        using var img = new Image<Rgba32>(width, height);
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = ((x + y) & 1) == 0 ? new Rgba32(20, 40, 200) : new Rgba32(240, 220, 30);
                }
            }
        });
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static (int Width, int Height) Dimensions(byte[] bytes)
    {
        var info = Image.Identify(bytes);
        return (info.Width, info.Height);
    }

    [Fact]
    public void Preserves_Dimensions_And_Redacts_The_Box()
    {
        var source = Checkerboard(200, 160);
        var box = new ImageRedactionRenderer.NormalizedBox(0.40, 0.20, 0.12, 0.16);

        var redacted = _renderer.Render(source, new[] { box }, expansionRatio: 0.35, pixelBlockSize: 32, quality: 85);
        var empty = _renderer.Render(source, Array.Empty<ImageRedactionRenderer.NormalizedBox>(),
            expansionRatio: 0.35, pixelBlockSize: 32, quality: 85);

        Assert.Equal(200, redacted.Width);
        Assert.Equal(160, redacted.Height);
        var (w, h) = Dimensions(redacted.Jpeg);
        Assert.Equal(200, w);
        Assert.Equal(160, h);

        // A box changes bytes vs the same encode with no redaction.
        Assert.False(redacted.Jpeg.AsSpan().SequenceEqual(empty.Jpeg));
    }

    [Theory]
    // Box overflowing the bottom-right corner.
    [InlineData(0.90, 0.90, 0.30, 0.30)]
    // Box with a negative origin (out of [0..1]).
    [InlineData(-0.10, -0.10, 0.30, 0.20)]
    // Zero-area box (nothing to redact) — must not throw.
    [InlineData(0.50, 0.50, 0.0, 0.0)]
    // Full-image box.
    [InlineData(0.0, 0.0, 1.0, 1.0)]
    public void Handles_Edge_And_OutOfRange_Boxes_Without_Throwing(double x, double y, double w, double h)
    {
        var source = Checkerboard(128, 96);
        var box = new ImageRedactionRenderer.NormalizedBox(x, y, w, h);

        var redacted = _renderer.Render(source, new[] { box }, expansionRatio: 0.5, pixelBlockSize: 24, quality: 80);

        Assert.Equal(128, redacted.Width);
        Assert.Equal(96, redacted.Height);
        var (dw, dh) = Dimensions(redacted.Jpeg);
        Assert.Equal(128, dw);
        Assert.Equal(96, dh);
    }

    [Fact]
    public void Empty_Boxes_Produce_A_Valid_SameSize_Image()
    {
        var source = Checkerboard(64, 64);
        var redacted = _renderer.Render(source, Array.Empty<ImageRedactionRenderer.NormalizedBox>(),
            expansionRatio: 0.35, pixelBlockSize: 16, quality: 82);

        var (w, h) = Dimensions(redacted.Jpeg);
        Assert.Equal(64, w);
        Assert.Equal(64, h);
    }
}
