namespace NubArca.Api.Ai.Video.Faces;

// VFACE-01, Gate 1: the deterministic face-sampling policy.
//
// PURE and independently testable: segment intervals in, frame timestamps out.
// No I/O, no clock, no randomness — the same manifest and the same options
// always yield the same plan, which is what makes AnalysisVersion a meaningful
// reanalysis key.
//
// It reads only the SEGMENT BOUNDARIES of a completed VSEM-01 manifest and never
// touches VSEM-01 sample rows: the semantic sample manifest keeps exactly the
// timestamps it was built with.
//
// Policy, per segment [start, end):
//   n = clamp(floor(length / interval), 1, MaximumFramesPerSegment)
//   the n positions are spaced by `interval` and CENTRED in the segment, so a
//   frame is never taken exactly on a scene cut (the least stable representative
//   of either side), and every position is clamped into [start, end-1].
//
// Then, per video: identical timestamps are collapsed and, if the plan still
// exceeds MaximumFramesPerVideo, it is thinned EVENLY across the whole video
// (first and last kept) rather than truncated — a two-hour video keeps uniform
// coverage at a coarser effective interval instead of analysing only its head.
public static class VideoFaceSamplePlanner
{
    // One planned frame. `SegmentIndex` is carried for diagnostics only; the
    // tracker never uses it (a face crossing a cut is a normal association
    // candidate, subject to the same gates as any other).
    public readonly record struct PlannedFrame(int SegmentIndex, long TimestampMilliseconds);

    public static IReadOnlyList<PlannedFrame> Plan(
        IReadOnlyList<VideoFaceSegmentInterval> segments, VideoFaceAnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(options);

        if (segments.Count == 0 || options.FrameIntervalMilliseconds <= 0)
        {
            return Array.Empty<PlannedFrame>();
        }

        var interval = (long)options.FrameIntervalMilliseconds;
        var perSegmentCap = Math.Max(1, options.MaximumFramesPerSegment);
        var planned = new List<PlannedFrame>();

        foreach (var segment in segments.OrderBy(s => s.StartMilliseconds).ThenBy(s => s.SegmentIndex))
        {
            var length = segment.EndMilliseconds - segment.StartMilliseconds;
            if (length <= 0)
            {
                // A zero/negative interval cannot come from a completed manifest;
                // it is skipped rather than trusted.
                continue;
            }

            // A segment shorter than one interval still deserves exactly one
            // representative frame.
            var count = (int)Math.Min(perSegmentCap, Math.Max(1L, length / interval));

            // Centre the run of `count` positions inside the segment.
            var span = (count - 1) * interval;
            var offset = (length - span) / 2;
            if (offset < 0)
            {
                offset = 0;
            }

            var last = segment.EndMilliseconds - 1;
            for (var k = 0; k < count; k++)
            {
                var timestamp = segment.StartMilliseconds + offset + (k * interval);
                if (timestamp > last)
                {
                    timestamp = last;
                }

                if (timestamp < segment.StartMilliseconds)
                {
                    timestamp = segment.StartMilliseconds;
                }

                planned.Add(new PlannedFrame(segment.SegmentIndex, timestamp));
            }
        }

        // Collapse duplicates (a clamped position at a segment edge can coincide
        // with a neighbour's) while preserving chronological order.
        var deduplicated = new List<PlannedFrame>(planned.Count);
        var seen = new HashSet<long>();
        foreach (var frame in planned.OrderBy(f => f.TimestampMilliseconds).ThenBy(f => f.SegmentIndex))
        {
            if (seen.Add(frame.TimestampMilliseconds))
            {
                deduplicated.Add(frame);
            }
        }

        return Thin(deduplicated, options.MaximumFramesPerVideo);
    }

    // Evenly spaced subset of `frames` with at most `maximum` entries, keeping
    // the first and last. Deterministic and index-based — no floating-point
    // accumulation drift.
    private static IReadOnlyList<PlannedFrame> Thin(List<PlannedFrame> frames, int maximum)
    {
        if (maximum <= 0 || frames.Count <= maximum)
        {
            return frames;
        }

        if (maximum == 1)
        {
            return new[] { frames[0] };
        }

        var kept = new List<PlannedFrame>(maximum);
        var lastIndex = -1;
        for (var i = 0; i < maximum; i++)
        {
            var index = (int)Math.Round((double)i * (frames.Count - 1) / (maximum - 1),
                MidpointRounding.AwayFromZero);
            if (index == lastIndex)
            {
                continue;
            }

            lastIndex = index;
            kept.Add(frames[index]);
        }

        return kept;
    }
}

// One segment interval of a completed temporal manifest: [Start, End) in
// integral milliseconds. A read-only projection of VideoSemanticSegment so the
// planner stays free of EF and of the domain entity.
public readonly record struct VideoFaceSegmentInterval(
    int SegmentIndex, long StartMilliseconds, long EndMilliseconds);
