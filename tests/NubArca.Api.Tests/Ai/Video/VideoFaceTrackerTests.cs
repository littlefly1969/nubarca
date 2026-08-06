using NubArca.Api.Ai.Video.Faces;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VFACE-01, Gate 2: the deterministic association pass. Pure input/output — no
// database, no models, no clock.
//
// The invariants pinned here are the ones a face substrate cannot get wrong:
// a detection belongs to AT MOST ONE track, two faces visible in the SAME frame
// are never merged, and the same input always produces the same output.
public sealed class VideoFaceTrackerTests
{
    private static readonly float[] Alice = [1f, 0f, 0f, 0f];
    private static readonly float[] Bob = [0f, 1f, 0f, 0f];

    private static VideoFaceAnalysisOptions Options(int gapMs = 2000) => new()
    {
        MaximumTrackGapMilliseconds = gapMs,
        MinimumAssociationSimilarity = 0.35,
        MinimumAssociationIou = 0.2,
        MaximumAssociationCenterDistance = 0.15,
        MaximumAssociationScaleRatio = 2.0,
    };

    private static VideoFaceObservation Face(
        int frame, long timestamp, float[] embedding,
        double x = 0.4, double y = 0.4, double size = 0.2,
        int faceIndex = 0, double quality = 0.8)
        => new(frame, timestamp, faceIndex, x, y, size, size, 0.95, quality, embedding);

    [Fact]
    public void One_Stable_Face_Produces_One_Track()
    {
        var observations = Enumerable.Range(0, 5)
            .Select(i => Face(i, i * 1000L, Alice, x: 0.40 + (i * 0.005)))
            .ToList();

        var tracks = VideoFaceTracker.Associate(observations, Options());

        var track = Assert.Single(tracks);
        Assert.Equal(5, track.Detections.Count);
    }

