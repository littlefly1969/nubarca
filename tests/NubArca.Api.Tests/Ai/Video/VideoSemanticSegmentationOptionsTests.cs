using NubArca.Api.Ai.Video;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-01: the options guard. A self-contradictory or unbounded configuration
// must fail at startup, not produce a pathological manifest at 3 a.m.
public sealed class VideoSemanticSegmentationOptionsTests
{
    private static readonly VideoSemanticSegmentationOptionsValidator Validator = new();

    private static bool IsValid(VideoSemanticSegmentationOptions options)
        => Validator.Validate(null, options).Succeeded;

    [Fact]
    public void Defaults_Are_Valid_And_Disabled()
    {
        var options = new VideoSemanticSegmentationOptions();

        Assert.False(options.Enabled);       // AI is off by default
        Assert.True(IsValid(options));
    }

    [Fact]
    public void Duration_Ordering_Must_Be_Minimum_Target_Maximum()
    {
        Assert.False(IsValid(new VideoSemanticSegmentationOptions
        {
            MinimumSegmentSeconds = 10, TargetSegmentSeconds = 5, MaximumSegmentSeconds = 20,
        }));
        Assert.False(IsValid(new VideoSemanticSegmentationOptions
        {
            MinimumSegmentSeconds = 2, TargetSegmentSeconds = 30, MaximumSegmentSeconds = 20,
        }));
        Assert.True(IsValid(new VideoSemanticSegmentationOptions
        {
            MinimumSegmentSeconds = 2, TargetSegmentSeconds = 8, MaximumSegmentSeconds = 20,
        }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Segmentation_Version_Must_Be_Positive(int version)
        => Assert.False(IsValid(new VideoSemanticSegmentationOptions { SegmentationVersion = version }));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void Scene_Threshold_Must_Be_Inside_The_Open_Unit_Interval(double threshold)
        => Assert.False(IsValid(new VideoSemanticSegmentationOptions { SceneThreshold = threshold }));

    [Fact]
    public void Caps_And_Timeouts_Must_Be_Positive()
    {
        Assert.False(IsValid(new VideoSemanticSegmentationOptions { MaximumSegmentsPerVideo = 0 }));
        Assert.False(IsValid(new VideoSemanticSegmentationOptions { SamplesPerSegment = 0 }));
        Assert.False(IsValid(new VideoSemanticSegmentationOptions { MaximumSamplesPerVideo = 0 }));
        Assert.False(IsValid(new VideoSemanticSegmentationOptions { ProcessTimeoutSeconds = 0 }));
        Assert.False(IsValid(new VideoSemanticSegmentationOptions { MaximumProcessOutputBytes = 0 }));
    }

    [Fact]
    public void Sub_Millisecond_Minimum_Is_Rejected()
    {
        // It would round to a zero-length interval, which no segment can be.
        Assert.False(IsValid(new VideoSemanticSegmentationOptions
        {
            MinimumSegmentSeconds = 0.0004, TargetSegmentSeconds = 8, MaximumSegmentSeconds = 20,
        }));
    }

    [Fact]
    public void Derived_Millisecond_Views_Are_Integral()
    {
        var options = new VideoSemanticSegmentationOptions
        {
            MinimumSegmentSeconds = 2.5, TargetSegmentSeconds = 8.25, MaximumSegmentSeconds = 20,
        };

        Assert.Equal(2_500, options.MinimumSegmentMilliseconds);
        Assert.Equal(8_250, options.TargetSegmentMilliseconds);
        Assert.Equal(20_000, options.MaximumSegmentMilliseconds);
    }

    [Fact]
    public void Maximum_Capacity_Is_The_Product_Of_Both_Hard_Limits()
    {
        var options = new VideoSemanticSegmentationOptions
        {
            MaximumSegmentsPerVideo = 400, MaximumSegmentSeconds = 20,
        };

        Assert.Equal(400L * 20_000L, options.MaximumCapacityMilliseconds);
    }

    [Fact]
    public void Maximum_Capacity_Saturates_Instead_Of_Overflowing()
    {
        // A pathological configuration must clamp to long.MaxValue ("nothing
        // exceeds it"), never wrap into a small bogus capacity.
        var options = new VideoSemanticSegmentationOptions
        {
            MaximumSegmentsPerVideo = int.MaxValue,
            MaximumSegmentSeconds = 1e15,
        };

        Assert.Equal(long.MaxValue, options.MaximumCapacityMilliseconds);
    }

    [Fact]
    public void Validation_Runs_Even_When_The_Capability_Is_Disabled()
    {
        // A broken section must be caught before someone flips the switch.
        Assert.False(IsValid(new VideoSemanticSegmentationOptions
        {
            Enabled = false, MaximumSegmentsPerVideo = -5,
        }));
    }
}
