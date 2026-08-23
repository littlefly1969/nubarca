using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Help;
using Xunit;

namespace NubArca.Api.Tests.Help;

// E and F — what may become retrievable, and from which revision.
//
// The corpus is the complete list of things an external model can be shown. It
// is built from an ALLOWLIST rather than filtered by a denylist, because a
// denylist is a claim to have thought of every secret, while an allowlist is a
// statement of what was deliberately included.
public sealed class HelpKnowledgeBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"nubarca-help-corpus-{Guid.NewGuid():N}");

    public HelpKnowledgeBoundaryTests() => Directory.CreateDirectory(_root);

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

    // ---- F. the retrieval boundary ----------------------------------------

    [Fact]
    public void Only_Allowlisted_Public_Product_Documents_Can_Be_Indexed()
    {
        const string secret = "PRIVATE_FILE_SENTINEL_X91";

        // Public product material.
        Write("README.md", "# NubArca\n\nNubArca is a self-hosted personal media library.");
        Write("ARCHITECTURE.md", "# Architecture\n\nThe API is an ASP.NET Core minimal API.");
        Write("docs/albums.md", "# Albums\n\nAn album is a named collection of media.");

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

        var corpus = HelpCorpusBuilder.Build(_root, "rev-1");
        // A SET, not a sequence: what may leave is the question, not its order.
        var indexed = corpus.Documents.Select(d => d.Path).Distinct()
            .OrderBy(p => p, StringComparer.Ordinal).ToList();

        Assert.Equal(
            new[] { "ARCHITECTURE.md", "README.md", "docs/albums.md" }
                .OrderBy(p => p, StringComparer.Ordinal).ToList(),
            indexed);
        foreach (var document in corpus.Documents)
        {
            Assert.DoesNotContain(secret, document.Text, StringComparison.Ordinal);
        }

        // …and the sentinel is unreachable through the retriever, which is the
        // surface that actually feeds the provider.
        var retriever = new FileHelpKnowledgeRetriever(corpus);
        var hits = retriever.Retrieve(secret, 10, 10000);
        Assert.Empty(hits);
        var broad = retriever.Retrieve("album collection media library", 10, 10000);
        Assert.NotEmpty(broad);
        Assert.All(broad, h => Assert.DoesNotContain(secret, h.Text, StringComparison.Ordinal));
    }

    [Theory]
    // Included: named public product material.
    [InlineData("README.md", true)]
    [InlineData("ARCHITECTURE.md", true)]
    [InlineData("docs/anything.md", true)]
    [InlineData("docs/model-deployment/nested.md", true)]
    // Excluded by not being named, by being hidden, or by being non-prose.
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
    public void Eligibility_Is_Decided_By_The_Allowlist(string path, bool eligible)
        => Assert.Equal(eligible, HelpCorpusBuilder.IsEligible(path));

    // ---- E. revision provenance -------------------------------------------

    [Fact]
    public void A_Corpus_From_A_Different_Revision_Is_Refused()
    {
        Write("README.md", "# NubArca\n\nNubArca is a self-hosted personal media library.");
        var corpus = HelpCorpusBuilder.Build(_root, "corpus-revision-aaa");
        var path = Path.Combine(_root, "corpus.json");
        File.WriteAllText(path, JsonSerializer.Serialize(corpus));

        // Help that answered from a different revision would describe features
        // the installed release does not have, which is worse than no Help.
        var mismatched = Load(path, running: "running-revision-bbb");
        Assert.False(mismatched.IsAvailable);
        Assert.Empty(mismatched.Retrieve("nubarca", 5, 5000));

        var matched = Load(path, running: "corpus-revision-aaa");
        Assert.True(matched.IsAvailable);
        Assert.Equal("corpus-revision-aaa", matched.Revision);
        Assert.NotEmpty(matched.Retrieve("nubarca media library", 5, 5000));
    }

    [Fact]
    public void The_Corpus_Records_The_Revision_It_Was_Built_From()
    {
        Write("README.md", "# NubArca\n\nA self-hosted personal media library.");
        var corpus = HelpCorpusBuilder.Build(_root, "abc123");
        Assert.Equal("abc123", corpus.Revision);
        Assert.NotEmpty(corpus.Documents);
    }

    [Fact]
    public void A_Missing_Corpus_Leaves_Help_Knowledge_Unavailable_Rather_Than_Failing()
    {
        var retriever = Load(Path.Combine(_root, "does-not-exist.json"), running: "any");
        Assert.False(retriever.IsAvailable);
        Assert.Null(retriever.Revision);
        Assert.Empty(retriever.Retrieve("anything", 5, 5000));
    }

    [Fact]
    public void Retrieval_Respects_Its_Excerpt_And_Character_Budgets()
    {
        for (var i = 0; i < 12; i++)
        {
            Write($"docs/topic{i}.md", $"# Topic {i}\n\nAlbums and media library behaviour, part {i}.");
        }
        var retriever = new FileHelpKnowledgeRetriever(HelpCorpusBuilder.Build(_root, "r"));

        var few = retriever.Retrieve("albums media library", maxExcerpts: 3, maxCharacters: 10000);
        Assert.True(few.Count <= 3);

        var tight = retriever.Retrieve("albums media library", maxExcerpts: 10, maxCharacters: 60);
        Assert.True(tight.Sum(e => e.Text.Length) <= 60);
    }

    /// The revision gate lives in the options-based constructor, so this drives
    /// the same path production uses rather than a test-only shortcut.
    private static FileHelpKnowledgeRetriever Load(string corpusPath, string running)
    {
        var previous = Environment.GetEnvironmentVariable("NUBARCA_GIT_SHA");
        Environment.SetEnvironmentVariable("NUBARCA_GIT_SHA", running);
        try
        {
            return new FileHelpKnowledgeRetriever(
                Options.Create(new ExternalHelpOptions { CorpusPath = corpusPath }),
                NullLogger<FileHelpKnowledgeRetriever>.Instance);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUBARCA_GIT_SHA", previous);
        }
    }
}
