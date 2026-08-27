namespace NubArca.Api.Domain.Rag;

/// The CANONICAL embedding of a chunk under one profile.
///
/// Canonical means: this row is the truth, and the pgvector table is a search
/// accelerator built from it. The photo substrate learned this the useful way —
/// a vector store that is also the only copy makes changing index strategy, or
/// running on a Postgres without pgvector, a data-migration problem instead of a
/// configuration one.
///
/// Keyed by (ChunkId, ProfileId): a new model or a new dimension is a NEW
/// profile with its own rows, never a reinterpretation of existing bytes.
public class RagChunkEmbedding
{
    public Guid Id { get; set; }

    public Guid ChunkId { get; set; }

    /// The AiProfile that produced these bytes. Retrieval filters on it
    /// exactly; two profiles are never compared.
    public Guid ProfileId { get; set; }

    /// float32 little-endian packed, via IAiVectorSerializer. INTERNAL — raw
    /// vectors are never exposed through an API, a CLI or a normal log line.
    public byte[] EmbeddingBytes { get; set; } = Array.Empty<byte>();

    public int Dimension { get; set; }

    public DateTime CreatedAt { get; set; }
}
