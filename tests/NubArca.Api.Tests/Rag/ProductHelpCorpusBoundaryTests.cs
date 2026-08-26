using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NubArca.Api.Rag;
using NubArca.Api.Rag.ProductHelp;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// What may become retrievable, and from which revision.
//
// The corpus is the complete list of things a model can be shown. It is built
// from a MANIFEST rather than filtered by a denylist, because a denylist is a
// claim to have thought of every secret, while a manifest is a statement of
// what was deliberately included — and now also of who each document is for.
public sealed class ProductHelpCorpusBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"nubarca-product-help-{Guid.NewGuid():N}");

    public ProductHelpCorpusBoundaryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Write(string relative, string text)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    // ---- the retrieval boundary -------------------------------------------

    [Fact]
    public void Only_Approved_Public_Product_Documents_Can_Be_Indexed()
    {
        const string secret = "PRIVATE_FILE_SENTINEL_X91";

        // Approved product material.
        Write("README.md", "# NubArca\n\nNubArca is a self-hosted personal media library.");
        Write("ARCHITECTURE.md", "# Architecture\n\nThe API is an ASP.NET Core minimal API.");
        Write("docs/help/faces.md", "# Volti\n\nI gruppi suggeriti raccolgono volti simili.");

        // Everything a corpus must never absorb, each carrying the sentinel so a
        // leak is unmistakable rather than a judgement call.
        Write(".env", $"SECRET={secret}");
        Write(".env.production", $"SECRET={secret}");
        Write("docker-compose.prod.local.yml", $"# operator override {secret}");
        Write("deploy/FAST_DEPLOY.md", $"# Deploy\n\nssh {secret}");
        Write("src/NubArca.Api/Program.cs", $"// {secret}");
        Write("frontend/src/App.tsx", $"// {secret}");
        Write("tests/Something.md", $"# test artifact {secret}");
        Write("node_modules/pkg/README.md", $"# dependency {secret}");
        Write("bin/Debug/output.md", $"# build output {secret}");
        Write("docs/current-work.md", $"# internal notes {secret}");
        Write(".github/workflows/ci.md", $"# ci {secret}");
        Write("scratch.md", $"# local scratch {secret}");
        // …and the new failure mode the manifest exists to prevent: a public,
        // tracked, perfectly innocent document that nobody classified. It used
        // to be indexed automatically for being under docs/.
        Write("docs/some-new-runbook.md", $"# unclassified {secret}");
        Write("docs/help/internal-implementation-plan.md", $"# slice plan {secret}");

        var corpus = ProductHelpCorpusBuilder.Build(_root, "rev-1");
        // A SET, not a sequence: what may be shown is the question, not its order.
        var indexed = corpus.Documents.Select(d => d.Path).Distinct()
            .OrderBy(p => p, StringComparer.Ordinal).ToList();

        Assert.Equal(
            new[] { "ARCHITECTURE.md", "README.md", "docs/help/faces.md" }
                .OrderBy(p => p, StringComparer.Ordinal).ToList(),
            indexed);
        foreach (var document in corpus.Documents)
        {
            Assert.DoesNotContain(secret, document.Text, StringComparison.Ordinal);
        }

        // …and the sentinel is unreachable through the retriever, which is the
        // surface that actually feeds the model.
        var retriever = new ProductHelpRetriever(corpus);
        Assert.Empty(Ask(retriever, secret));
        var broad = Ask(retriever, "gruppi suggeriti volti");
        Assert.NotEmpty(broad);
        Assert.All(broad, h => Assert.DoesNotContain(secret, h.Text, StringComparison.Ordinal));
    }

    [Theory]
    // Included: named, classified product material.
    [InlineData("README.md", true)]
    [InlineData("ARCHITECTURE.md", true)]
    [InlineData("docs/help/faces.md", true)]
    [InlineData("docs/help/faces.en.md", true)]
    [InlineData("docs/OPERATIONS.md", true)]
    // Excluded by not being named — which now includes ordinary public docs.
    [InlineData("docs/anything.md", false)]
    [InlineData("docs/model-deployment/nested.md", false)]
    [InlineData("docs/tv-release.md", false)]
    [InlineData(".env", false)]
    [InlineData(".env.production", false)]
    [InlineData(".github/workflows/ci.yml", false)]
    [InlineData("CLAUDE.md", false)]
    [InlineData("docs/current-work.md", false)]
    [InlineData("deploy/FAST_DEPLOY.md", false)]
    [InlineData("src/NubArca.Api/Program.cs", false)]
    [InlineData("frontend/src/App.tsx", false)]
    [InlineData("node_modules/x/README.md", false)]
    [InlineData("docker-compose.prod.local.yml", false)]
    [InlineData("../outside/secrets.md", false)]
    [InlineData("docs/../.env", false)]
    public void Eligibility_Is_Decided_By_The_Manifest(string path, bool eligible)
        => Assert.Equal(eligible, ProductHelpCorpusBuilder.IsEligible(path));

    [Fact]
    public void Every_Approved_Source_Is_Classified_With_The_Controlled_Vocabulary()
    {
        // An unbounded tag space would make every new source a new ranking
        // special case, and a misspelled `SourceKind` would silently stop
        // ranking rather than fail.
        var audiences = new[]
        {
            ProductHelpVocabulary.Audience.User,
            ProductHelpVocabulary.Audience.Admin,
            ProductHelpVocabulary.Audience.Technical,
        };
        var intents = new[]
        {
            ProductHelpVocabulary.Intent.HowTo, ProductHelpVocabulary.Intent.Explanation,
            ProductHelpVocabulary.Intent.Troubleshooting, ProductHelpVocabulary.Intent.Reference,
        };
        var kinds = new[]
        {
            ProductHelpVocabulary.SourceKind.UserGuide, ProductHelpVocabulary.SourceKind.UiContract,
            ProductHelpVocabulary.SourceKind.FeatureCatalog, ProductHelpVocabulary.SourceKind.AdminGuide,
            ProductHelpVocabulary.SourceKind.TechnicalReference,
        };
        var languages = new[]
        {
            ProductHelpVocabulary.Language.Italian, ProductHelpVocabulary.Language.English,
        };

        Assert.NotEmpty(ProductHelpSources.Manifest);
        foreach (var source in ProductHelpSources.Manifest)
        {
            Assert.Contains(source.Audience, audiences);
            Assert.Contains(source.Intent, intents);
            Assert.Contains(source.SourceKind, kinds);
            Assert.Contains(source.Language, languages);
            Assert.InRange(source.Priority, 1, 100);
            Assert.NotEmpty(source.Aliases);
            Assert.False(string.IsNullOrWhiteSpace(source.Feature));
        }
    }

    [Fact]
    public void Every_Approved_Source_Exists_In_The_Repository()
    {
        // A manifest entry with no file is knowledge that silently stopped
        // shipping — a rename nobody noticed, which shows up as an assistant
        // that has become vaguer for no visible reason.
        var root = ProductHelpRetrievalTests.RepositoryRoot();
        foreach (var source in ProductHelpSources.Manifest)
        {
            Assert.True(
                File.Exists(Path.Combine(root, source.Path.Replace('/', Path.DirectorySeparatorChar))),
                $"approved Product Help source is missing: {source.Path}");
        }
    }

    [Fact]
    public void Internal_Planning_Material_Is_Not_Product_Help_Knowledge()
    {
        // Implementation slices, agent prompts and branch notes describe how the
        // product is BUILT. Nobody asking how to use faces wants one, and an
        // assistant quoting one sounds like it is describing unreleased work.
        foreach (var path in new[]
                 {
                     "docs/current-work.md", "CLAUDE.md", "AGENTS.md", "ROADMAP.md",
                     "DEVELOPMENT_STATE.md", "docs/help/assistant-rag-slice-01.md",
                 })
        {
            Assert.False(ProductHelpCorpusBuilder.IsEligible(path), $"{path} must not be indexed");
        }
    }

    // ---- revision provenance -------------------------------------------------

    [Fact]
    public void A_Corpus_From_A_Different_Revision_Is_Refused()
    {
        Write("README.md", "# NubArca\n\nNubArca is a self-hosted personal media library.");
        var corpus = ProductHelpCorpusBuilder.Build(_root, "corpus-revision-aaa");
        var path = Path.Combine(_root, "corpus.json");
        File.WriteAllText(path, JsonSerializer.Serialize(corpus));

        // Help that answered from a different revision would describe features
        // the installed release does not have, which is worse than no Help.
        var mismatched = Load(path, running: "running-revision-bbb");
        Assert.False(mismatched.IsAvailable);
        Assert.Empty(Ask(mismatched, "nubarca media library"));

        var matched = Load(path, running: "corpus-revision-aaa");
        Assert.True(matched.IsAvailable);
        Assert.Equal("corpus-revision-aaa", matched.Revision);
        Assert.NotEmpty(Ask(matched, "nubarca media library"));
    }

    [Fact]
    public void The_Corpus_Records_Its_Domain_And_The_Revision_It_Was_Built_From()
    {
        Write("README.md", "# NubArca\n\nA self-hosted personal media library.");
        var corpus = ProductHelpCorpusBuilder.Build(_root, "abc123");
        Assert.Equal("abc123", corpus.Revision);
        Assert.Equal(RagDomainKey.ProductHelp.Value, corpus.Domain);
        Assert.NotEmpty(corpus.Documents);
    }

    [Fact]
    public void A_Missing_Corpus_Leaves_Help_Knowledge_Unavailable_Rather_Than_Failing()
    {
        var retriever = Load(Path.Combine(_root, "does-not-exist.json"), running: "any");
        Assert.False(retriever.IsAvailable);
        Assert.Null(retriever.Revision);
        Assert.Equal(
            RagRetrievalOutcome.Unavailable,
            retriever.Retrieve(new RagQuery(RagDomainKey.ProductHelp, "anything", 5, 5000)).Outcome);
    }

    private static IReadOnlyList<RagEvidence> Ask(ProductHelpRetriever retriever, string question)
        => retriever.Retrieve(new RagQuery(RagDomainKey.ProductHelp, question, 10, 10000)).Evidence;

    /// Drives the same load path production uses rather than a test-only
    /// shortcut, so the revision gate itself is what is under test.
    private static ProductHelpRetriever Load(string corpusPath, string running)
        => new(ProductHelpCorpusLoader.Load(
            corpusPath, running, NullLogger<ProductHelpRetriever>.Instance));
}
