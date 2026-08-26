using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Sources;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// What may become repository knowledge.
//
// Everything here runs against a TEMPORARY fixture tree with a fake file
// lister, never against the developer's own working copy: a test whose result
// depends on what somebody has checked out is a test that fails on one machine
// and passes on another, and its failures teach nobody anything.
public sealed class RepositorySourceEligibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "nubarca-rag-src-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    // ---- the path gate -------------------------------------------------------

    [Theory]
    // Included: the source, tests, migrations, docs and scripts that describe
    // what NubArca is and how it behaves.
    [InlineData("src/NubArca.Api/Help/HelpAssistantService.cs", true)]
    [InlineData("tests/NubArca.Api.Tests/Help/HelpPrivacyTests.cs", true)]
    [InlineData("frontend/src/pages/PeoplePage.tsx", true)]
    [InlineData("docs/help-assistant.md", true)]
    [InlineData("scripts/check-nubarca-identity.sh", true)]
    [InlineData("docker-compose.prod.yml", true)]
    [InlineData("src/NubArca.Api/Data/Migrations/20260101_Whatever.cs", true)]
    // Excluded: git internals, build output, dependencies, generated bundles.
    [InlineData(".git/config", false)]
    [InlineData(".git/objects/ab/cdef", false)]
    [InlineData("src/NubArca.Api/bin/Debug/net10.0/NubArca.Api.dll", false)]
    [InlineData("src/NubArca.Api/obj/project.assets.json", false)]
    [InlineData("node_modules/react/index.js", false)]
    [InlineData("frontend/dist/assets/index-abc.js", false)]
    [InlineData("coverage/lcov.info", false)]
    [InlineData("graphify-out/graph.json", false)]
    // Excluded: configuration that carries, or looks like it carries, secrets.
    [InlineData(".env", false)]
    [InlineData(".env.production", false)]
    [InlineData("deploy/.env.local", false)]
    [InlineData("secrets/service-account.json", false)]
    [InlineData("keys/id_rsa", false)]
    [InlineData("certs/server.pem", false)]
    [InlineData("tv/release.keystore", false)]
    // Excluded: not a text type this policy indexes.
    [InlineData("assets/logo.png", false)]
    [InlineData("docs/diagram.pdf", false)]
    [InlineData("models/model.onnx", false)]
    // Excluded: lockfiles — thousands of lines that describe npm, not NubArca.
    [InlineData("frontend/package-lock.json", false)]
    // Excluded: traversal, which cannot reach the manifest but is refused
    // explicitly rather than by luck.
    [InlineData("docs/../.env", false)]
    public void The_Path_Policy_Decides_What_Is_Indexable(string path, bool eligible)
        => Assert.Equal(eligible, RepositorySourcePolicy.CheckPath(path).IsEligible);

    [Fact]
    public void Source_Files_Are_Not_Denied_For_Describing_Credentials()
    {
        // The suspect-word rule applies to CONFIGURATION, not to code. Applied
        // to everything, it excluded an EF migration named
        // `AddTvPersonalSecretScheme` and would exclude `PasswordResetToken.cs`
        // — which are the answer to "how does NubArca handle credentials", the
        // exact kind of question this domain exists for. This was found by
        // indexing the real repository, not by reading the rule.
        Assert.True(RepositorySourcePolicy.CheckPath(
            "src/NubArca.Api/Data/Migrations/20260809003319_AddTvPersonalSecretScheme.cs").IsEligible);
        Assert.True(RepositorySourcePolicy.CheckPath(
            "src/NubArca.Api/Domain/PasswordResetToken.cs").IsEligible);
        Assert.True(RepositorySourcePolicy.CheckPath(
            "src/NubArca.Api/Auth/Recovery/PasswordRecoveryService.cs").IsEligible);
        Assert.True(RepositorySourcePolicy.CheckPath("docs/tv-ota-updates.md").IsEligible);

        // …and a configuration file with the same word in its name stays out.
        Assert.False(RepositorySourcePolicy.CheckPath("deploy/secrets.json").IsEligible);
        Assert.False(RepositorySourcePolicy.CheckPath("config/credentials.yml").IsEligible);
        Assert.False(RepositorySourcePolicy.CheckPath("tokens.txt").IsEligible);
    }

    [Fact]
    public void SafeExampleConfig_RequiresExplicitEligibility()
    {
        // `.env.example` is genuinely the answer to "what configuration does
        // NubArca take", and it is allowed by NAME rather than by a pattern —
        // so a neighbour nobody reviewed does not inherit the exception.
        Assert.True(RepositorySourcePolicy.CheckPath(".env.example").IsEligible);
        Assert.Equal(
            RagSourceKinds.ExampleConfiguration,
            RepositorySourcePolicy.SourceKindOf(".env.example"));

        Assert.False(RepositorySourcePolicy.CheckPath(".env.production.example").IsEligible);
        Assert.False(RepositorySourcePolicy.CheckPath("deploy/.env.example.bak").IsEligible);
    }

    [Fact]
    public void SourceKind_Is_Decided_By_Where_A_File_Lives()
    {
        Assert.Equal(RagSourceKinds.SourceCode,
            RepositorySourcePolicy.SourceKindOf("src/NubArca.Api/Program.cs"));
        Assert.Equal(RagSourceKinds.Test,
            RepositorySourcePolicy.SourceKindOf("tests/NubArca.Api.Tests/Help/HelpPrivacyTests.cs"));
        Assert.Equal(RagSourceKinds.Test,
            RepositorySourcePolicy.SourceKindOf("frontend/src/pages/HelpPage.test.tsx"));
        Assert.Equal(RagSourceKinds.Migration,
            RepositorySourcePolicy.SourceKindOf("src/NubArca.Api/Data/Migrations/20260101_X.cs"));
        Assert.Equal(RagSourceKinds.Documentation,
            RepositorySourcePolicy.SourceKindOf("docs/ai-substrate.md"));
        Assert.Equal(RagSourceKinds.Script,
            RepositorySourcePolicy.SourceKindOf("scripts/prod-dc.sh"));
        Assert.Equal(RagSourceKinds.Configuration,
            RepositorySourcePolicy.SourceKindOf("docker-compose.prod.yml"));
    }

    // ---- the content gate ----------------------------------------------------

    [Fact]
    public void RejectsBinaryFiles()
    {
        // A NUL byte is the same test `git diff` uses, and it is the right one:
        // no text file NubArca stores contains one, and every binary does. An
        // extension allowlist alone would let a `.json` full of base64-decoded
        // rubbish through.
        var binary = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02 };
        Assert.True(RepositorySourcePolicy.LooksBinary(binary));
        Assert.False(RepositorySourcePolicy.CheckContent("a.json", binary).IsEligible);

        Assert.False(RepositorySourcePolicy.LooksBinary("plain text\nmore text"u8.ToArray()));
    }

    [Fact]
    public void RejectsEmptyAndOversizedFiles()
    {
        Assert.False(RepositorySourcePolicy.CheckContent("a.md", Array.Empty<byte>()).IsEligible);

        var huge = new byte[RepositorySourcePolicy.MaximumBytes + 1];
        Array.Fill(huge, (byte)'a');
        Assert.False(RepositorySourcePolicy.CheckContent("a.md", huge).IsEligible);
    }

    // ---- the provider --------------------------------------------------------

    [Fact]
    public async Task UsesTrackedFilesOnly_And_Never_Reads_DotGit()
    {
        Write("src/Service.cs", CSharpFixture);
        Write("docs/guide.md", MarkdownFixture);
        Write("untracked-experiment.cs", CSharpFixture);
        Write(".git/config", "[remote \"origin\"]\n\turl = git@example.invalid:someone/nubarca.git\n");
        Write(".env", "POSTGRES_PASSWORD=hunter2\n");
        Write("src/bin/Debug/Generated.cs", CSharpFixture);

        // The lister reports what git tracks. `.git/config` and the untracked
        // experiment are on disk and are not in it — and `.env` and the build
        // output ARE tracked here on purpose, because "tracked" is the first
        // gate and not the last one.
        var provider = new RepositorySnapshotSourceProvider(new FakeLister(
            "src/Service.cs", "docs/guide.md", ".env", "src/bin/Debug/Generated.cs"));

        var keys = await KeysAsync(provider);

        Assert.Equal(new[] { "docs/guide.md", "src/Service.cs" }, keys.Order().ToArray());
        Assert.DoesNotContain("untracked-experiment.cs", keys);
        Assert.DoesNotContain(".git/config", keys);
        Assert.DoesNotContain(".env", keys);
        Assert.DoesNotContain("src/bin/Debug/Generated.cs", keys);
    }

    [Fact]
    public async Task Sources_Carry_Revision_And_Content_Hash()
    {
        Write("docs/guide.md", MarkdownFixture);
        var provider = new RepositorySnapshotSourceProvider(new FakeLister("docs/guide.md"));

        var sources = await ListAsync(provider, revision: "abc123def456");
        var source = Assert.Single(sources);

        Assert.Equal("abc123def456", source.Revision);
        Assert.Equal(64, source.ContentHash.Length);
        Assert.Equal(
            RagHash.Sha256Hex(await File.ReadAllBytesAsync(Path.Combine(_root, "docs/guide.md"))),
            source.ContentHash);
        Assert.Equal(RagCodeLanguages.Markdown, source.CodeLanguage);
        Assert.Equal(RagSourceKinds.Documentation, source.SourceKind);

        // A repository-relative path, never an absolute one: a physical layout
        // is not knowledge and is not something to cite.
        Assert.Equal("docs/guide.md", source.Path);
        Assert.DoesNotContain(_root, source.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Unchanged_File_Hashes_The_Same_And_An_Edited_One_Does_Not()
    {
        Write("docs/guide.md", MarkdownFixture);
        var provider = new RepositorySnapshotSourceProvider(new FakeLister("docs/guide.md"));

        var first = (await ListAsync(provider, "r1")).Single().ContentHash;
        var again = (await ListAsync(provider, "r1")).Single().ContentHash;
        Assert.Equal(first, again);

        Write("docs/guide.md", MarkdownFixture + "\n\nUn paragrafo nuovo che cambia il contenuto.\n");
        Assert.NotEqual(first, (await ListAsync(provider, "r1")).Single().ContentHash);
    }

    [Fact]
    public async Task The_Provider_Reports_Why_It_Skipped_Without_Listing_Every_Path()
    {
        Write("src/Service.cs", CSharpFixture);
        Write(".env", "SECRET=1\n");
        Write("assets/logo.png", "not really a png but the extension decides");
        var provider = new RepositorySnapshotSourceProvider(new FakeLister(
            "src/Service.cs", ".env", "assets/logo.png"));

        await ListAsync(provider, "r");

        Assert.Equal(3, provider.Tally.Tracked);
        Assert.Equal(1, provider.Tally.Included);
        Assert.Equal(2, provider.Tally.Skipped);
        Assert.All(provider.Tally.SkipReasons.Keys, reason =>
            Assert.DoesNotContain('/', reason));
    }

    [Fact]
    public async Task A_Provider_Serves_Exactly_One_Domain()
    {
        var repository = new RepositorySnapshotSourceProvider(new FakeLister());
        Assert.Equal(RagDomains.NubArcaRepository, repository.Domain);
        Assert.Equal(RagDomains.ProductHelp, new ProductHelpSourceProvider().Domain);
        await Task.CompletedTask;
    }

    // ---- fixtures ------------------------------------------------------------

    private const string CSharpFixture = """
        namespace NubArca.Api.Tests.Fixtures;

        /// A service with enough body to clear the minimum length.
        public sealed class Service
        {
            public string Describe() => "a fixture service used by the eligibility tests";
        }
        """;

    private const string MarkdownFixture = """
        # Guida

        Questa guida descrive una funzione di esempio usata dai test di idoneità.

        ## Sezione

        Il corpo della sezione contiene abbastanza testo da superare la soglia minima.
        """;

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task<List<RagSourceDescriptor>> ListAsync(
        IRagSourceProvider provider, string revision = "test-revision")
    {
        var sources = new List<RagSourceDescriptor>();
        await foreach (var source in provider.EnumerateAsync(new RagSourceRequest(_root, revision)))
        {
            sources.Add(source);
        }
        return sources;
    }

    private async Task<List<string>> KeysAsync(IRagSourceProvider provider)
        => (await ListAsync(provider)).Select(s => s.SourceKey).ToList();

    /// Stands in for `git ls-files`. The eligibility rules are what is worth
    /// testing exhaustively; that git reports tracked files is git's problem.
    private sealed class FakeLister(params string[] tracked) : IRepositoryFileLister
    {
        public Task<string> ResolveRootAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(path);

        public Task<IReadOnlyList<string>> ListTrackedAsync(
            string root, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(tracked);

        public Task<string> ResolveRevisionAsync(
            string root, CancellationToken cancellationToken = default)
            => Task.FromResult("fake-revision");
    }
}
