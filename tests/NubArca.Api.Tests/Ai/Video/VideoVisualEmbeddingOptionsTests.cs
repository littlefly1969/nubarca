using NubArca.Api.Ai.Video;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-02: the options guard for video visual embeddings.
public sealed class VideoVisualEmbeddingOptionsTests
{
    private static readonly VideoVisualEmbeddingOptionsValidator Validator = new();

    private static bool IsValid(VideoVisualEmbeddingOptions options)
        => Validator.Validate(null, options).Succeeded;

    [Fact]
    public void Defaults_Are_Valid_And_Disabled()
    {
        var options = new VideoVisualEmbeddingOptions();

        Assert.False(options.Enabled);       // AI is off by default
        Assert.True(IsValid(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Frame_Timeout_Must_Be_Positive(int seconds)
        => Assert.False(IsValid(new VideoVisualEmbeddingOptions { FrameTimeoutSeconds = seconds }));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Frame_Output_Cap_Must_Be_Positive(int bytes)
        => Assert.False(IsValid(new VideoVisualEmbeddingOptions { MaximumFrameOutputBytes = bytes }));

    [Fact]
    public void Frame_Edge_Below_The_Model_Input_Is_Rejected()
    {
        // Frames smaller than the SigLIP2 input edge would be upsampled by the
        // model preprocessing — degraded inference for no gain.
        Assert.False(IsValid(new VideoVisualEmbeddingOptions { FrameMaxEdge = 383 }));
        Assert.True(IsValid(new VideoVisualEmbeddingOptions { FrameMaxEdge = 384 }));
    }

    [Fact]
    public void Validation_Runs_Even_When_The_Capability_Is_Disabled()
    {
        Assert.False(IsValid(new VideoVisualEmbeddingOptions
        {
            Enabled = false, FrameTimeoutSeconds = 0,
        }));
    }
}
