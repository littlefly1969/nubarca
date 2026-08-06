using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Video;

// VSEM-01: the PURE, deterministic normalizer. No I/O, no clock, no randomness,
// no database, no process — the same (duration, candidates, options) triple
// always produces byte-identical output, which is what makes a manifest safe to
// key by (blob, segmentation version).
//
// Pipeline:
//   1. sanitize candidates — drop non-finite / out-of-range, round to whole ms,
//      sort, deduplicate;
//   2. merge — reject a boundary that would create a segment shorter than the
//      minimum (a fast cut or a camera flash emits a burst of near-identical
//      candidates);
//   3. bracket — force a boundary at 0 and at the normalized duration;
//   4. split — cut any segment longer than the maximum into BALANCED parts;
//   5. cap — if the result still exceeds the per-video segment cap, rebuild it
//      deterministically as exactly `cap` balanced uniform segments;
//   6. sample — pick evenly spaced INTERIOR timestamps per segment, never on a
//      boundary, within the per-video sample cap.
//
// With no usable candidates the builder falls back to bounded uniform
// segmentation. That fallback is a NORMALIZATION outcome only: cancellation,
// storage exhaustion, database errors and application bugs are classified by
// the service and never reach this code.
public static class VideoSemanticManifestBuilder
{
    public static VideoSemanticManifest Build(
        long durationMilliseconds,
        IReadOnlyList<double> sceneCandidateSeconds,
        VideoSemanticSegmentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (durationMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationMilliseconds), "Duration must be a positive number of milliseconds.");
        }

        var candidates = SanitizeCandidates(sceneCandidateSeconds, durationMilliseconds);
        var boundaries = MergeBoundaries(candidates, durationMilliseconds, options.MinimumSegmentMilliseconds);

        // A single boundary at 0 means every candidate was merged away — the
        // detector found nothing usable, so fall back to uniform segmentation.
        var fallbackUsed = boundaries.Count <= 1;
        var intervals = fallbackUsed
            ? UniformIntervals(durationMilliseconds, options, VideoSemanticBoundaryReasons.Uniform)
            : SplitOverlongIntervals(IntervalsFrom(boundaries, durationMilliseconds), options.MaximumSegmentMilliseconds);

        // Hard cap. A rebuild (rather than a truncation) is what keeps the
        // completed-manifest invariants: contiguous, gapless, last segment
        // reaches the duration.
        if (intervals.Count > options.MaximumSegmentsPerVideo)
        {
            intervals = BalancedIntervals(
                0, durationMilliseconds,
                EffectivePartCount(options.MaximumSegmentsPerVideo, durationMilliseconds),
                VideoSemanticBoundaryReasons.Cap);
        }

        var segments = BuildSegmentsWithSamples(intervals, options);
        return new VideoSemanticManifest(
            durationMilliseconds, segments, candidates.Count, fallbackUsed);
    }

    // ---- 1. sanitize ------------------------------------------------------

    // Keeps only finite, strictly interior timestamps. 0 and the duration are
    // added later as mandatory brackets, so a candidate exactly on either is
    // redundant rather than an error.
    private static List<long> SanitizeCandidates(
        IReadOnlyList<double>? candidateSeconds, long durationMilliseconds)
    {
        var result = new List<long>();
        if (candidateSeconds is null || candidateSeconds.Count == 0)
        {
            return result;
        }

        var seen = new HashSet<long>();
        foreach (var seconds in candidateSeconds)
        {
            if (!double.IsFinite(seconds) || seconds <= 0)
            {
                continue;
            }

            // Guard the cast: a garbage value far beyond long range must not
            // wrap around into a plausible timestamp.
            var scaled = Math.Round(seconds * 1000d);
            if (scaled <= 0 || scaled >= durationMilliseconds)
            {
                continue;
            }

            var ms = (long)scaled;
            if (seen.Add(ms))
            {
                result.Add(ms);
            }
        }

        result.Sort();
        return result;
    }

    // ---- 2./3. merge + bracket -------------------------------------------

    // Returns the accepted segment START boundaries, always beginning with 0.
    // A candidate is accepted only when it is at least `minimumMilliseconds`
    // after the previous accepted boundary; trailing boundaries too close to
    // the end are dropped so the final segment is not a sliver either.
    private static List<long> MergeBoundaries(
        List<long> candidates, long durationMilliseconds, long minimumMilliseconds)
    {
        var minimum = Math.Max(1, minimumMilliseconds);
        var accepted = new List<long> { 0 };

        foreach (var candidate in candidates)
        {
            if (candidate - accepted[^1] >= minimum)
            {
                accepted.Add(candidate);
            }
        }

        while (accepted.Count > 1 && durationMilliseconds - accepted[^1] < minimum)
        {
            accepted.RemoveAt(accepted.Count - 1);
        }

        return accepted;
    }

    private static List<Interval> IntervalsFrom(List<long> boundaries, long durationMilliseconds)
    {
        var intervals = new List<Interval>(boundaries.Count);
        for (var i = 0; i < boundaries.Count; i++)
        {
            var start = boundaries[i];
            var end = i + 1 < boundaries.Count ? boundaries[i + 1] : durationMilliseconds;
            var reason = i == 0
                ? VideoSemanticBoundaryReasons.Start
                : VideoSemanticBoundaryReasons.Scene;
            intervals.Add(new Interval(start, end, reason));
        }

        return intervals;
    }

    // ---- 4. split ---------------------------------------------------------

    private static List<Interval> SplitOverlongIntervals(List<Interval> intervals, long maximumMilliseconds)
    {
        var maximum = Math.Max(1, maximumMilliseconds);
        var result = new List<Interval>(intervals.Count);

        foreach (var interval in intervals)
        {
            var length = interval.End - interval.Start;
            if (length <= maximum)
            {
                result.Add(interval);
                continue;
            }

            // Balanced parts, not "maximum-sized parts plus a remainder": an
            // 85 s segment with a 20 s ceiling becomes 5 × 17 s, not 4 × 20 s
            // plus a 5 s orphan.
            var parts = (int)Math.Min(int.MaxValue, (length + maximum - 1) / maximum);
            var pieces = BalancedIntervals(
                interval.Start, interval.End, EffectivePartCount(parts, length), VideoSemanticBoundaryReasons.Split);

            // The first piece keeps the reason of the boundary it inherits.
            pieces[0] = pieces[0] with { Reason = interval.Reason };
            result.AddRange(pieces);
        }

        return result;
    }

    // ---- 5. uniform / balanced construction -------------------------------

    private static List<Interval> UniformIntervals(
        long durationMilliseconds, VideoSemanticSegmentationOptions options, string reason)
    {
        var target = Math.Max(1, options.TargetSegmentMilliseconds);
        var desired = (durationMilliseconds + target - 1) / target;
        var count = (int)Math.Clamp(desired, 1, options.MaximumSegmentsPerVideo);
        var intervals = BalancedIntervals(
            0, durationMilliseconds, EffectivePartCount(count, durationMilliseconds), reason);
        intervals[0] = intervals[0] with { Reason = VideoSemanticBoundaryReasons.Start };
        return intervals;
    }

    // Splits [start,end) into exactly `count` contiguous, gapless parts whose
    // lengths differ by at most 1 ms. Integer arithmetic only, so the parts sum
    // EXACTLY to the interval — no rounding drift can open a gap or overshoot
    // the duration.
    private static List<Interval> BalancedIntervals(long start, long end, int count, string reason)
    {
        var length = end - start;
        var baseLength = length / count;
        var remainder = length % count;

        var intervals = new List<Interval>(count);
        var position = start;
        for (var i = 0; i < count; i++)
        {
            var size = baseLength + (i < remainder ? 1 : 0);
            intervals.Add(new Interval(position, position + size, reason));
            position += size;
        }

        return intervals;
    }

    // Never ask for more parts than there are milliseconds: every segment must
    // be at least 1 ms long for [start,end) to be a valid interval.
    private static int EffectivePartCount(int desired, long lengthMilliseconds)
        => (int)Math.Clamp(Math.Min(desired, lengthMilliseconds), 1, int.MaxValue);

    // ---- 6. samples -------------------------------------------------------

    private static List<VideoSemanticManifestSegment> BuildSegmentsWithSamples(
        List<Interval> intervals, VideoSemanticSegmentationOptions options)
    {
        var segmentCount = intervals.Count;
        var perSegment = Math.Max(1, options.SamplesPerSegment);
        var budget = Math.Max(1, options.MaximumSamplesPerVideo);

        // Spread the per-video budget evenly rather than exhausting it on the
        // first segments. When even one sample each does not fit, the trailing
        // segments simply get none (the model allows a segment with no samples).
        if ((long)segmentCount * perSegment > budget)
        {
            perSegment = Math.Max(1, budget / segmentCount);
        }

        var segments = new List<VideoSemanticManifestSegment>(segmentCount);
        var remaining = budget;

        for (var i = 0; i < segmentCount; i++)
        {
            var interval = intervals[i];
            var length = interval.End - interval.Start;
            var count = (int)Math.Min(Math.Min(perSegment, remaining), length);
            var samples = count <= 0
                ? Array.Empty<VideoSemanticManifestSample>()
                : SelectSamples(interval, count);

            remaining -= samples.Count;
            segments.Add(new VideoSemanticManifestSegment(
                i, interval.Start, interval.End, interval.Reason, samples));
        }

        return segments;
    }

    // Evenly spaced INTERIOR positions: for k samples the i-th sits at
    // start + length*(i+1)/(k+1). For k=1 that is the midpoint. The offset is
    // always inward, so a sample never lands on a scene cut — the frame exactly
    // at a cut is the least representative frame of either shot.
    private static IReadOnlyList<VideoSemanticManifestSample> SelectSamples(Interval interval, int count)
    {
        var length = interval.End - interval.Start;
        var reason = count == 1
            ? VideoSemanticSelectionReasons.Midpoint
            : VideoSemanticSelectionReasons.Interior;

        var samples = new List<VideoSemanticManifestSample>(count);
        long? previous = null;
        for (var i = 0; i < count; i++)
        {
            var offset = length * (i + 1) / (count + 1);
            var timestamp = Math.Clamp(interval.Start + offset, interval.Start, interval.End - 1);

            // Defensive: integer flooring cannot collide for count <= length,
            // but a duplicate timestamp would be meaningless, so drop it rather
            // than persist two identical samples.
            if (previous is long p && timestamp <= p)
            {
                continue;
            }

            previous = timestamp;
            samples.Add(new VideoSemanticManifestSample(samples.Count, timestamp, reason));
        }

        return samples;
    }

    private readonly record struct Interval(long Start, long End, string Reason);
}
