using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Indexing;
using NubArca.Api.Rag.Sources;
using NubArca.Api.Rag.Storage;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// Indexing, and the property the whole design rests on: running it twice does
// nothing the second time.
//
// SQLite in memory, so the vector backend reports itself unavailable and the
// canonical path is what is under test. That is the correct separation — the
// canonical embedding is the truth and pgvector is a derived accelerator, so
// every rule about what is stored and what is dropped has to hold without it.
public sealed class RagIndexerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public RagIndexerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ---- idempotence ---------------------------------------------------------

    [Fact]
    public async Task IndexingSameSnapshotTwice_IsIdempotent()
    {
        var provider = Repository(Source("src/A.cs", BodyA), Source("docs/b.md", BodyB));

        var first = await IndexAsync(provider);
        Assert.Equal(2, first.SourcesCreated);
        Assert.True(first.ChunksCreated > 0);

        var second = await IndexAsync(provider);
        Assert.Equal(0, second.SourcesCreated);
        Assert.Equal(0, second.SourcesUpdated);
        Assert.Equal(2, second.SourcesUnchanged);
        Assert.Equal(0, second.ChunksCreated);
        Assert.Equal(0, second.ChunksUpdated);
        Assert.Equal(0, second.ChunksRemoved);

        Assert.Equal(2, await _db.RagSources.CountAsync());
        Assert.Equal(2, await _db.RagDomainSources.CountAsync());
        Assert.Equal(first.ChunksCreated, await _db.RagChunks.CountAsync());
    }

    [Fact]
    public async Task ChangedSource_ReplacesChangedChunks_And_UnchangedChunks_DoNotReembed()
    {
        var profile = SeedDeterministicProfile();
        var provider = Repository(Source("docs/b.md", BodyB));

        await IndexAsync(provider, embed: true);
        var embeddedBefore = await _db.RagChunkEmbeddings
            .Select(e => new { e.Id, e.ChunkId }).ToListAsync();
        Assert.NotEmpty(embeddedBefore);

        // Edit the LAST section only. Everything before it is byte-identical, so
        // its chunks — and therefore its embeddings — must survive: re-deriving
        // the same vector is the difference between a reindex costing seconds
        // and costing an hour of inference.
        var edited = Repository(Source("docs/b.md", BodyB + "\n\n## Nuova sezione\n\n"
            + "Un paragrafo aggiunto in fondo al documento, abbastanza lungo da diventare un chunk.\n"));

        var outcome = await IndexAsync(edited, embed: true);
        Assert.Equal(1, outcome.SourcesUpdated);
        Assert.True(outcome.ChunksUnchanged > 0, "unchanged sections must keep their chunks");

        var survivors = await _db.RagChunkEmbeddings.Select(e => e.Id).ToListAsync();
        Assert.Contains(embeddedBefore[0].Id, survivors);
        Assert.All(await _db.RagChunkEmbeddings.ToListAsync(),
            e => Assert.Equal(profile.Id, e.ProfileId));
    }

    [Fact]
    public async Task ChangedChunk_LosesItsStaleEmbedding()
    {
        SeedDeterministicProfile();
        await IndexAsync(Repository(Source("docs/b.md", BodyB)), embed: true);
        var before = await _db.RagChunkEmbeddings.CountAsync();
        Assert.True(before > 0);

        // The FIRST section changes, so ordinal 1's text hash changes. Its old
        // vector describes text that no longer exists anywhere.
        var rewritten = BodyB.Replace(
            "Questa guida descrive", "Questa guida riscritta descrive", StringComparison.Ordinal);
        var outcome = await IndexAsync(Repository(Source("docs/b.md", rewritten)), embed: false);

        Assert.True(outcome.ChunksUpdated > 0);
        Assert.True(outcome.EmbeddingsRemoved > 0);
        Assert.True(await _db.RagChunkEmbeddings.CountAsync() < before);
    }

    [Fact]
    public async Task DeletedSource_RemovesStaleDomainMembership_And_Chunks()
    {
        await IndexAsync(Repository(Source("src/A.cs", BodyA), Source("docs/b.md", BodyB)));
        Assert.Equal(2, await _db.RagSources.CountAsync());

        var outcome = await IndexAsync(Repository(Source("src/A.cs", BodyA)));

        Assert.Equal(1, outcome.SourcesRemoved);
        Assert.Equal(1, await _db.RagSources.CountAsync());
        Assert.Equal(1, await _db.RagDomainSources.CountAsync());
        // An index that only ever grew would keep answering from a file somebody
        // deleted three releases ago.
        Assert.Empty(await _db.RagChunks
            .Where(c => !_db.RagSources.Any(s => s.Id == c.SourceId)).ToListAsync());
    }

    // ---- two domains, one source --------------------------------------------

    [Fact]
    public async Task SameSourceInTwoDomains_DoesNotDuplicateChunksOrEmbeddings()
    {
        SeedDeterministicProfile();
        var path = "docs/help/faces.md";

        await IndexAsync(Repository(Source(path, BodyB)), embed: true);
        var chunks = await _db.RagChunks.CountAsync();
        var embeddings = await _db.RagChunkEmbeddings.CountAsync();

        await IndexAsync(Help(Source(path, BodyB, feature: "faces")), embed: true);

        // ONE source row, ONE set of chunks, ONE embedding per chunk — and two
        // memberships. That is the whole reason membership is its own table.
        Assert.Equal(1, await _db.RagSources.CountAsync());
        Assert.Equal(chunks, await _db.RagChunks.CountAsync());
        Assert.Equal(embeddings, await _db.RagChunkEmbeddings.CountAsync());
        Assert.Equal(2, await _db.RagDomainSources.CountAsync());
        Assert.Equal(
            new[] { RagDomains.NubArcaRepository, RagDomains.ProductHelp },
            await _db.RagDomainSources.Select(m => m.DomainKey).OrderBy(k => k).ToListAsync());
    }

    [Fact]
    public async Task Removing_A_Source_From_One_Domain_Leaves_It_In_The_Other()
    {
        var path = "docs/help/faces.md";
        await IndexAsync(Repository(Source(path, BodyB)));
        await IndexAsync(Help(Source(path, BodyB, feature: "faces")));

        // The repository snapshot no longer contains it; Product Help still
        // classifies it.
        await IndexAsync(Repository());

        Assert.Equal(1, await _db.RagSources.CountAsync());
        var membership = Assert.Single(await _db.RagDomainSources.ToListAsync());
        Assert.Equal(RagDomains.ProductHelp, membership.DomainKey);
    }

    // ---- profiles and validation ---------------------------------------------

    [Fact]
    public async Task EmbeddingProfileChange_CreatesDistinctEmbeddings()
    {
        var first = SeedDeterministicProfile("rag-text-a");
        await IndexAsync(Repository(Source("docs/b.md", BodyB)), embed: true, profileKey: first.Key);
        var afterFirst = await _db.RagChunkEmbeddings.CountAsync();

        var second = SeedDeterministicProfile("rag-text-b");
        await IndexAsync(Repository(Source("docs/b.md", BodyB)), embed: true, profileKey: second.Key);

        // A second profile is a second coordinate system, so it gets its own
        // rows rather than reinterpreting the first one's bytes.
        Assert.Equal(afterFirst * 2, await _db.RagChunkEmbeddings.CountAsync());
        Assert.Equal(afterFirst, await _db.RagChunkEmbeddings.CountAsync(e => e.ProfileId == first.Id));
        Assert.Equal(afterFirst, await _db.RagChunkEmbeddings.CountAsync(e => e.ProfileId == second.Id));
    }

    [Fact]
    public async Task Embeddings_Are_Canonical_Bytes_Of_The_Declared_Dimension()
    {
        var profile = SeedDeterministicProfile();
        await IndexAsync(Repository(Source("docs/b.md", BodyB)), embed: true);

        var serializer = new AiVectorSerializer();
        Assert.All(await _db.RagChunkEmbeddings.ToListAsync(), e =>
        {
            Assert.Equal(profile.Dimension, e.Dimension);
            // Byte length validated against the dimension, not trusted from it.
            Assert.Equal(e.Dimension * 4, e.EmbeddingBytes.Length);
            var vector = serializer.Deserialize(e.EmbeddingBytes, e.Dimension);
            Assert.All(vector, v => Assert.True(float.IsFinite(v)));
            Assert.NotEqual(0.0, vector.Sum(v => Math.Abs(v)), 6);
        });
    }

    [Fact]
    public async Task Without_A_Profile_Indexing_Still_Stores_Text_And_Says_Why_It_Embedded_Nothing()
    {
        // Semantic being unavailable is a supported configuration, not an error:
        // the lexical index is complete and the reason is reported.
        var outcome = await IndexAsync(Repository(Source("docs/b.md", BodyB)), embed: true);

        Assert.True(outcome.ChunksCreated > 0);
        Assert.Equal(0, outcome.EmbeddingsCreated);
        Assert.Equal(RagFailureReasons.EmbeddingProfileUnavailable, outcome.EmbeddingReason);
    }

    [Fact]
    public async Task UnknownDomain_FailsClosedBeforeReadingAnything()
    {
        var provider = Repository(Source("src/A.cs", BodyA));
        var indexer = Build(provider);

        await Assert.ThrowsAsync<RagDomainUnknownException>(() => indexer.IndexAsync(
            new RagIndexRequest("private-library", "/tmp", "r")));

        Assert.Empty(await _db.RagSources.ToListAsync());
    }

    [Fact]
    public async Task A_Revisionless_Index_Is_Refused()
    {
        // An index that cannot say which snapshot it describes cannot be checked
        // against anything.
        var indexer = Build(Repository(Source("src/A.cs", BodyA)));

        await Assert.ThrowsAsync<ArgumentException>(() => indexer.IndexAsync(
            new RagIndexRequest(RagDomains.NubArcaRepository, "/tmp", "")));
    }

    [Fact]
    public async Task Sources_Record_The_Revision_They_Were_Indexed_From()
    {
        await IndexAsync(Repository(Source("src/A.cs", BodyA)), revision: "aaa111");
        Assert.Equal("aaa111", (await _db.RagSources.SingleAsync()).Revision);

        // Same content, new checkout: the revision moves and the chunks do not.
        var chunks = await _db.RagChunks.Select(c => c.Id).OrderBy(i => i).ToListAsync();
        await IndexAsync(Repository(Source("src/A.cs", BodyA)), revision: "bbb222");

        Assert.Equal("bbb222", (await _db.RagSources.SingleAsync()).Revision);
        Assert.Equal(chunks, await _db.RagChunks.Select(c => c.Id).OrderBy(i => i).ToListAsync());
    }

    // ---- harness -------------------------------------------------------------

    private const string BodyA = """
        namespace NubArca.Api.Tests.Fixtures;

        /// A source file with a declaration and enough body to become a chunk of
        /// its own rather than a fragment folded into whatever preceded it.
        public sealed class ExampleService
        {
            public string Describe() => "an example service used by the indexer tests";
        }
        """;

    private const string BodyB = """
        # Volti

        Questa guida descrive come usare la funzione dei volti in NubArca, dalla
        prima scansione fino all'assegnazione dei nomi alle persone riconosciute.

        ## Gruppi suggeriti

        Apri un gruppo suggerito e scegli Assegna nome per dargli un'identità, oppure
        Aggiungi a persona esistente per unirlo a una persona già presente in libreria.
        """;

    private static RagSourceDescriptor Source(string key, string text, string? feature = null)
        => new(
            SourceKey: key,
            Path: key,
            Title: key[(key.LastIndexOf('/') + 1)..],
            SourceKind: key.EndsWith(".md", StringComparison.Ordinal)
                ? RagSourceKinds.Documentation
                : RagSourceKinds.SourceCode,
            Revision: "placeholder",
            ContentHash: RagHash.Sha256Hex(text),
            Language: RagLanguages.Italian,
            CodeLanguage: key.EndsWith(".md", StringComparison.Ordinal)
                ? RagCodeLanguages.Markdown
                : RagCodeLanguages.CSharp,
            Text: text,
            Priority: 60,
            DomainMetadata: feature is null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [RagMetadataKeys.Feature] = feature,
                    [RagMetadataKeys.Intent] = RagIntents.HowTo,
                    [RagMetadataKeys.SourceKind] = RagSourceKinds.UserGuide,
                });

    private static FakeSourceProvider Repository(params RagSourceDescriptor[] sources)
        => new(RagDomains.NubArcaRepository, sources);

    private static FakeSourceProvider Help(params RagSourceDescriptor[] sources)
        => new(RagDomains.ProductHelp, sources);

    private AiProfile SeedDeterministicProfile(string key = "rag-text-deterministic-v1")
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = key + "-model",
            Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.TextEmbedding,
            Modality = AiModalities.Text,
            Dimension = DeterministicTextEmbeddingProvider.Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = key,
            AiModelId = model.Id,
            Capability = AiCapabilities.TextEmbedding,
            Modality = AiModalities.Text,
            Dimension = DeterministicTextEmbeddingProvider.Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(profile);
        _db.SaveChanges();
        return profile;
    }

    private Task<RagIndexOutcome> IndexAsync(
        FakeSourceProvider provider,
        bool embed = false,
        string revision = "test-revision",
        string? profileKey = "rag-text-deterministic-v1")
        => Build(provider, profileKey).IndexAsync(
            new RagIndexRequest(provider.Domain, "/fixture", revision, EmbedPassages: embed));

    private RagIndexer Build(FakeSourceProvider provider, string? profileKey = "rag-text-deterministic-v1")
    {
        var options = Options.Create(new RagOptions
        {
            SemanticEnabled = true,
            TextEmbeddingProfileKey = profileKey,
        });
        var serializer = new AiVectorSerializer();
        return new RagIndexer(
            _db,
            RagDomainRegistry.Instance,
            new[] { provider },
            new TextEmbeddingResolver(
                _db, new ITextEmbeddingProvider[] { new DeterministicTextEmbeddingProvider() }, options),
            serializer,
            new RagVectorIndexService(_db, serializer, TimeProvider.System),
            options,
            TimeProvider.System,
            NullLogger<RagIndexer>.Instance);
    }

    private sealed class FakeSourceProvider(string domain, IReadOnlyList<RagSourceDescriptor> sources)
        : IRagSourceProvider
    {
        public string Domain { get; } = domain;

        public async IAsyncEnumerable<RagSourceDescriptor> EnumerateAsync(
            RagSourceRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The provider stamps the revision the run asked for, exactly as
                // the real ones do.
                yield return source with { Revision = request.Revision };
                await Task.Yield();
            }
        }
    }
}
