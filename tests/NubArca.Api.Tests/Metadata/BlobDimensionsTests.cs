using NubArca.Api.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Metadata;

// Unit tests for the dimension sanitizer that guards the blob_metadata CHECK
// constraints (ck_blob_metadata_{width,height}_positive). Both ingest paths
// (admin batch import + per-file upload) delegate to it, so the constraint
// violation that crashed imports (Height = 0 -> 23514) can never recur.
public sealed class BlobDimensionsTests
{
    [Fact]
    public void ValidDimensions_ArePreserved_AndPixelCountIsProduct()
    {
        var (w, h, px) = BlobDimensions.Normalize(1920, 1080);
        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
        Assert.Equal(1920L * 1080L, px);
    }

    [Fact]
    public void ZeroHeight_BecomesNull_WithNullPixelCount()
    {
        // The exact production failure: detection reported Height = 0.
        var (w, h, px) = BlobDimensions.Normalize(800, 0);
        Assert.Null(h);
        Assert.Null(px);
        // A still-valid width is kept (the constraint allows Width>0, Height NULL).
        Assert.Equal(800, w);
    }

    [Fact]
    public void ZeroWidth_BecomesNull_WithNullPixelCount()
    {
        var (w, h, px) = BlobDimensions.Normalize(0, 600);
        Assert.Null(w);
        Assert.Null(px);
        Assert.Equal(600, h);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    [InlineData(0, 0)]
    [InlineData(-5, -5)]
    public void NonPositiveDimensions_ProduceNullPixelCount(int width, int height)
    {
        var (_, _, px) = BlobDimensions.Normalize(width, height);
        Assert.Null(px);
    }

    [Fact]
    public void NegativeDimensions_BecomeNull()
    {
        var (w, h, px) = BlobDimensions.Normalize(-3, -4);
        Assert.Null(w);
        Assert.Null(h);
        Assert.Null(px);
    }

    [Fact]
    public void NullInputs_StayNull()
    {
        var (w, h, px) = BlobDimensions.Normalize(null, null);
        Assert.Null(w);
        Assert.Null(h);
        Assert.Null(px);
    }

    [Fact]
    public void LargeDimensions_DoNotOverflow_PixelCountStaysPositive()
    {
        // 60000 * 40000 = 2_400_000_000 overflows int32 (max 2_147_483_647);
        // computing in 64-bit keeps it positive so it satisfies
        // ck_blob_metadata_pixel_count_non_negative.
        var (w, h, px) = BlobDimensions.Normalize(60000, 40000);
        Assert.Equal(60000, w);
        Assert.Equal(40000, h);
        Assert.Equal(2_400_000_000L, px);
        Assert.True(px > 0);
    }
}
