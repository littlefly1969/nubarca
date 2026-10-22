using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Ai.Documents.Indexing;
using NubArca.Api.Ai.Documents.Sources;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Retrieval;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Uploads;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

// Automatic document indexing: the `ai.documents.index` job, its per-source
// per-phase state, and the change signal — driven through the real host
// registrations against a real filesystem store.
//
// What is asserted is the job contract from CLAUDE.md and the source contract:
// disabled by default means nothing runs; a run resumes from its checkpoint and
// repeating it changes nothing; a retryable failure is retried up to a budget;
// a permanent verdict is not retried until the bytes change; cancellation and a
// missing model are never recorded as failures; one owner's job touches only
// that owner.
public sealed class DocumentIndexJobTests
{
    private const string Manual = """
        # Manuale della caldaia

        Questo manuale descrive la manutenzione ordinaria della caldaia installata
        nell'appartamento, comprese le operazioni che il proprietario può eseguire
        senza chiamare un tecnico specializzato.

        ## Pulizia del filtro

        Il filtro dell'acqua va pulito ogni sei mesi. Chiudere il rubinetto di
        ingresso, svitare il corpo del filtro e sciacquare la cartuccia sotto acqua
        corrente fino a rimuovere ogni residuo visibile.
        """;

    private const string Recipe = """
        # Ricetta del ragù

        Soffriggere cipolla, carota e sedano in olio extravergine, aggiungere la
        carne macinata e rosolare a fuoco vivo. Sfumare con vino rosso, unire la
        passata di pomodoro e cuocere a fuoco lento per almeno tre ore.
        """;

    private static readonly (string, string)[] Enabled =
    {
        ("Ai:Enabled", "true"),
        ("Ai:DocumentExtractionEnabled", "true"),
        ("Ai:DocumentEmbeddingsEnabled", "true"),
        ("Rag:Domains:user-documents:SemanticEnabled", "true"),
        ("Rag:Domains:user-documents:TextEmbeddingProfileKey", DeterministicTextEmbeddingProvider.ProfileKey),
    };

    // ---- disabled by default ------------------------------------------------

