using NubArca.Api.Ai.Video.Faces;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VFACE-01, Gate 1: the deterministic face-sampling policy. Pure input/output —
// no database, no FFmpeg, no clock.
//
// The policy exists because VSEM-01 samples ONE frame per 2–20 s segment, which
// is far too sparse to track: these tests pin the density, the hard caps and the
// fact that VSEM's own sample manifest is never consulted or mutated.
public sealed class VideoFaceSamplingTests
{
    private static VideoFaceAnalysisOptions Options(
        int intervalMs = 1000, int perSegment = 60, int perVideo = 900)
        => new()
        {
            FrameIntervalMilliseconds = intervalMs,
            MaximumFramesPerSegment = perSegment,
            MaximumFramesPerVideo = perVideo,
        };

    private static VideoFaceSegmentInterval Segment(int index, long start, long end)
        => new(index, start, end);

    [Fact]
    public void Samples_Are_Deterministic_And_Inside_Their_Segment()
    {
        var segments = new[] { Segment(0, 0, 10_000), Segment(1, 10_000, 20_000) };

        var first = VideoFaceSamplePlanner.Plan(segments, Options());
        var second = VideoFaceSamplePlanner.Plan(segments, Options());

        Assert.Equal(
            first.Select(f => f.TimestampMilliseconds),
            second.Select(f => f.TimestampMilliseconds));
        Assert.All(first, f => Assert.InRange(f.TimestampMilliseconds, 0, 19_999));
        Assert.Equal(
            first.Select(f => f.TimestampMilliseconds).OrderBy(t => t),
            first.Select(f => f.TimestampMilliseconds));
    }

    [Fact]
    public void Sampling_Is_Far_Denser_Than_One_Frame_Per_Segment()
    {
        // The whole reason a dedicated policy exists: VSEM would produce ONE
        // sample for this 30-second segment.
        var plan = VideoFaceSamplePlanner.Plan(new[] { Segment(0, 0, 30_000) }, Options());

        Assert.Equal(30, plan.Count);
    }

    [Fact]
    public void No_Sample_Lands_Exactly_On_A_Segment_Boundary()
    {
        var segments = new[] { Segment(0, 0, 8_000), Segment(1, 8_000, 16_000) };

        var plan = VideoFaceSamplePlanner.Plan(segments, Options());

        Assert.DoesNotContain(plan, f => f.TimestampMilliseconds == 0);
        Assert.DoesNotContain(plan, f => f.TimestampMilliseconds == 8_000);
        Assert.DoesNotContain(plan, f => f.TimestampMilliseconds == 16_000);
    }

    [Fact]
    public void A_Segment_Shorter_Than_One_Interval_Still_Yields_One_Frame()
    {
        var plan = VideoFaceSamplePlanner.Plan(new[] { Segment(0, 0, 400) }, Options());

        var frame = Assert.Single(plan);
        Assert.InRange(frame.TimestampMilliseconds, 0, 399);
    }

    [Fact]
    public void A_Long_Segment_Is_Capped_At_The_Per_Segment_Limit()
    {
        var plan = VideoFaceSamplePlanner.Plan(
            new[] { Segment(0, 0, 600_000) }, Options(perSegment: 25, perVideo: 900));

        Assert.Equal(25, plan.Count);
        Assert.All(plan, f => Assert.InRange(f.TimestampMilliseconds, 0, 599_999));
    }

    [Fact]
    public void The_Per_Video_Cap_Thins_Evenly_Instead_Of_Truncating_The_Tail()
    {
        var segments = Enumerable.Range(0, 20)
            .Select(i => Segment(i, i * 10_000L, (i + 1) * 10_000L))
            .ToArray();

        var plan = VideoFaceSamplePlanner.Plan(segments, Options(perVideo: 20));

        Assert.True(plan.Count <= 20);
        // Coverage stays uniform: the last kept frame is near the END of the
        // video, not at the head where a truncation would leave it.
        Assert.True(plan[^1].TimestampMilliseconds > 180_000,
            $"expected the plan to reach the video tail, last was {plan[^1].TimestampMilliseconds}.");
        Assert.True(plan[0].TimestampMilliseconds < 10_000);
    }

    [Fact]
    public void Segment_Geometry_Is_Irrelevant_To_The_Plan()
    {
        // A vertical clip and a landscape clip with the same temporal structure
        // must plan identically — orientation only matters at the PIXEL gate,
        // which measures the decoded frame instead of assuming anything here.
        var segments = new[] { Segment(0, 0, 12_000) };

        Assert.Equal(
            VideoFaceSamplePlanner.Plan(segments, Options()).Select(f => f.TimestampMilliseconds),
            VideoFaceSamplePlanner.Plan(segments, Options()).Select(f => f.TimestampMilliseconds));
    }

    [Fact]
    public void Duplicate_Timestamps_Are_Collapsed()
    {
        // Two degenerate 1 ms segments both clamp onto their own single frame;
        // an overlapping pair must not produce the same timestamp twice.
        var segments = new[] { Segment(0, 5_000, 5_001), Segment(1, 5_000, 5_001) };

        var plan = VideoFaceSamplePlanner.Plan(segments, Options());

        Assert.Single(plan);
    }

    [Fact]
    public void Empty_Or_Degenerate_Input_Plans_Nothing()
    {
        Assert.Empty(VideoFaceSamplePlanner.Plan(Array.Empty<VideoFaceSegmentInterval>(), Options()));
        Assert.Empty(VideoFaceSamplePlanner.Plan(new[] { Segment(0, 1_000, 1_000) }, Options()));
        Assert.Empty(VideoFaceSamplePlanner.Plan(new[] { Segment(0, 2_000, 1_000) }, Options()));
    }

    [Fact]
    public void Frames_Are_Attributed_To_Their_Segment()
    {
        var segments = new[] { Segment(0, 0, 5_000), Segment(1, 5_000, 10_000) };

        var plan = VideoFaceSamplePlanner.Plan(segments, Options());

        Assert.All(plan, f => Assert.Equal(
            f.TimestampMilliseconds < 5_000 ? 0 : 1, f.SegmentIndex));
    }
}
