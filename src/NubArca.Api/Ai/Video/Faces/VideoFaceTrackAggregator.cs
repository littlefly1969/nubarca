namespace NubArca.Api.Ai.Video.Faces;

// VFACE-01, Gate 3: track finalization — evidence floor, outlier rejection,
// representative selection and the aggregate embedding.
//
// PURE and independently testable. The aggregation strategy is a QUALITY-WEIGHTED
// NORMALIZED MEAN over the accepted detections, after outlier rejection against a
// provisional mean:
//
//   1. drop detections whose embedding is unusable (wrong dimension, non-finite,
//      zero norm) — a defect must never silently poison a track vector;
//   2. build a provisional quality-weighted mean of the survivors;
//   3. reject detections whose cosine similarity to that mean is below
//      TrackOutlierSimilarity (a mis-association, or a frame where the box drifted
//      onto a different face). If rejection would empty the set, nothing is
//      rejected — a uniformly "distant" track is still one track;
//   4. re-aggregate over the accepted set and L2-normalize;
//   5. the representative detection is the accepted detection with the HIGHEST
//      quality (ties: earliest timestamp, then frame, then face ordinal) — never
//      the first detection blindly, which is usually the smallest and blurriest
//      one as the subject enters the shot;
//   6. a track with fewer than MinimumTrackDetections accepted detections is
//      discarded as insufficient evidence.
//
// The representative timestamp is always the SELECTED detection's own timestamp,
// so it addresses a frame that really contains that face.
public static class VideoFaceTrackAggregator
{
    public static VideoFaceTrackResult? Finalize(
        VideoFaceTrackDraft draft, int expectedDimension, VideoFaceAnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(options);

        if (expectedDimension <= 0)
        {
            return null;
        }

        var usable = draft.Detections
            .Where(d => IsUsable(d.Embedding, expectedDimension))
            .ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        var provisional = WeightedMean(usable, expectedDimension);
        if (provisional is null)
        {
            return null;
        }

        var accepted = usable
            .Where(d => VideoFaceTracker.CosineSimilarity(d.Embedding, provisional)
                >= options.TrackOutlierSimilarity)
            .ToList();
        if (accepted.Count == 0)
        {
            accepted = usable;
        }

        if (accepted.Count < options.MinimumTrackDetections)
        {
            return null;
        }

        var aggregate = WeightedMean(accepted, expectedDimension);
        if (aggregate is null)
        {
            return null;
        }

        var representative = accepted
            .OrderByDescending(d => d.QualityScore)
            .ThenBy(d => d.TimestampMilliseconds)
            .ThenBy(d => d.FrameIndex)
            .ThenBy(d => d.FaceIndex)
            .First();

        var start = accepted.Min(d => d.TimestampMilliseconds);
        var end = accepted.Max(d => d.TimestampMilliseconds);
        var quality = Math.Clamp(accepted.Average(d => d.QualityScore), 0d, 1d);

        return new VideoFaceTrackResult(
            StartMilliseconds: start,
            EndMilliseconds: end,
            RepresentativeTimestampMilliseconds: representative.TimestampMilliseconds,
            DetectionCount: accepted.Count,
            Embedding: aggregate,
            QualityScore: quality,
            RepresentativeBoundingBoxX: Clamp01(representative.X),
            RepresentativeBoundingBoxY: Clamp01(representative.Y),
            RepresentativeBoundingBoxWidth: Clamp01(representative.Width),
            RepresentativeBoundingBoxHeight: Clamp01(representative.Height));
    }

    // A quality-weighted mean of L2-normalized inputs, itself L2-normalized.
    // Normalizing each input first stops a large-magnitude vector from
    // out-voting the rest on magnitude alone. Returns null when the result is
    // degenerate (zero norm or non-finite).
    private static float[]? WeightedMean(
        IReadOnlyList<VideoFaceObservation> detections, int dimension)
    {
        var sum = new double[dimension];
        var totalWeight = 0d;

        foreach (var detection in detections)
        {
            var norm = 0d;
            for (var i = 0; i < dimension; i++)
            {
                norm += (double)detection.Embedding[i] * detection.Embedding[i];
            }

            norm = Math.Sqrt(norm);
            if (norm <= 0 || !double.IsFinite(norm))
            {
                continue;
            }

            // A zero-quality detection must still contribute direction.
            var weight = Math.Max(detection.QualityScore, 1e-3);
            for (var i = 0; i < dimension; i++)
            {
                sum[i] += weight * (detection.Embedding[i] / norm);
            }

            totalWeight += weight;
        }

        if (totalWeight <= 0)
        {
            return null;
        }

        var magnitude = 0d;
        for (var i = 0; i < dimension; i++)
        {
            sum[i] /= totalWeight;
            magnitude += sum[i] * sum[i];
        }

        magnitude = Math.Sqrt(magnitude);
        if (magnitude <= 0 || !double.IsFinite(magnitude))
        {
            return null;
        }

        var result = new float[dimension];
        for (var i = 0; i < dimension; i++)
        {
            var value = (float)(sum[i] / magnitude);
            if (!float.IsFinite(value))
            {
                return null;
            }

            result[i] = value;
        }

        return result;
    }

    private static bool IsUsable(float[] embedding, int expectedDimension)
    {
        if (embedding.Length != expectedDimension)
        {
            return false;
        }

        var norm = 0d;
        foreach (var value in embedding)
        {
            if (!float.IsFinite(value))
            {
                return false;
            }

            norm += (double)value * value;
        }

        return norm > 0;
    }

    private static double Clamp01(double value)
        => double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : 0d;
}

// A finalized, persistable track. Carries evidence only — no owner, file, person
// or storage identity is representable here.
public sealed record VideoFaceTrackResult(
    long StartMilliseconds,
    long EndMilliseconds,
    long RepresentativeTimestampMilliseconds,
    int DetectionCount,
    float[] Embedding,
    double QualityScore,
    double RepresentativeBoundingBoxX,
    double RepresentativeBoundingBoxY,
    double RepresentativeBoundingBoxWidth,
    double RepresentativeBoundingBoxHeight);
