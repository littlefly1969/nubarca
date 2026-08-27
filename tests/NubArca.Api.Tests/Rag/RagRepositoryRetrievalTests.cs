using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Retrieval;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// Lexical retrieval for the REPOSITORY domain, whose questions look nothing
// like Product Help's.
//
// "Where is PhotoVectorIndexService" is answered by a path and a symbol, and the
// ranking profile has to reflect that: the high-weight field holds path segments
// and declared symbols rather than a feature vocabulary, and alias expansion is
// off, because `persona` and `person` are one concept to somebody asking how to
// use NubArca and two identifiers to somebody reading it.
public sealed class RagRepositoryRetrievalTests
{
    [Fact]
    public void ExactSymbolQuery_RanksDeclarationHighly()
    {
        var index = Index(
            Chunk("src/Ai/Photos/PhotoVectorIndexService.cs#1",
                symbols: new[] { "PhotoVectorIndexService", "SearchAsync" },
                text: "pgvector-backed photo similarity gateway bridging canonical embeddings."),
            Chunk("docs/ai-substrate.md#4",
                kind: RagSourceKinds.Documentation,
                text: "The substrate mentions the photo vector index service in passing."),
            Chunk("src/Files/FileItemService.cs#7",
                symbols: new[] { "FileItemService" },
                text: "Logical file operations; move and rename are database-only."));

        var hits = index.Search(Shape("PhotoVectorIndexService"), 10);

        Assert.NotEmpty(hits);
        Assert.Equal("src/Ai/Photos/PhotoVectorIndexService.cs#1", hits[0].Chunk.Id);
    }

    [Fact]
    public void ExactConfigKeyQuery_RemainsLexicallyStrong()
    {
        // A configuration key is punctuation and two words. The tokenizer splits
        // it the same way wherever it appears, so a question typed as
        // `Ai__Face__ClusterSimilarityThreshold` reaches the file that declares
        // it — which no embedding model reliably does.
        var index = Index(
            Chunk("src/Ai/AiOptions.cs#3",
                symbols: new[] { "AiFaceOptions", "ClusterSimilarityThreshold" },
                text: "Bound from Ai:Face:*. ClusterSimilarityThreshold groups faces above cosine."),
            Chunk("docs/OPERATIONS.md#9",
                kind: RagSourceKinds.Documentation,
                text: "Restore the database from the most recent backup before proceeding."));

        var hits = index.Search(Shape("Ai__Face__ClusterSimilarityThreshold"), 10);

        Assert.NotEmpty(hits);
        Assert.Equal("src/Ai/AiOptions.cs#3", hits[0].Chunk.Id);
    }

    [Fact]
    public void A_Path_Is_A_Searchable_Term()
    {
        // People half-remember paths. `facesTabs` is not in the body of the file
        // that defines it as often as it is in its name.
        var index = Index(
            Chunk("frontend/src/pages/people/facesTabs.ts#1",
                symbols: new[] { "facesTabs", "FacesTab" },
                text: "export const facesTabs = [...] describing the tab order."),
            Chunk("frontend/src/pages/AlbumsPage.tsx#2",
                symbols: new[] { "AlbumsPage" },
                text: "Renders albums with their covers and item counts."));

        var hits = index.Search(Shape("where are the face tabs defined"), 10);

        Assert.NotEmpty(hits);
        Assert.Equal("frontend/src/pages/people/facesTabs.ts#1", hits[0].Chunk.Id);
    }

    [Fact]
    public void A_Test_File_Is_Reachable_By_Its_Test_Name()
    {
        var index = Index(
            Chunk("tests/Help/HelpAssistantServiceTests.cs#4",
                kind: RagSourceKinds.Test,
                symbols: new[] { "HelpAssistantServiceTests", "RevisionMismatch_DoesNotCallModel" },
                text: "A corpus built from a different revision leaves the retriever unavailable."),
            Chunk("src/Help/HelpAssistantService.cs#2",
                symbols: new[] { "HelpAssistantService" },
                text: "Fails closed without approved product knowledge for the running revision."));

        var hits = index.Search(Shape("RevisionMismatch_DoesNotCallModel"), 10);

        Assert.NotEmpty(hits);
        Assert.Equal("tests/Help/HelpAssistantServiceTests.cs#4", hits[0].Chunk.Id);
    }

