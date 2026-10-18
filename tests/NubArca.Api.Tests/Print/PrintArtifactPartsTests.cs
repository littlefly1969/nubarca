using NubArca.Api.Print;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Print;

public sealed class PrintArtifactPartsTests
{
    private static byte[] Jpeg(int width, int height, Rgb24 colour)
    {
        using var image = new Image<Rgb24>(width, height, colour);
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream);
        return stream.ToArray();
    }

    [Fact]
    public async Task Each_Part_Comes_Back_Whole_And_In_Order()
    {
        var first = Jpeg(12, 18, new Rgb24(200, 30, 30));
        var second = Jpeg(12, 18, new Rgb24(30, 30, 200));
        var packed = PrintArtifactParts.Pack([first, second]);

        var lengths = await PrintArtifactParts.ReadLengthsAsync(new MemoryStream(packed), default);
        Assert.Equal([first.Length, second.Length], lengths);
        foreach (var (index, part) in new[] { (0, first), (1, second) })
        {
            var (offset, length) = PrintArtifactParts.Locate(lengths!, index)!.Value;
            Assert.Equal(part, packed.AsSpan((int)offset, length).ToArray());
        }
        Assert.Null(PrintArtifactParts.Locate(lengths!, 2));
        var all = await PrintArtifactParts.ReadAllAsync(new MemoryStream(packed), default);
        Assert.Equal([first, second], all);
    }

    [Fact]
    public async Task A_Single_JPEG_Is_Not_A_Parts_Artifact()
    {
        Assert.Null(await PrintArtifactParts.ReadLengthsAsync(new MemoryStream(Jpeg(4, 4, new Rgb24(0, 0, 0))), default));
        Assert.Null(await PrintArtifactParts.ReadLengthsAsync(new MemoryStream([1, 2]), default));
    }

    [Fact]
    public async Task The_Simulated_View_Puts_Standing_Parts_Side_By_Side_And_Lying_Ones_Stacked()
    {
        using var standing = Image.Load<Rgb24>(await PrintArtifactParts.JoinForViewAsync(
            [Jpeg(12, 18, new Rgb24(200, 30, 30)), Jpeg(12, 18, new Rgb24(30, 30, 200))], default));
        Assert.Equal((24, 18), (standing.Width, standing.Height));
        using var lying = Image.Load<Rgb24>(await PrintArtifactParts.JoinForViewAsync(
            [Jpeg(18, 12, new Rgb24(200, 30, 30)), Jpeg(18, 12, new Rgb24(30, 30, 200))], default));
        Assert.Equal((18, 24), (lying.Width, lying.Height));
        Assert.True(lying[9, 3].R > 150 && lying[9, 20].B > 150);
    }
}
