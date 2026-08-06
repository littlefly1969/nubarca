namespace NubArca.Api.Domain.Ai;

// Whole-blob embedding output (e.g. visual similarity), keyed by (blob, profile).
// Blob-level because it is deterministic on the immutable bytes, so it is shared
// by every FileItem that references the blob — exactly like BlobMetadata.
//
// Phase 0A stores the embedding as a provider-agnostic float32 byte[] only
// (EmbeddingBytes). pgvector and ANN search are a later phase; there is no
// vector column here.
public class BlobEmbedding
{
    public Guid Id { get; set; }

    public Guid BlobObjectId { get; set; }

    public Guid ProfileId { get; set; }

    // Canonical embedding: float32 little-endian packed. bytea on PostgreSQL,
    // BLOB on SQLite.
    public byte[] EmbeddingBytes { get; set; } = Array.Empty<byte>();

    public int Dimension { get; set; }

    public DateTime CreatedAt { get; set; }
}
