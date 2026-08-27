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
    public void The_Evaluation_Set_Is_Not_Part_Of_The_Corpus_It_Measures()
    {
        // `RagGoldenSet.cs` holds the golden queries as string literals. Once
        // the repository indexed itself, the best lexical match for a golden
        // question became the file containing that exact sentence — it led
        // three of four failures in the first real evaluation run and dropped
        // MRR from 0.583 to 0.395. A benchmark that searches its own question
        // list measures nothing.
        Assert.False(RepositorySourcePolicy.CheckPath(
            "src/NubArca.Api/Rag/Evaluation/RagGoldenSet.cs").IsEligible);
        Assert.Equal(
            "evaluation-set",
            RepositorySourcePolicy.CheckPath(
                "src/NubArca.Api/Rag/Evaluation/RagEvaluator.cs").Reason);

        // The rest of the RAG substrate stays indexable — it is exactly the
        // knowledge this domain exists to hold.
        Assert.True(RepositorySourcePolicy.CheckPath(
            "src/NubArca.Api/Rag/Retrieval/RagRetriever.cs").IsEligible);
        Assert.True(RepositorySourcePolicy.CheckPath(
            "src/NubArca.Api/Rag/Domains/RagDomainRegistry.cs").IsEligible);
    }

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

    [Fact]
    public void SizeIsRefusableWithoutTheBytes()
    {
        // The same verdict as CheckContent, reached from a number instead of
        // from an allocation. That is the entire difference between a bound and
        // a report.
        Assert.False(RepositorySourcePolicy.CheckSize(RepositorySourcePolicy.MaximumBytes + 1).IsEligible);
        Assert.Equal(
            "too-large",
            RepositorySourcePolicy.CheckSize(RepositorySourcePolicy.MaximumBytes + 1).Reason);
        Assert.True(RepositorySourcePolicy.CheckSize(RepositorySourcePolicy.MaximumBytes).IsEligible);
        Assert.True(RepositorySourcePolicy.CheckSize(0).IsEligible);

        // A size git did not report is not a small size. It falls through to the
        // content check rather than being refused or waved past.
        Assert.True(RepositorySourcePolicy.CheckSize(RepositorySnapshotEntry.UnknownSize).IsEligible);
    }

    // ---- the provider --------------------------------------------------------

    [Fact]
    public async Task OversizedTrackedBlob_IsRejectedBeforeContentRead()
    {
        // A tracked blob whose SIZE disqualifies it must never be fetched. The
        // predecessor learned the size by reading the object, so `too-large` was
        // a verdict delivered after allocating the thing it was refusing — and a
        // tracked multi-gigabyte file was an OutOfMemoryException in a service.
        var reader = new FakeSnapshotReader()
            .WithContent("docs/guide.md", MarkdownFixture)
            .WithDeclaredSize(
                "docs/enormous.md", MarkdownFixture, RepositorySourcePolicy.MaximumBytes + 1);

        var keys = await KeysAsync(new RepositorySnapshotSourceProvider(reader));

        Assert.DoesNotContain("docs/enormous.md", keys);
        Assert.Contains("docs/guide.md", keys);
        // Not merely absent from the output — never opened.
        var opened = Assert.Single(reader.Opened);
        Assert.DoesNotContain("docs/enormous.md", opened.Read);
        Assert.Contains("docs/guide.md", opened.Read);
    }

    [Fact]
    public async Task NormalBlob_UnderLimit_StillReads()
    {
        // The control. A size gate that refused everything would pass the test
        // above and delete the corpus.
        var reader = new FakeSnapshotReader()
            .WithDeclaredSize("docs/guide.md", MarkdownFixture, RepositorySourcePolicy.MaximumBytes);

        var keys = await KeysAsync(new RepositorySnapshotSourceProvider(reader));

        Assert.Contains("docs/guide.md", keys);
        Assert.Contains("docs/guide.md", Assert.Single(reader.Opened).Read);
    }

    [Fact]
    public async Task OversizedSymlink_RemainsUnreadForTheOlderReason()
    {
        // Two independent refusals must not become one. A link is refused by
        // MODE whatever its size says, so a future change to the size rule
        // cannot quietly start dereferencing links.
        var reader = new FakeSnapshotReader()
            .WithContent("docs/escape.md", "/etc/passwd", mode: RepositorySnapshotEntry.SymbolicLinkMode);

        var keys = await KeysAsync(new RepositorySnapshotSourceProvider(reader));

        Assert.DoesNotContain("docs/escape.md", keys);
        Assert.Empty(Assert.Single(reader.Opened).Read);
    }

    [Fact]
    public async Task UsesTrackedFilesOnly_And_Never_Reads_DotGit()
    {
        // The snapshot is the COMMIT's tree. `.git/config` and an untracked
        // experiment are not in it — and `.env` and the build output ARE, on
        // purpose, because tracked is the first gate and not the last one.
        var provider = new RepositorySnapshotSourceProvider(new FakeSnapshotReader()
            .WithContent("src/Service.cs", CSharpFixture)
            .WithContent("docs/guide.md", MarkdownFixture)
            .WithContent(".env", "POSTGRES_PASSWORD=hunter2\n")
            .WithContent("src/bin/Debug/Generated.cs", CSharpFixture));

        var keys = await KeysAsync(provider);

        Assert.Equal(new[] { "docs/guide.md", "src/Service.cs" }, keys.Order().ToArray());
        Assert.DoesNotContain(".git/config", keys);
        Assert.DoesNotContain(".env", keys);
        Assert.DoesNotContain("src/bin/Debug/Generated.cs", keys);
    }

    [Fact]
    public async Task TrackedSymlink_IsRejected_And_Its_Target_Is_Never_Read()
    {
        // A symlink's blob is its TARGET PATH, so following one imports whatever
        // that path names — including a file outside the checkout entirely. It
        // is refused by MODE, and the target is never resolved to decide whether
        // it happens to be safe.
        var provider = new RepositorySnapshotSourceProvider(new FakeSnapshotReader()
            .WithContent("docs/guide.md", MarkdownFixture)
            .WithContent("docs/escape.md", "/etc/shadow", mode: "120000")
            .WithContent("vendored", "abc123", mode: "160000"));

        var keys = await KeysAsync(provider);

        Assert.Equal(new[] { "docs/guide.md" }, keys.ToArray());
        Assert.DoesNotContain("docs/escape.md", keys);
        Assert.DoesNotContain("vendored", keys);
        Assert.Equal(1, provider.Tally.SkipReasons.GetValueOrDefault("symlink"));
        Assert.Equal(1, provider.Tally.SkipReasons.GetValueOrDefault("submodule"));
    }

    [Fact]
    public void The_Policy_Refuses_Link_Modes_Independently_Of_The_Reader()
    {
        // Stated as policy so a future implementation that went back to
        // filesystem reads would still have to walk past an explicit refusal.
        Assert.False(RepositorySourcePolicy.CheckGitMode("120000").IsEligible);
        Assert.False(RepositorySourcePolicy.CheckGitMode("160000").IsEligible);
        Assert.False(RepositorySourcePolicy.CheckGitMode(null).IsEligible);
        Assert.True(RepositorySourcePolicy.CheckGitMode("100644").IsEligible);
        Assert.True(RepositorySourcePolicy.CheckGitMode("100755").IsEligible);
    }

    [Fact]
    public async Task Sources_Carry_Revision_And_Content_Hash()
    {
        var provider = new RepositorySnapshotSourceProvider(
            new FakeSnapshotReader().WithContent("docs/guide.md", MarkdownFixture));

        var sources = await ListAsync(provider, revision: "abc123def456");
        var source = Assert.Single(sources);

        Assert.Equal("abc123def456", source.Revision);
        Assert.Equal(64, source.ContentHash.Length);
        Assert.Equal(
            RagHash.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(MarkdownFixture)),
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
        var reader = new FakeSnapshotReader().WithContent("docs/guide.md", MarkdownFixture);
        var provider = new RepositorySnapshotSourceProvider(reader);

        var first = (await ListAsync(provider, "r1")).Single().ContentHash;
        var again = (await ListAsync(provider, "r1")).Single().ContentHash;
        Assert.Equal(first, again);

        reader.WithContent(
            "docs/guide.md", MarkdownFixture + "\n\nUn paragrafo nuovo che cambia il contenuto.\n");
        Assert.NotEqual(first, (await ListAsync(provider, "r1")).Single().ContentHash);
    }

    [Fact]
    public async Task The_Provider_Reports_Why_It_Skipped_Without_Listing_Every_Path()
    {
        var provider = new RepositorySnapshotSourceProvider(new FakeSnapshotReader()
            .WithContent("src/Service.cs", CSharpFixture)
            .WithContent(".env", "SECRET=1\n")
            .WithContent("assets/logo.png", "not really a png but the extension decides"));

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
        var reader = new FakeSnapshotReader();
        var repository = new RepositorySnapshotSourceProvider(reader);
        Assert.Equal(RagDomains.NubArcaRepository, repository.Domain);
        Assert.Equal(RagDomains.ProductHelp, new ProductHelpSourceProvider(reader).Domain);
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

    /// Stands in for a real Git checkout. The eligibility rules are what is
    /// worth testing exhaustively; that git can read its own object store is
    /// git's problem, and the real reader has its own integration test.
    private sealed class FakeSnapshotReader : IRepositorySnapshotReader
    {
        private readonly Dictionary<string, (string Mode, byte[] Content, long DeclaredSize)> _entries = new(StringComparer.Ordinal);

        public FakeSnapshotReader(params string[] paths)
        {
            foreach (var path in paths) _entries[path] = ("100644", Array.Empty<byte>(), 0);
        }

        public FakeSnapshotReader WithContent(string path, string content, string mode = "100644")
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            _entries[path] = (mode, bytes, bytes.LongLength);
            return this;
        }

        /// A tree entry that CLAIMS a size without the fixture allocating one.
        /// Reading it hands back the small body, so a test that gets content
        /// back has proven the size gate did not fire — which is the whole
        /// point, and is not something a real 4 GB blob could demonstrate.
        public FakeSnapshotReader WithDeclaredSize(string path, string content, long declaredSize)
        {
            _entries[path] = ("100644", System.Text.Encoding.UTF8.GetBytes(content), declaredSize);
            return this;
        }

        /// The snapshots this reader handed out, so a test can ask which entries
        /// were actually opened.
        public List<FakeSnapshot> Opened { get; } = new();

        public Task<string> ResolveRootAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(path);

        public Task<string> ResolveRevisionAsync(
            string root, string? revision = null, CancellationToken cancellationToken = default)
            => Task.FromResult(revision ?? "fake-revision");

        public Task<IRepositorySnapshot> OpenAsync(
            string root, string revision, CancellationToken cancellationToken = default)
        {
            var snapshot = new FakeSnapshot(root, revision, _entries);
            Opened.Add(snapshot);
            return Task.FromResult<IRepositorySnapshot>(snapshot);
        }
    }

    internal sealed class FakeSnapshot : IRepositorySnapshot
    {
        private readonly Dictionary<string, (string Mode, byte[] Content, long DeclaredSize)> _entries;

        public FakeSnapshot(
            string root, string revision, Dictionary<string, (string Mode, byte[] Content, long DeclaredSize)> entries)
        {
            Root = root;
            Revision = revision;
            _entries = entries;
            Entries = entries
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new RepositorySnapshotEntry(
                    e.Key, e.Value.Mode, $"oid-{e.Key}", e.Value.DeclaredSize))
                .ToList();
        }

        public string Root { get; }
        public string Revision { get; }
        public IReadOnlyList<RepositorySnapshotEntry> Entries { get; }

        /// Every path that reached this method is one the provider decided to
        /// read. Recording them is how "it was never opened" is asserted as a
        /// fact rather than inferred from the absence of a source.
        public List<string> Read { get; } = new();

        public Task<byte[]> ReadAsync(
            RepositorySnapshotEntry entry, CancellationToken cancellationToken = default)
        {
            // Mirrors the real snapshot: a link's bytes are never handed out.
            if (entry.IsSymbolicLink || entry.IsSubmodule)
            {
                throw new InvalidOperationException("Refusing to read a link entry as content.");
            }
            Read.Add(entry.Path);
            return Task.FromResult(_entries[entry.Path].Content);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
