namespace NubArca.Api.Ai.Video.Faces;

// VFACE-01, Gate 2: the deterministic temporal association pass.
//
// PURE and independently testable — accepted detections in, drafted tracks out.
// No I/O, no clock, no randomness, no external tracking framework (a Kalman/
// ByteTrack-class dependency would be unjustifiable for 1 fps sampling, where the
// motion model carries almost no information and the recognition embedding
// carries most of it).
//
// Algorithm: greedy, score-ordered, one-to-one association per FRAME.
//   1. detections are processed in timestamp order, frame by frame;
//   2. tracks with no detection for more than MaximumTrackGapMilliseconds are
//      closed before the frame is matched;
//   3. every (active track × detection) pair is gated on temporal distance,
//      embedding similarity, and one of the two spatial rules (box overlap, or a
//      small centre move with a compatible scale). A pair failing any gate is
//      never a candidate;
//   4. surviving pairs are assigned greedily in descending score, and each track
//      and each detection is used AT MOST ONCE — so a detection belongs to
//      exactly one track and two faces visible in the SAME frame can never
//      collapse into one track;
//   5. unmatched detections open new tracks;
//   6. everything still open is closed at end of input.
//
// A real person may legitimately produce SEVERAL tracks in one video. Merging
// them (within or across videos) is clustering and belongs to VFACE-02.
public static class VideoFaceTracker
{
    // Association score weights. The recognition embedding dominates because at
    // ~1 fps a face can move a long way between samples while its identity
    // vector barely moves; box overlap is the secondary, corroborating signal.
    private const double SimilarityWeight = 0.6;
    private const double OverlapWeight = 0.4;

    public static IReadOnlyList<VideoFaceTrackDraft> Associate(
        IReadOnlyList<VideoFaceObservation> observations, VideoFaceAnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(options);

        if (observations.Count == 0)
        {
            return Array.Empty<VideoFaceTrackDraft>();
        }

        var ordered = observations
            .OrderBy(o => o.TimestampMilliseconds)
            .ThenBy(o => o.FrameIndex)
            .ThenBy(o => o.FaceIndex)
            .ToList();

        var active = new List<ActiveTrack>();
        var closed = new List<ActiveTrack>();
        var maximumGap = (long)options.MaximumTrackGapMilliseconds;

        var index = 0;
        while (index < ordered.Count)
        {
            var timestamp = ordered[index].TimestampMilliseconds;
            var frame = new List<VideoFaceObservation>();
            while (index < ordered.Count && ordered[index].TimestampMilliseconds == timestamp)
            {
                frame.Add(ordered[index]);
                index++;
            }

            // Close everything that has gone quiet for too long BEFORE matching,
            // so a stale track can never win an association it should not.
            for (var i = active.Count - 1; i >= 0; i--)
            {
                if (timestamp - active[i].LastTimestampMilliseconds > maximumGap)
                {
                    closed.Add(active[i]);
                    active.RemoveAt(i);
                }
            }

            // Stable ordering after the removals above, so candidate ranking and
            // tie-breaking stay deterministic.
            active.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));

            var candidates = new List<Candidate>();
            for (var t = 0; t < active.Count; t++)
            {
                for (var o = 0; o < frame.Count; o++)
                {
                    if (TryScore(active[t], frame[o], timestamp, options) is { } score)
                    {
                        candidates.Add(new Candidate(t, o, score));
                    }
                }
            }

            // Descending score; ties resolved by track then detection ordinal, so
            // the outcome never depends on sort stability or hash order.
            candidates.Sort(static (a, b) =>
            {
                var byScore = b.Score.CompareTo(a.Score);
                if (byScore != 0)
                {
                    return byScore;
                }

                var byTrack = a.TrackSlot.CompareTo(b.TrackSlot);
                return byTrack != 0 ? byTrack : a.ObservationSlot.CompareTo(b.ObservationSlot);
            });

            var trackTaken = new bool[active.Count];
            var observationTaken = new bool[frame.Count];
            foreach (var candidate in candidates)
            {
                if (trackTaken[candidate.TrackSlot] || observationTaken[candidate.ObservationSlot])
                {
                    continue;
                }

                trackTaken[candidate.TrackSlot] = true;
                observationTaken[candidate.ObservationSlot] = true;
                active[candidate.TrackSlot].Append(frame[candidate.ObservationSlot]);
            }

