namespace NubArca.Api.Rag.Chunking;

/// Picks the chunker for a source.
///
/// The choice is made from the source's declared code language rather than
/// sniffed from its content: the provider already decided what the file is, and
/// two independent guesses about the same file are two chances to disagree.
public static class RagChunkers
{
    public static IReadOnlyList<RagChunkDraft> Chunk(string text, string codeLanguage)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<RagChunkDraft>();

        return codeLanguage switch
        {
            RagCodeLanguages.Markdown or RagCodeLanguages.None => MarkdownRagChunker.Chunk(text),
            RagCodeLanguages.Text => MarkdownRagChunker.Chunk(text),
            _ => SourceCodeRagChunker.Chunk(text, codeLanguage),
        };
    }
}
