using NubArca.Api.Ai.Video;
using NubArca.Api.Domain.Ai;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-01: the PURE normalizer. No database, no process, no clock — every case
// here is a deterministic function of (duration, candidates, options), which is
// exactly what makes a manifest safe to key by (blob, segmentation version).
public sealed class VideoSemanticManifestBuilderTests
{
    private static VideoSemanticSegmentationOptions Options(
        double minimum = 2, double target = 8, double maximum = 20,
        int maxSegments = 400, int samplesPerSegment = 1, int maxSamples = 600) => new()
        {
            MinimumSegmentSeconds = minimum,
            TargetSegmentSeconds = target,
            MaximumSegmentSeconds = maximum,
            MaximumSegmentsPerVideo = maxSegments,
            SamplesPerSegment = samplesPerSegment,
            MaximumSamplesPerVideo = maxSamples,
        };

    // The invariants every COMPLETED manifest must satisfy, asserted after each
    // scenario rather than restated per test.
    private static void AssertManifestInvariants(VideoSemanticManifest manifest)
    {
        Assert.NotEmpty(manifest.Segments);

        for (var i = 0; i < manifest.Segments.Count; i++)
        {
            var segment = manifest.Segments[i];

            Assert.Equal(i, segment.SegmentIndex);                       // contiguous indexes
            Assert.True(segment.EndMilliseconds > segment.StartMilliseconds); // positive length

            if (i == 0)
            {
                Assert.Equal(0, segment.StartMilliseconds);              // starts at zero
            }
            else
            {
                // chronological, non-overlapping, gapless
                Assert.Equal(manifest.Segments[i - 1].EndMilliseconds, segment.StartMilliseconds);
            }

            for (var s = 0; s < segment.Samples.Count; s++)
            {
                var sample = segment.Samples[s];
                Assert.Equal(s, sample.SampleIndex);
                Assert.InRange(
                    sample.TimestampMilliseconds,
                    segment.StartMilliseconds,
                    segment.EndMilliseconds - 1);                        // containment
            }
        }

        // last segment reaches the normalized duration
        Assert.Equal(manifest.DurationMilliseconds, manifest.Segments[^1].EndMilliseconds);
        Assert.Equal(manifest.Segments.Sum(s => s.Samples.Count), manifest.SampleCount);
    }

    [Fact]
    public void No_Boundaries_Falls_Back_To_Bounded_Uniform_Segmentation()
    {
        var manifest = VideoSemanticManifestBuilder.Build(60_000, Array.Empty<double>(), Options());

        Assert.True(manifest.FallbackUsed);
        Assert.Equal(0, manifest.CandidateCount);
        // 60 s at an 8 s target → 8 balanced segments (7 × 7500 ms + 1 × 7500).
        Assert.Equal(8, manifest.SegmentCount);
        Assert.All(manifest.Segments.Skip(1), s =>
            Assert.Equal(VideoSemanticBoundaryReasons.Uniform, s.BoundaryReason));
        Assert.Equal(VideoSemanticBoundaryReasons.Start, manifest.Segments[0].BoundaryReason);
        AssertManifestInvariants(manifest);
    }

