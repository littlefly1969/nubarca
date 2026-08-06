using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Video;
using NubArca.Api.Ai.Video.Faces;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VFACE-01: the configuration contract. Validation runs even when the capability
// is disabled, so a section that could produce unbounded work or meaningless
// tracks is caught before anyone flips the switch in production.
public sealed class VideoFaceAnalysisOptionsTests
{
    private static VideoFaceAnalysisOptions Valid() => new();

    private static IReadOnlyList<string> Validate(VideoFaceAnalysisOptions options)
    {
        var result = new VideoFaceAnalysisOptionsValidator().Validate(null, options);
        return result.Failures?.ToList() ?? [];
    }

    [Fact]
    public void The_Capability_Is_Disabled_By_Default()
    {
        Assert.False(Valid().Enabled);
    }

    [Fact]
    public void Defaults_Are_Valid_And_Bounded()
    {
        var options = Valid();

        Assert.Empty(Validate(options));
        Assert.True(options.MaximumFramesPerVideo > 0);
        Assert.True(options.MaximumFramesPerSegment > 0);
        Assert.True(options.MaximumFacesPerFrame > 0);
        Assert.True(options.ProcessTimeoutSeconds > 0);
    }

    [Fact]
    public void Validation_Runs_Even_When_Disabled()
    {
        var options = Valid();
        options.Enabled = false;
        options.MaximumFramesPerVideo = 0;

        Assert.NotEmpty(Validate(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_Non_Positive_Analysis_Version_Is_Rejected(int version)
    {
        var options = Valid();
        options.AnalysisVersion = version;

        Assert.Contains(Validate(options), e => e.Contains("AnalysisVersion"));
    }

    [Fact]
    public void Non_Positive_Caps_Are_Rejected()
    {
        foreach (var mutate in new Action<VideoFaceAnalysisOptions>[]
        {
            o => o.FrameIntervalMilliseconds = 0,
            o => o.MaximumFramesPerSegment = 0,
            o => o.MaximumFramesPerVideo = 0,
            o => o.MaximumFacesPerFrame = 0,
            o => o.MinimumFaceSizePixels = 0,
            o => o.MinimumTrackDetections = 0,
            o => o.MaximumTrackGapMilliseconds = 0,
            o => o.ProcessTimeoutSeconds = 0,
        })
        {
            var options = Valid();
            mutate(options);
            Assert.NotEmpty(Validate(options));
        }
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Out_Of_Range_Unit_Thresholds_Are_Rejected(double value)
    {
        foreach (var mutate in new Action<VideoFaceAnalysisOptions>[]
        {
            o => o.MinimumDetectionConfidence = value,
            o => o.MinimumQualityScore = value,
            o => o.MinimumAssociationSimilarity = value,
            o => o.MinimumAssociationIou = value,
            o => o.MaximumAssociationCenterDistance = value,
            o => o.TrackOutlierSimilarity = value,
        })
        {
            var options = Valid();
            mutate(options);
            Assert.NotEmpty(Validate(options));
        }
    }

    [Fact]
    public void A_Scale_Ratio_Below_One_Is_Rejected()
    {
        var options = Valid();
        options.MaximumAssociationScaleRatio = 0.5;

        Assert.Contains(Validate(options), e => e.Contains("MaximumAssociationScaleRatio"));
    }

    [Fact]
    public void A_Segment_Cap_Above_The_Video_Cap_Is_Rejected()
    {
        // Otherwise the per-segment cap is dead configuration and the real bound
        // is invisible to the operator.
        var options = Valid();
        options.MaximumFramesPerSegment = 1000;
        options.MaximumFramesPerVideo = 100;

        Assert.Contains(Validate(options), e => e.Contains("MaximumFramesPerSegment"));
    }

    [Fact]
    public void A_Gap_Shorter_Than_The_Sampling_Interval_Is_Rejected()
    {
        // Every track would close after a single detection and never reach the
        // evidence floor — a silently empty substrate.
        var options = Valid();
        options.FrameIntervalMilliseconds = 2000;
        options.MaximumTrackGapMilliseconds = 500;

        Assert.Contains(Validate(options), e => e.Contains("MaximumTrackGapMilliseconds"));
    }

    // ---- VFACE-01C: frame resolution is this pipeline's own -----------------

    [Fact]
    public void The_Default_Face_Frame_Edge_Is_768()
    {
        Assert.Equal(768, Valid().FrameMaxEdge);
        Assert.Equal(768, VideoFaceAnalysisOptions.DefaultFrameMaxEdge);
    }

    [Theory]
    [InlineData(639)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8193)]
    public void An_Out_Of_Range_Face_Frame_Edge_Is_Rejected(int edge)
    {
        var options = Valid();
        options.FrameMaxEdge = edge;

        Assert.Contains(Validate(options), e => e.Contains("FrameMaxEdge"));
    }

    [Theory]
    [InlineData(640)]
    [InlineData(768)]
    [InlineData(8192)]
    public void An_In_Range_Face_Frame_Edge_Is_Accepted(int edge)
    {
        var options = Valid();
        options.FrameMaxEdge = edge;

        Assert.Empty(Validate(options));
    }

    [Fact]
    public void A_Pixel_Gate_Larger_Than_The_Frame_Is_Rejected()
    {
        // No face could ever clear it — the analysis would be silently empty.
        var options = Valid();
        options.FrameMaxEdge = 640;
        options.MinimumFaceSizePixels = 1024;
        options.QualityReferenceFaceSizePixels = 2048;

        Assert.Contains(Validate(options), e => e.Contains("MinimumFaceSizePixels"));
    }

    [Fact]
    public void The_Face_Frame_Edge_Is_Independent_Of_The_Video_Embedding_One()
    {
        // Two separate option objects bound from two separate sections: neither
        // default nor validation of one can move the other.
        var face = new VideoFaceAnalysisOptions { FrameMaxEdge = 1280 };
        var semantic = new VideoVisualEmbeddingOptions { FrameMaxEdge = 384 };

        Assert.Empty(Validate(face));
        Assert.Equal(1280, face.FrameMaxEdge);
        Assert.Equal(384, semantic.FrameMaxEdge);
        Assert.NotEqual(
            VideoFaceAnalysisOptions.SectionName, VideoVisualEmbeddingOptions.SectionName);

        // A value VSEM-02 accepts (its floor is the 384 SigLIP2 input edge) is
        // NOT automatically valid for face analysis, whose floor is the 640
        // detector input edge. The two contracts are genuinely different.
        var tooSmallForFaces = new VideoFaceAnalysisOptions { FrameMaxEdge = 384 };
        Assert.NotEmpty(Validate(tooSmallForFaces));
        Assert.Equal(
            ValidateOptionsResult.Success,
            new VideoVisualEmbeddingOptionsValidator().Validate(null, semantic));
    }

    [Fact]
    public void A_Minimum_Face_Size_Above_The_Quality_Reference_Is_Rejected()
    {
        var options = Valid();
        options.MinimumFaceSizePixels = 200;
        options.QualityReferenceFaceSizePixels = 100;

        Assert.Contains(Validate(options), e => e.Contains("MinimumFaceSizePixels"));
    }

    [Fact]
    public void Every_Failure_Is_Prefixed_With_The_Section_Name()
    {
        var options = Valid();
        options.MaximumFramesPerVideo = 0;

        Assert.All(Validate(options), e => Assert.StartsWith(
            VideoFaceAnalysisOptions.SectionName, e, StringComparison.Ordinal));
    }

    // ---- the quality heuristic ---------------------------------------------

    [Fact]
    public void Quality_Combines_Confidence_And_Face_Size()
    {
        // A big confident face beats a small confident one, and both stay in
        // [0, 1] — the database check constraint depends on it.
        var big = VideoFaceQuality.Score(0.9, 160, 160, 160);
        var small = VideoFaceQuality.Score(0.9, 40, 40, 160);

        Assert.True(big > small);
        Assert.InRange(big, 0d, 1d);
        Assert.InRange(small, 0d, 1d);
        Assert.Equal(0.9, big, 6);
    }

    [Fact]
    public void Quality_Saturates_At_The_Reference_Size()
    {
        Assert.Equal(
            VideoFaceQuality.Score(1.0, 160, 160, 160),
            VideoFaceQuality.Score(1.0, 1600, 1600, 160),
            6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    public void Quality_Is_Zero_Without_A_Usable_Confidence(double? confidence)
    {
        Assert.Equal(0d, VideoFaceQuality.Score(confidence, 160, 160, 160));
    }

    [Fact]
    public void Quality_Is_Zero_For_A_Degenerate_Face()
    {
        Assert.Equal(0d, VideoFaceQuality.Score(1.0, 0, 100, 160));
        Assert.Equal(0d, VideoFaceQuality.Score(1.0, double.NaN, 100, 160));
        Assert.Equal(0d, VideoFaceQuality.Score(1.0, 100, 100, 0));
    }
}