    [Fact]
    public void The_Repository_Profile_Does_Not_Expand_Product_Aliases()
    {
        // In Product Help, `persona` expands to `person`, `people`, `face`… In a
        // codebase those are distinct identifiers, and expanding would blur
        // exactly the exact-symbol queries this domain exists for.
        Assert.False(RagRankingProfiles.Repository.ExpandAliases);
        Assert.True(RagRankingProfiles.ProductHelp.ExpandAliases);

        var shape = RagQueryShape.For("persona", expandAliases: false);
        Assert.Empty(shape.Expanded);
        Assert.Equal(new[] { "persona" }, shape.Literal);
    }

    [Fact]
    public void NoStrongEvidence_ReturnsNoEvidence()
    {
        var index = Index(
            Chunk("src/Files/FileItemService.cs#1",
                symbols: new[] { "FileItemService" },
                text: "Logical file operations; move and rename are database-only."));

        // Shares nothing but ordinary words. `Score > 0` would have made this
        // "evidence".
        Assert.Empty(index.Search(Shape("quanto costa un abbonamento mensile premium"), 10));
    }

    [Fact]
    public void Ranking_Is_Deterministic()
    {
        var index = Index(
            Chunk("a.cs#1", symbols: new[] { "Alpha" }, text: "the alpha service handles requests"),
            Chunk("b.cs#1", symbols: new[] { "Alpha" }, text: "the alpha service handles requests"));

        Assert.Equal(
            index.Search(Shape("Alpha service"), 10).Select(h => h.Chunk.Id),
            index.Search(Shape("Alpha service"), 10).Select(h => h.Chunk.Id));
    }

    [Fact]
    public async Task Retrieval_Stamps_Every_Piece_Of_Evidence_With_Its_Own_Domain()
    {
        // The Assistant's gate reads the evidence's domain rather than the
        // request's, so retrieval has to put the right one there.
        var retriever = RagTestHarness.Build(
            new BundledProductHelpCorpusSource(RagTestHarness.ShippedProductHelp()));

        var result = await retriever.RetrieveAsync(new RagQuery(
            RagDomainKey.ProductHelp, "come faccio a utilizzare la funzione dei volti?", 5, 8000));

        Assert.True(result.HasStrongEvidence);
        Assert.All(result.Evidence, e => Assert.Equal(RagDomainKey.ProductHelp, e.Domain));
        Assert.All(result.Evidence, e => Assert.NotEqual(0, e.FusionRank));
        Assert.All(result.Evidence, e => Assert.NotNull(e.LexicalRank));
        // Semantic is off by default, and the mode says so rather than implying
        // a hybrid run that did not happen.
        Assert.Equal(RagRetrievalModes.Lexical, result.Mode);
        Assert.Null(result.EmbeddingProfileKey);
    }

    // ---- harness -------------------------------------------------------------

    private static RagQueryShape Shape(string query)
        => RagQueryShape.For(query, RagRankingProfiles.Repository.ExpandAliases);

    private static RagLexicalIndex Index(params RagIndexedChunk[] chunks)
        => new(
            new RagCorpus(RagDomainKey.NubArcaRepository, "test-revision", chunks),
            RagRankingProfiles.Repository);

    /// Mirrors what DatabaseRagCorpusSource composes: declared symbols and path
    /// segments share the high-weight field, which is this domain's equivalent
    /// of Product Help's feature vocabulary.
    private static RagIndexedChunk Chunk(
        string id,
        string kind = RagSourceKinds.SourceCode,
        string[]? symbols = null,
        string text = "")
    {
        var sourceKey = id[..id.IndexOf('#')];
        var pathTerms = sourceKey.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.LastIndexOf('.') > 0 ? s[..s.LastIndexOf('.')] : s);

        return new RagIndexedChunk(
            Id: id,
            Domain: RagDomainKey.NubArcaRepository,
            SourceKey: sourceKey,
            Path: sourceKey,
            Title: sourceKey[(sourceKey.LastIndexOf('/') + 1)..],
            Section: string.Join(' ', symbols ?? Array.Empty<string>()),
            Text: text,
            SourceKind: kind,
            Language: RagLanguages.Unknown,
            Revision: "test-revision",
            Feature: string.Empty,
            Aliases: (symbols ?? Array.Empty<string>()).Concat(pathTerms)
                .SelectMany(NubArca.Api.Rag.Text.RagText.IdentifierTerms)
                .Distinct(StringComparer.Ordinal).ToList(),
            Audience: string.Empty,
            Intent: string.Empty,
            Priority: kind == RagSourceKinds.Documentation ? 70 : 65);
    }
}
