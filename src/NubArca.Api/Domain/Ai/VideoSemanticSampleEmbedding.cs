namespace NubArca.Api.Domain.Ai;

// VSEM-02: the canonical SigLIP2 embedding of ONE temporal sample under ONE AI
// profile.
//
// BLOB-LEVEL AND OWNER-FREE, exactly like BlobEmbedding and the temporal
// manifest it hangs off: the sample belongs to a segment of a versioned
// VideoSemanticIndex, which is keyed by blob — so nothing here names an owner,
// a FileItem, a person, a filename, a path or a storage key. Every FileItem
// referencing the blob (any owner, vaulted or not) shares these rows; whether a
// viewer may reach them is decided by the later retrieval path, never here.
//
// One row exists per (VideoSemanticSampleId, ProfileId). Profiles never share
// an embedding space: a second profile writes its own rows next to the first,
// and a segmentation reindex writes a whole new sample tree under the new
// manifest version, so versions coexist without contamination.
//
// A row is written only at a terminal per-sample outcome: `completed` carries
// the packed float32 vector; `failed` carries an empty payload plus a sanitized
// error code and stays retryable. A MISSING row means "not attempted yet"
// (implicit pending — same sparse rule as BlobAiArtifactStatus).
public class VideoSemanticSampleEmbedding
{
    public Guid Id { get; set; }

    public Guid VideoSemanticSampleId { get; set; }

    public Guid ProfileId { get; set; }

    // Canonical embedding: float32 little-endian packed via IAiVectorSerializer
    // (same representation as BlobEmbedding.EmbeddingBytes). Empty for a failed
    // attempt.
    public byte[] EmbeddingBytes { get; set; } = Array.Empty<byte>();

    // Vector dimension (profile-declared). 0 for a failed attempt.
    public int Dimension { get; set; }

    // One of AiArtifactStatuses: completed | failed. There is no stored
    // "pending" and no per-sample "skipped" — a content-level skip is recorded
    // on the aggregate status, never by materialising per-sample rows.
    public string Status { get; set; } = AiArtifactStatuses.Completed;

    // Sanitized machine-readable code from VideoSemanticErrorCodes for a failed
    // attempt. Never raw FFmpeg/provider output, exception text or paths.
    public string? ErrorCode { get; set; }

    public int AttemptCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    // Set when the embedding completed successfully.
    public DateTime? CompletedAt { get; set; }
}
