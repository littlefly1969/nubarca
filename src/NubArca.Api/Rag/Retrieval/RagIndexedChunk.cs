namespace NubArca.Api.Rag.Retrieval;

/// One chunk as retrieval sees it: the text, plus every field ranking uses.
///
/// A flat in-memory record rather than the EF entities: it is what the owner's
/// corpus source produces, and it is immutable, so an index built from it holds
/// no reference to a database context.
///
/// `Feature`, `Aliases`, `Audience`, `Intent` and `Priority` are editorial
/// metadata a person's own documents do not have: the corpus source leaves them
/// empty, and the ranking profile never reads them.
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

    /// WHICH OWNER-PRIVATE SOURCE this chunk came from: the kind
    /// (DocumentSourceKinds — `file`, or a kind registered later) and that
    /// kind's id (for `file`, the FileItemId). Null for every system domain,
    /// whose chunks come from a repository or a corpus file rather than from
    /// somebody's library. Not to be confused with `SourceKind` above, which is
    /// a ranking hint about CONTENT (documentation/code).
    ///
    /// It exists so the server-only narrowing (`RagQuery.AllowedSourceIds` /
    /// `AllowedSourceKinds`) can ask the SAME index for "the best chunks, but
    /// only from these sources". Carrying it here rather than re-querying the
    /// database is what lets that be a filter on an already-built,
    /// already-owner-eligible corpus instead of a second read with a second
    /// copy of the boundary.
    ///
    /// Internal, like `OwnerUserId`: never a citation and never in a prompt.
    /// The ONE place it leaves retrieval is the owner's own document search,
    /// which returns the caller's own source kind and id so the UI can open it
    /// — a reviewed exception, see docs/rag-platform.md "Search returns the
    /// owner's own file id".
    string? DocumentSourceKind = null,
    Guid? DocumentSourceId = null,

    /// WHERE in its document this chunk sits, copied from `DocumentChunk`.
    /// `Page` is a real PDF page and nothing else; the locator triple is the
    /// typed position in the document's own units (page / section / sheet /
    /// slide). Null for every system domain and for native text. See
    /// docs/rag-platform.md "Location is typed".
    int? Page = null,
    string? LocatorKind = null,
    int? LocatorIndex = null,
    string? LocatorLabel = null,

    /// The chunk's position in its document (`DocumentChunk.Ordinal`). A
    /// position, not an id: it lets a result say "the third passage" and a
    /// benchmark compare runs, and addresses nothing on its own.
    int? Ordinal = null);

/// A domain's complete lexical corpus at one revision.
///
/// `Revision` is a property of the SNAPSHOT, not of a chunk.
public sealed record RagCorpus(
    RagDomainKey Domain,
    string Revision,
    IReadOnlyList<RagIndexedChunk> Chunks)
{
    public static RagCorpus Empty(RagDomainKey domain)
        => new(domain, string.Empty, Array.Empty<RagIndexedChunk>());

    public bool IsEmpty => Chunks.Count == 0;
}
