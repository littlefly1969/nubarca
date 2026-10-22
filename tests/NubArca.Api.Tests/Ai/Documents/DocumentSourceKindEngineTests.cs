using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Ai.Documents.Indexing;
using NubArca.Api.Ai.Documents.Sources;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Assistant;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Retrieval;
using NubArca.Api.Tests.Assistant;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

// A SOURCE KIND IS A REGISTRATION, NOT AN ENGINE EDIT.
//
// A fake owner-private kind — already-normalized sections, the way an archived
// email will hand them over — is registered as an IOwnerDocumentSource and
// nothing else. These tests prove that registration alone carries it through
// the whole engine: change signal and backfill, extraction from sections,
// chunking, EMBEDDING, the lexical corpus, vector retrieval, hybrid search,
// the search `SourceKinds` filter and the Assistant's evidence and citations —
// each with its real kind.
//
// And that the boundary holds for it exactly as for files: another owner never
// sees it, a branch that forgot its owner clause still cannot widen anybody's
// corpus, and the moment its own liveness predicate turns false it is gone
// from every surface while its derived rows are still in the database.
public sealed class DocumentSourceKindEngineTests : IAsyncLifetime
{
    private const string ArchiveSentinelA = "ARCHIVE_A_SENTINEL";
    private const string ArchiveSentinelB = "ARCHIVE_B_SENTINEL";
    private const string Question = "pulizia del filtro della caldaia";

    private const string Manual = """
        # Manuale della caldaia

        ## Pulizia del filtro

        Il filtro dell'acqua della caldaia va pulito ogni sei mesi. Chiudere il
        rubinetto di ingresso, svitare il corpo del filtro e sciacquare la
        cartuccia sotto acqua corrente.
        """;

    private SqliteWebApplicationFactory _factory = null!;
    private CapturingProviderHandler _handler = null!;
    private readonly FakeArchiveKind _archive = new();
    private Guid _ownerA;
    private Guid _ownerB;
    private HttpClient _clientA = null!;
    private Guid _archiveA;
    private Guid _archiveB;
    private Guid _manualA;

