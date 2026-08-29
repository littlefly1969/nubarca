using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// REAL PostgreSQL 17 WITH pgvector, via Testcontainers.
//
// The unit tests run on SQLite, where the accelerator reports itself
// unavailable — which is the right place to prove that the canonical bytes are
// the truth and the accelerator is optional. This is the other half, and it
// asks a question SQLite cannot: when the accelerator IS present and the
// ranking happens inside the database, is the owner boundary still applied
// BEFORE the limit?
//
// The fixture is deliberately hostile. The asker owns ONE unit that merely
// resembles the query; every other owner holds units that are EXACTLY it. A
// filter-after-search implementation returns nothing for the asker, and a
// missing filter returns somebody else's pages. Both are visible here and
// invisible on SQLite.
[Collection(PgVectorIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class DocumentVisualPgIntegrationTests : IAsyncLifetime
{
    private const int Dimension = DocumentVisualProfiles.DenseDimension;

    private readonly PgVectorContainerFixture _fixture;

    public DocumentVisualPgIntegrationTests(PgVectorContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- the schema ----------------------------------------------------------

    [SkippableFact]
    public async Task The_Migration_Creates_The_Visual_Tables_And_The_Accelerator()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var table in new[]
                 {
                     "document_visual_indexes",
                     "document_visual_units",
                     "document_visual_embeddings",
                     "document_visual_embedding_vectors_1152",
                 })
        {
            Assert.True(await TableExistsAsync(db, table), table);
        }

        // AND THE SLICE-4 TABLES ARE UNTOUCHED. This is an ADDITIVE migration:
        // an installation upgrading from Slice 4 keeps every extraction, chunk
        // and embedding it had.
        foreach (var table in new[] { "document_texts", "document_chunks", "document_chunk_embeddings" })
        {
            Assert.True(await TableExistsAsync(db, table), table);
        }
    }

    [SkippableFact]
    public async Task There_Is_No_Ann_Index_On_The_Visual_Accelerator()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        // AN ABSENCE, ASSERTED. An HNSW index here would give the planner a way
        // to rank over every owner's vectors and apply the owner predicate to
        // whatever the traversal surfaced — which is not an owner-prefiltered
        // search, and fails silently by returning fewer and worse rows.
        //
        // With no ANN index there is exactly one plan: restrict through the
        // joins, then rank the survivors exactly.
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var indexes = await ScalarListAsync(db, """
            SELECT indexdef FROM pg_indexes
            WHERE tablename = 'document_visual_embedding_vectors_1152';
            """);

        Assert.NotEmpty(indexes);
        Assert.DoesNotContain(indexes, i => i.Contains("hnsw", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(indexes, i => i.Contains("ivfflat", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task A_Completed_Index_With_No_Units_Is_Refused_By_PostgreSQL()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seeded = await SeedAsync(scope.ServiceProvider);

        db.DocumentVisualIndexes.Add(new DocumentVisualIndex
        {
            Id = Guid.NewGuid(),
            FileItemId = seeded.TargetFileId,
            OwnerUserId = seeded.OwnerA,
            SourceBlobObjectId = seeded.TargetBlobId,
            RenderProfileKey = DocumentVisualRenderProfiles.LibreOfficePdf,
            EmbeddingProfileId = seeded.ProfileId,
            Status = AiArtifactStatuses.Completed,
            UnitCount = 0,
            CreatedAt = DateTime.UtcNow,
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }

    // ---- the accelerator's semantics ------------------------------------------

    [SkippableFact]
    public async Task Owner_And_Live_Eligibility_Are_Applied_Before_The_Limit()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var seeded = await SeedAsync(scope.ServiceProvider);

        var accelerator = scope.ServiceProvider
            .GetRequiredService<DocumentVisualVectorIndexService>();
        Assert.True(await accelerator.IsBackendAvailableAsync(Dimension));

        var synced = await accelerator.SyncAsync(seeded.ProfileId);
        Assert.True(synced > 0);

        var renderKeys = scope.ServiceProvider
            .GetRequiredService<DocumentVisualRenderers>().ActiveRenderProfileKeys;

        // A LIMIT SMALLER THAN THE DISTRACTOR SET. Every distractor is a
        // perfect match; the asker's own unit is not. Post-filtering returns
        // nothing.
        var hits = await accelerator.SearchAsync(
            seeded.ProfileId, seeded.OwnerA, Unit(0), renderKeys, take: 3);

        Assert.NotNull(hits);
        Assert.NotEmpty(hits!);
        Assert.All(hits!, h => Assert.Equal(seeded.TargetFileId, h.FileItemId));
    }

    [SkippableFact]
    public async Task Stale_Rows_Are_Unreachable_Through_The_Accelerator_Too()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seeded = await SeedAsync(scope.ServiceProvider);

        var accelerator = scope.ServiceProvider
            .GetRequiredService<DocumentVisualVectorIndexService>();
        await accelerator.SyncAsync(seeded.ProfileId);

        var renderKeys = scope.ServiceProvider
            .GetRequiredService<DocumentVisualRenderers>().ActiveRenderProfileKeys;

        Assert.NotEmpty((await accelerator.SearchAsync(
            seeded.ProfileId, seeded.OwnerA, Unit(0), renderKeys, take: 5))!);

        // Delete the file NOW, leaving every derived row and every accelerator
        // row in place. The next question sees nothing, with no sweeper.
        var file = await db.FileItems.SingleAsync(f => f.Id == seeded.TargetFileId);
        file.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var after = await accelerator.SearchAsync(
            seeded.ProfileId, seeded.OwnerA, Unit(0), renderKeys, take: 5);

        Assert.NotNull(after);
        Assert.Empty(after!);
        // The vector rows are still there.
        Assert.True(await accelerator.CountIndexedAsync(seeded.ProfileId) > 0);
    }

    [SkippableFact]
    public async Task A_Superseded_Render_Profile_Is_Unreachable_Through_The_Accelerator()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var seeded = await SeedAsync(scope.ServiceProvider);

        var accelerator = scope.ServiceProvider
            .GetRequiredService<DocumentVisualVectorIndexService>();
        await accelerator.SyncAsync(seeded.ProfileId);

        var hits = await accelerator.SearchAsync(
            seeded.ProfileId, seeded.OwnerA, Unit(0),
            new[] { "pdfium-page-render-v0" }, take: 5);

        Assert.NotNull(hits);
        Assert.Empty(hits!);
    }

    [SkippableFact]
    public async Task The_Accelerated_And_Exact_Paths_Agree_On_The_Same_Fixture()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        // TWO SPELLINGS OF ONE RULE. The accelerator restates the eligibility
        // clauses in SQL because it does not go through EF; two spellings drift
        // unless something compares them, so the same adversarial fixture is run
        // through both and the answers must match.
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var seeded = await SeedAsync(scope.ServiceProvider);

        var accelerator = scope.ServiceProvider
            .GetRequiredService<DocumentVisualVectorIndexService>();
        await accelerator.SyncAsync(seeded.ProfileId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var renderKeys = scope.ServiceProvider
            .GetRequiredService<DocumentVisualRenderers>().ActiveRenderProfileKeys;

        var accelerated = (await accelerator.SearchAsync(
                seeded.ProfileId, seeded.OwnerA, Unit(0), renderKeys, take: 50))!
            .Select(h => h.VisualUnitId).OrderBy(id => id).ToList();

        var exact = await OwnerDocumentVisualEligibility.EligibleUnits(
                db.DocumentVisualUnits.AsNoTracking(),
                db.DocumentVisualIndexes.AsNoTracking(),
                db.FileItems.AsNoTracking(),
                seeded.OwnerA, seeded.ProfileId, renderKeys)
            .Select(r => r.Unit.Id)
            .ToListAsync();

        Assert.Equal(accelerated, exact.OrderBy(id => id).ToList());
    }

    [SkippableFact]
    public async Task The_Accelerator_Is_Rebuildable_From_The_Canonical_Bytes()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seeded = await SeedAsync(scope.ServiceProvider);

        var accelerator = scope.ServiceProvider
            .GetRequiredService<DocumentVisualVectorIndexService>();
        var first = await accelerator.SyncAsync(seeded.ProfileId);
        Assert.True(first > 0);

        // Idempotent: a second run adds nothing.
        Assert.Equal(0, await accelerator.SyncAsync(seeded.ProfileId));

        // Dropped and rebuilt: the canonical bytes are the truth, and this table
        // is derived from them.
        var indexed = await accelerator.CountIndexedAsync(seeded.ProfileId);
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE document_visual_embedding_vectors_1152;");
        Assert.Equal(0, await accelerator.CountIndexedAsync(seeded.ProfileId));

        Assert.Equal(indexed, await accelerator.SyncAsync(seeded.ProfileId));
        Assert.Equal(indexed, await accelerator.CountIndexedAsync(seeded.ProfileId));
    }

    // ---- the Slice-4 upgrade ---------------------------------------------------

    [SkippableFact]
    public async Task Private_Text_Retrieval_Works_With_Zero_Visual_Rows()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        // THE UPGRADE PATH. An installation that migrates to this slice and
        // never enables visual retrieval has three empty tables and behaves
        // exactly as it did — asserted by reading the corpus the private
        // Assistant answers from.
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var seeded = await SeedAsync(scope.ServiceProvider, withVisualRows: false);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.DocumentVisualIndexes.CountAsync());

        var corpus = scope.ServiceProvider
            .GetRequiredService<NubArca.Api.Rag.Retrieval.OwnerDocumentCorpusSource>();
        var stats = await corpus.GetStatsAsync(seeded.OwnerA);

        Assert.True(stats.Chunks > 0);
        Assert.True(stats.Documents > 0);
    }

    // ---- fixture -----------------------------------------------------------------

    private PostgresWebApplicationFactory Factory()
        => new(
            _fixture.ConnectionString!,
            new Dictionary<string, string?>
            {
                ["Ai:Enabled"] = "true",
                ["Ai:DocumentVisual:Enabled"] = "true",
            });

    private sealed record Seeded(
        Guid OwnerA, Guid ProfileId, Guid TargetFileId, Guid TargetBlobId);

    /// Owner A holds ONE unit that merely resembles the query; three other
    /// owners hold units that are EXACTLY it, and owner A also holds a vaulted
    /// and a deleted document whose units are exact matches too. Every one of
    /// them keeps its rows.
    private static async Task<Seeded> SeedAsync(
        IServiceProvider services, bool withVisualRows = true)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var serializer = services.GetRequiredService<IAiVectorSerializer>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var visualModel = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = $"document-visual-{suffix}",
            Provider = AiProviders.Onnx,
            Capability = AiCapabilities.DocumentVisualEmbedding,
            Modality = AiModalities.Multimodal,
            Dimension = Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var visualProfile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = $"document-visual-profile-{suffix}",
            AiModelId = visualModel.Id,
            Capability = AiCapabilities.DocumentVisualEmbedding,
            Modality = AiModalities.Multimodal,
            Dimension = Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var extractionModel = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = $"extract-{suffix}",
            Provider = AiProviders.None,
            Capability = AiCapabilities.DocumentExtraction,
            Modality = AiModalities.Document,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var extractionProfile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = $"extract-profile-{suffix}",
            AiModelId = extractionModel.Id,
            Capability = AiCapabilities.DocumentExtraction,
            Modality = AiModalities.Document,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.AddRange(visualModel, extractionModel);
        db.AiProfiles.AddRange(visualProfile, extractionProfile);

        var ownerA = AddUser(db, $"a-{suffix}");
        var vault = new PrivateVault
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerA,
            CreatedAt = DateTime.UtcNow,
        };
        db.PrivateVaults.Add(vault);
        await db.SaveChangesAsync();

        var (targetFile, targetBlob) = AddFile(db, ownerA, $"target-{suffix}.pdf");
        AddText(db, extractionProfile.Id, ownerA, targetFile, targetBlob);
        if (withVisualRows)
        {
            // Merely RESEMBLES the query.
            AddVisual(db, serializer, visualProfile.Id, ownerA, targetFile, targetBlob, Unit(0, 0.7f));
        }

        if (withVisualRows)
        {
            // Three other owners, each holding a PERFECT match.
            for (var i = 0; i < 3; i++)
            {
                var other = AddUser(db, $"b{i}-{suffix}");
                await db.SaveChangesAsync();
                var (file, blob) = AddFile(db, other, $"theirs-{i}-{suffix}.pdf");
                AddVisual(db, serializer, visualProfile.Id, other, file, blob, Unit(0));
            }

            // Owner A's own vaulted and deleted documents, also perfect matches.
            var (vaulted, vaultedBlob) = AddFile(db, ownerA, $"vault-{suffix}.pdf", vaultId: vault.Id);
            AddVisual(db, serializer, visualProfile.Id, ownerA, vaulted, vaultedBlob, Unit(0));

            var (deleted, deletedBlob) = AddFile(db, ownerA, $"deleted-{suffix}.pdf", deleted: true);
            AddVisual(db, serializer, visualProfile.Id, ownerA, deleted, deletedBlob, Unit(0));

            // And one of owner A's own whose SOURCE BLOB no longer matches the
            // file — the replaced-content case, a perfect match, unreachable.
            var (stale, staleBlob) = AddFile(db, ownerA, $"stale-{suffix}.pdf");
            AddVisual(
                db, serializer, visualProfile.Id, ownerA, stale, Guid.NewGuid(), Unit(0),
                blobIsCurrent: false);
        }

        await db.SaveChangesAsync();
        return new Seeded(ownerA, visualProfile.Id, targetFile, targetBlob);
    }

    private static Guid AddUser(AppDbContext db, string tag)
    {
        var id = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = id,
            Email = $"{tag}@example.invalid",
            DisplayName = tag,
            CreatedAt = DateTime.UtcNow,
        });
        return id;
    }

    private static (Guid FileId, Guid BlobId) AddFile(
        AppDbContext db, Guid owner, string name, Guid? vaultId = null, bool deleted = false)
    {
        var sha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = sha,
            StorageKey = $"objects/{sha[..2]}/{sha[2..4]}/{sha}",
            SizeBytes = 1024,
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
            MimeType = "application/pdf",
            SizeBytes = 1024,
            PrivateVaultId = vaultId,
            DeletedAt = deleted ? DateTime.UtcNow : null,
            MediaLibraryState = MediaLibraryState.Active,
            CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
            EffectiveDateTakenSource = "uploaded",
        };
        db.FileItems.Add(file);
        return (file.Id, blob.Id);
    }

    private static void AddText(
        AppDbContext db, Guid profileId, Guid owner, Guid fileId, Guid blobId)
    {
        var document = new DocumentText
        {
            Id = Guid.NewGuid(),
            FileItemId = fileId,
            OwnerUserId = owner,
            ProfileId = profileId,
            SourceBlobObjectId = blobId,
            Source = DocumentTextSources.Pdf,
            Status = AiArtifactStatuses.Completed,
            IsCurrent = true,
            ChunkFormatVersion = OwnerDocumentChunkFormat.Current,
            Text = "il filtro va pulito ogni sei mesi",
            CharCount = 33,
            CreatedAt = DateTime.UtcNow,
        };
        db.DocumentTexts.Add(document);
        db.DocumentChunks.Add(new DocumentChunk
        {
            Id = Guid.NewGuid(),
            DocumentTextId = document.Id,
            OwnerUserId = owner,
            ProfileId = profileId,
            Ordinal = 0,
            Heading = "Manutenzione",
            Text = "il filtro va pulito ogni sei mesi",
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static void AddVisual(
        AppDbContext db, IAiVectorSerializer serializer, Guid profileId, Guid owner,
        Guid fileId, Guid blobId, float[] vector, bool blobIsCurrent = true)
    {
        var index = new DocumentVisualIndex
        {
            Id = Guid.NewGuid(),
            FileItemId = fileId,
            OwnerUserId = owner,
            SourceBlobObjectId = blobIsCurrent ? blobId : Guid.NewGuid(),
            RenderProfileKey = DocumentVisualRenderProfiles.PdfiumPage,
            EmbeddingProfileId = profileId,
            Status = AiArtifactStatuses.Completed,
            UnitCount = 1,
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        };
        db.DocumentVisualIndexes.Add(index);

        var unit = new DocumentVisualUnit
        {
            Id = Guid.NewGuid(),
            DocumentVisualIndexId = index.Id,
            Ordinal = 0,
            RenderKind = DocumentVisualRenderKinds.PdfPage,
            SourceLocatorKind = DocumentLocatorKinds.Page,
            SourcePage = 1,
            Width = 1_240,
            Height = 1_754,
            PixelHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
        };
        db.DocumentVisualUnits.Add(unit);

        db.DocumentVisualEmbeddings.Add(new DocumentVisualEmbedding
        {
            Id = Guid.NewGuid(),
            DocumentVisualUnitId = unit.Id,
            ProfileId = profileId,
            Layout = DocumentVisualEmbeddingLayouts.Dense,
            Dimension = Dimension,
            VectorCount = 1,
            EmbeddingBytes = serializer.Serialize(vector, Dimension),
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static float[] Unit(int axis, float strength = 1f)
    {
        var vector = new float[Dimension];
        vector[axis % Dimension] = strength;
        vector[(axis + 1) % Dimension] = 1f - strength;

        double sum = 0;
        foreach (var value in vector) sum += (double)value * value;
        var norm = Math.Sqrt(sum);
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
        return vector;
    }

    private static async Task<bool> TableExistsAsync(AppDbContext db, string table)
    {
        var rows = await ScalarListAsync(
            db, $"SELECT to_regclass('public.{table}')::text WHERE to_regclass('public.{table}') IS NOT NULL;");
        return rows.Count > 0;
    }

    private static async Task<List<string>> ScalarListAsync(AppDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var results = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }
        return results;
    }
}