    [Fact]
    public async Task Disabled_By_Default_Enqueues_Nothing_And_The_Job_Writes_Nothing()
    {
        using var f = Factory();
        var owner = await AddUserAsync(f);
        var file = await AddFileAsync(f, owner, "manuale.md", Manual);

        using (var scope = f.Services.CreateScope())
        {
            var sink = scope.ServiceProvider.GetRequiredService<IDocumentSourceChangeSink>();
            await sink.SourceChangedAsync(DocumentSourceKinds.File, owner, file.Id);
            await sink.OwnerSourcesChangedAsync(owner);
        }

        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));

        using var check = f.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.BackgroundJobs.CountAsync(j => j.Type == JobTypes.AiDocumentsIndex));
        Assert.Equal(0, await db.DocumentTexts.CountAsync());
        Assert.Equal(0, await db.DocumentSourceStates.CountAsync());
    }

    [Fact]
    public async Task Upload_Of_A_Document_Signals_A_Job_And_A_Photo_Does_Not()
    {
        using var f = Factory(Enabled);
        var owner = await AddUserAsync(f);

        using (var scope = f.Services.CreateScope())
        {
            var ingestion = scope.ServiceProvider.GetRequiredService<IOwnerUploadIngestion>();
            await ingestion.IngestAsync(
                new OwnerUploadRequest(owner, null, "manuale.md", null, "text/markdown",
                    UploadNameConflictPolicy.Fail, null, null),
                new MemoryStream(Encoding.UTF8.GetBytes(Manual)), CancellationToken.None);
            await ingestion.IngestAsync(
                new OwnerUploadRequest(owner, null, "note.bin", null, "application/octet-stream",
                    UploadNameConflictPolicy.Fail, null, null),
                new MemoryStream(new byte[] { 1, 2, 3 }), CancellationToken.None);
        }

        using var check = f.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var jobs = await db.BackgroundJobs.Where(j => j.Type == JobTypes.AiDocumentsIndex).ToListAsync();
        var job = Assert.Single(jobs);
        Assert.Equal(JobScheduling.Compute, job.Priority);
        // Nothing was extracted inline: the upload only recorded the change.
        Assert.Equal(0, await db.DocumentTexts.CountAsync());
    }

    // ---- the happy path, idempotence, resume ---------------------------------

    [Fact]
    public async Task Owner_Backfill_Extracts_Chunks_And_Embeds_With_State_Per_Phase()
    {
        using var f = Factory(Enabled);
        var owner = await AddUserAsync(f);
        await AddFileAsync(f, owner, "manuale.md", Manual);
        await AddFileAsync(f, owner, "ricetta.md", Recipe);

        var context = await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));
        Assert.False(context.ContinuationRequested);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var states = await db.DocumentSourceStates.ToListAsync();
        foreach (var phase in new[] { DocumentSourcePhases.Extract, DocumentSourcePhases.Chunk, DocumentSourcePhases.Embed })
        {
            Assert.Equal(2, states.Count(s => s.Phase == phase && s.Status == DocumentSourceStatuses.Completed));
        }
        Assert.All(states, s =>
        {
            Assert.Equal(owner, s.OwnerUserId);
            Assert.Equal(0, s.Attempts);
            Assert.Null(s.ReasonCode);
        });
        // No visual phase: the capability is off, so nothing is recorded for it.
        Assert.DoesNotContain(states, s => s.Phase == DocumentSourcePhases.Visual);

        var chunks = await db.DocumentChunks.CountAsync();
        Assert.True(chunks > 0);
        Assert.Equal(chunks, await db.DocumentChunkEmbeddings.CountAsync());
        Assert.All(await db.DocumentTexts.ToListAsync(), d =>
        {
            Assert.Equal(DocumentSourceKinds.File, d.SourceKind);
            Assert.Equal(d.FileItemId, d.SourceId);
        });
    }

    [Fact]
    public async Task Repeating_The_Job_Changes_Nothing()
    {
        using var f = Factory(Enabled);
        var owner = await AddUserAsync(f);
        await AddFileAsync(f, owner, "manuale.md", Manual);

        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));
        var before = await SnapshotAsync(f);

        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));
        var after = await SnapshotAsync(f);

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task A_Sliced_Run_Checkpoints_And_Resumes_Where_It_Stopped()
    {
        using var f = Factory(Enabled);
        var owner = await AddUserAsync(f);
        for (var i = 0; i < 3; i++)
        {
            await AddFileAsync(f, owner, $"doc-{i}.md", Manual + $"\n\nCopia numero {i} del manuale.");
        }

        // An item budget of one: each slice processes one source and yields.
        var first = await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner), itemBudget: 1);
        Assert.True(first.ContinuationRequested);
        Assert.Equal(1, await CompletedAsync(f, DocumentSourcePhases.Embed));

        var checkpoint = first.ContinuationCheckpoint;
        var slices = 1;
        while (checkpoint is not null && slices < 10)
        {
            var next = await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner),
                itemBudget: 1, checkpoint: checkpoint, sliceNumber: slices);
            slices++;
            checkpoint = next.ContinuationRequested ? next.ContinuationCheckpoint : null;
        }

        Assert.Equal(3, await CompletedAsync(f, DocumentSourcePhases.Embed));
        Assert.InRange(slices, 3, 4);
    }

    [Fact]
    public async Task A_Run_Killed_Mid_Source_Is_Finished_By_The_Next_Run()
    {
        using var f = Factory(Enabled);
        var owner = await AddUserAsync(f);
        var file = await AddFileAsync(f, owner, "manuale.md", Manual);

        // The state a killed process leaves: extraction done and chunked, the
        // embed phase marked running, no vectors written.
        using (var scope = f.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var source = sp.GetRequiredService<OwnerDocumentSources>().For(DocumentSourceKinds.File)!;
            var candidate = (await source.GetCandidateAsync(owner, file.Id))!;
            await sp.GetRequiredService<OwnerDocumentIndexer>().IndexSourceAsync(source, owner, candidate);
            await sp.GetRequiredService<DocumentSourceStateStore>().RecordAsync(
                DocumentSourceKinds.File, file.Id, owner, DocumentSourcePhases.Embed, candidate.RevisionHash,
                DocumentSourceStatuses.Running);
        }
        Assert.Equal(0, await CountAsync(f, db => db.DocumentChunkEmbeddings.CountAsync()));

        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));

        Assert.Equal(1, await CompletedAsync(f, DocumentSourcePhases.Embed));
        Assert.Equal(
            await CountAsync(f, db => db.DocumentChunks.CountAsync()),
            await CountAsync(f, db => db.DocumentChunkEmbeddings.CountAsync()));
    }

    // ---- failures, verdicts, environment -------------------------------------

    [Fact]
    public async Task A_Retryable_Failure_Is_Retried_Up_To_The_Budget_And_Then_On_Request()
    {
        using var f = Factory(Enabled);
        var owner = await AddUserAsync(f);
        var file = await AddFileAsync(f, owner, "manuale.md", Manual);
        var bytesPath = DeleteStoredBytes(f, file.BlobObjectId, out var original);

        for (var run = 1; run <= 4; run++)
        {
            await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));
        }

        var state = await StateAsync(f, file.Id, DocumentSourcePhases.Extract);
        Assert.Equal(DocumentSourceStatuses.Failed, state!.Status);
        Assert.Equal(DocumentSourceReasons.Unreadable, state.ReasonCode);
        // Three attempts (the default budget), not four: the fourth run left it
        // alone. Never `skipped` — storage trouble is not a content verdict.
        Assert.Equal(3, state.Attempts);

        // The bytes come back; an automatic run still respects the budget…
        await File.WriteAllBytesAsync(bytesPath, original);
        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));
        Assert.Equal(DocumentSourceStatuses.Failed, (await StateAsync(f, file.Id, DocumentSourcePhases.Extract))!.Status);

        // …and an operator's --retry-failed recovers it.
        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner, RetryFailed: true));
        var recovered = await StateAsync(f, file.Id, DocumentSourcePhases.Extract);
        Assert.Equal(DocumentSourceStatuses.Completed, recovered!.Status);
        Assert.Equal(0, recovered.Attempts);
    }

    [Fact]
    public async Task A_Permanent_Verdict_Is_Not_Retried_Until_The_Bytes_Change()
    {
        using var f = Factory(Enabled);
        var owner = await AddUserAsync(f);
        var bytes = new byte[512];
        bytes[0] = (byte)'P';
        var file = await AddFileAsync(f, owner, "archivio.txt", bytes, "text/plain");

        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));
        var skipped = await StateAsync(f, file.Id, DocumentSourcePhases.Extract);
        Assert.Equal(DocumentSourceStatuses.Skipped, skipped!.Status);
        Assert.Equal(DocumentExtractionReasons.Binary, skipped.ReasonCode);

        // Proof it is not re-read: with the bytes gone, a re-read would fail.
        DeleteStoredBytes(f, file.BlobObjectId, out _);
        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner, RetryFailed: true));
        var again = await StateAsync(f, file.Id, DocumentSourcePhases.Extract);
        Assert.Equal(DocumentSourceStatuses.Skipped, again!.Status);
        Assert.Equal(skipped.UpdatedAt, again.UpdatedAt);

        // New bytes are a new revision, and the question is open again.
        await ReplaceContentAsync(f, file.Id, Encoding.UTF8.GetBytes(Manual));
        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));
        Assert.Equal(DocumentSourceStatuses.Completed,
            (await StateAsync(f, file.Id, DocumentSourcePhases.Extract))!.Status);
    }

    [Fact]
    public async Task A_Missing_Embedding_Model_Is_An_Environment_State_Not_A_Failure()
    {
        using var f = Factory(
            ("Ai:Enabled", "true"),
            ("Ai:DocumentExtractionEnabled", "true"),
            ("Ai:DocumentEmbeddingsEnabled", "true"),
            ("Rag:Domains:user-documents:SemanticEnabled", "true"),
            ("Rag:Domains:user-documents:TextEmbeddingProfileKey", "no-such-profile"));
        var owner = await AddUserAsync(f);
        var file = await AddFileAsync(f, owner, "manuale.md", Manual);

        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));
        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));

        Assert.Equal(DocumentSourceStatuses.Completed,
            (await StateAsync(f, file.Id, DocumentSourcePhases.Extract))!.Status);
        var embed = await StateAsync(f, file.Id, DocumentSourcePhases.Embed);
        Assert.Equal(DocumentSourceStatuses.Pending, embed!.Status);
        Assert.Equal(RagFailureReasons.EmbeddingProfileUnavailable, embed.ReasonCode);
        Assert.Equal(0, embed.Attempts);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.DoesNotContain(await db.DocumentSourceStates.ToListAsync(),
            s => s.Status is DocumentSourceStatuses.Failed or DocumentSourceStatuses.Skipped);
        // One aggregate provider diagnostic, not one per document.
        Assert.True(await db.AiIndexDiagnostics.CountAsync() >= 1);
    }

    [Fact]
    public async Task Cancellation_Mid_Source_Records_Nothing_Permanent()
    {
        var gate = new CancellingSource();
        using var f = Factory(Enabled);
        f.ConfigureExtraServices = services => services.AddScoped<IOwnerDocumentSource>(_ => gate);
        var owner = await AddUserAsync(f);

        using var cts = new CancellationTokenSource();
        gate.OnLoad = () => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner), token: cts.Token));

        var state = await StateAsync(f, CancellingSource.Id, DocumentSourcePhases.Extract, CancellingSource.KindName);
        Assert.Equal(DocumentSourceStatuses.Pending, state!.Status);
        Assert.Equal(DocumentSourceReasons.Cancelled, state.ReasonCode);
        Assert.Equal(0, state.Attempts);

        gate.OnLoad = null;
        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));
        Assert.Equal(DocumentSourceStatuses.Completed,
            (await StateAsync(f, CancellingSource.Id, DocumentSourcePhases.Extract, CancellingSource.KindName))!.Status);
    }

    // ---- PDFs ---------------------------------------------------------------

    [Fact]
    public async Task Corrupt_Encrypted_And_Huge_Pdfs_Are_Permanent_Verdicts_With_Their_Own_Tokens()
    {
        using var f = Factory(Enabled);
        var owner = await AddUserAsync(f);
        var corrupt = await AddFileAsync(f, owner, "rotto.pdf",
            Encoding.ASCII.GetBytes("%PDF-1.7\nthis is not a pdf body at all\n"), "application/pdf");
        var encrypted = await AddFileAsync(f, owner, "cifrato.pdf", EncryptedPdf(), "application/pdf");
        var huge = await AddFileAsync(f, owner, "enorme.pdf", Encoding.ASCII.GetBytes("%PDF-1.7\n"), "application/pdf");
        await SetRecordedSizeAsync(f, huge.Id, 900L * 1024 * 1024);

        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));

        var corruptState = await StateAsync(f, corrupt.Id, DocumentSourcePhases.Extract);
        Assert.Equal(DocumentSourceStatuses.Skipped, corruptState!.Status);
        Assert.Equal(DocumentExtractionReasons.PdfInvalid, corruptState.ReasonCode);

        var encryptedState = await StateAsync(f, encrypted.Id, DocumentSourcePhases.Extract);
        Assert.Equal(DocumentSourceStatuses.Skipped, encryptedState!.Status);
        Assert.Equal(DocumentExtractionReasons.EncryptedDocument, encryptedState.ReasonCode);

        var hugeState = await StateAsync(f, huge.Id, DocumentSourcePhases.Extract);
        Assert.Equal(DocumentSourceStatuses.Skipped, hugeState!.Status);
        Assert.Equal(DocumentExtractionReasons.TooLarge, hugeState.ReasonCode);
    }

    // ---- owner isolation and progress --------------------------------------

    [Fact]
    public async Task One_Owners_Job_Touches_Only_That_Owner()
    {
        using var f = Factory(Enabled);
        var alice = await AddUserAsync(f);
        var bob = await AddUserAsync(f);
        await AddFileAsync(f, alice, "manuale.md", Manual);
        var bobs = await AddFileAsync(f, bob, "ricetta.md", Recipe);

        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: alice));

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.All(await db.DocumentSourceStates.ToListAsync(), s => Assert.Equal(alice, s.OwnerUserId));
            Assert.All(await db.DocumentTexts.ToListAsync(), d => Assert.Equal(alice, d.OwnerUserId));
            Assert.False(await db.DocumentTexts.AnyAsync(d => d.SourceId == bobs.Id));

            var progress = scope.ServiceProvider.GetRequiredService<DocumentIndexProgressService>();
            var bobView = await progress.GetAsync(bob);
            Assert.Equal(1, bobView.EligibleSources);
            Assert.Equal(1, bobView.Phases[DocumentSourcePhases.Extract][DocumentSourceStatuses.Pending]);
            Assert.Equal(0, bobView.Phases[DocumentSourcePhases.Extract][DocumentSourceStatuses.Completed]);

            var aliceView = await progress.GetAsync(alice);
            Assert.Equal(1, aliceView.Phases[DocumentSourcePhases.Embed][DocumentSourceStatuses.Completed]);
        }

        // Every owner: still one owner at a time, and now Bob is indexed too.
        await RunAsync(f, new DocumentIndexJobPayload(AllOwners: true));
        Assert.Equal(DocumentSourceStatuses.Completed,
            (await StateAsync(f, bobs.Id, DocumentSourcePhases.Embed))!.Status);
    }

    [Fact]
    public async Task A_Deleted_Source_Stops_Counting_At_Once()
    {
        using var f = Factory(Enabled);
        var owner = await AddUserAsync(f);
        var file = await AddFileAsync(f, owner, "manuale.md", Manual);
        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.FileItems.SingleAsync(x => x.Id == file.Id);
        row.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var view = await scope.ServiceProvider.GetRequiredService<DocumentIndexProgressService>().GetAsync(owner);
        Assert.Equal(0, view.EligibleSources);
        Assert.Equal(0, view.Phases[DocumentSourcePhases.Extract][DocumentSourceStatuses.Completed]);
    }

    // ---- the sections path ----------------------------------------------------

    [Fact]
    public async Task A_Sections_Kind_Is_Chunked_On_Its_Locators_And_Is_In_No_Corpus()
    {
        var sections = new SectionsSource();
        using var f = Factory(Enabled);
        f.ConfigureExtraServices = services => services.AddScoped<IOwnerDocumentSource>(_ => sections);
        var owner = await AddUserAsync(f);

        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var text = await db.DocumentTexts.SingleAsync();
        Assert.Equal(SectionsSource.KindName, text.SourceKind);
        Assert.Equal(SectionsSource.Id, text.SourceId);
        Assert.Null(text.FileItemId);
        Assert.Equal("rev-1", text.SourceRevision);
        Assert.True(text.IsCurrent);

        var chunks = await db.DocumentChunks.OrderBy(c => c.Ordinal).ToListAsync();
        Assert.Equal(2, chunks.Count);
        Assert.Equal(new[] { 1, 2 }, chunks.Select(c => c.LocatorIndex));
        Assert.Equal(DocumentLocatorKinds.Section, chunks[0].LocatorKind);

        // ELIGIBILITY IS PER REGISTERED KIND. This test kind registered a source
        // but no eligibility branch, so its chunks are in nobody's corpus — and
        // the embed phase has nothing it is allowed to embed.
        Assert.Empty(await OwnerDocumentEligibility.EligibleChunks(
            db.DocumentChunks, db.DocumentTexts, db.FileItems, owner).ToListAsync());

        // Idempotent on the revision token.
        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));
        Assert.Equal(1, await db.DocumentTexts.CountAsync());
        Assert.Equal(2, await db.DocumentChunks.CountAsync());
    }

    // ---- vector ceiling ------------------------------------------------------

    [Fact]
    public async Task Past_The_Vector_Ceiling_Semantic_Search_Says_Unavailable_Instead_Of_Sampling()
    {
        using var f = Factory(Enabled);
        var owner = await AddUserAsync(f);
        await AddFileAsync(f, owner, "manuale.md", Manual);
        await AddFileAsync(f, owner, "ricetta.md", Recipe);
        await RunAsync(f, new DocumentIndexJobPayload(OwnerUserId: owner));

        using var scope = f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var corpus = sp.GetRequiredService<OwnerDocumentCorpusSource>();
        var index = new RagLexicalIndex(
            await corpus.LoadAsync(owner), RagRankingProfiles.For(RagDomainKey.UserDocuments));

        var within = await sp.GetRequiredService<OwnerDocumentVectorRetriever>()
            .SearchAsync(index, owner, "pulizia del filtro", 10);
        Assert.Null(within.Reason);
        Assert.NotEmpty(within.Hits);

        var capped = new OwnerDocumentVectorRetriever(
            sp.GetRequiredService<AppDbContext>(), corpus,
            sp.GetRequiredService<TextEmbeddingResolver>(),
            sp.GetRequiredService<NubArca.Api.Ai.IAiVectorSerializer>())
        {
            CandidateCeiling = 1,
        };
        var over = await capped.SearchAsync(index, owner, "pulizia del filtro", 10);
        Assert.Equal(RagFailureReasons.CorpusTooLarge, over.Reason);
        Assert.Empty(over.Hits);
    }

    // ---- fixture ------------------------------------------------------------

    private static SqliteWebApplicationFactory Factory(params (string Key, string Value)[] settings)
    {
        var factory = new SqliteWebApplicationFactory(
            settings.ToDictionary(s => s.Key, s => (string?)s.Value));
        factory.EnsureDatabaseCreated();
        using var scope = factory.Services.CreateScope();
        SeedEmbeddingProfile(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        return factory;
    }

    private static async Task<JobContext> RunAsync(
        SqliteWebApplicationFactory f, DocumentIndexJobPayload payload,
        int? itemBudget = null, string? checkpoint = null, int sliceNumber = 0,
        CancellationToken token = default)
    {
        using var scope = f.Services.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>()
            .Single(h => h.JobType == JobTypes.AiDocumentsIndex);
        var context = new JobContext(
            Guid.NewGuid(), JsonSerializer.Serialize(payload),
            _ => { }, token, (_, _, _, _) => Task.CompletedTask,
            TimeProvider.System, JobScheduling.Compute, checkpoint,
            sliceNumber: sliceNumber, sliceDeadline: null, sliceItemBudget: itemBudget);
        await handler.ExecuteAsync(context, token);
        return context;
    }

    private static async Task<Guid> AddUserAsync(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = id,
            Email = $"owner-{id:N}@example.invalid",
            DisplayName = "Owner",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static Task<FileItem> AddFileAsync(
        SqliteWebApplicationFactory f, Guid owner, string name, string content)
        => AddFileAsync(f, owner, name, Encoding.UTF8.GetBytes(content), "text/markdown");

    private static async Task<FileItem> AddFileAsync(
        SqliteWebApplicationFactory f, Guid owner, string name, byte[] content, string mime)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = await WriteBlobAsync(f, db, content);
        var file = new FileItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            BlobObjectId = blob.Id,
            Name = name,
            MimeType = mime,
            SizeBytes = content.Length,
            CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
            EffectiveDateTakenSource = "uploaded",
        };
        db.FileItems.Add(file);
        await db.SaveChangesAsync();
        return file;
    }

    private static async Task<BlobObject> WriteBlobAsync(
        SqliteWebApplicationFactory f, AppDbContext db, byte[] content)
    {
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var key = $"objects/{sha[..2]}/{sha[2..4]}/{sha}";
        var path = Path.Combine(f.StorageRoot, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);

        var blob = await db.BlobObjects.FirstOrDefaultAsync(b => b.Sha256 == sha);
        if (blob is not null) return blob;
        blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = sha,
            StorageKey = key,
            SizeBytes = content.Length,
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.BlobObjects.Add(blob);
        await db.SaveChangesAsync();
        return blob;
    }

    private static async Task ReplaceContentAsync(SqliteWebApplicationFactory f, Guid fileId, byte[] content)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = await WriteBlobAsync(f, db, content);
        var file = await db.FileItems.SingleAsync(x => x.Id == fileId);
        file.BlobObjectId = blob.Id;
        file.SizeBytes = content.Length;
        file.MimeType = "text/markdown";
        await db.SaveChangesAsync();
    }

    private static async Task SetRecordedSizeAsync(SqliteWebApplicationFactory f, Guid fileId, long size)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.SingleAsync(x => x.Id == fileId);
        file.SizeBytes = size;
        await db.SaveChangesAsync();
    }

    private static string DeleteStoredBytes(SqliteWebApplicationFactory f, Guid blobId, out byte[] original)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = db.BlobObjects.Where(b => b.Id == blobId).Select(b => b.StorageKey).Single();
        var path = Path.Combine(f.StorageRoot, key.Replace('/', Path.DirectorySeparatorChar));
        original = File.ReadAllBytes(path);
        File.Delete(path);
        return path;
    }

    private static async Task<DocumentSourceState?> StateAsync(
        SqliteWebApplicationFactory f, Guid sourceId, string phase, string kind = DocumentSourceKinds.File)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DocumentSourceStates.AsNoTracking()
            .SingleOrDefaultAsync(s => s.SourceKind == kind && s.SourceId == sourceId && s.Phase == phase);
    }

    private static Task<int> CompletedAsync(SqliteWebApplicationFactory f, string phase)
        => CountAsync(f, db => db.DocumentSourceStates.CountAsync(
            s => s.Phase == phase && s.Status == DocumentSourceStatuses.Completed));

    private static async Task<int> CountAsync(SqliteWebApplicationFactory f, Func<AppDbContext, Task<int>> count)
    {
        using var scope = f.Services.CreateScope();
        return await count(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task<string> SnapshotAsync(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var texts = await db.DocumentTexts.OrderBy(d => d.Id).Select(d => d.Id + d.TextHash!).ToListAsync();
        var chunks = await db.DocumentChunks.OrderBy(c => c.Id).Select(c => c.Id.ToString()).ToListAsync();
        var vectors = await db.DocumentChunkEmbeddings.OrderBy(e => e.Id).Select(e => e.Id.ToString()).ToListAsync();
        var states = await db.DocumentSourceStates.OrderBy(s => s.Id)
            .Select(s => s.Id + s.Status + s.UpdatedAt.Ticks).ToListAsync();
        return string.Join('|', texts.Concat(chunks).Concat(vectors).Concat(states));
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

    /// A PDF whose trailer names a Standard security handler with an owner and
    /// user password nobody knows — what an encrypted PDF looks like to a
    /// reader without the credential.
    private static byte[] EncryptedPdf()
    {
        var o = new string('A', 64);
        var u = new string('B', 64);
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n",
            $"4 0 obj\n<< /Filter /Standard /V 1 /R 2 /O <{o}> /U <{u}> /P -44 >>\nendobj\n",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var obj in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append(obj);
        }
        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append($"{offset:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R /Encrypt 4 0 R /ID [<0123456789ABCDEF0123456789ABCDEF> <0123456789ABCDEF0123456789ABCDEF>] >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// A test-only source kind that cancels the run while a source is loading.
    private sealed class CancellingSource : IOwnerDocumentSource
    {
        public const string KindName = "test-cancel";
        public static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

        public Action? OnLoad { get; set; }

        public string Kind => KindName;

        public IQueryable<Guid> EligibleSourceIds(AppDbContext db, Guid ownerUserId)
            => db.Users.Where(_ => false).Select(u => u.Id);

        public Task<IReadOnlyList<DocumentSourceCandidate>> ListCandidatesAsync(
            Guid ownerUserId, Guid? afterSourceId, int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DocumentSourceCandidate>>(
                afterSourceId is null ? new[] { new DocumentSourceCandidate(Id, "rev-1", "n") } : []);

        public Task<DocumentSourceCandidate?> GetCandidateAsync(
            Guid ownerUserId, Guid sourceId, CancellationToken cancellationToken = default)
            => Task.FromResult<DocumentSourceCandidate?>(new DocumentSourceCandidate(Id, "rev-1", "n"));

        public Task<DocumentSourceLoad> LoadAsync(
            Guid ownerUserId, DocumentSourceCandidate candidate, CancellationToken cancellationToken = default)
        {
            if (OnLoad is { } hook)
            {
                hook();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return Task.FromResult(SectionsSource.Load());
        }
    }

    /// A test-only kind that hands over already-normalized sections, the way
    /// an archived email will.
    private sealed class SectionsSource : IOwnerDocumentSource
    {
        public const string KindName = "test-sections";
        public static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

        public string Kind => KindName;

        public IQueryable<Guid> EligibleSourceIds(AppDbContext db, Guid ownerUserId)
            => db.Users.Where(_ => false).Select(u => u.Id);

        public Task<IReadOnlyList<DocumentSourceCandidate>> ListCandidatesAsync(
            Guid ownerUserId, Guid? afterSourceId, int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DocumentSourceCandidate>>(
                afterSourceId is null ? new[] { new DocumentSourceCandidate(Id, "rev-1", "n") } : []);

        public Task<DocumentSourceCandidate?> GetCandidateAsync(
            Guid ownerUserId, Guid sourceId, CancellationToken cancellationToken = default)
            => Task.FromResult<DocumentSourceCandidate?>(new DocumentSourceCandidate(Id, "rev-1", "n"));

        public Task<DocumentSourceLoad> LoadAsync(
            Guid ownerUserId, DocumentSourceCandidate candidate, CancellationToken cancellationToken = default)
            => Task.FromResult(Load());

        public static DocumentSourceLoad Load() => DocumentSourceLoad.FromSections(
            new[]
            {
                new ExtractedDocumentBlock(1, DocumentBlockKinds.Heading,
                    "Oggetto: manutenzione della caldaia programmata per lunedì mattina",
                    "Oggetto", new DocumentLocator(DocumentLocatorKinds.Section, 1, "header")),
                new ExtractedDocumentBlock(2, DocumentBlockKinds.Body,
                    "Il tecnico passerà alle nove per pulire il filtro e controllare la pressione.",
                    null, new DocumentLocator(DocumentLocatorKinds.Section, 2, "body")),
            },
            source: "test-sections",
            profileKey: "doc-test-sections-v1");
    }
}