    public async Task InitializeAsync()
    {
        _handler = new CapturingProviderHandler(
            _ => CapturingProviderHandler.Answer("Il filtro va pulito ogni sei mesi."));

        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:DocumentExtractionEnabled"] = "true",
            ["Ai:DocumentEmbeddingsEnabled"] = "true",
            ["Rag:Domains:user-documents:SemanticEnabled"] = "true",
            ["Rag:Domains:user-documents:TextEmbeddingProfileKey"] = DeterministicTextEmbeddingProvider.ProfileKey,
            ["Assistant:Enabled"] = "true",
            ["Assistant:PrivateKnowledgeModel"] = "private",
            ["Assistant:Models:private:Protocol"] = "OpenAiCompatible",
            ["Assistant:Models:private:Trust"] = "LocalTrusted",
            ["Assistant:Models:private:BaseUrl"] = "http://model.internal:11434",
            ["Assistant:Models:private:ApiKey"] = "",
            ["Assistant:Models:private:Model"] = "local-model-1",
            ["Assistant:Models:private:Label"] = "Local Model",
        });
        _factory.ConfigureExtraServices = services =>
        {
            // THE ONLY THING THE KIND DOES TO JOIN THE ENGINE.
            services.AddScoped<IOwnerDocumentSource>(_ => _archive);
            services.AddHttpClient<IAssistantTextModel, OpenAiCompatibleTextModel>()
                .ConfigurePrimaryHttpMessageHandler(() => _handler);
        };
        _factory.EnsureDatabaseCreated();
        using (var scope = _factory.Services.CreateScope())
        {
            SeedEmbeddingProfile(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        (_ownerA, _clientA) = await _factory.CreateAuthenticatedClientAsync("a@example.invalid");
        (_ownerB, _) = await _factory.CreateAuthenticatedClientAsync("b@example.invalid");

        _archiveA = _archive.Add(_ownerA, "Posta: manutenzione caldaia",
            $"{ArchiveSentinelA}. Il tecnico ha scritto che la pulizia del filtro della caldaia "
            + "va fatta lunedì mattina, insieme al controllo della pressione.");
        _archiveB = _archive.Add(_ownerB, "Posta: caldaia condominio",
            $"{ArchiveSentinelB}. L'amministratore ricorda la pulizia del filtro della caldaia "
            + "condominiale prevista per venerdì.");
        _manualA = await AddFileAsync(_ownerA, "manuale.md", Manual);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    // ---- registration alone carries a kind through the engine ---------------

    [Fact]
    public async Task A_Registered_Kind_Is_Signalled_Extracted_Chunked_And_Embedded()
    {
        // THE CHANGE SIGNAL, as a kind's own ingestion would raise it: a job is
        // recorded and nothing is derived inline.
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDocumentSourceChangeSink>()
                .SourceChangedAsync(FakeArchiveKind.KindName, _ownerA, _archiveA);
        }
        var payload = await SingleQueuedPayloadAsync();
        Assert.Equal(FakeArchiveKind.KindName, payload.SourceKind);
        Assert.Equal(_archiveA, payload.SourceId);
        Assert.Equal(0, await CountAsync(db => db.DocumentTexts.CountAsync()));

        await RunAsync(payload);

        using var check = _factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var text = await db.DocumentTexts.SingleAsync();
        Assert.Equal(FakeArchiveKind.KindName, text.SourceKind);
        Assert.Equal(_archiveA, text.SourceId);
        Assert.Null(text.FileItemId);

        var chunkIds = await db.DocumentChunks.Where(c => c.DocumentTextId == text.Id)
            .Select(c => c.Id).ToListAsync();
        Assert.NotEmpty(chunkIds);
        // EMBEDDED: the embed phase unions over the registered kinds, so the
        // kind's chunks got vectors from the active profile.
        Assert.Equal(chunkIds.Count,
            await db.DocumentChunkEmbeddings.CountAsync(e => chunkIds.Contains(e.DocumentChunkId)));
        Assert.Equal(DocumentSourceStatuses.Completed, (await db.DocumentSourceStates.SingleAsync(
            s => s.SourceKind == FakeArchiveKind.KindName && s.Phase == DocumentSourcePhases.Embed)).Status);
    }

    [Fact]
    public async Task Lexical_Vector_And_Hybrid_Search_Find_The_Kind_With_Its_Real_Kind()
    {
        await BackfillAsync(_ownerA);

        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        // LEXICAL: the corpus carries the kind and the display name.
        var corpus = await sp.GetRequiredService<OwnerDocumentCorpusSource>().LoadAsync(_ownerA);
        var archiveChunks = corpus.Chunks.Where(c => c.DocumentSourceKind == FakeArchiveKind.KindName).ToList();
        Assert.NotEmpty(archiveChunks);
        Assert.All(archiveChunks, c =>
        {
            Assert.Equal(_archiveA, c.DocumentSourceId);
            Assert.Equal("Posta: manutenzione caldaia", c.Title);
            Assert.Equal(_ownerA, c.OwnerUserId);
        });
        var index = new RagLexicalIndex(corpus, RagRankingProfiles.For(RagDomainKey.UserDocuments));
        var lexical = index.Search(RagQueryShape.For(Question, expandAliases: false), 10);
        Assert.Contains(lexical, h => h.Chunk.DocumentSourceKind == FakeArchiveKind.KindName);

        // VECTOR: the same kinds as the lexical corpus.
        var vector = await sp.GetRequiredService<OwnerDocumentVectorRetriever>()
            .SearchAsync(index, _ownerA, Question, 10);
        Assert.Null(vector.Reason);
        Assert.Contains(vector.Hits, h => h.Chunk.DocumentSourceKind == FakeArchiveKind.KindName);

        // HYBRID SEARCH over HTTP: both the file and the archive, each with its
        // real kind, and nothing of owner B's.
        var response = await _clientA.PostAsJsonAsync("/api/documents/search", new { query = Question });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(RagRetrievalModes.Hybrid, json.GetProperty("mode").GetString());
        var found = json.GetProperty("documents").EnumerateArray()
            .Select(d => (Kind: d.GetProperty("sourceKind").GetString(), Id: d.GetProperty("sourceId").GetGuid(),
                          Name: d.GetProperty("name").GetString()))
            .ToList();
        Assert.Contains((FakeArchiveKind.KindName, _archiveA, "Posta: manutenzione caldaia"), found);
        Assert.Contains((DocumentSourceKinds.File, _manualA, "manuale.md"), found);
        Assert.DoesNotContain(ArchiveSentinelB, body, StringComparison.Ordinal);
        Assert.DoesNotContain(_archiveB.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_SourceKinds_Filter_Filters()
    {
        await BackfillAsync(_ownerA);
        using var scope = _factory.Services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<OwnerDocumentSearchService>();

        var all = await search.SearchAsync(_ownerA, new OwnerDocumentSearchRequest(Question));
        Assert.Equal(
            new[] { DocumentSourceKinds.File, FakeArchiveKind.KindName },
            all.Documents.Select(d => d.SourceKind).Distinct().OrderBy(k => k, StringComparer.Ordinal));

        var archiveOnly = await search.SearchAsync(_ownerA, new OwnerDocumentSearchRequest(
            Question, SourceKinds: new[] { FakeArchiveKind.KindName }));
        Assert.Equal(_archiveA, Assert.Single(archiveOnly.Documents).SourceId);
        Assert.Equal(FakeArchiveKind.KindName, archiveOnly.Documents[0].SourceKind);

        var filesOnly = await search.SearchAsync(_ownerA, new OwnerDocumentSearchRequest(
            Question, SourceKinds: new[] { DocumentSourceKinds.File }));
        Assert.Equal(_manualA, Assert.Single(filesOnly.Documents).SourceId);

        // A kind nobody registered matches no branch: nothing, never everything.
        var unknown = await search.SearchAsync(_ownerA, new OwnerDocumentSearchRequest(
            Question, SourceKinds: new[] { "email" }));
        Assert.Empty(unknown.Documents);

        // Ids narrow within the kinds, and another owner's id is simply absent.
        var forged = await search.SearchAsync(_ownerA, new OwnerDocumentSearchRequest(
            Question, AllowedSourceIds: new[] { _archiveB }));
        Assert.Empty(forged.Documents);
    }

    [Fact]
    public async Task The_Assistant_Answers_From_The_Kind_And_Cites_It_By_Name_And_Kind()
    {
        await BackfillAsync(_ownerA);
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var retrieval = await sp.GetRequiredService<NubArca.Api.Ai.DocumentVisual.OwnerDocumentRetrievalPipeline>()
            .RetrieveAsync(_ownerA, Question, 10, 20_000);
        Assert.Contains(retrieval.Evidence, e =>
            e.DocumentSourceKind == FakeArchiveKind.KindName && e.DocumentSourceId == _archiveA);

        var answer = await sp.GetRequiredService<PrivateDocumentAssistantService>()
            .AskAsync(_ownerA, Question, Array.Empty<PrivateDocumentTurn>());
        Assert.True(answer.Ok, answer.Reason);
        Assert.Contains(ArchiveSentinelA, _handler.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain(ArchiveSentinelB, _handler.Body!, StringComparison.Ordinal);
        Assert.Contains(answer.Sources, s =>
            s.SourceKind == FakeArchiveKind.KindName && s.Document == "Posta: manutenzione caldaia");

        // Citations stay ids-free on the wire: the kind, the name, no source id.
        var wire = await _clientA.PostAsJsonAsync("/api/assistant/documents/chat", new { message = Question });
        var body = await wire.Content.ReadAsStringAsync();
        Assert.Contains($"\"sourceKind\":\"{FakeArchiveKind.KindName}\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain(_archiveA.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the boundary, for a kind that is not a file -------------------------

    [Fact]
    public async Task Another_Owner_Never_Sees_The_Kind()
    {
        await BackfillAsync(_ownerA);
        await BackfillAsync(_ownerB);

        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var corpusB = await sp.GetRequiredService<OwnerDocumentCorpusSource>().LoadAsync(_ownerB);
        Assert.NotEmpty(corpusB.Chunks);
        Assert.All(corpusB.Chunks, c =>
        {
            Assert.Equal(_ownerB, c.OwnerUserId);
            Assert.Equal(_archiveB, c.DocumentSourceId);
            Assert.DoesNotContain(ArchiveSentinelA, c.Text, StringComparison.Ordinal);
        });

        var resultB = await sp.GetRequiredService<OwnerDocumentSearchService>()
            .SearchAsync(_ownerB, new OwnerDocumentSearchRequest(Question, AllowedSourceIds: new[] { _archiveA }));
        Assert.Empty(resultB.Documents);
    }

    [Fact]
    public async Task A_Branch_That_Forgot_Its_Owner_Clause_Still_Cannot_Widen_A_Corpus()
    {
        await BackfillAsync(_ownerA);
        await BackfillAsync(_ownerB);

        _archive.ForgetOwnerClause = true;

        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var corpusA = await sp.GetRequiredService<OwnerDocumentCorpusSource>().LoadAsync(_ownerA);
        Assert.DoesNotContain(corpusA.Chunks, c => c.DocumentSourceId == _archiveB);
        Assert.Contains(corpusA.Chunks, c => c.DocumentSourceId == _archiveA);
    }

    [Fact]
    public async Task When_The_Kinds_Liveness_Turns_False_It_Is_Gone_At_Once_With_Derived_Rows_Left_In_Place()
    {
        await BackfillAsync(_ownerA);
        var derivedBefore = await DerivedCountsAsync(_archiveA);
        Assert.True(derivedBefore.Chunks > 0 && derivedBefore.Vectors > 0);

        // The kind's own row stops qualifying (the message deleted, the archive
        // removed). No job runs, no cleanup runs.
        _archive.SetLive(_archiveA, false);

        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var corpus = await sp.GetRequiredService<OwnerDocumentCorpusSource>().LoadAsync(_ownerA);
        Assert.DoesNotContain(corpus.Chunks, c => c.DocumentSourceId == _archiveA);

        var search = await sp.GetRequiredService<OwnerDocumentSearchService>()
            .SearchAsync(_ownerA, new OwnerDocumentSearchRequest(Question));
        Assert.DoesNotContain(search.Documents, d => d.SourceId == _archiveA);
        Assert.Contains(search.Documents, d => d.SourceId == _manualA);

        var index = new RagLexicalIndex(corpus, RagRankingProfiles.For(RagDomainKey.UserDocuments));
        var vector = await sp.GetRequiredService<OwnerDocumentVectorRetriever>()
            .SearchAsync(index, _ownerA, Question, 10);
        Assert.DoesNotContain(vector.Hits, h => h.Chunk.DocumentSourceId == _archiveA);

        var answer = await sp.GetRequiredService<PrivateDocumentAssistantService>()
            .AskAsync(_ownerA, Question, Array.Empty<PrivateDocumentTurn>());
        Assert.True(answer.Ok, answer.Reason);
        Assert.DoesNotContain(ArchiveSentinelA, _handler.Body!, StringComparison.Ordinal);

        // The cache is still there; it is simply not authority.
        Assert.Equal(derivedBefore, await DerivedCountsAsync(_archiveA));
    }

    [Fact]
    public async Task A_Source_That_Is_Not_Live_Is_Not_Embedded()
    {
        // Extract and chunk while live, then the source stops qualifying before
        // the embed phase: embedding reads the same boundary as retrieval, so
        // no fresh vector is minted for it.
        using (var scope = _factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var candidate = (await _archive.GetCandidateAsync(_ownerA, _archiveA))!;
            var indexer = sp.GetRequiredService<OwnerDocumentIndexer>();
            var outcome = await indexer.IndexSourceAsync(_archive, _ownerA, candidate);
            Assert.True(outcome.HasReading);

            _archive.SetLive(_archiveA, false);
            var resolution = await sp.GetRequiredService<TextEmbeddingResolver>()
                .ResolveAsync(RagDomainKey.UserDocuments);
            Assert.True(resolution.IsAvailable);
            var embedded = await indexer.EmbedDocumentAsync(_ownerA, outcome.DocumentTextId!.Value, resolution);
            Assert.Equal(0, embedded.EmbeddingsCreated);
        }
        Assert.Equal(0, (await DerivedCountsAsync(_archiveA)).Vectors);
    }

    // ---- fixture ------------------------------------------------------------

    private async Task BackfillAsync(Guid owner) => await RunAsync(new DocumentIndexJobPayload(OwnerUserId: owner));

    private async Task RunAsync(DocumentIndexJobPayload payload)
    {
        using var scope = _factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>()
            .Single(h => h.JobType == JobTypes.AiDocumentsIndex);
        var context = new JobContext(
            Guid.NewGuid(), JsonSerializer.Serialize(payload),
            _ => { }, CancellationToken.None, (_, _, _, _) => Task.CompletedTask,
            TimeProvider.System, JobScheduling.Compute, null,
            sliceNumber: 0, sliceDeadline: null, sliceItemBudget: null);
        await handler.ExecuteAsync(context, CancellationToken.None);
    }

    private async Task<DocumentIndexJobPayload> SingleQueuedPayloadAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.SingleAsync(j => j.Type == JobTypes.AiDocumentsIndex);
        return JsonSerializer.Deserialize<DocumentIndexJobPayload>(job.PayloadJson!)!;
    }

    private async Task<int> CountAsync(Func<AppDbContext, Task<int>> count)
    {
        using var scope = _factory.Services.CreateScope();
        return await count(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task<(int Texts, int Chunks, int Vectors)> DerivedCountsAsync(Guid sourceId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var texts = db.DocumentTexts.Where(d => d.SourceKind == FakeArchiveKind.KindName && d.SourceId == sourceId);
        var chunks = db.DocumentChunks.Where(c => texts.Any(t => t.Id == c.DocumentTextId));
        return (await texts.CountAsync(), await chunks.CountAsync(),
            await db.DocumentChunkEmbeddings.CountAsync(e => chunks.Any(c => c.Id == e.DocumentChunkId)));
    }

    private async Task<Guid> AddFileAsync(Guid owner, string name, string content)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bytes = Encoding.UTF8.GetBytes(content);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var key = $"objects/{sha[..2]}/{sha[2..4]}/{sha}";
        var path = Path.Combine(_factory.StorageRoot, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);

        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = sha,
            StorageKey = key,
            SizeBytes = bytes.Length,
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.BlobObjects.Add(blob);
        var file = new FileItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            BlobObjectId = blob.Id,
            Name = name,
            MimeType = "text/markdown",
            SizeBytes = bytes.Length,
            CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
            EffectiveDateTakenSource = "uploaded",
        };
        db.FileItems.Add(file);
        await db.SaveChangesAsync();
        return file.Id;
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

    /// A test-only owner-private kind that hands over already-normalized
    /// sections (subject, body), the way an archived email will — the same
    /// shape as DocumentIndexJobTests' sections kind, plus a live predicate.
    ///
    /// Its "live rows" are held in memory; the EF query anchors on the owner's
    /// `User` row and reads each entry's liveness as a query PARAMETER, so the
    /// predicate is evaluated when the engine's query executes — exactly when a
    /// real kind's table would be read.
    private sealed class FakeArchiveKind : IOwnerDocumentSource
    {
        public const string KindName = "test-archive";

        private sealed class Entry
        {
            public Guid Id { get; init; }
            public Guid Owner { get; init; }
            public string Name { get; init; } = string.Empty;
            public string Body { get; init; } = string.Empty;
            public bool Live { get; set; } = true;
        }

        private readonly List<Entry> _entries = new();

        /// A deliberately broken branch: returns every owner's sources.
        public bool ForgetOwnerClause { get; set; }

        public string Kind => KindName;

        public Guid Add(Guid owner, string name, string body)
        {
            var id = Guid.NewGuid();
            _entries.Add(new Entry { Id = id, Owner = owner, Name = name, Body = body });
            return id;
        }

        public void SetLive(Guid id, bool live) => _entries.Single(e => e.Id == id).Live = live;

        public IQueryable<EligibleSourceRow> Eligible(AppDbContext db, Guid ownerUserId)
        {
            IQueryable<EligibleSourceRow>? union = null;
            foreach (var entry in _entries)
            {
                var e = entry;
                var forget = ForgetOwnerClause;
                var branch = db.Users.AsNoTracking()
                    .Where(u => u.Id == e.Owner && e.Live && (forget || u.Id == ownerUserId))
                    .Select(u => new EligibleSourceRow { SourceId = e.Id, OwnerUserId = u.Id, DisplayName = e.Name });
                union = union is null ? branch : union.Concat(branch);
            }
            return union ?? db.Users.Where(_ => false).Select(u => new EligibleSourceRow { SourceId = u.Id });
        }

        private IEnumerable<Entry> LiveFor(Guid owner) => _entries.Where(e => e.Owner == owner && e.Live);

        public Task<IReadOnlyList<DocumentSourceCandidate>> ListCandidatesAsync(
            Guid ownerUserId, Guid? afterSourceId, int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DocumentSourceCandidate>>(LiveFor(ownerUserId)
                .Where(e => afterSourceId is null || e.Id.CompareTo(afterSourceId.Value) > 0)
                .OrderBy(e => e.Id)
                .Take(take)
                .Select(e => new DocumentSourceCandidate(e.Id, "rev-1", e.Name))
                .ToList());

        public Task<DocumentSourceCandidate?> GetCandidateAsync(
            Guid ownerUserId, Guid sourceId, CancellationToken cancellationToken = default)
            => Task.FromResult(LiveFor(ownerUserId).Where(e => e.Id == sourceId)
                .Select(e => new DocumentSourceCandidate(e.Id, "rev-1", e.Name)).FirstOrDefault());

        public Task<DocumentSourceLoad> LoadAsync(
            Guid ownerUserId, DocumentSourceCandidate candidate, CancellationToken cancellationToken = default)
        {
            var entry = LiveFor(ownerUserId).FirstOrDefault(e => e.Id == candidate.SourceId);
            if (entry is null) return Task.FromResult(DocumentSourceLoad.NotEligible());

            return Task.FromResult(DocumentSourceLoad.FromSections(
                new[]
                {
                    new ExtractedDocumentBlock(1, DocumentBlockKinds.Heading,
                        entry.Name, "Oggetto", new DocumentLocator(DocumentLocatorKinds.Section, 1, "header")),
                    new ExtractedDocumentBlock(2, DocumentBlockKinds.Body,
                        entry.Body, null, new DocumentLocator(DocumentLocatorKinds.Section, 2, "body")),
                },
                source: "test-archive",
                profileKey: "doc-test-archive-v1"));
        }
    }
}
