using NubArca.Api.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Metadata;

public sealed class VideoDisplayDimensionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(180)]
    [InlineData(360)]
    [InlineData(null)]
    public void No_Quarter_Turn_Keeps_Coded_Dimensions(int? rotation)
    {
        var (w, h) = VideoDisplayDimensions.Resolve(1920, 1080, rotation);
        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    [InlineData(-90)] // defensive: not normalized, still a quarter turn
    public void Quarter_Turn_Swaps_Width_And_Height(int rotation)
    {
        // A landscape-coded phone clip shot vertically displays PORTRAIT.
        var (w, h) = VideoDisplayDimensions.Resolve(1920, 1080, rotation);
        Assert.Equal(1080, w);
        Assert.Equal(1920, h);
    }

    [Fact]
    public void Null_Dimensions_Pass_Through()
    {
        Assert.Equal((null, null), VideoDisplayDimensions.Resolve(null, null, 90));
        Assert.Equal(((int?)null, (int?)1080), VideoDisplayDimensions.Resolve(null, 1080, 0));
    }

    [Fact]
    public void Square_Is_Unaffected_By_A_Quarter_Turn()
    {
        var (w, h) = VideoDisplayDimensions.Resolve(1080, 1080, 90);
        Assert.Equal(1080, w);
        Assert.Equal(1080, h);
    }
}
