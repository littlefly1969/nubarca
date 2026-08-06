namespace NubArca.Api.Domain.Ai;

// Owner/file-scoped chunk of an extracted DocumentText, for semantic search.
// Keyed uniquely by (document text, profile, ordinal). `Text` is internal-only.
// Phase 0A defines the shape only; nothing chunks anything yet.
public class DocumentChunk
{
    public Guid Id { get; set; }

    public Guid DocumentTextId { get; set; }

    // Explicit owner scope, denormalized for owner-scoped queries/isolation.
    public Guid OwnerUserId { get; set; }

    // The extraction profile that produced this chunking.
    public Guid ProfileId { get; set; }

    // Position of the chunk within the document.
    public int Ordinal { get; set; }

    // Chunk text. INTERNAL ONLY — never serialized to a normal DTO.
    public string? Text { get; set; }

    public string? TextHash { get; set; }

    public int? StartOffset { get; set; }
    public int? EndOffset { get; set; }
    public int? Page { get; set; }
    public int? TokenCount { get; set; }

    public DateTime CreatedAt { get; set; }
}
