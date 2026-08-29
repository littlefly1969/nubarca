namespace NubArca.Api.Rag.Retrieval;

/// One chunk as retrieval sees it: the text, plus every field ranking uses.
///
/// A flat in-memory record rather than the EF entities, for two reasons. It is
/// the shape BOTH corpus sources produce — the database index and the bundled
/// Product Help corpus that ships in the image — so lexical retrieval has one
/// implementation instead of two. And it is immutable, so the index built from
/// it can be shared across requests without a lock.
///
/// `Feature`, `Aliases`, `Audience`, `Intent` and `Priority` are DOMAIN
/// MEMBERSHIP metadata. Product Help fills them; a repository chunk leaves them
/// empty, and its ranking profile never reads them. The schema being able to
/// hold an `intent` does not mean a C# file needs one.
public sealed record RagIndexedChunk(
    string Id,
    RagDomainKey Domain,
    string SourceKey,
    string Path,
    string Title,
    string Section,
    string Text,
    string SourceKind,
    string Language,
    string Revision,
    string Feature,
    IReadOnlyList<string> Aliases,
    string Audience,
    string Intent,
    int Priority,
    Guid ChunkId = default,

    /// WHOSE knowledge this chunk is, for an owner-scoped domain. Null for
    /// every system domain, whose knowledge belongs to the installation.
    ///
    /// Stamped by the corpus source from the LIVE owner the eligibility join
    /// verified — never from the query that asked. The difference is the whole
    /// point: an owner copied off the request and back onto the evidence proves
    /// only that the request was consistent with itself, so a later gate
    /// comparing the two would be checking the caller against the caller. This
    /// field is what the corpus actually contained, so the gate compares two
    /// independently-derived facts.
    ///
    /// Internal provenance, like `Revision`: never a response field, a citation,
    /// a log line or anything a prompt sees.
    Guid? OwnerUserId = null,

    /// WHICH FILE this chunk came from, for an owner-scoped domain. Null for
    /// every system domain, whose chunks come from a repository or a corpus
    /// file rather than from somebody's library.
    ///
    /// It exists for exactly one caller: the visual candidate expansion, which
    /// needs to ask the SAME index for "the best chunks, but only from these
    /// documents". Carrying it here rather than re-querying the database is what
    /// lets that be a filter on an already-built, already-owner-eligible corpus
    /// instead of a second read with a second copy of the boundary.
    ///
    /// Internal, like `OwnerUserId`: never a response field, never a citation,
    /// never in a prompt.
    Guid? FileItemId = null);

/// A domain's complete lexical corpus at one revision.
///
/// `Revision` is a property of the SNAPSHOT, not of a chunk: a corpus that
/// mixes revisions cannot be checked against the running build, and "which
/// version of NubArca is this answer about" would have no answer.
/// `IsMixedRevision` means the domain's sources came from more than one commit —
/// an interrupted reindex, since indexing commits incrementally. Such a corpus
/// is refused rather than served: half of it describes one release and half
/// another, and no single revision is an honest answer to "which version is this
/// about". It resolves itself once a complete reindex finishes.
public sealed record RagCorpus(
    RagDomainKey Domain,
    string Revision,
    IReadOnlyList<RagIndexedChunk> Chunks,
    bool IsMixedRevision = false)
{
    public static RagCorpus Empty(RagDomainKey domain)
        => new(domain, string.Empty, Array.Empty<RagIndexedChunk>());

    public bool IsEmpty => Chunks.Count == 0;
}
