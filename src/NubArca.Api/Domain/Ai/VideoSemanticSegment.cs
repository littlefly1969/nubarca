namespace NubArca.Api.Domain.Ai;

// VSEM-01: one bounded temporal interval of a video blob's manifest.
//
// Interval contract: [StartMilliseconds, EndMilliseconds) — start inclusive,
// end exclusive. Integral milliseconds only; no floating-point database values,
// so ordering/containment comparisons are exact.
//
// Across a COMPLETED manifest the segments are: contiguous in SegmentIndex
// (0..n-1), chronologically ordered, non-overlapping, duplicate-free, normally
// gapless, starting at zero and ending exactly at the manifest's normalized
// duration.
public class VideoSemanticSegment
{
    public Guid Id { get; set; }

    public Guid VideoSemanticIndexId { get; set; }

    // 0-based, contiguous, chronological.
    public int SegmentIndex { get; set; }

    public long StartMilliseconds { get; set; }

    public long EndMilliseconds { get; set; }

    // Why this segment begins here — one of VideoSemanticBoundaryReasons.
    // Diagnostic only; correctness never depends on it.
    public string BoundaryReason { get; set; } = VideoSemanticBoundaryReasons.Start;

    public DateTime CreatedAt { get; set; }
}
