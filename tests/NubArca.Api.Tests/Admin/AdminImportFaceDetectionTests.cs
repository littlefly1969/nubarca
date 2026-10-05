using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Admin;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Admin;

/// <summary>
/// A bulk import's photos are detected for faces — exactly those, never the
/// library's backlog, which stays an explicit global backfill.
/// </summary>
public sealed class AdminImportFaceDetectionTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task An_Import_Detects_Faces_On_Its_Own_Photos_And_Leaves_The_Backlog_Alone()
    {
        // 3 photos and 2 videos imported; 100 older photos still waiting for
        // detection in the same library.
        var root = NewTree(r =>
        {
            for (var i = 0; i < 3; i++) File.WriteAllBytes(Path.Combine(r, $"photo-{i}.png"), Png(48 + i, 48));
            File.WriteAllBytes(Path.Combine(r, "clip-a.mp4"), ImageFixtures.MinimalMp4("isom"));
            File.WriteAllBytes(Path.Combine(r, "clip-b.mp4"), ImageFixtures.MinimalMp4("mp42"));
        });
        using var factory = Factory(root);
        var (_, client) = await AdminAsync(factory);
        var targetId = await factory.SeedUserAsync("target@example.com");
        var backlog = await SeedPendingPhotosAsync(factory, targetId, count: 100);

        var run = await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory, maxJobs: 1); // the import only

        var imported = await ImportedPhotoBlobsAsync(factory, run.ImportRunId);
        Assert.Equal(3, imported.Count);
        var targeted = await DetectionTargetsAsync(factory);
        Assert.Equal(imported.OrderBy(x => x), targeted.OrderBy(x => x));
        Assert.Empty(targeted.Intersect(backlog));

        await DrainJobsAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var detected = await db.BlobAiArtifactStatuses.AsNoTracking()
            .Where(s => s.Capability == AiCapabilities.FaceDetection)
            .Select(s => s.BlobObjectId).ToListAsync();
        Assert.Equal(imported.OrderBy(x => x), detected.OrderBy(x => x));
        // Recognition followed for the same photos, and nothing else.
        var recognised = await db.FaceEmbeddings.AsNoTracking()
            .Where(e => e.EmbeddingStatus == AiArtifactStatuses.Completed)
            .Select(e => db.FaceDetections.Where(d => d.Id == e.FaceDetectionId).Select(d => d.BlobObjectId).First())
            .Distinct().ToListAsync();
        Assert.NotEmpty(recognised);
        Assert.All(recognised, id => Assert.Contains(id, imported));
        Assert.Equal(0, await db.BackgroundJobs.CountAsync(j =>
            (j.Type == JobTypes.AiFacesDetectBackfill || j.Type == JobTypes.AiFacesEmbeddingsBackfill)
            && !j.PayloadJson.Contains("BlobObjectIds")));
    }

    [Fact]
    public async Task An_Import_Without_Photos_Queues_No_Face_Job()
    {
        var root = NewTree(r =>
        {
            File.WriteAllBytes(Path.Combine(r, "clip.mp4"), ImageFixtures.MinimalMp4());
            File.WriteAllText(Path.Combine(r, "notes.txt"), "no faces here");
        });
        using var factory = Factory(root);
        var (_, client) = await AdminAsync(factory);
        var targetId = await factory.SeedUserAsync("target@example.com");

        await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory, maxJobs: 1);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.BackgroundJobs.CountAsync(j => j.Type == JobTypes.AiFacesDetectBackfill));
    }

    [Fact]
    public async Task Chunks_Carry_Only_This_Runs_Photos()
    {
        var root = NewTree(r =>
        {
            for (var i = 0; i < 3; i++) File.WriteAllBytes(Path.Combine(r, $"photo-{i}.png"), Png(40 + i, 40));
        });
        using var factory = Factory(root, batchSize: 2);
        var (_, client) = await AdminAsync(factory);
        var targetId = await factory.SeedUserAsync("target@example.com");
        await SeedPendingPhotosAsync(factory, targetId, count: 5);

        var run = await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory, maxJobs: 1);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var chunks = await db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Type == JobTypes.AiFacesDetectBackfill)
            .OrderBy(j => j.IdempotencyKey)
            .Select(j => new { j.IdempotencyKey, j.PayloadJson })
            .ToListAsync();
        Assert.Equal(
            [$"ai-faces-detect:import:{run.ImportRunId:N}:0", $"ai-faces-detect:import:{run.ImportRunId:N}:1"],
            chunks.Select(c => c.IdempotencyKey));
        var ids = chunks.Select(c => Payload(c.PayloadJson).BlobObjectIds!).ToList();
        Assert.Equal([2, 1], ids.Select(c => c.Count));
        Assert.Equal(
            (await ImportedPhotoBlobsAsync(factory, run.ImportRunId)).OrderBy(x => x),
            ids.SelectMany(c => c).OrderBy(x => x));
    }

    [Fact]
    public async Task Finishing_A_Run_Again_Processes_Nothing_Twice()
    {
        var root = NewTree(r =>
        {
            for (var i = 0; i < 3; i++) File.WriteAllBytes(Path.Combine(r, $"photo-{i}.png"), Png(44 + i, 44));
        });
        using var factory = Factory(root);
        var (_, client) = await AdminAsync(factory);
        var targetId = await factory.SeedUserAsync("target@example.com");
        var run = await StartRunAsync(client, targetId);
        await DrainJobsAsync(factory);
        var before = await FaceRowsAsync(factory);
        Assert.True(before.Detections > 0);

        // Queued again while the first is still queued: one job, not two.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var import = (AdminImportService)scope.ServiceProvider.GetRequiredService<IAdminImportService>();
            await import.EnqueueImportedFaceDetectionAsync(run.ImportRunId, null, default);
            await import.EnqueueImportedFaceDetectionAsync(run.ImportRunId, null, default);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.BackgroundJobs.CountAsync(j =>
                j.Type == JobTypes.AiFacesDetectBackfill && j.Status == JobStatuses.Queued));
        }
        await DrainJobsAsync(factory);

        // Already detected and recognised: the rerun finds nothing to do.
        Assert.Equal(before, await FaceRowsAsync(factory));
    }

    // --- helpers -----------------------------------------------------------

    private SqliteWebApplicationFactory Factory(string root, int batchSize = 100)
    {
        var factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["AdminImport:Enabled"] = "true",
            ["AdminImport:Roots:0"] = root,
            ["AdminImport:FaceDetectionBatchSize"] = batchSize.ToString(),
            ["Ai:Enabled"] = "true",
            ["Ai:FaceDetectionEnabled"] = "true",
            ["Ai:FaceEmbeddingsEnabled"] = "true",
        });
        factory.EnsureDatabaseCreated();
        using var scope = factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
        registry.SeedDeterministicProfilesAsync().GetAwaiter().GetResult();
        registry.SeedOnnxFaceEvalProfilesAsync().GetAwaiter().GetResult();
        return factory;
    }

    private string NewTree(Action<string> build)
    {
        var root = Directory.CreateTempSubdirectory("nubarca-import-faces-").FullName;
        build(root);
        _tempDirs.Add(root);
        return root;
    }

    private static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    private static AiBackfillJobPayload Payload(string json) =>
        JsonSerializer.Deserialize<AiBackfillJobPayload>(json)!;

    private static async Task<(Guid UserId, HttpClient Client)> AdminAsync(SqliteWebApplicationFactory factory)
    {
        var id = await factory.SeedUserAsync("admin@example.com");
        await factory.PromoteToAdminAsync(id);
        return (id, await factory.LoginAsync("admin@example.com"));
    }

    private static async Task<AdminImportRunResponse> StartRunAsync(HttpClient client, Guid targetUserId)
    {
        var roots = await client.GetFromJsonAsync<AdminImportRootsResponse>("/api/admin/import/roots");
        var response = await client.PostAsJsonAsync("/api/admin/import/run", new
        {
            rootId = roots!.Roots[0].RootId, relativePath = "", targetUserId, destinationFolderId = (Guid?)null,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AdminImportRunResponse>())!;
    }

    private static async Task ProcessJobsAsync(SqliteWebApplicationFactory factory, int maxJobs)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(maxJobs);
    }

    private static async Task DrainJobsAsync(SqliteWebApplicationFactory factory)
    {
        for (var i = 0; i < 50; i++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(20) == 0) return;
        }
    }

    /// <summary>Photos already in the library, never detected: stored for real, each its own blob.</summary>
    private static async Task<List<Guid>> SeedPendingPhotosAsync(SqliteWebApplicationFactory factory, Guid ownerId, int count)
    {
        var storage = factory.Services.GetRequiredService<IBlobStorage>();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var bytes = Png(100 + i, 30);
            var write = await storage.WriteAsync(new MemoryStream(bytes));
            var blob = new BlobObject
            {
                Id = Guid.NewGuid(), Sha256 = write.Sha256, SizeBytes = write.SizeBytes,
                StorageKey = write.StorageKey, ReferenceCount = 1, CreatedAt = DateTime.UtcNow,
            };
            db.BlobObjects.Add(blob);
            db.BlobMetadata.Add(new BlobMetadata
            {
                Id = Guid.NewGuid(), BlobObjectId = blob.Id, SizeBytes = write.SizeBytes,
                MediaCategory = MediaCategories.Image, DetectedContentType = "image/png",
                Width = 100 + i, Height = 30, CreatedAt = DateTime.UtcNow,
            });
            db.FileItems.Add(new FileItem
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerId, BlobObjectId = blob.Id, Name = $"old-{i}.png",
                MimeType = "image/png", SizeBytes = write.SizeBytes, CreatedAt = DateTime.UtcNow,
            });
            ids.Add(blob.Id);
        }
        await db.SaveChangesAsync();
        return ids;
    }

    private static async Task<List<Guid>> ImportedPhotoBlobsAsync(SqliteWebApplicationFactory factory, Guid runId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await (
                from item in db.AdminImportItems
                where item.ImportRunId == runId && item.FileItemId != null
                join file in db.FileItems on item.FileItemId equals file.Id
                join meta in db.BlobMetadata on file.BlobObjectId equals meta.BlobObjectId
                where meta.MediaCategory == MediaCategories.Image
                select file.BlobObjectId)
            .Distinct().ToListAsync();
    }

    private static async Task<List<Guid>> DetectionTargetsAsync(SqliteWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payloads = await db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Type == JobTypes.AiFacesDetectBackfill)
            .Select(j => j.PayloadJson).ToListAsync();
        Assert.All(payloads, json => Assert.Null(Payload(json).BlobObjectId));
        return payloads.SelectMany(json => Payload(json).BlobObjectIds ?? []).ToList();
    }

    private static async Task<(int Detections, int Statuses, int Embeddings)> FaceRowsAsync(SqliteWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (
            await db.FaceDetections.CountAsync(),
            await db.BlobAiArtifactStatuses.CountAsync(s => s.Capability == AiCapabilities.FaceDetection),
            await db.FaceEmbeddings.CountAsync());
    }
}
