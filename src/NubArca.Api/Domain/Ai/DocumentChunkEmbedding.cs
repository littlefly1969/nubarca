namespace NubArca.Api.Domain.Ai;

// Embedding of a single DocumentChunk, keyed by (chunk, profile). Phase 0A
// stores the provider-agnostic float32 byte[] only; no pgvector/ANN yet.
public class DocumentChunkEmbedding
{
    public Guid Id { get; set; }

    public Guid DocumentChunkId { get; set; }

    public Guid ProfileId { get; set; }

    // Canonical embedding: float32 little-endian packed.
    public byte[] EmbeddingBytes { get; set; } = Array.Empty<byte>();

    public int Dimension { get; set; }

    public DateTime CreatedAt { get; set; }
}
