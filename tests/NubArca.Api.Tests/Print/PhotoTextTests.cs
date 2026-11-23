using NubArca.Api.Print;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Print;

public sealed class PhotoTextTests
{
    [Theory]
    [InlineData(double.NaN, .5, .65, .35)]
    [InlineData(.5, double.PositiveInfinity, .65, .35)]
    [InlineData(.5, .5, .19, .35)]
    [InlineData(.5, .5, .65, .81)]
    public void Invalid_numbers_are_refused(double x, double y, double width, double height) =>
        Assert.False(new PhotoTextLayout("bubble", x, y, width, height).IsValid());

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(.5, .5)]
    public void Drawing_is_confined_to_the_requested_photo_box(double x, double y)
    {
        var red = new Rgba32(200, 30, 30);
        using var sheet = new Image<Rgba32>(500, 400, red);
        var frame = new Rectangle(50, 40, 400, 320);
        var layout = new PhotoTextLayout("bubble", x, y, .5, .5);
        PhotoTextRenderer.Draw(sheet, frame, "Auguri!", layout);
        var left = 50 + x * 200; var top = 40 + y * 160;
        var changed = 0;
        for (var row = 0; row < sheet.Height; row++)
            for (var col = 0; col < sheet.Width; col++)
            {
                if (sheet[col,row] == red) continue;
                changed++;
                Assert.InRange(col, (int)left, (int)left + 200);
                Assert.InRange(row, (int)top, (int)top + 160);
            }
        Assert.True(changed > 1000);
    }
}