    [Fact]
    public void Unordered_And_Duplicate_Boundaries_Are_Sorted_And_Deduplicated()
    {
        var manifest = VideoSemanticManifestBuilder.Build(
            60_000, new[] { 40.0, 10.0, 25.0, 10.0, 25.0, 40.0 }, Options());

        Assert.Equal(3, manifest.CandidateCount);   // 10, 25, 40 — duplicates removed
        Assert.False(manifest.FallbackUsed);
        Assert.Equal(
            new long[] { 0, 10_000, 25_000, 40_000 },
            manifest.Segments.Select(s => s.StartMilliseconds).ToArray());
        AssertManifestInvariants(manifest);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-5.0)]
    [InlineData(0.0)]
    [InlineData(60.0)]      // exactly the duration — not interior
    [InlineData(9999.0)]    // beyond the duration
    [InlineData(1e300)]     // would overflow a naive seconds→ms cast
    public void Invalid_And_Out_Of_Range_Timestamps_Are_Rejected(double candidate)
    {
        var manifest = VideoSemanticManifestBuilder.Build(60_000, new[] { candidate }, Options());

        Assert.Equal(0, manifest.CandidateCount);
        Assert.True(manifest.FallbackUsed);
        AssertManifestInvariants(manifest);
    }

    [Fact]
    public void Near_Duplicate_Boundaries_Collapse_To_One()
    {
        // A hard cut emits a burst of near-identical candidates; they must not
        // become a burst of millisecond-long segments.
        var manifest = VideoSemanticManifestBuilder.Build(
            60_000, new[] { 20.000, 20.017, 20.033, 20.050 }, Options(maximum: 60));

        Assert.Equal(4, manifest.CandidateCount);
        Assert.Equal(2, manifest.SegmentCount);
        Assert.Equal(new long[] { 0, 20_000 }, manifest.Segments.Select(s => s.StartMilliseconds).ToArray());
        AssertManifestInvariants(manifest);
    }

    [Fact]
    public void Short_Segments_Are_Merged_Into_Their_Predecessor()
    {
        // Candidates at 3 s and 4 s: with a 2 s minimum only the first survives.
        var manifest = VideoSemanticManifestBuilder.Build(
            30_000, new[] { 3.0, 4.0, 4.5 }, Options(minimum: 2, maximum: 60));

        Assert.Equal(new long[] { 0, 3_000 }, manifest.Segments.Select(s => s.StartMilliseconds).ToArray());
        Assert.All(manifest.Segments, s => Assert.True(s.LengthMilliseconds >= 2_000));
        AssertManifestInvariants(manifest);
    }

    [Fact]
    public void Trailing_Sliver_Boundary_Is_Dropped()
    {
        // A candidate 500 ms before the end would leave a sliver final segment.
        // Once it is merged away nothing usable is left, so the bounded uniform
        // fallback takes over — never a 29.5 s shot plus a 500 ms tail.
        var manifest = VideoSemanticManifestBuilder.Build(
            30_000, new[] { 29.5 }, Options(minimum: 2, target: 30, maximum: 60));

        Assert.True(manifest.FallbackUsed);
        Assert.Single(manifest.Segments);
        Assert.Equal(30_000, manifest.Segments[0].EndMilliseconds);
        AssertManifestInvariants(manifest);
    }

    [Fact]
    public void Long_Segments_Are_Split_Into_Balanced_Parts()
    {
        // A 15 s shot then one 85 s shot with a 20 s ceiling → the long shot
        // becomes 5 balanced parts of 17 s, NOT 4 × 20 s plus a 5 s orphan.
        var manifest = VideoSemanticManifestBuilder.Build(
            100_000, new[] { 15.0 }, Options(target: 20, maximum: 20));

        Assert.Equal(6, manifest.SegmentCount);
        Assert.Equal(15_000, manifest.Segments[0].LengthMilliseconds);
        Assert.All(manifest.Segments.Skip(1), s => Assert.Equal(17_000, s.LengthMilliseconds));
        AssertManifestInvariants(manifest);
    }

    [Fact]
    public void Split_Preserves_The_Inherited_Boundary_Reason_On_The_First_Piece()
    {
        var manifest = VideoSemanticManifestBuilder.Build(
            100_000, new[] { 50.0 }, Options(maximum: 20, target: 20));

        Assert.Equal(VideoSemanticBoundaryReasons.Start, manifest.Segments[0].BoundaryReason);
        var atFifty = manifest.Segments.Single(s => s.StartMilliseconds == 50_000);
        Assert.Equal(VideoSemanticBoundaryReasons.Scene, atFifty.BoundaryReason);
        Assert.Contains(manifest.Segments, s => s.BoundaryReason == VideoSemanticBoundaryReasons.Split);
        AssertManifestInvariants(manifest);
    }

    [Fact]
    public void Balanced_Split_Distributes_The_Remainder_Exactly()
    {
        // 10 001 ms over 3 parts: 3334 + 3334 + 3333 — the pieces must sum to
        // the interval EXACTLY, or a gap/overshoot appears at the end.
        var manifest = VideoSemanticManifestBuilder.Build(
            10_001, Array.Empty<double>(), Options(minimum: 1, target: 4, maximum: 4));

        Assert.Equal(10_001, manifest.Segments.Sum(s => s.LengthMilliseconds));
        Assert.True(manifest.Segments.Max(s => s.LengthMilliseconds)
            - manifest.Segments.Min(s => s.LengthMilliseconds) <= 1);
        AssertManifestInvariants(manifest);
    }

    [Fact]
    public void Segment_Cap_Rebuilds_A_Bounded_Uniform_Manifest()
    {
        // 1000 candidates, cap 10: the result is capped AND still gapless and
        // still ends exactly at the duration.
        var candidates = Enumerable.Range(1, 1000).Select(i => i * 5.0).ToArray();
        var manifest = VideoSemanticManifestBuilder.Build(
            6_000_000, candidates, Options(minimum: 2, target: 8, maximum: 20, maxSegments: 10));

        Assert.Equal(10, manifest.SegmentCount);
        Assert.All(manifest.Segments.Skip(1), s =>
            Assert.Equal(VideoSemanticBoundaryReasons.Cap, s.BoundaryReason));
        AssertManifestInvariants(manifest);
    }

    [Fact]
    public void Sample_Cap_Bounds_The_Whole_Video()
    {
        var manifest = VideoSemanticManifestBuilder.Build(
            600_000, Array.Empty<double>(),
            Options(target: 10, maximum: 10, maxSegments: 60, samplesPerSegment: 5, maxSamples: 60));

        Assert.Equal(60, manifest.SegmentCount);
        Assert.True(manifest.SampleCount <= 60);
        // The budget is spread, not spent on the first segments.
        Assert.All(manifest.Segments, s => Assert.Single(s.Samples));
        AssertManifestInvariants(manifest);
    }

    [Fact]
    public void Single_Sample_Sits_At_The_Inward_Midpoint_Never_On_A_Cut()
    {
        var manifest = VideoSemanticManifestBuilder.Build(
            20_000, new[] { 10.0 }, Options(maximum: 60));

        Assert.Equal(2, manifest.SegmentCount);
        Assert.Equal(5_000, manifest.Segments[0].Samples[0].TimestampMilliseconds);
        Assert.Equal(15_000, manifest.Segments[1].Samples[0].TimestampMilliseconds);
        Assert.All(manifest.Segments, s =>
            Assert.Equal(VideoSemanticSelectionReasons.Midpoint, s.Samples[0].SelectionReason));
        // No sample lands exactly on a boundary.
        Assert.DoesNotContain(manifest.Segments.SelectMany(s => s.Samples),
            sample => sample.TimestampMilliseconds == 0 || sample.TimestampMilliseconds == 10_000);
        AssertManifestInvariants(manifest);
    }

    [Fact]
    public void Multiple_Samples_Are_Evenly_Spaced_Interior_Positions()
    {
        var manifest = VideoSemanticManifestBuilder.Build(
            12_000, Array.Empty<double>(),
            Options(target: 12, maximum: 12, samplesPerSegment: 3, maxSamples: 600));

        var samples = manifest.Segments.Single().Samples;
        Assert.Equal(new long[] { 3_000, 6_000, 9_000 },
            samples.Select(s => s.TimestampMilliseconds).ToArray());
        Assert.All(samples, s =>
            Assert.Equal(VideoSemanticSelectionReasons.Interior, s.SelectionReason));
        AssertManifestInvariants(manifest);
    }

    [Fact]
    public void Very_Short_Video_Still_Produces_A_Valid_Single_Segment()
    {
        var manifest = VideoSemanticManifestBuilder.Build(1, Array.Empty<double>(), Options());

        Assert.Single(manifest.Segments);
        Assert.Equal(0, manifest.Segments[0].StartMilliseconds);
        Assert.Equal(1, manifest.Segments[0].EndMilliseconds);
        AssertManifestInvariants(manifest);
    }

    [Fact]
    public void Output_Is_Deterministic_Across_Runs_And_Input_Order()
    {
        var options = Options();
        var forward = VideoSemanticManifestBuilder.Build(
            120_000, new[] { 12.5, 30.25, 61.0, 95.75 }, options);
        var shuffled = VideoSemanticManifestBuilder.Build(
            120_000, new[] { 95.75, 12.5, 61.0, 30.25, 12.5 }, options);

        Assert.Equal(
            forward.Segments.Select(s => (s.SegmentIndex, s.StartMilliseconds, s.EndMilliseconds, s.BoundaryReason)),
            shuffled.Segments.Select(s => (s.SegmentIndex, s.StartMilliseconds, s.EndMilliseconds, s.BoundaryReason)));
        Assert.Equal(
            forward.Segments.SelectMany(s => s.Samples).Select(s => s.TimestampMilliseconds),
            shuffled.Segments.SelectMany(s => s.Samples).Select(s => s.TimestampMilliseconds));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_Positive_Duration_Is_Rejected(long duration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VideoSemanticManifestBuilder.Build(duration, Array.Empty<double>(), Options()));
    }
}
