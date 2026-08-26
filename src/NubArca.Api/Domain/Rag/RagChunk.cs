namespace NubArca.Api.Domain.Rag;

/// One retrievable passage of a source.
///
/// Deliberately NOT DocumentChunk. That entity is an owner/file-scoped artifact
/// of the document-extraction pipeline, keyed by DocumentTextId and OwnerUserId
/// and meaningful only inside one person's library; making it also mean "a piece
/// of system knowledge" would give one table two ownership stories and one
/// privacy story to get wrong. A user document can become a RAG source later
/// through an adapter — which is a mapping, not a redefinition.
public class RagChunk
{
    public Guid Id { get; set; }

    public Guid SourceId { get; set; }

    /// Position within the source. Part of the chunk's identity, so reindexing
    /// the same snapshot updates rows instead of appending them.
    public int Ordinal { get; set; }

    /// Where in the document this came from — a heading trail for prose, a
    /// symbol or line range for code. A citation a person can act on.
    public string Heading { get; set; } = string.Empty;

    /// The passage. INTERNAL ONLY — retrieval returns it as bounded evidence,
    /// and nothing else serializes it.
    public string Text { get; set; } = string.Empty;

    /// SHA-256 of Text, hex. Unchanged text keeps its embedding.
    public string TextHash { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public string? MetadataJson { get; set; }

    public DateTime CreatedAt { get; set; }
}
