using NubArca.Api.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Metadata;

public sealed class ImageDisplayDimensionsTests
{
    [Theory]
    [InlineData(1)]  // normal
    [InlineData(2)]  // mirrored
    [InlineData(3)]  // 180
    [InlineData(4)]  // mirrored 180
    [InlineData(null)]
    public void Non_Quarter_Turn_Keeps_Coded_Dimensions(int? orientation)
    {
        var (w, h) = ImageDisplayDimensions.Resolve(4000, 3000, orientation);
        Assert.Equal(4000, w);
        Assert.Equal(3000, h);
    }

    [Theory]
    [InlineData(5)] // transpose
    [InlineData(6)] // 90 CW  (portrait phone photo stored landscape)
    [InlineData(7)] // transverse
    [InlineData(8)] // 270 CW
    public void Quarter_Turn_Orientation_Swaps_Width_And_Height(int orientation)
    {
        var (w, h) = ImageDisplayDimensions.Resolve(4000, 3000, orientation);
        Assert.Equal(3000, w);
        Assert.Equal(4000, h);
    }

    [Fact]
    public void Null_Dimensions_Pass_Through()
    {
        Assert.Equal((null, null), ImageDisplayDimensions.Resolve(null, null, 6));
    }
}
