using NubArca.Api.Rag.Chunking;

namespace NubArca.Api.Rag.ProductHelp;

/// Builds the `product-help` corpus from a source checkout, at image-build time.
///
/// This is the BUNDLED corpus: the one that ships inside the production image so
/// Help answers on an installation that has never run `rag index` and has no
/// repository checkout. The general path — sources, chunks, embeddings in
/// PostgreSQL — is RagIndexer, and it wins whenever it has content.
///
/// Two things this must keep doing, both about answer quality rather than about
/// the boundary:
///
///  1. Eligibility comes from ProductHelpSources.Manifest, so a runbook does not
///     compete with a user guide merely by existing.
///  2. Chunks follow SECTIONS — and they follow them through the SAME chunker
///     the database index uses, so the bundled corpus and the indexed one are
///     not two different opinions about where a document divides.
///
/// The boundary itself is unchanged: build-time, public-only, revision-pinned,
/// no runtime repository access.
public static class ProductHelpCorpusBuilder
{
    public static ProductHelpCorpus Build(string sourceRoot, string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        var documents = new List<ProductHelpDocument>();

        // Manifest ORDER, not directory order: the corpus is deterministic
        // across machines and filesystems, which is what makes a golden
        // retrieval test meaningful.
        foreach (var source in ProductHelpSources.Manifest)
        {
            var full = Path.Combine(sourceRoot, source.Path.Replace('/', Path.DirectorySeparatorChar));
            string text;
            try
            {
                if (!File.Exists(full)) continue;
                text = File.ReadAllText(full);
            }
            catch (IOException)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(text)) continue;

            var title = MarkdownRagChunker.FirstHeading(text)
                        ?? Path.GetFileNameWithoutExtension(source.Path);

            foreach (var draft in MarkdownRagChunker.Chunk(text))
            {
                documents.Add(new ProductHelpDocument(
                    Id: $"{source.Path}#{draft.Ordinal}",
                    Path: source.Path,
                    Title: title,
                    Section: draft.Heading,
                    Text: draft.Text,
                    Feature: source.Feature,
                    Intent: source.Intent,
                    Audience: source.Audience,
                    Language: source.Language,
                    SourceKind: source.SourceKind,
                    Aliases: source.Aliases,
                    Priority: source.Priority));
            }
        }

        return new ProductHelpCorpus(
            RagDomainKey.ProductHelp.Value, revision ?? string.Empty, documents);
    }

    /// Which paths may be indexed at all. Public so a test asserts the boundary
    /// directly rather than inferring it from what happened to be indexed.
    public static bool IsEligible(string relativePath) => ProductHelpSources.IsApproved(relativePath);
}
