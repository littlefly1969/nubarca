namespace NubArca.Api.Domain.Rag;

/// One CONTENT INTERPRETATION of a document, identified independently of any
/// domain and of any snapshot.
///
/// Identity is (SourceKey, ContentHash, IndexFormatVersion) — what the document
/// is, what its bytes are, and how NubArca read them. Deliberately NOT the
/// revision. `frontend/src/pages/PeoplePage.tsx` unchanged across ten commits is
/// ONE row with one set of chunks and one embedding per profile, and each domain
/// that uses it says separately which revision it is using it AT
/// (RagDomainSource.Revision).
///
/// The predecessor put the revision here, and it deadlocked the release
/// lifecycle. One row per source key owning both the bytes and the revision
/// meant advancing `nubarca-repository` from commit A to commit B would rewrite
/// the row `product-help` was serving at A — refused, correctly — and Help could
/// not advance first for the same reason. Two domains sharing a file could
/// therefore never move at all except in one atomic multi-domain reindex.
/// Separating content identity from snapshot membership dissolves that: the
/// bytes did not change, so nothing has to be rewritten, and each membership
/// moves its own revision forward on its own schedule.
///
/// `OwnerUserId` is null for system knowledge. Owner-private knowledge does NOT
/// live here — see DocumentText/DocumentChunk, which are keyed by FileItem and
/// owner. The column stays as the scoping half of source identity for a future
/// owner-scoped system-style provider, and nothing writes a non-null value.
public class RagSource
{
    public Guid Id { get; set; }

    /// Null for installation-wide knowledge. Reserved for owner-private sources.
    public Guid? OwnerUserId { get; set; }

    /// Stable logical identity for the document — for repository knowledge, the
    /// repository-relative path. NOT unique on its own any more: the same key
    /// may have one row per content interpretation while domains upgrade
    /// independently. An owner-scoped provider must namespace its keys so this
    /// stays unambiguous across scopes.
    public string SourceKey { get; set; } = string.Empty;

    /// See RagSourceKinds. What KIND of document this is, which is a ranking
    /// input and a safety statement, not a file extension.
    public string SourceKind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// Repository-relative display path. Never an absolute filesystem path:
    /// a physical layout is not knowledge and is not something to cite.
    public string Path { get; set; } = string.Empty;

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
