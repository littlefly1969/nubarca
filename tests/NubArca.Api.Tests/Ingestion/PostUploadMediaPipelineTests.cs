using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Ingestion;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Ingestion;

// Post-upload media pipeline: a direct UI upload must enter the SAME background
// media pipeline as bulk/import — bounded, idempotent, vault-safe — without
// running medium preview / AI inference inline in the request.
public sealed class PostUploadMediaPipelineTests
{
    private static readonly string[] ForbiddenNeedles =
    {
        "StorageKey", "storageKey", "storage_key", "BlobObjectId", "blobObjectId",
        "Sha256", "sha256", "/storage/objects/", "PasswordHash", "TokenHash", "PayloadJson",
    };

    private static SqliteWebApplicationFactory NewFactory(params (string Key, string Value)[] settings)
    {
        var dict = settings.ToDictionary(s => s.Key, s => (string?)s.Value);
        var factory = new SqliteWebApplicationFactory(dict, poolHost: true);
        factory.EnsureDatabaseCreated();
        return factory;
    }

    private static SqliteWebApplicationFactory AiEnabledFactory()
        => NewFactory(("Ai:Enabled", "true"), ("Ai:ImageEmbeddingsEnabled", "true"));

    private static SqliteWebApplicationFactory FacesEnabledFactory()
        => NewFactory(
            ("Ai:Enabled", "true"),
            ("Ai:FaceDetectionEnabled", "true"),
            ("Ai:FaceEmbeddingsEnabled", "true"));

    private static async Task SeedDeterministicProfilesAsync(SqliteWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
        await registry.SeedDeterministicProfilesAsync();
    }

    private static byte[] Png(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static async Task<Guid> UploadPngAsync(HttpClient client, string name, int dim, byte[]? exact = null)
    {
        var part = new ByteArrayContent(exact ?? Png(dim));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var resp = await client.PostAsync("/api/files",
            new MultipartFormDataContent { { part, "file", name } });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<FileSummary>())!.Id;
    }

    private static async Task<Guid> UploadTextAsync(HttpClient client, string name)
    {
        var part = new ByteArrayContent("hello world"u8.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        var resp = await client.PostAsync("/api/files",
            new MultipartFormDataContent { { part, "file", name } });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<FileSummary>())!.Id;
    }

    private static async Task<List<(string Type, string? Key)>> JobsAsync(SqliteWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BackgroundJobs.AsNoTracking()
            .OrderBy(j => j.CreatedAt)
            .Select(j => new ValueTuple<string, string?>(j.Type, j.IdempotencyKey))
            .ToListAsync();
    }

    private static async Task RunAllJobsAsync(SqliteWebApplicationFactory factory)
    {
        for (var i = 0; i < 100; i++)
        {
            using var scope = factory.Services.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            if (await processor.ProcessAvailableAsync(20) == 0) break;
        }
    }

