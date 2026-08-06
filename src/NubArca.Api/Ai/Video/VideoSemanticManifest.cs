namespace NubArca.Api.Ai.Video;

// VSEM-01: the in-memory result of normalization, before persistence. Pure
// data — integral milliseconds only, no entity ids, nothing owner-specific.
public sealed record VideoSemanticManifest(
    long DurationMilliseconds,
    IReadOnlyList<VideoSemanticManifestSegment> Segments,
    int CandidateCount,
    bool FallbackUsed)
{
    public int SegmentCount => Segments.Count;

    public int SampleCount => Segments.Sum(s => s.Samples.Count);
}

public sealed record VideoSemanticManifestSegment(
    int SegmentIndex,
    long StartMilliseconds,
    long EndMilliseconds,
    string BoundaryReason,
    IReadOnlyList<VideoSemanticManifestSample> Samples)
{
    public long LengthMilliseconds => EndMilliseconds - StartMilliseconds;
}

public sealed record VideoSemanticManifestSample(
    int SampleIndex,
    long TimestampMilliseconds,
    string SelectionReason);
