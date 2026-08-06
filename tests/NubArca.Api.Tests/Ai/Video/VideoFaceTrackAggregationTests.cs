using NubArca.Api.Ai.Video.Faces;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VFACE-01, Gate 3: track finalization. Pure input/output.
//
// Pins the aggregation contract a downstream identity layer depends on: the
// track vector is a QUALITY-WEIGHTED normalized mean over outlier-filtered
// detections, the representative is the BEST detection rather than the first,
// and a degenerate or under-evidenced track never reaches the database.
public sealed class VideoFaceTrackAggregationTests
{
    private const int Dim = 4;

    private static VideoFaceAnalysisOptions Options(
        int minimumDetections = 3, double outlierSimilarity = 0.3) => new()
        {
            MinimumTrackDetections = minimumDetections,
            TrackOutlierSimilarity = outlierSimilarity,
        };

    private static VideoFaceObservation Face(
        int frame, long timestamp, float[] embedding, double quality = 0.5,
        double x = 0.4, int faceIndex = 0)
        => new(frame, timestamp, faceIndex, x, 0.4, 0.2, 0.2, 0.9, quality, embedding);

    private static VideoFaceTrackDraft Draft(params VideoFaceObservation[] detections)
        => new(0, detections);

    private static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
        => VideoFaceTrackerProbe.Cosine(a, b);

    [Fact]
    public void The_Aggregate_Is_Normalized_And_Quality_Weighted()
    {
        // Two identities in one draft, one clearly higher quality: the aggregate
        // must lean towards the high-quality evidence, not sit at the midpoint.
        var strong = new[] { 1f, 0f, 0f, 0f };
        var weak = new[] { 0f, 1f, 0f, 0f };
        var draft = Draft(
            Face(0, 0, strong, quality: 0.9),
            Face(1, 1000, strong, quality: 0.9),
            Face(2, 2000, weak, quality: 0.05));

        var result = VideoFaceTrackAggregator.Finalize(draft, Dim, Options(outlierSimilarity: 0d));

        Assert.NotNull(result);
        var norm = Math.Sqrt(result!.Embedding.Sum(v => (double)v * v));
        Assert.Equal(1d, norm, 5);
        Assert.True(Cosine(result.Embedding, strong) > Cosine(result.Embedding, weak));
    }

    [Fact]
    public void The_Representative_Is_The_Highest_Quality_Detection_Not_The_First()
    {
        var vector = new[] { 1f, 0f, 0f, 0f };
        var draft = Draft(
            Face(0, 0, vector, quality: 0.1),
            Face(1, 1000, vector, quality: 0.95, x: 0.55),
            Face(2, 2000, vector, quality: 0.3));

        var result = VideoFaceTrackAggregator.Finalize(draft, Dim, Options());

        Assert.NotNull(result);
        Assert.Equal(1000, result!.RepresentativeTimestampMilliseconds);
        Assert.Equal(0.55, result.RepresentativeBoundingBoxX, 6);
    }

    [Fact]
    public void The_Representative_Timestamp_Lies_Inside_The_Track_Interval()
    {
        var vector = new[] { 1f, 0f, 0f, 0f };
        var draft = Draft(
            Face(0, 4000, vector, quality: 0.2),
            Face(1, 5000, vector, quality: 0.9),
            Face(2, 6000, vector, quality: 0.2));

        var result = VideoFaceTrackAggregator.Finalize(draft, Dim, Options());

        Assert.NotNull(result);
        Assert.Equal(4000, result!.StartMilliseconds);
        Assert.Equal(6000, result.EndMilliseconds);
        Assert.InRange(
            result.RepresentativeTimestampMilliseconds,
            result.StartMilliseconds, result.EndMilliseconds);
    }

    [Fact]
    public void An_Outlier_Detection_Is_Rejected_From_The_Aggregate()
    {
        var identity = new[] { 1f, 0f, 0f, 0f };
        var intruder = new[] { 0f, 0f, 1f, 0f };
        var draft = Draft(
            Face(0, 0, identity),
            Face(1, 1000, identity),
            Face(2, 2000, identity),
            Face(3, 3000, intruder));

        var result = VideoFaceTrackAggregator.Finalize(draft, Dim, Options(outlierSimilarity: 0.5));

        Assert.NotNull(result);
        Assert.Equal(3, result!.DetectionCount);
        Assert.True(Cosine(result.Embedding, identity) > 0.99);
    }

    [Fact]
    public void Outlier_Rejection_Never_Empties_A_Uniformly_Scattered_Track()
    {
        // Every detection is "far" from the mean. Rejecting them all would lose
        // a real track, so the full set is kept instead.
        var draft = Draft(
            Face(0, 0, [1f, 0f, 0f, 0f]),
            Face(1, 1000, [0f, 1f, 0f, 0f]),
            Face(2, 2000, [0f, 0f, 1f, 0f]));

        var result = VideoFaceTrackAggregator.Finalize(draft, Dim, Options(outlierSimilarity: 0.99));

        Assert.NotNull(result);
        Assert.Equal(3, result!.DetectionCount);
    }