    [Fact]
    public void Two_Simultaneous_Faces_Are_Never_Merged()
    {
        var observations = new List<VideoFaceObservation>();
        for (var i = 0; i < 4; i++)
        {
            observations.Add(Face(i, i * 1000L, Alice, x: 0.10, faceIndex: 0));
            observations.Add(Face(i, i * 1000L, Bob, x: 0.70, faceIndex: 1));
        }

        var tracks = VideoFaceTracker.Associate(observations, Options());

        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, t => Assert.Equal(4, t.Detections.Count));
        // Each track holds detections from a single identity.
        Assert.All(tracks, t => Assert.Single(t.Detections.Select(d => d.X).Distinct()));
    }

    [Fact]
    public void Two_Faces_In_One_Frame_Cannot_Both_Join_The_Same_Track()
    {
        // Both new detections look like the existing track; only ONE may win.
        var observations = new List<VideoFaceObservation>
        {
            Face(0, 0, Alice, x: 0.40),
            Face(1, 1000, Alice, x: 0.40, faceIndex: 0),
            Face(1, 1000, Alice, x: 0.42, faceIndex: 1),
        };

        var tracks = VideoFaceTracker.Associate(observations, Options());

        Assert.Equal(2, tracks.Count);
        Assert.Equal(3, tracks.Sum(t => t.Detections.Count));
    }

    [Fact]
    public void A_Brief_Occlusion_Below_The_Gap_Keeps_One_Track()
    {
        var observations = new List<VideoFaceObservation>
        {
            Face(0, 0, Alice),
            Face(1, 1000, Alice),
            // frame at 2000 ms is occluded — no detection
            Face(3, 3000, Alice),
            Face(4, 4000, Alice),
        };

        var tracks = VideoFaceTracker.Associate(observations, Options(gapMs: 2000));

        var track = Assert.Single(tracks);
        Assert.Equal(4, track.Detections.Count);
    }

    [Fact]
    public void A_Gap_Above_The_Threshold_Closes_The_Track()
    {
        var observations = new List<VideoFaceObservation>
        {
            Face(0, 0, Alice),
            Face(1, 1000, Alice),
            Face(2, 9000, Alice),
            Face(3, 10_000, Alice),
        };

        var tracks = VideoFaceTracker.Associate(observations, Options(gapMs: 2000));

        Assert.Equal(2, tracks.Count);
        Assert.Equal(0, tracks[0].Detections[0].TimestampMilliseconds);
        Assert.Equal(9000, tracks[1].Detections[0].TimestampMilliseconds);
    }

    [Fact]
    public void A_Spatial_Mismatch_Starts_A_New_Track()
    {
        // Same identity vector, but the box jumps across the frame AND changes
        // scale far beyond the allowed ratio: not a plausible continuation.
        var observations = new List<VideoFaceObservation>
        {
            Face(0, 0, Alice, x: 0.02, y: 0.02, size: 0.08),
            Face(1, 1000, Alice, x: 0.80, y: 0.80, size: 0.19),
        };

        var tracks = VideoFaceTracker.Associate(observations, Options());

        Assert.Equal(2, tracks.Count);
    }

    [Fact]
    public void An_Embedding_Mismatch_Starts_A_New_Track()
    {
        // Identical geometry, orthogonal identity vectors: a cut to a different
        // person framed the same way.
        var observations = new List<VideoFaceObservation>
        {
            Face(0, 0, Alice),
            Face(1, 1000, Bob),
        };

        var tracks = VideoFaceTracker.Associate(observations, Options());

        Assert.Equal(2, tracks.Count);
    }

    [Fact]
    public void Crossing_Faces_Follow_Their_Identity_Not_Their_Position()
    {
        // Two people walk past each other: their boxes converge and swap sides.
        var observations = new List<VideoFaceObservation>
        {
            Face(0, 0, Alice, x: 0.30, faceIndex: 0),
            Face(0, 0, Bob, x: 0.50, faceIndex: 1),
            Face(1, 1000, Alice, x: 0.38, faceIndex: 0),
            Face(1, 1000, Bob, x: 0.42, faceIndex: 1),
            Face(2, 2000, Bob, x: 0.32, faceIndex: 0),
            Face(2, 2000, Alice, x: 0.48, faceIndex: 1),
        };

        var tracks = VideoFaceTracker.Associate(observations, Options());

        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, t =>
        {
            var identities = t.Detections.Select(d => d.Embedding[0]).Distinct().ToList();
            Assert.Single(identities);
        });
    }

    [Fact]
    public void Every_Detection_Is_Assigned_Exactly_Once()
    {
        var observations = new List<VideoFaceObservation>();
        for (var i = 0; i < 6; i++)
        {
            observations.Add(Face(i, i * 1000L, Alice, x: 0.10, faceIndex: 0));
            observations.Add(Face(i, i * 1000L, Bob, x: 0.70, faceIndex: 1));
            observations.Add(Face(i, i * 1000L, Alice, x: 0.45, faceIndex: 2));
        }

        var tracks = VideoFaceTracker.Associate(observations, Options());

        var assigned = tracks.SelectMany(t => t.Detections).ToList();
        Assert.Equal(observations.Count, assigned.Count);
        Assert.Equal(
            observations.Count,
            assigned.Select(d => (d.TimestampMilliseconds, d.FaceIndex)).Distinct().Count());
    }

    [Fact]
    public void All_Remaining_Tracks_Are_Closed_At_End_Of_Input()
    {
        var observations = new List<VideoFaceObservation>
        {
            Face(0, 0, Alice, x: 0.10, faceIndex: 0),
            Face(0, 0, Bob, x: 0.70, faceIndex: 1),
        };

        var tracks = VideoFaceTracker.Associate(observations, Options());

        Assert.Equal(2, tracks.Count);
        Assert.Equal(2, tracks.Sum(t => t.Detections.Count));
    }

    [Fact]
    public void Output_Is_Deterministic_Regardless_Of_Input_Order()
    {
        var observations = new List<VideoFaceObservation>();
        for (var i = 0; i < 5; i++)
        {
            observations.Add(Face(i, i * 1000L, Alice, x: 0.10, faceIndex: 0));
            observations.Add(Face(i, i * 1000L, Bob, x: 0.70, faceIndex: 1));
        }

        var forward = VideoFaceTracker.Associate(observations, Options());
        var reversed = VideoFaceTracker.Associate(
            observations.AsEnumerable().Reverse().ToList(), Options());

        Assert.Equal(forward.Count, reversed.Count);
        for (var i = 0; i < forward.Count; i++)
        {
            Assert.Equal(forward[i].DraftIndex, reversed[i].DraftIndex);
            Assert.Equal(
                forward[i].Detections.Select(d => (d.TimestampMilliseconds, d.FaceIndex)),
                reversed[i].Detections.Select(d => (d.TimestampMilliseconds, d.FaceIndex)));
        }
    }

    [Fact]
    public void Track_Indices_Are_Contiguous_And_Chronological()
    {
        var observations = new List<VideoFaceObservation>
        {
            Face(0, 0, Alice),
            Face(1, 1000, Alice),
            Face(5, 20_000, Bob),
            Face(6, 21_000, Bob),
        };

        var tracks = VideoFaceTracker.Associate(observations, Options());

        Assert.Equal(new[] { 0, 1 }, tracks.Select(t => t.DraftIndex));
        Assert.True(
            tracks[0].Detections[0].TimestampMilliseconds
            < tracks[1].Detections[0].TimestampMilliseconds);
    }

    [Fact]
    public void A_Low_Quality_Detection_Still_Extends_Its_Track()
    {
        // Quality FILTERING happens upstream, at the acceptance gate. Whatever
        // reaches the tracker is trackable evidence.
        var observations = new List<VideoFaceObservation>
        {
            Face(0, 0, Alice, quality: 0.9),
            Face(1, 1000, Alice, quality: 0.01),
            Face(2, 2000, Alice, quality: 0.9),
        };

        var tracks = VideoFaceTracker.Associate(observations, Options());

        var track = Assert.Single(tracks);
        Assert.Equal(3, track.Detections.Count);
    }

    [Fact]
    public void No_Face_Input_Produces_No_Track()
    {
        Assert.Empty(VideoFaceTracker.Associate(Array.Empty<VideoFaceObservation>(), Options()));
    }
}
