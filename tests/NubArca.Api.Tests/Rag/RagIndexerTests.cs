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
using NubArca.Api.Rag.Chunking;
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

    // ---- partial runs never reconcile ---------------------------------------

    [Fact]
    public async Task LimitedIndexRun_DoesNotRemoveUnseenSources()
    {
        // The bug: `rag index --limit 10` against a complete index saw ten
        // sources and concluded that every other one had left the snapshot, so
        // a command meant to do LESS work deleted most of the index.
        var full = Repository(
            Source("src/A.cs", BodyA), Source("docs/b.md", BodyB), Source("docs/c.md", BodyC));
        await IndexAsync(full);
        Assert.Equal(3, await _db.RagSources.CountAsync());

        var outcome = await IndexAsync(full, limit: 1);

        Assert.True(outcome.Partial);
        Assert.False(outcome.ReconciliationPerformed);
        Assert.Equal(0, outcome.SourcesRemoved);
        Assert.Equal(3, await _db.RagSources.CountAsync());
        Assert.Equal(3, await _db.RagDomainSources.CountAsync());
    }

    [Fact]
    public async Task ZeroLimit_DoesNotRemoveExistingSources()
    {
        // The worst case of the same bug: a run that enumerated NOTHING would
        // have concluded that the entire domain was gone.
        var full = Repository(Source("src/A.cs", BodyA), Source("docs/b.md", BodyB));
        await IndexAsync(full);

        var outcome = await IndexAsync(full, limit: 0);

        Assert.Equal(0, outcome.SourcesSeen);
        Assert.True(outcome.Partial);
        Assert.Equal(0, outcome.SourcesRemoved);
        Assert.Equal(2, await _db.RagSources.CountAsync());
    }

    [Fact]
    public async Task FullIndexRun_StillRemovesActuallyDepartedSources()
    {
        // The capability must survive the fix: a complete run is still allowed
        // to conclude that a source it did not see has left.
        await IndexAsync(Repository(Source("src/A.cs", BodyA), Source("docs/b.md", BodyB)));

        var outcome = await IndexAsync(Repository(Source("src/A.cs", BodyA)));

        Assert.False(outcome.Partial);
        Assert.True(outcome.ReconciliationPerformed);
        Assert.Equal(1, outcome.SourcesRemoved);
        Assert.Equal(1, await _db.RagSources.CountAsync());
    }

    [Fact]
    public async Task PartialRun_FollowedByFullRun_ReconcilesNormally()
    {
        await IndexAsync(Repository(
            Source("src/A.cs", BodyA), Source("docs/b.md", BodyB), Source("docs/c.md", BodyC)));

        await IndexAsync(Repository(Source("src/A.cs", BodyA)), limit: 1);
        Assert.Equal(3, await _db.RagSources.CountAsync());

        // The partial run deferred the decision; it did not cancel it.
        var outcome = await IndexAsync(Repository(Source("src/A.cs", BodyA)));
        Assert.Equal(2, outcome.SourcesRemoved);
        Assert.Equal(1, await _db.RagSources.CountAsync());
    }

    [Fact]
    public async Task DryRun_Reconciles_Nothing_And_Writes_Nothing()
    {
        await IndexAsync(Repository(Source("src/A.cs", BodyA), Source("docs/b.md", BodyB)));

        var outcome = await Build(Repository(Source("src/A.cs", BodyA))).IndexAsync(
            new RagIndexRequest(
                RagDomains.NubArcaRepository, "/fixture", "test-revision", DryRun: true));

        Assert.False(outcome.ReconciliationPerformed);
        Assert.Equal(2, await _db.RagSources.CountAsync());
    }

    [Fact]
    public async Task LimitedRun_EmbedsOnlyTheSourcesItSaw()
    {
        // `--limit` capped enumeration and nothing else, so the embedding pass
        // still walked every chunk in the domain — a command whose entire
        // purpose is a bounded trial run starting an hour of inference over the
        // whole corpus. Found by running it against the real repository.
        SeedDeterministicProfile();
        var full = Repository(
            Source("src/A.cs", BodyA), Source("docs/b.md", BodyB), Source("docs/c.md", BodyC));
        await IndexAsync(full);
        var allChunks = await _db.RagChunks.CountAsync();

        var outcome = await IndexAsync(full, embed: true, limit: 1);

        Assert.True(outcome.Partial);
        Assert.True(outcome.EmbeddingsCreated > 0, "the sources it did see are embedded");
        Assert.True(outcome.EmbeddingsCreated < allChunks,
            "a bounded run must not embed the whole corpus");

        // Precisely: every embedding belongs to a chunk of the one source seen.
        var embeddedSources = await (
            from embedding in _db.RagChunkEmbeddings
            join chunk in _db.RagChunks on embedding.ChunkId equals chunk.Id
            select chunk.SourceId).Distinct().CountAsync();
        Assert.Equal(1, embeddedSources);
    }

    [Fact]
    public async Task A_Full_Run_After_A_Bounded_One_Embeds_The_Rest()
    {
        // Bounded work is resumable work: the embeddings already paid for are
        // kept, and the complete run finishes the remainder.
        SeedDeterministicProfile();
        var full = Repository(
            Source("src/A.cs", BodyA), Source("docs/b.md", BodyB), Source("docs/c.md", BodyC));
        await IndexAsync(full);
        await IndexAsync(full, embed: true, limit: 1);
        var afterBounded = await _db.RagChunkEmbeddings.CountAsync();

        await IndexAsync(full, embed: true);

        Assert.Equal(await _db.RagChunks.CountAsync(), await _db.RagChunkEmbeddings.CountAsync());
        Assert.True(await _db.RagChunkEmbeddings.CountAsync() > afterBounded);
    }

    // ---- shared source snapshot conflict ------------------------------------

    [Fact]
    public async Task SharedSource_SameRevisionSameContent_IsReused()
    {
        const string path = "docs/help/faces.md";
        await IndexAsync(Repository(Source(path, BodyB)), revision: "rev-a");
        var chunks = await _db.RagChunks.CountAsync();

        await IndexAsync(Help(Source(path, BodyB, feature: "faces")), revision: "rev-a");

        Assert.Equal(1, await _db.RagSources.CountAsync());
        Assert.Equal(chunks, await _db.RagChunks.CountAsync());
        Assert.Equal(2, await _db.RagDomainSources.CountAsync());
    }

    [Fact]
    public async Task SharedSource_DifferentRevision_IsRefusedWithoutMutation()
    {
        // One row owns Revision, ContentHash and the chunks. Indexing the other
        // domain at a different commit would rewrite the bytes the first domain
        // is serving — so it is refused rather than resolved.
        const string path = "docs/help/faces.md";
        await IndexAsync(Repository(Source(path, BodyB)), revision: "rev-a");
        var before = await SnapshotAsync();

        var conflict = await Assert.ThrowsAsync<RagSharedSourceConflictException>(
            () => IndexAsync(Help(Source(path, BodyB, feature: "faces")), revision: "rev-b"));

        Assert.Equal(path, conflict.SourceKey);
        Assert.Equal(RagDomains.ProductHelp, conflict.DomainKey);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task SharedSource_DifferentContent_IsRefusedWithoutMutation()
    {
        const string path = "docs/help/faces.md";
        await IndexAsync(Repository(Source(path, BodyB)), revision: "rev-a");
        var before = await SnapshotAsync();

        await Assert.ThrowsAsync<RagSharedSourceConflictException>(
            () => IndexAsync(
                Help(Source(path, BodyB + "\n\nUn paragrafo diverso.", feature: "faces")),
                revision: "rev-a"));

        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task SharedSourceConflict_DoesNotDropExistingMembershipsOrEmbeddings()
    {
        SeedDeterministicProfile();
        const string path = "docs/help/faces.md";
        await IndexAsync(Repository(Source(path, BodyB)), embed: true, revision: "rev-a");
        var memberships = await _db.RagDomainSources.CountAsync();
        var embeddings = await _db.RagChunkEmbeddings.CountAsync();
        Assert.True(embeddings > 0);

        await Assert.ThrowsAsync<RagSharedSourceConflictException>(
            () => IndexAsync(Help(Source(path, BodyB, feature: "faces")), revision: "rev-b"));

        Assert.Equal(memberships, await _db.RagDomainSources.CountAsync());
        Assert.Equal(embeddings, await _db.RagChunkEmbeddings.CountAsync());
    }

    [Fact]
    public async Task A_Source_Only_This_Domain_Owns_May_Change_Revision_Freely()
    {
        // The conflict is about SHARING, not about immutability. A source no
        // other domain claims follows its snapshot forward normally.
        await IndexAsync(Repository(Source("src/A.cs", BodyA)), revision: "rev-a");
        await IndexAsync(Repository(Source("src/A.cs", BodyA + "\n// edited")), revision: "rev-b");

        var source = await _db.RagSources.SingleAsync();
        Assert.Equal("rev-b", source.Revision);
    }

    // ---- chunk interpretation version ---------------------------------------

    [Fact]
    public async Task SameContentNewIndexVersion_Rechunks()
    {
        // Chunks that an OLDER interpretation would have produced, over bytes
        // that never changed. Content hashing alone keeps them forever, so
        // improving a chunker would reach new files only and the corpus would
        // quietly hold two interpretations at once.
        await IndexAsync(Repository(Source("docs/b.md", BodyB)));
        await StaleChunksFromAnOlderInterpretationAsync(RagIndexFormat.Current - 1);

        var outcome = await IndexAsync(Repository(Source("docs/b.md", BodyB)));

        Assert.True(outcome.ChunksUpdated > 0,
            "a source chunked by an older interpretation must be rechunked");
        Assert.DoesNotContain(
            await _db.RagChunks.Select(c => c.Text).ToListAsync(),
            t => t.Contains("OLD_INTERPRETATION", StringComparison.Ordinal));
        Assert.Equal(RagIndexFormat.Current, (await _db.RagSources.SingleAsync()).IndexFormatVersion);
    }

    [Fact]
    public async Task SameContentSameIndexVersion_DoesNotRechunk()
    {
        // The control for the test above: identical doctored chunks, but the
        // CURRENT version. Nothing is rewritten, which is what proves the
        // version — and not the doctoring — is what triggers a rechunk.
        await IndexAsync(Repository(Source("docs/b.md", BodyB)));
        await StaleChunksFromAnOlderInterpretationAsync(RagIndexFormat.Current);

        var outcome = await IndexAsync(Repository(Source("docs/b.md", BodyB)));

        Assert.Equal(0, outcome.ChunksUpdated);
        Assert.Contains(
            await _db.RagChunks.Select(c => c.Text).ToListAsync(),
            t => t.Contains("OLD_INTERPRETATION", StringComparison.Ordinal));
    }

    /// Rewrites the stored chunks as though a different chunker had produced
    /// them, and stamps the source with `version`.
    private async Task StaleChunksFromAnOlderInterpretationAsync(int version)
    {
        foreach (var chunk in await _db.RagChunks.ToListAsync())
        {
            chunk.Text = $"OLD_INTERPRETATION {chunk.Ordinal}";
            chunk.TextHash = RagHash.Sha256Hex(chunk.Text);
        }
        (await _db.RagSources.SingleAsync()).IndexFormatVersion = version;
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task SameContentSameIndexVersion_ReusesChunks()
    {
        await IndexAsync(Repository(Source("docs/b.md", BodyB)));
        var outcome = await IndexAsync(Repository(Source("docs/b.md", BodyB)));

        Assert.Equal(0, outcome.ChunksCreated);
        Assert.Equal(0, outcome.ChunksUpdated);
        Assert.True(outcome.ChunksUnchanged > 0);
    }

    [Fact]
    public async Task IndexVersionRechunk_DropsEmbeddingsForChangedChunks()
    {
        SeedDeterministicProfile();
        await IndexAsync(Repository(Source("docs/b.md", BodyB)), embed: true);
        Assert.True(await _db.RagChunkEmbeddings.CountAsync() > 0);

        // Chunk text must actually differ for an embedding to be stale: a
        // rechunk producing byte-identical chunks correctly keeps its vectors.
        await StaleChunksFromAnOlderInterpretationAsync(RagIndexFormat.Current - 1);

        var outcome = await IndexAsync(Repository(Source("docs/b.md", BodyB)), embed: false);
        Assert.True(outcome.EmbeddingsRemoved > 0);
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

    // ---- embedding failure is resumable, not fatal ---------------------------

    [Fact]
    public async Task Indexer_EmbeddingTimeout_ReturnsOutcomeInsteadOfCrashing()
    {
        // A slow model must not abort an index run with a native exception. The
        // text is already written and useful — lexical retrieval works on it —
        // so the run reports WHY it stopped embedding and stays resumable.
        var profile = SeedDeterministicProfile();
        var indexer = Build(
            Repository(Source("docs/b.md", BodyB)),
            embedding: new ThrowingEmbeddingProvider(RagFailureReasons.EmbeddingTimeout));

        var outcome = await indexer.IndexAsync(new RagIndexRequest(
            RagDomains.NubArcaRepository, "/fixture", "test-revision", EmbedPassages: true));

        Assert.True(outcome.ChunksCreated > 0, "the text is indexed even when embedding stops");
        Assert.Equal(0, outcome.EmbeddingsCreated);
        Assert.Equal(RagFailureReasons.EmbeddingTimeout, outcome.EmbeddingReason);
        Assert.Equal(profile.Key, outcome.EmbeddingProfileKey);
        Assert.True(await _db.RagChunks.AnyAsync());
    }

    [Fact]
    public async Task Indexer_KeepsEmbeddingsWrittenBeforeATimeout()
    {
        // Resumable means the work already paid for is kept: re-running the
        // index continues from where it stopped rather than starting over.
        SeedDeterministicProfile();
        var indexer = Build(
            Repository(Source("docs/b.md", BodyB)),
            embedding: new ThrowingEmbeddingProvider(
                RagFailureReasons.EmbeddingTimeout, succeedFirst: 1));

        var outcome = await indexer.IndexAsync(new RagIndexRequest(
            RagDomains.NubArcaRepository, "/fixture", "test-revision", EmbedPassages: true));

        Assert.Equal(1, outcome.EmbeddingsCreated);
        Assert.Equal(RagFailureReasons.EmbeddingTimeout, outcome.EmbeddingReason);
        Assert.Equal(1, await _db.RagChunkEmbeddings.CountAsync());
    }

    /// Fails after a configured number of successes, with a sanitized reason —
    /// the shape a real provider produces on a model timeout.
    private sealed class ThrowingEmbeddingProvider(string reason, int succeedFirst = 0)
        : ITextEmbeddingProvider
    {
        private int _served;

        public string Provider => AiProviders.Deterministic;

        public TextEmbeddingReadiness CheckReadiness(AiProfile profile)
            => TextEmbeddingReadiness.Ready;

        public Task<TextEmbeddingResult> EmbedAsync(
            AiProfile profile, string text, TextEmbeddingInputKind inputKind,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _served) > succeedFirst)
            {
                throw new TextEmbeddingUnavailableException(reason);
            }
            return new DeterministicTextEmbeddingProvider()
                .EmbedAsync(profile, text, inputKind, cancellationToken);
        }
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

    private const string BodyC = """
        # Album

        Questa guida descrive come raccogliere le foto in un album condiviso,
        abbastanza lunga da produrre almeno un chunk indicizzabile.
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
        string? profileKey = "rag-text-deterministic-v1",
        int? limit = null)
        => Build(provider, profileKey).IndexAsync(
            new RagIndexRequest(
                provider.Domain, "/fixture", revision, EmbedPassages: embed, Limit: limit));

    /// Everything an index run could have mutated, as one comparable value —
    /// so a refusal can be asserted to have changed NOTHING rather than to have
    /// changed nothing anybody thought to check.
    private async Task<string> SnapshotAsync()
    {
        var sources = await _db.RagSources.AsNoTracking()
            .OrderBy(s => s.SourceKey)
            .Select(s => $"{s.SourceKey}|{s.Revision}|{s.ContentHash}|{s.IndexFormatVersion}")
            .ToListAsync();
        var memberships = await _db.RagDomainSources.AsNoTracking()
            .OrderBy(m => m.DomainKey).ThenBy(m => m.SourceId)
            .Select(m => $"{m.DomainKey}|{m.Priority}|{m.MetadataJson}")
            .ToListAsync();
        var chunks = await _db.RagChunks.AsNoTracking()
            .OrderBy(c => c.SourceId).ThenBy(c => c.Ordinal)
            .Select(c => $"{c.Ordinal}|{c.TextHash}")
            .ToListAsync();
        return string.Join("\n", sources.Concat(memberships).Concat(chunks));
    }

    private RagIndexer Build(
        FakeSourceProvider provider,
        string? profileKey = "rag-text-deterministic-v1",
        ITextEmbeddingProvider? embedding = null)
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
                _db, new[] { embedding ?? new DeterministicTextEmbeddingProvider() }, options),
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