    [Fact]
    public void A_Track_Below_The_Evidence_Floor_Is_Discarded()
    {
        var vector = new[] { 1f, 0f, 0f, 0f };
        var draft = Draft(Face(0, 0, vector), Face(1, 1000, vector));

        Assert.Null(VideoFaceTrackAggregator.Finalize(draft, Dim, Options(minimumDetections: 3)));
    }

    [Fact]
    public void A_Track_Whose_Detections_Are_All_Outliers_Falls_Below_The_Floor_Only_On_Count()
    {
        var identity = new[] { 1f, 0f, 0f, 0f };
        var intruder = new[] { 0f, 0f, 1f, 0f };
        var draft = Draft(
            Face(0, 0, identity),
            Face(1, 1000, identity),
            Face(2, 2000, intruder));

        // Two survive outlier rejection; the floor of three discards the track.
        Assert.Null(VideoFaceTrackAggregator.Finalize(
            draft, Dim, Options(minimumDetections: 3, outlierSimilarity: 0.5)));
    }

    [Fact]
    public void A_Wrong_Dimension_Detection_Is_Not_Aggregated()
    {
        var draft = Draft(
            Face(0, 0, [1f, 0f, 0f, 0f]),
            Face(1, 1000, [1f, 0f, 0f, 0f]),
            Face(2, 2000, [1f, 0f]));

        var result = VideoFaceTrackAggregator.Finalize(draft, Dim, Options(minimumDetections: 2));

        Assert.NotNull(result);
        Assert.Equal(2, result!.DetectionCount);
        Assert.Equal(Dim, result.Embedding.Length);
    }

    [Fact]
    public void A_Track_Whose_Detections_All_Have_The_Wrong_Dimension_Is_Discarded()
    {
        var draft = Draft(
            Face(0, 0, [1f, 0f]),
            Face(1, 1000, [1f, 0f]),
            Face(2, 2000, [1f, 0f]));

        Assert.Null(VideoFaceTrackAggregator.Finalize(draft, Dim, Options()));
    }

    [Fact]
    public void Profiles_Are_Isolated_By_The_Expected_Dimension()
    {
        var draft = Draft(
            Face(0, 0, [1f, 0f, 0f, 0f]),
            Face(1, 1000, [1f, 0f, 0f, 0f]),
            Face(2, 2000, [1f, 0f, 0f, 0f]));

        Assert.NotNull(VideoFaceTrackAggregator.Finalize(draft, 4, Options()));
        // The SAME detections under a different profile's dimension are simply
        // not that profile's evidence.
        Assert.Null(VideoFaceTrackAggregator.Finalize(draft, 8, Options()));
        Assert.Null(VideoFaceTrackAggregator.Finalize(draft, 0, Options()));
    }

    [Fact]
    public void Non_Finite_And_Zero_Norm_Detections_Are_Rejected()
    {
        var good = new[] { 1f, 0f, 0f, 0f };
        var draft = Draft(
            Face(0, 0, good),
            Face(1, 1000, good),
            Face(2, 2000, [float.NaN, 0f, 0f, 0f]),
            Face(3, 3000, [float.PositiveInfinity, 0f, 0f, 0f]),
            Face(4, 4000, [0f, 0f, 0f, 0f]));

        var result = VideoFaceTrackAggregator.Finalize(draft, Dim, Options(minimumDetections: 2));

        Assert.NotNull(result);
        Assert.Equal(2, result!.DetectionCount);
        Assert.All(result.Embedding, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void An_Entirely_Non_Finite_Track_Is_Discarded()
    {
        var draft = Draft(
            Face(0, 0, [float.NaN, 0f, 0f, 0f]),
            Face(1, 1000, [float.NaN, 0f, 0f, 0f]),
            Face(2, 2000, [float.NaN, 0f, 0f, 0f]));

        Assert.Null(VideoFaceTrackAggregator.Finalize(draft, Dim, Options()));
    }

    [Fact]
    public void The_Track_Quality_Is_The_Mean_Of_Accepted_Detections()
    {
        var vector = new[] { 1f, 0f, 0f, 0f };
        var draft = Draft(
            Face(0, 0, vector, quality: 0.2),
            Face(1, 1000, vector, quality: 0.4),
            Face(2, 2000, vector, quality: 0.6));

        var result = VideoFaceTrackAggregator.Finalize(draft, Dim, Options());

        Assert.NotNull(result);
        Assert.Equal(0.4, result!.QualityScore, 6);
        Assert.InRange(result.QualityScore, 0d, 1d);
    }
}

// The tracker's cosine helper is internal to the association pass; these tests
// need the same measure to assert on aggregates without duplicating it loosely.
internal static class VideoFaceTrackerProbe
{
    public static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count && i < b.Count; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        return na <= 0 || nb <= 0 ? 0d : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
