namespace NubArca.Api.Rag.Chunking;

/// One chunk, before it has an identity in the database.
///
/// `Symbols` is what this region DECLARES — a heading trail for prose, type and
/// method names for code. It is indexed in the high-weight field, which is how
/// "where is ExternalHelpService" reaches the file that declares it rather than
/// the five files that mention it.
public sealed record RagChunkDraft(
    int Ordinal,
    string Heading,
    string Text,
    IReadOnlyList<string> Symbols)
{
    public static RagChunkDraft Of(int ordinal, string heading, string text)
        => new(ordinal, heading, text, Array.Empty<string>());
}

/// Shared sizing, so every chunker produces evidence of comparable size.
///
/// One set of numbers rather than per-chunker constants, because retrieval
/// compares chunks across kinds: if code chunks were four times longer than
/// prose chunks, BM25's length normalization would be doing the ranking.
public static class RagChunkSizes
{
    /// Below this a chunk is a fragment — a heading and one line — and is merged
    /// into its neighbour rather than shipped as evidence that says nothing.
    public const int MinimumCharacters = 400;

    /// The size evidence should normally be: enough to stand on its own, small
    /// enough that six of them fit a context budget with room left.
    public const int TargetCharacters = 1200;

    /// A chunk may exceed the target to avoid splitting a paragraph or a
    /// declaration, up to here.
    public const int MaximumCharacters = 1800;

    /// And never past here. One indivisible region longer than this is cut.
    public const int HardCharacters = 3000;
}

/// The version of NubArca's CHUNK INTERPRETATION.
///
/// Content hash alone decides whether a source is rechunked, which is correct
/// for "did the bytes change" and silently wrong for "did our reading of them
/// change". Edit MarkdownRagChunker's heading rules, teach
/// SourceCodeRagChunker a new declaration form, or change which symbols are
/// extracted, and every already-indexed source keeps its old chunks forever —
/// the improvement reaches new files only, and a corpus quietly becomes a mix
/// of two interpretations.
///
/// So a source is unchanged for reuse only when the bytes AND this number are
/// unchanged. Bumping it is a deliberate developer act with a visible cost: the
/// next index run rechunks everything and re-embeds what it rechunked. It is
/// never derived from an application version, because a release that did not
/// touch chunking should not pay for one.
public static class RagIndexFormat
{
    /// 1 — Slice 2's chunkers. Increment when chunk boundaries, headings or
    /// extracted symbols would come out differently for identical bytes.
    public const int Current = 1;
}
