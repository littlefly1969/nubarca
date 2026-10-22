using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Ai.Documents.Indexing;
using NubArca.Api.Ai.Documents.Sources;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

// A READING AND ITS CHUNKS ARE ONE PUBLICATION, AND A CITATION FOLLOWS THE
// LAYOUT.
//
// Two failures the automatic indexer would otherwise make permanent:
//
// - the same words moved to another page/section kept the OLD locator,
//   because unchanged text short-circuited chunk reconciliation and an
//   unchanged chunk hash skipped the placement fields;
// - the reading was saved Completed and current BEFORE its chunks were
//   replaced, so a failure in between left a current reading answering from the
//   previous reading's chunks — and the unchanged-source short-circuit then
//   kept it that way.
public sealed class DocumentChunkPublicationTests : IAsyncLifetime
{
    private const string Body =
        "Il filtro dell'acqua della caldaia va pulito ogni sei mesi chiudendo il rubinetto di ingresso.";

    private SqliteWebApplicationFactory _factory = null!;
    private readonly MovableSectionsKind _source = new();
    private Guid _owner;
    private Guid _sourceId;

    public async Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:DocumentExtractionEnabled"] = "true",
            ["Ai:DocumentEmbeddingsEnabled"] = "true",
            ["Rag:Domains:user-documents:SemanticEnabled"] = "true",
            ["Rag:Domains:user-documents:TextEmbeddingProfileKey"] = DeterministicTextEmbeddingProvider.ProfileKey,
        });
        _factory.ConfigureExtraServices = services =>
            services.AddScoped<IOwnerDocumentSource>(_ => _source);
        _factory.EnsureDatabaseCreated();
        using (var scope = _factory.Services.CreateScope())
        {
            SeedEmbeddingProfile(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        (_owner, _) = await _factory.CreateAuthenticatedClientAsync("owner@example.invalid");
        _sourceId = _source.Add(_owner, Body);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Same_Text_Moved_To_Another_Section_Moves_Its_Citation_And_Keeps_Its_Vector()
    {
        await IndexAsync();
        var before = await SingleChunkAsync();
        Assert.Equal(1, before.LocatorIndex);
        Assert.Equal("Pulizia", before.Heading);
        Assert.True(before.Embedded);

        // A new revision with IDENTICAL text, now under another section.
        _source.Move(_sourceId, revision: "rev-2", sectionIndex: 7, heading: "Manutenzione");
        await IndexAsync();

        var after = await SingleChunkAsync();
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(7, after.LocatorIndex);
        Assert.Equal("Manutenzione", after.Heading);
        // Same words, same vector: placement is rewritten, inference is not repeated.
        Assert.True(after.Embedded);
    }

    [Fact]
    public async Task A_Failure_While_Publishing_Chunks_Leaves_The_Previous_Publication_Whole_And_Repairable()
    {
        await IndexAsync();
        var original = await SingleChunkAsync();

        // New words in a new revision, and the chunk write fails half-way.
        _source.Rewrite(_sourceId, "rev-2",
            "Il bruciatore va controllato da un tecnico abilitato una volta l'anno.");
        await ExecuteSqlAsync(
            "CREATE TRIGGER inject_chunk_failure BEFORE INSERT ON document_chunks "
            + "BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        await ExecuteSqlAsync(
            "CREATE TRIGGER inject_chunk_update_failure BEFORE UPDATE ON document_chunks "
            + "BEGIN SELECT RAISE(ABORT, 'injected'); END;");

        await IndexAsync();

        // Nothing of the failed publication is visible. The source's content
        // changed, so the previous reading stopped being authority before the
        // new one was attempted (the platform's rule for changed bytes) — and
        // the failed attempt rolled back whole: the row still holds the
        // original text WITH its original chunk, never new text over old chunks.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reading = await db.DocumentTexts.SingleAsync(d => d.SourceId == _sourceId);
            Assert.False(reading.IsCurrent);
            Assert.Contains("filtro", reading.Text);
            var chunk = await db.DocumentChunks.SingleAsync(c => c.DocumentTextId == reading.Id);
            Assert.Equal(original.Id, chunk.Id);
            Assert.Contains("filtro", chunk.Text);
        }

        // Once the fault is gone the next pass publishes the new reading: the
        // failed attempt did not leave a "completed" row to short-circuit on.
        await ExecuteSqlAsync("DROP TRIGGER inject_chunk_failure;");
        await ExecuteSqlAsync("DROP TRIGGER inject_chunk_update_failure;");
        await IndexAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reading = await db.DocumentTexts.SingleAsync(d => d.SourceId == _sourceId);
            Assert.Contains("bruciatore", reading.Text);
            var chunk = await db.DocumentChunks.SingleAsync(c => c.DocumentTextId == reading.Id);
            Assert.Contains("bruciatore", chunk.Text);
        }
    }

    // ---- helpers ------------------------------------------------------------

    private sealed record ChunkView(Guid Id, int? LocatorIndex, string? Heading, bool Embedded);

    private async Task<ChunkView> SingleChunkAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reading = await db.DocumentTexts.SingleAsync(d => d.SourceId == _sourceId && d.IsCurrent);
        var chunk = await db.DocumentChunks.SingleAsync(c => c.DocumentTextId == reading.Id);
        var embedded = await db.DocumentChunkEmbeddings.AnyAsync(e => e.DocumentChunkId == chunk.Id);
        return new ChunkView(chunk.Id, chunk.LocatorIndex, chunk.Heading, embedded);
    }

    private async Task ExecuteSqlAsync(string sql)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task IndexAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>()
            .Single(h => h.JobType == JobTypes.AiDocumentsIndex);
        var payload = new DocumentIndexJobPayload(
            OwnerUserId: _owner,
            SourceKind: MovableSectionsKind.KindName,
            SourceId: _sourceId,
            RetryFailed: true);
        var context = new JobContext(
            Guid.NewGuid(), JsonSerializer.Serialize(payload),
            _ => { }, CancellationToken.None, (_, _, _, _) => Task.CompletedTask,
            TimeProvider.System, JobScheduling.Compute, null,
            sliceNumber: 0, sliceDeadline: null, sliceItemBudget: null);
        await handler.ExecuteAsync(context, CancellationToken.None);
    }

    private static void SeedEmbeddingProfile(AppDbContext db)
    {
        var key = DeterministicTextEmbeddingProvider.ProfileKey;
        if (db.AiProfiles.Any(p => p.Key == key)) return;
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
        db.AiModels.Add(model);
        db.AiProfiles.Add(new AiProfile
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
        });
        db.SaveChanges();
    }

    /// One section whose revision, text, section index and heading the test
    /// can change between passes.
    private sealed class MovableSectionsKind : IOwnerDocumentSource
    {
        public const string KindName = "test-movable";

        private sealed class Entry
        {
            public Guid Id { get; init; }
            public Guid Owner { get; init; }
            public string Revision { get; set; } = "rev-1";
            public string Body { get; set; } = string.Empty;
            public int SectionIndex { get; set; } = 1;
            public string Heading { get; set; } = "Pulizia";
        }

        private readonly List<Entry> _entries = new();

        public string Kind => KindName;

        public Guid Add(Guid owner, string body)
        {
            var id = Guid.NewGuid();
            _entries.Add(new Entry { Id = id, Owner = owner, Body = body });
            return id;
        }

        public void Move(Guid id, string revision, int sectionIndex, string heading)
        {
            var e = _entries.Single(x => x.Id == id);
            e.Revision = revision;
            e.SectionIndex = sectionIndex;
            e.Heading = heading;
        }

        public void Rewrite(Guid id, string revision, string body)
        {
            var e = _entries.Single(x => x.Id == id);
            e.Revision = revision;
            e.Body = body;
        }

        public IQueryable<EligibleSourceRow> Eligible(AppDbContext db, Guid ownerUserId)
        {
            var e = _entries.Single();
            return db.Users.AsNoTracking()
                .Where(u => u.Id == e.Owner && u.Id == ownerUserId)
                .Select(u => new EligibleSourceRow { SourceId = e.Id, OwnerUserId = u.Id, DisplayName = "Nota", LiveBlobObjectId = null, LiveRevision = e.Revision });
        }

        private DocumentSourceCandidate? Candidate(Guid owner, Guid id)
            => _entries.Where(e => e.Owner == owner && e.Id == id)
                .Select(e => new DocumentSourceCandidate(e.Id, e.Revision, "Nota"))
                .FirstOrDefault();

        public Task<IReadOnlyList<DocumentSourceCandidate>> ListCandidatesAsync(
            Guid ownerUserId, Guid? afterSourceId, int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DocumentSourceCandidate>>(_entries
                .Where(e => e.Owner == ownerUserId
                            && (afterSourceId is null || e.Id.CompareTo(afterSourceId.Value) > 0))
                .OrderBy(e => e.Id)
                .Take(take)
                .Select(e => new DocumentSourceCandidate(e.Id, e.Revision, "Nota"))
                .ToList());

        public Task<DocumentSourceCandidate?> GetCandidateAsync(
            Guid ownerUserId, Guid sourceId, CancellationToken cancellationToken = default)
            => Task.FromResult(Candidate(ownerUserId, sourceId));

        public Task<DocumentSourceLoad> LoadAsync(
            Guid ownerUserId, DocumentSourceCandidate candidate, CancellationToken cancellationToken = default)
        {
            var e = _entries.FirstOrDefault(x => x.Owner == ownerUserId && x.Id == candidate.SourceId);
            if (e is null) return Task.FromResult(DocumentSourceLoad.NotEligible());

            return Task.FromResult(DocumentSourceLoad.FromSections(
                new[]
                {
                    new ExtractedDocumentBlock(1, DocumentBlockKinds.Body, e.Body, e.Heading,
                        new DocumentLocator(DocumentLocatorKinds.Section, e.SectionIndex, e.Heading)),
                },
                source: "test-movable",
                profileKey: "doc-test-movable-v1"));
        }
    }
}