            for (var o = 0; o < frame.Count; o++)
            {
                if (!observationTaken[o])
                {
                    active.Add(ActiveTrack.Start(frame[o], active.Count + closed.Count));
                }
            }
        }

        closed.AddRange(active);

        // Deterministic track order: chronological by start, then by the position
        // of the first detection (a stable, content-derived tiebreak for two
        // tracks that begin in the same frame).
        return closed
            .OrderBy(t => t.Detections[0].TimestampMilliseconds)
            .ThenBy(t => t.Detections[0].FrameIndex)
            .ThenBy(t => t.Detections[0].FaceIndex)
            .Select((t, ordinal) => new VideoFaceTrackDraft(ordinal, t.Detections))
            .ToList();
    }

    // Returns the association score for a candidate pair, or null when any gate
    // rejects it.
    private static double? TryScore(
        ActiveTrack track, VideoFaceObservation observation, long timestamp,
        VideoFaceAnalysisOptions options)
    {
        // Temporal: strictly forward, within the configured gap. (The frame-level
        // grouping already guarantees the track cannot have a detection at this
        // exact timestamp.)
        var gap = timestamp - track.LastTimestampMilliseconds;
        if (gap <= 0 || gap > options.MaximumTrackGapMilliseconds)
        {
            return null;
        }

        var similarity = CosineSimilarity(track.MeanEmbedding, observation.Embedding);
        if (similarity < options.MinimumAssociationSimilarity)
        {
            return null;
        }

        var overlap = IntersectionOverUnion(track.LastBox, observation);
        var spatiallyPlausible = overlap >= options.MinimumAssociationIou
            || (CenterDistance(track.LastBox, observation) <= options.MaximumAssociationCenterDistance
                && ScaleRatio(track.LastBox, observation) <= options.MaximumAssociationScaleRatio);
        if (!spatiallyPlausible)
        {
            return null;
        }

        return (SimilarityWeight * similarity) + (OverlapWeight * overlap);
    }

    // ---- geometry ----------------------------------------------------------

    internal static double IntersectionOverUnion(in BoundingBox a, in VideoFaceObservation b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);

        var overlapWidth = right - left;
        var overlapHeight = bottom - top;
        if (overlapWidth <= 0 || overlapHeight <= 0)
        {
            return 0d;
        }

        var intersection = overlapWidth * overlapHeight;
        var union = (a.Width * a.Height) + (b.Width * b.Height) - intersection;
        return union <= 0 ? 0d : Math.Clamp(intersection / union, 0d, 1d);
    }

    private static double CenterDistance(in BoundingBox a, in VideoFaceObservation b)
    {
        var dx = (a.X + (a.Width / 2)) - (b.X + (b.Width / 2));
        var dy = (a.Y + (a.Height / 2)) - (b.Y + (b.Height / 2));
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double ScaleRatio(in BoundingBox a, in VideoFaceObservation b)
    {
        var first = Math.Sqrt(Math.Max(a.Width * a.Height, 0d));
        var second = Math.Sqrt(Math.Max(b.Width * b.Height, 0d));
        if (first <= 0 || second <= 0)
        {
            return double.PositiveInfinity;
        }

        return first >= second ? first / second : second / first;
    }

    // Cosine similarity of two vectors. Zero (i.e. "no evidence", which every
    // gate rejects) for mismatched dimensions, empty vectors, a zero norm or any
    // non-finite component — a defensive path the service already prevents.
    internal static double CosineSimilarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count == 0 || a.Count != b.Count)
        {
            return 0d;
        }

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Count; i++)
        {
            double x = a[i];
            double y = b[i];
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                return 0d;
            }

            dot += x * y;
            normA += x * x;
            normB += y * y;
        }

        if (normA <= 0 || normB <= 0)
        {
            return 0d;
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    // ---- internals ---------------------------------------------------------

    private readonly record struct Candidate(int TrackSlot, int ObservationSlot, double Score);

    internal readonly record struct BoundingBox(double X, double Y, double Width, double Height);

    // A track being built. Keeps a quality-weighted running sum so the
    // association compares against the whole track so far, not only its last
    // detection (which would let one bad frame hijack the track).
    private sealed class ActiveTrack
    {
        private readonly List<VideoFaceObservation> _detections = new();
        private readonly double[] _weightedSum;
        private double _weight;

        private ActiveTrack(VideoFaceObservation first, int sequence)
        {
            Sequence = sequence;
            _weightedSum = new double[first.Embedding.Length];
            Append(first);
        }

        public static ActiveTrack Start(VideoFaceObservation first, int sequence)
            => new(first, sequence);

        // Creation order: the deterministic tiebreak for candidate ranking.
        public int Sequence { get; }

        public IReadOnlyList<VideoFaceObservation> Detections => _detections;

        public long LastTimestampMilliseconds { get; private set; }

        public BoundingBox LastBox { get; private set; }

        public IReadOnlyList<float> MeanEmbedding
        {
            get
            {
                var mean = new float[_weightedSum.Length];
                if (_weight <= 0)
                {
                    return mean;
                }

                for (var i = 0; i < mean.Length; i++)
                {
                    mean[i] = (float)(_weightedSum[i] / _weight);
                }

                return mean;
            }
        }

        public void Append(VideoFaceObservation observation)
        {
            _detections.Add(observation);
            LastTimestampMilliseconds = observation.TimestampMilliseconds;
            LastBox = new BoundingBox(
                observation.X, observation.Y, observation.Width, observation.Height);

            // A zero-quality detection must still contribute direction, so the
            // weight floor keeps the running mean defined.
            var weight = Math.Max(observation.QualityScore, 1e-3);
            if (observation.Embedding.Length == _weightedSum.Length)
            {
                for (var i = 0; i < _weightedSum.Length; i++)
                {
                    _weightedSum[i] += weight * observation.Embedding[i];
                }

                _weight += weight;
            }
        }
    }
}

// One ACCEPTED face detection inside one sampled frame: everything the tracker
// needs and nothing else. Blob-level and owner-free — no owner, file, person,
// filename, path or storage key is representable here.
public sealed record VideoFaceObservation(
    int FrameIndex,
    long TimestampMilliseconds,
    // 0-based position within the frame, in the detector's deterministic order.
    int FaceIndex,
    // Normalized bounding box, fractions of frame width/height in [0..1].
    double X,
    double Y,
    double Width,
    double Height,
    double? Confidence,
    double QualityScore,
    // L2-normalized recognition embedding in the embedding profile's space.
    float[] Embedding);

// A track as the association pass produced it: its detections, in order. It is
// not yet an accepted track — the evidence floor, outlier rejection and
// aggregation are applied by VideoFaceTrackAggregator.
public sealed record VideoFaceTrackDraft(
    int DraftIndex, IReadOnlyList<VideoFaceObservation> Detections);
