namespace NubArca.Api.Domain.Rag;

/// One retrievable document, identified independently of any domain.
///
/// A source exists ONCE. `frontend/src/pages/PeoplePage.tsx` is one row whether
/// it is only repository knowledge or also — through an explicit classification
/// — Product Help knowledge, and its chunks and embeddings are computed once.
/// Domain membership is a separate table (RagDomainSource) precisely so that
/// adding a domain costs a membership row rather than a second copy of the text
/// and a second copy of every vector.
///
/// `OwnerUserId` is null for system knowledge and is reserved for owner-private
/// domains. Nothing writes a non-null value in this slice.
public class RagSource
{
    public Guid Id { get; set; }

    /// Null for installation-wide knowledge. Reserved for owner-private sources.
    public Guid? OwnerUserId { get; set; }

    /// Stable, globally unique identity for the source — for repository
    /// knowledge, the repository-relative path. An owner-scoped provider must
    /// namespace its keys so this stays unique across scopes.
    public string SourceKey { get; set; } = string.Empty;

    /// See RagSourceKinds. What KIND of document this is, which is a ranking
    /// input and a safety statement, not a file extension.
    public string SourceKind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// Repository-relative display path. Never an absolute filesystem path:
    /// a physical layout is not knowledge and is not something to cite.
    public string Path { get; set; } = string.Empty;

    /// The snapshot this source was read from. A domain that cannot say which
    /// revision it describes cannot be checked against the running build.
    public string Revision { get; set; } = string.Empty;

    /// SHA-256 of the source BYTES, hex. Exactly that and nothing else — half of
    /// the idempotence key for reindexing.
    public string ContentHash { get; set; } = string.Empty;

    /// The chunk-interpretation version these chunks were produced by (see
    /// RagIndexFormat). The other half of the idempotence key.
    ///
    /// Kept as its own column rather than folded into ContentHash, because a
    /// hash that silently mixed in a version number would be documented as the
    /// source's SHA-256 and not be one — and the first person to compare it
    /// against `git hash-object` would be debugging the wrong thing.
    public int IndexFormatVersion { get; set; }

    /// Natural language of the prose (see RagLanguages), where known.
    public string Language { get; set; } = string.Empty;

    /// Programming/markup language (see RagCodeLanguages), where the source is
    /// code or structured configuration. Separate from `Language` because a
    /// TypeScript file with Italian comments has both, and collapsing them
    /// would make one of the two wrong.
    public string CodeLanguage { get; set; } = string.Empty;

    /// Provider-specific extras as JSON. INTERNAL — never serialized to a DTO.
    public string? MetadataJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
