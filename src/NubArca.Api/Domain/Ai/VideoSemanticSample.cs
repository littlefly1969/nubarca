namespace NubArca.Api.Domain.Ai;

// VSEM-01: one deterministic representative timestamp inside a segment.
//
// TimestampMilliseconds always satisfies
//   segment.StartMilliseconds <= Timestamp < segment.EndMilliseconds
// so a sample can never drift outside the interval it represents. Timestamps
// are integral milliseconds and are chosen with a small inward offset, never
// exactly on a scene cut (a frame taken exactly at a cut is the least stable
// representative of either side).
//
// The model deliberately supports MANY samples per segment even though the
// current default is conservative: later slices (SigLIP2 frame embeddings)
// select multiple frames per segment without a schema change.
public class VideoSemanticSample
{
    public Guid Id { get; set; }

    public Guid VideoSemanticSegmentId { get; set; }

    // 0-based, contiguous within the segment, chronological.
    public int SampleIndex { get; set; }

    public long TimestampMilliseconds { get; set; }

    // Why this timestamp was picked — one of VideoSemanticSelectionReasons.
    public string SelectionReason { get; set; } = VideoSemanticSelectionReasons.Midpoint;

    public DateTime CreatedAt { get; set; }
}
