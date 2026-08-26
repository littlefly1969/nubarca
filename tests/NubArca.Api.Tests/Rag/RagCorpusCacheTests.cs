using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain.Rag;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Retrieval;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// The corpus signature, which decides when a cached lexical index is stale.
//
// The defect this file exists for was invisible to every behavioural test: all
// the ranking metadata a retrieval reads — Priority, Feature, Aliases, Intent,
// Audience, SourceKind, Language — lives on the MEMBERSHIP row, not on the
// source. Reclassifying a document therefore left every source timestamp
// untouched, the signature unchanged, and a running web host serving an index
// built from the old classification until somebody restarted it. The CLI clears
// its own cache after indexing, which is exactly why nobody noticed: the web
// host never runs that code.
public sealed class RagCorpusCacheTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly DatabaseRagCorpusSource _corpus;

    public RagCorpusCacheTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _corpus = new DatabaseRagCorpusSource(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task MembershipPriorityChange_InvalidatesCachedIndex()
    {
        var membership = Seed();
        var before = await SignatureAsync();

        membership.Priority = 20;
        membership.UpdatedAt = DateTime.UtcNow.AddSeconds(1);
        await _db.SaveChangesAsync();

        Assert.NotEqual(before, await SignatureAsync());
    }

    [Fact]
    public async Task MembershipAliasChange_InvalidatesCachedIndex()
    {
        var membership = Seed();
        var before = await SignatureAsync();

        membership.MetadataJson = """{"feature":"faces","aliases":"[\"volti\",\"facce\"]"}""";
        membership.UpdatedAt = DateTime.UtcNow.AddSeconds(1);
        await _db.SaveChangesAsync();

        Assert.NotEqual(before, await SignatureAsync());
    }

    [Fact]
    public async Task MembershipIntentChange_InvalidatesCachedIndex()
    {
        var membership = Seed();
        var before = await SignatureAsync();

        membership.MetadataJson = """{"intent":"reference"}""";
        membership.UpdatedAt = DateTime.UtcNow.AddSeconds(1);
        await _db.SaveChangesAsync();

        Assert.NotEqual(before, await SignatureAsync());
    }

    [Fact]
    public async Task UnchangedMembership_DoesNotRebuildIndex()
    {
        Seed();
        var first = await SignatureAsync();
        var second = await SignatureAsync();

        // Stability matters as much as invalidation: a signature that moved on
        // its own — a clock, a random token — would rebuild the repository index
        // on every question.
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task A_New_Membership_Invalidates_The_Cache()
    {
        Seed();
        var before = await SignatureAsync();

        var second = AddSource("docs/help/albums.md", "rev-a");
        AddMembership(second, RagDomains.ProductHelp);
        await _db.SaveChangesAsync();

        Assert.NotEqual(before, await SignatureAsync());
    }

    [Fact]
    public async Task The_Cache_Rebuilds_Only_When_The_Signature_Moves()
    {
        Seed();
        var cache = new RagLexicalIndexCache();
        var builds = 0;

        Task<RagCorpus> Load(CancellationToken ct)
        {
            builds++;
            return _corpus.LoadAsync(RagDomainKey.ProductHelp, ct);
        }

        var signature = await SignatureAsync();
        await cache.GetOrBuildAsync(RagDomainKey.ProductHelp, signature, Load);
        await cache.GetOrBuildAsync(RagDomainKey.ProductHelp, signature, Load);
        Assert.Equal(1, builds);

        await cache.GetOrBuildAsync(RagDomainKey.ProductHelp, signature + "-moved", Load);
        Assert.Equal(2, builds);
    }

    // ---- mixed revision ------------------------------------------------------

    [Fact]
    public async Task MixedRevisionDomain_IsMarkedAndCarriesNoRevision()
    {
        // Indexing commits incrementally, so an interrupted reindex leaves one
        // domain describing two commits. There is no honest single revision for
        // that corpus — not the newest, not the most common, not the first.
        var a = AddSource("docs/help/faces.md", "rev-a");
        AddMembership(a, RagDomains.ProductHelp);
        AddChunk(a, "il flusso dei volti");
        var b = AddSource("docs/help/albums.md", "rev-b");
        AddMembership(b, RagDomains.ProductHelp);
        AddChunk(b, "gli album condivisi");
        await _db.SaveChangesAsync();

        var corpus = await _corpus.LoadAsync(RagDomainKey.ProductHelp);

        Assert.True(corpus.IsMixedRevision);
        Assert.Equal(string.Empty, corpus.Revision);
    }

    [Fact]
    public async Task SingleRevisionDomain_RemainsAvailable()
    {
        Seed();
        var corpus = await _corpus.LoadAsync(RagDomainKey.ProductHelp);

        Assert.False(corpus.IsMixedRevision);
        Assert.Equal("rev-a", corpus.Revision);
    }

    [Fact]
    public async Task CompletedReindex_ClearsMixedRevisionState()
    {
        var a = AddSource("docs/help/faces.md", "rev-a");
        AddMembership(a, RagDomains.ProductHelp);
        AddChunk(a, "il flusso dei volti");
        var b = AddSource("docs/help/albums.md", "rev-b");
        AddMembership(b, RagDomains.ProductHelp);
        AddChunk(b, "gli album condivisi");
        await _db.SaveChangesAsync();
        Assert.True((await _corpus.LoadAsync(RagDomainKey.ProductHelp)).IsMixedRevision);

        // The reindex converges: every source now names one commit.
        foreach (var source in await _db.RagSources.ToListAsync()) source.Revision = "rev-b";
        await _db.SaveChangesAsync();

        var corpus = await _corpus.LoadAsync(RagDomainKey.ProductHelp);
        Assert.False(corpus.IsMixedRevision);
        Assert.Equal("rev-b", corpus.Revision);
    }

    [Fact]
    public async Task MixedRevisionDomain_ReturnsNoEvidence()
    {
        var a = AddSource("docs/help/faces.md", "rev-a");
        AddMembership(a, RagDomains.ProductHelp);
        AddChunk(a, "Apri Volti e scegli Assegna nome per il gruppo suggerito.");
        var b = AddSource("docs/help/albums.md", "rev-b");
        AddMembership(b, RagDomains.ProductHelp);
        AddChunk(b, "Gli album raccolgono le foto senza spostarle.");
        await _db.SaveChangesAsync();

        var retriever = new RagRetriever(
            RagDomainRegistry.Instance,
            new RagDatabaseServices(_corpus, null!, null!, null!),
            new BundledProductHelpCorpusSource(ProductHelpCorpusStub()),
            new RagLexicalIndexCache(),
            Options.Create(new RagOptions()),
            NullLogger<RagRetriever>.Instance);

        var result = await retriever.RetrieveAsync(
            new RagQuery(RagDomainKey.ProductHelp, "volti gruppi suggeriti assegna nome", 5, 4000));

        Assert.Equal(RagRetrievalOutcome.Unavailable, result.Outcome);
        Assert.Equal(RagFailureReasons.MixedRevisionIndex, result.Reason);
        Assert.Empty(result.Evidence);
    }

    // ---- fixtures ------------------------------------------------------------

    private static NubArca.Api.Rag.ProductHelp.ProductHelpCorpus ProductHelpCorpusStub()
        => NubArca.Api.Rag.ProductHelp.ProductHelpCorpus.Empty;

    private Task<string> SignatureAsync() => _corpus.GetSignatureAsync(RagDomainKey.ProductHelp);

    private RagDomainSource Seed()
    {
        var source = AddSource("docs/help/faces.md", "rev-a");
        var membership = AddMembership(source, RagDomains.ProductHelp);
        AddChunk(source, "Apri Volti e scegli Assegna nome.");
        _db.SaveChanges();
        return membership;
    }

    private RagSource AddSource(string key, string revision)
    {
        var source = new RagSource
        {
            Id = Guid.NewGuid(),
            SourceKey = key,
            Path = key,
            Title = key,
            SourceKind = RagSourceKinds.Documentation,
            Revision = revision,
            ContentHash = RagHash.Sha256Hex(key),
            Language = RagLanguages.Italian,
            CodeLanguage = RagCodeLanguages.Markdown,
            IndexFormatVersion = 1,
            CreatedAt = new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc),
        };
        _db.RagSources.Add(source);
        return source;
    }

    private RagDomainSource AddMembership(RagSource source, string domain)
    {
        var membership = new RagDomainSource
        {
            Id = Guid.NewGuid(),
            DomainKey = domain,
            SourceId = source.Id,
            Priority = 100,
            CreatedAt = new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc),
        };
        _db.RagDomainSources.Add(membership);
        return membership;
    }

    private void AddChunk(RagSource source, string text)
        => _db.RagChunks.Add(new RagChunk
        {
            Id = Guid.NewGuid(),
            SourceId = source.Id,
            Ordinal = 1,
            Heading = "Section",
            Text = text,
            TextHash = RagHash.Sha256Hex(text),
            Language = RagLanguages.Italian,
            CreatedAt = new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc),
        });
}
