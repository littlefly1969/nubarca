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
    Guid ChunkId = default);

/// A domain's complete lexical corpus at one revision.
///
/// `Revision` is a property of the SNAPSHOT, not of a chunk: a corpus that
/// mixes revisions cannot be checked against the running build, and "which
/// version of NubArca is this answer about" would have no answer.
public sealed record RagCorpus(
    RagDomainKey Domain,
    string Revision,
    IReadOnlyList<RagIndexedChunk> Chunks)
{
    public static RagCorpus Empty(RagDomainKey domain)
        => new(domain, string.Empty, Array.Empty<RagIndexedChunk>());

    public bool IsEmpty => Chunks.Count == 0;
}