    private static async Task<bool> HasThumbAsync(SqliteWebApplicationFactory factory, Guid fileId, string size)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FileThumbnails.AsNoTracking().AnyAsync(t => t.FileItemId == fileId && t.Size == size);
    }

    // 1. Direct image upload schedules bounded post-ingestion media work.
    [Fact]
    public async Task Direct_Image_Upload_Schedules_Scoped_Media_Work()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "p.png", 24);

        var jobs = await JobsAsync(factory);
        Assert.Contains(jobs, j =>
            j.Type == JobTypes.MediaDerivativesBackfill
            && j.Key == $"postingest:derivatives:{fileId:N}");
    }

    // 2. Upload does NOT synchronously produce the medium preview or an embedding.
    [Fact]
    public async Task Direct_Upload_Does_Not_Run_Medium_Or_Ai_Inline()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "p.png", 24);

        // Small is produced inline (fast, grid thumbnail); medium is deferred.
        Assert.True(await HasThumbAsync(factory, fileId, ThumbnailSizes.Small));
        Assert.False(await HasThumbAsync(factory, fileId, ThumbnailSizes.Medium));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.BlobEmbeddings.CountAsync());
    }

    // 3. After the scoped job runs, small + medium both exist — no need to open.
    [Fact]
    public async Task Post_Ingest_Job_Produces_Small_And_Medium()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "p.png", 24);

        await RunAllJobsAsync(factory);

        Assert.True(await HasThumbAsync(factory, fileId, ThumbnailSizes.Small));
        Assert.True(await HasThumbAsync(factory, fileId, ThumbnailSizes.Medium));
    }

    // 4. Metadata extraction is scheduled when the blob's metadata is pending.
    [Fact]
    public async Task Metadata_Scheduled_When_Blob_Metadata_Pending()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "p.png", 24);

        Guid blobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            blobId = await db.FileItems.AsNoTracking().Where(f => f.Id == fileId)
                .Select(f => f.BlobObjectId).SingleAsync();
            // Simulate a blob left pending by a deferred import.
            await db.BlobMetadata.Where(m => m.BlobObjectId == blobId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ExtractionStatus, MetadataStatuses.Pending));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var pipeline = scope.ServiceProvider.GetRequiredService<IPostIngestionMediaPipelineService>();
            var result = await pipeline.OnFileIngestedAsync(ownerId, fileId);
            Assert.True(result.MetadataScheduled);
        }

        Assert.Contains(await JobsAsync(factory), j =>
            j.Type == JobTypes.MetadataEmbeddedBackfill && j.Key == $"postingest:metadata:{blobId:N}");
    }

    // 5. AI embedding is scheduled AND produced when AI is enabled + profile usable.
    [Fact]
    public async Task Ai_Embedding_Scheduled_And_Produced_When_Enabled()
    {
        using var factory = AiEnabledFactory();
        await SeedDeterministicProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "p.png", 24);

        Assert.Contains(await JobsAsync(factory), j => j.Type == JobTypes.AiPhotosEmbeddingsBackfill);

        await RunAllJobsAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blobId = await db.FileItems.AsNoTracking().Where(f => f.Id == fileId)
            .Select(f => f.BlobObjectId).SingleAsync();
        Assert.True(await db.BlobEmbeddings.AsNoTracking().AnyAsync(e => e.BlobObjectId == blobId));
    }

    // 6. Dedup against a fully-processed blob does not re-enqueue blob-level work.
    [Fact]
    public async Task Dedup_Against_Completed_Blob_Skips_Blob_Level_Work()
    {
        using var factory = AiEnabledFactory();
        await SeedDeterministicProfilesAsync(factory);
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();

        var bytes = Png(24);
        await UploadPngAsync(client, "first.png", 24, exact: bytes);
        await RunAllJobsAsync(factory); // metadata + derivatives + embedding all done for the blob

        // Same bytes, different name → dedup to the SAME blob, NEW FileItem.
        var secondId = await UploadPngAsync(client, "second.png", 24, exact: bytes);

        using var scope = factory.Services.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IPostIngestionMediaPipelineService>();
        var result = await pipeline.OnFileIngestedAsync(ownerId, secondId);

        // Blob-level work already complete → NOT re-scheduled. Derivatives for the
        // new FileItem (its own medium) may still be scheduled — that is necessary,
        // not duplicate.
        Assert.False(result.MetadataScheduled);
        Assert.False(result.AiEmbeddingScheduled);
    }

    // 7. Dedup against a blob whose metadata is still pending DOES schedule it.
    [Fact]
    public async Task Dedup_Against_Pending_Metadata_Schedules_Metadata()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var bytes = Png(24);
        var firstId = await UploadPngAsync(client, "first.png", 24, exact: bytes);

        Guid blobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            blobId = await db.FileItems.AsNoTracking().Where(f => f.Id == firstId)
                .Select(f => f.BlobObjectId).SingleAsync();
            await db.BlobMetadata.Where(m => m.BlobObjectId == blobId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ExtractionStatus, MetadataStatuses.Pending));
        }

        var secondId = await UploadPngAsync(client, "second.png", 24, exact: bytes);
        using (var scope = factory.Services.CreateScope())
        {
            var pipeline = scope.ServiceProvider.GetRequiredService<IPostIngestionMediaPipelineService>();
            var result = await pipeline.OnFileIngestedAsync(ownerId, secondId);
            Assert.True(result.MetadataScheduled);
        }
    }

    // 8. Private Vault content schedules NO metadata/derivatives/AI work.
    [Fact]
    public async Task Private_Vault_File_Schedules_Nothing()
    {
        using var factory = AiEnabledFactory();
        await SeedDeterministicProfilesAsync(factory);
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "secret.png", 24);

        // Move the file into a vault (direct DB: create a vault row + set the FK).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var vault = new PrivateVault
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerId,
                DisplayName = "Private",
                PasswordHash = "x",
                EncryptionMode = PrivateVaultEncryptionModes.None,
                CreatedAt = DateTime.UtcNow,
            };
            db.PrivateVaults.Add(vault);
            await db.SaveChangesAsync();
            await db.FileItems.IgnoreQueryFilters().Where(f => f.Id == fileId)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.PrivateVaultId, vault.Id));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var pipeline = scope.ServiceProvider.GetRequiredService<IPostIngestionMediaPipelineService>();
            var result = await pipeline.OnFileIngestedAsync(ownerId, fileId);
            Assert.False(result.MetadataScheduled);
            Assert.False(result.DerivativesScheduled);
            Assert.False(result.AiEmbeddingScheduled);
            Assert.Equal("skipped-vault-or-missing", result.Outcome);
        }
    }

    // 8b. Even if a scoped AI job is somehow enqueued for a now-vault blob, the
    // scoped candidate re-checks eligibility and indexes nothing.
    [Fact]
    public async Task Scoped_Ai_Backfill_Does_Not_Index_Vault_Blob()
    {
        using var factory = AiEnabledFactory();
        await SeedDeterministicProfilesAsync(factory);
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "secret.png", 24);

        Guid blobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            blobId = await db.FileItems.AsNoTracking().Where(f => f.Id == fileId)
                .Select(f => f.BlobObjectId).SingleAsync();
            var vault = new PrivateVault
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerId,
                DisplayName = "Private",
                PasswordHash = "x",
                EncryptionMode = PrivateVaultEncryptionModes.None,
                CreatedAt = DateTime.UtcNow,
            };
            db.PrivateVaults.Add(vault);
            await db.SaveChangesAsync();
            await db.FileItems.IgnoreQueryFilters().Where(f => f.Id == fileId)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.PrivateVaultId, vault.Id));

            // Directly enqueue a scoped AI backfill for the (now vault) blob.
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            await queue.EnqueueAsync(JobTypes.AiPhotosEmbeddingsBackfill,
                new NubArca.Api.Ai.Jobs.AiBackfillJobPayload(BlobObjectId: blobId));
        }

        await RunAllJobsAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.BlobEmbeddings.CountAsync());
        }
    }

    // 9. Regression: the GLOBAL (null-scope) derivatives backfill still processes
    // multiple files (bulk/import path unchanged).
    [Fact]
    public async Task Global_Derivatives_Backfill_Still_Processes_All()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var a = await UploadPngAsync(client, "a.png", 20);
        var b = await UploadPngAsync(client, "b.png", 22);

        using (var scope = factory.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            // Null scope = the global backfill.
            await queue.EnqueueAsync(JobTypes.MediaDerivativesBackfill,
                new MediaDerivativesBackfillJobPayload());
        }
        await RunAllJobsAsync(factory);

        foreach (var id in new[] { a, b })
        {
            Assert.True(await HasThumbAsync(factory, id, ThumbnailSizes.Small));
            Assert.True(await HasThumbAsync(factory, id, ThumbnailSizes.Medium));
        }
    }

    // 10. Idempotency: repeated scheduling for the same file coalesces to one job.
    [Fact]
    public async Task Idempotent_Scheduling_Prevents_Duplicate_Jobs()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "p.png", 24); // upload runs the pipeline once

        using (var scope = factory.Services.CreateScope())
        {
            var pipeline = scope.ServiceProvider.GetRequiredService<IPostIngestionMediaPipelineService>();
            await pipeline.OnFileIngestedAsync(ownerId, fileId);
            await pipeline.OnFileIngestedAsync(ownerId, fileId);
        }

        var derivativeJobs = (await JobsAsync(factory))
            .Count(j => j.Key == $"postingest:derivatives:{fileId:N}");
        Assert.Equal(1, derivativeJobs);
    }

    // 3'/non-media: a non-image/non-video upload schedules nothing.
    [Fact]
    public async Task Non_Media_Upload_Schedules_Nothing()
    {
        using var factory = NewFactory();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadTextAsync(client, "notes.txt");

        using var scope = factory.Services.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IPostIngestionMediaPipelineService>();
        var result = await pipeline.OnFileIngestedAsync(ownerId, fileId);
        Assert.Equal("non-media", result.Outcome);
        Assert.False(result.DerivativesScheduled);
        Assert.False(result.MetadataScheduled);
        Assert.False(result.AiEmbeddingScheduled);
    }

    [Fact]
    public async Task Face_Indexing_Is_Targeted_And_Chains_Embeddings()
    {
        using var factory = FacesEnabledFactory();
        await SeedDeterministicProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "face.png", 48);

        Guid blobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            blobId = await db.FileItems.Where(f => f.Id == fileId).Select(f => f.BlobObjectId).SingleAsync();
            var detect = await db.BackgroundJobs.SingleAsync(j => j.Type == JobTypes.AiFacesDetectBackfill);
            Assert.Equal(JobScheduling.PostIngestFaces, detect.Priority);
            Assert.Contains(blobId.ToString(), detect.PayloadJson, StringComparison.OrdinalIgnoreCase);
        }

        await RunAllJobsAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.FaceDetections.AnyAsync(d => d.BlobObjectId == blobId));
            Assert.True(await db.FaceEmbeddings.AnyAsync(e => e.EmbeddingStatus == "completed"));
            var embedJob = await db.BackgroundJobs.SingleAsync(j => j.Type == JobTypes.AiFacesEmbeddingsBackfill);
            Assert.Equal(JobScheduling.PostIngestFaces, embedJob.Priority);
        }
    }

    [Fact]
    public async Task Party_Pipeline_Prioritizes_Preview_Then_Faces()
    {
        using var factory = FacesEnabledFactory();
        await SeedDeterministicProfilesAsync(factory);
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client, "party.png", 48);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BackgroundJobs.ExecuteDeleteAsync();
            var pipeline = scope.ServiceProvider.GetRequiredService<IPostIngestionMediaPipelineService>();
            var result = await pipeline.OnPartyFileIngestedAsync(ownerId, fileId);
            Assert.True(result.DerivativesScheduled);
            Assert.True(result.FaceIndexScheduled);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var preview = await db.BackgroundJobs.SingleAsync(j => j.Type == JobTypes.MediaDerivativesBackfill);
            var faces = await db.BackgroundJobs.SingleAsync(j => j.Type == JobTypes.AiFacesDetectBackfill);
            Assert.Equal(JobScheduling.PartyPreview, preview.Priority);
            Assert.Equal(JobScheduling.PartyFaces, faces.Priority);
            Assert.True(preview.Priority < faces.Priority);
        }
    }

    // 12. Forbidden-needle: user-facing surfaces expose no storage internals.
    [Fact]
    public async Task Upload_Response_Exposes_No_Internals()
    {
        using var factory = NewFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var part = new ByteArrayContent(Png(24));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var resp = await client.PostAsync("/api/files",
            new MultipartFormDataContent { { part, "file", "p.png" } });
        resp.EnsureSuccessStatusCode();
        var raw = await resp.Content.ReadAsStringAsync();
        foreach (var needle in ForbiddenNeedles)
        {
            Assert.DoesNotContain(needle, raw, StringComparison.Ordinal);
        }
    }
}
