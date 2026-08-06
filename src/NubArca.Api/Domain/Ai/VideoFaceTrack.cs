namespace NubArca.Api.Domain.Ai;

// VFACE-01: ONE canonical face track of a video blob — a temporally contiguous
// run of accepted face detections that the deterministic tracker associated with
// each other.
//
// CANONICAL BLOB-LEVEL EVIDENCE, nothing more. A track says "this face appeared
// between these two timestamps and this is its aggregate recognition vector"; it
// says NOTHING about who the person is. There is no OwnerUserId, FileItemId,
// PersonId or person name here, and there never will be — identity, naming and
// every user decision are owner-level and belong to VFACE-02. Duplicate FileItem
// references to the same blob therefore share ONE set of tracks.
//
// A real person may legitimately produce SEVERAL tracks in one video (they leave
// the frame, the shot cuts, the gap exceeds the configured maximum). Merging
// tracks — within a video or across videos — is clustering, and is explicitly
// out of scope here.
public class VideoFaceTrack
{
    public Guid Id { get; set; }

    public Guid VideoFaceAnalysisStatusId { get; set; }

    // 0-based, contiguous within the analysis, in deterministic track order
    // (chronological by start, then by first-detection position).
    public int TrackIndex { get; set; }

    // Closed interval covered by the track's accepted detections, in integral
    // milliseconds: Start <= Representative <= End. Integral milliseconds only,
    // so ordering/containment comparisons are exact.
    public long StartMilliseconds { get; set; }

    public long EndMilliseconds { get; set; }

    // Timestamp of the SELECTED representative detection — always one of the
    // track's accepted detection timestamps, never an interpolated midpoint.
    public long RepresentativeTimestampMilliseconds { get; set; }

    // Accepted detections that survived quality filtering and outlier rejection
    // and were aggregated into EmbeddingBytes. Always > 0.
    public int DetectionCount { get; set; }

    // Aggregate recognition embedding: float32 little-endian packed, L2-
    // normalized, in the EmbeddingProfileId space. bytea on PostgreSQL, BLOB on
    // SQLite. NEVER surfaced through API, CLI, logs or diagnostics.
    public byte[] EmbeddingBytes { get; set; } = Array.Empty<byte>();

    public int EmbeddingDimension { get; set; }

    // Aggregate quality of the accepted detections, in [0,1]. Derived from
    // detector confidence and face size — see VideoFaceQuality.
    public double QualityScore { get; set; }

    // Normalized ([0..1] fractions of frame width/height) bounding box of the
    // representative detection. Stored so a representative crop can be produced
    // deterministically LATER from the immutable blob without re-running the
    // analysis. No absolute pixel geometry / storage identity is implied.
    public double RepresentativeBoundingBoxX { get; set; }
    public double RepresentativeBoundingBoxY { get; set; }
    public double RepresentativeBoundingBoxWidth { get; set; }
    public double RepresentativeBoundingBoxHeight { get; set; }

    // Derived-store BlobObject holding an optional protected JPEG crop of the
    // representative detection.
    //
    // VFACE-01 DELIBERATELY NEVER WRITES THIS (Gate 4: "no persistent crop").
    // Storage ownership, refcounting and janitor protection for a per-track crop
    // are a separate concern; the representative timestamp + bounding box above
    // are sufficient to regenerate the crop on demand from the immutable
    // original. The nullable column exists so VFACE-02 can adopt persisted crops
    // without a schema migration. Plain correlation id — never exposed.
    public Guid? RepresentativeCropBlobObjectId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
