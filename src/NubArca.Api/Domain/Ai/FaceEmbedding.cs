namespace NubArca.Api.Domain.Ai;

// Recognition embedding of a single FaceDetection, keyed by (detection, profile).
// Blob-level (its detection is blob-level), so it is shared across owners for the
// same blob; owner boundaries are enforced when surfacing results.
//
// Stores the provider-agnostic float32 byte[] as the CANONICAL vector (source of
// truth + exact-scan fallback). A dimension-specific pgvector shadow table
// (face_embedding_vectors_512) mirrors it for ANN search. The raw vector is
// NEVER exposed through API/CLI.
public class FaceEmbedding
{
    public Guid Id { get; set; }

    public Guid FaceDetectionId { get; set; }

    public Guid ProfileId { get; set; }

    // Canonical embedding: float32 little-endian packed. bytea on PostgreSQL,
    // BLOB on SQLite. Never surfaced.
    public byte[] EmbeddingBytes { get; set; } = Array.Empty<byte>();

    public int Dimension { get; set; }

    // State of the embedding attempt (see AiArtifactStatuses):
    //   completed → a finite, correctly-dimensioned vector is stored;
    //   failed    → a TRANSIENT per-face error (retried on a later run);
    //   skipped   → a PERMANENT per-face non-embeddable condition (never retried).
    // A failed/skipped row exists so an attempted-but-unembedded face is never
    // indistinguishable from a never-attempted (missing) one; it carries empty
    // EmbeddingBytes and no pgvector row.
    public string EmbeddingStatus { get; set; } = AiArtifactStatuses.Completed;

    // Sanitized machine-readable reason for a failed/skipped face (see
    // FaceEmbeddingErrorCodes). Null for completed. Never a path/stack trace/secret.
    public string? ErrorCode { get; set; }

    // Number of embedding attempts on this face (observability + future backoff).
    public int AttemptCount { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
