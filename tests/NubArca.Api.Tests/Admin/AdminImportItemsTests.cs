using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Admin;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Admin;

// Slice 92 — persisted import manifest (admin_import_items): scan batching,
// item-derived progress, TRUE resume by item state (no re-walk), source
// drift/missing detection, derivative off-loading to a background job, the
// paginated items endpoint, and the no-leak posture of all new surfaces.
public sealed class AdminImportItemsTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string NewTree(Action<string>? build = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"nc-items92-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        build?.Invoke(root);
        _tempDirs.Add(root);
        return root;
    }

    private static Dictionary<string, string?> Enabled(string root, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AdminImport:Enabled"] = "true",
            ["AdminImport:Roots:0"] = root,
        };
        if (extra is not null)
        {
            foreach (var (k, v) in extra) settings[k] = v;
        }
        return settings;
    }

    private static async Task<(Guid UserId, HttpClient Client)> AdminAsync(
        SqliteWebApplicationFactory factory, string email)
    {
        factory.EnsureDatabaseCreated();
        var id = await factory.SeedUserAsync(email);
        await factory.PromoteToAdminAsync(id);
        return (id, await factory.LoginAsync(email));
    }

    private static async Task<AdminImportRunResponse> StartRunAsync(
        HttpClient client, Guid targetUserId, string relativePath = "")
    {
        var roots = await client.GetFromJsonAsync<AdminImportRootsResponse>("/api/admin/import/roots");
        var resp = await client.PostAsJsonAsync("/api/admin/import/run", new
        {
            rootId = roots!.Roots[0].RootId,
            relativePath,
            targetUserId,
            destinationFolderId = (Guid?)null,
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AdminImportRunResponse>())!;
    }

    private static async Task ProcessJobsAsync(SqliteWebApplicationFactory factory, int maxJobs = 10)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
        await processor.ProcessAvailableAsync(maxJobs);
    }

    private static async Task<AdminImportRunStatusResponse> GetStatusAsync(HttpClient client, Guid runId)
        => (await client.GetFromJsonAsync<AdminImportRunStatusResponse>($"/api/admin/import/runs/{runId}"))!;

    private static byte[] CreatePngBytes(int width, int height)
    {
        using var img = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    // Deterministic monotonic clock (see AdminImportRunsTests): GetTimestamp
    // advances `stepSeconds` per call so the wall-clock budget trips after a
    // fixed number of files; GetUtcNow is fixed.
    private sealed class StepTimeProvider : TimeProvider
    {
        private long _n;
        private readonly long _stepSeconds;
        public StepTimeProvider(long stepSeconds) => _stepSeconds = stepSeconds;
        public override long GetTimestamp() => _n++ * _stepSeconds;
        public override long TimestampFrequency => 1;
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- manifest creation + item-derived progress -------------------------

    [Fact]
    public async Task Run_Persists_Manifest_Items_And_Derives_Counters_From_Them()
    {
        var root = NewTree(r =>
        {
            File.WriteAllText(Path.Combine(r, "a.txt"), "aaa");
            var sub = Path.Combine(r, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "b.txt"), "bbbb");
            Directory.CreateDirectory(Path.Combine(r, "empty-dir"));
        });
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");

        var run = await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory);

        var status = await GetStatusAsync(client, run.ImportRunId);
        Assert.Equal("succeeded", status.Status);
        Assert.Equal(2, status.ImportedFiles);
        Assert.Equal(2, status.ScannedFiles);
        Assert.Equal(0, status.PendingFiles);
        Assert.Equal(2, status.TotalDirectories);
        Assert.Equal(7, status.TotalBytes); // 3 + 4 bytes
        Assert.NotNull(status.ScanCompletedAt);
        Assert.Null(status.Phase); // cleared at finalize

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var items = await db.AdminImportItems.AsNoTracking()
            .Where(i => i.ImportRunId == run.ImportRunId).ToListAsync();

        // 2 files + 2 directories, all imported; counters MATCH the item states.
        Assert.Equal(4, items.Count);
        Assert.All(items, i => Assert.Equal("imported", i.Status));
        Assert.Equal(2, items.Count(i => i.Kind == "file"));
        Assert.Equal(2, items.Count(i => i.Kind == "directory"));
        Assert.All(items.Where(i => i.Kind == "file"), i =>
        {
            Assert.NotNull(i.FileItemId); // internal bookkeeping only
            Assert.Equal(1, i.Attempts);
        });
        // The empty directory was materialised as a logical folder.
        Assert.True(await db.Folders.AnyAsync(
            f => f.OwnerUserId == targetId && f.Name == "empty-dir" && f.DeletedAt == null));
    }

    [Fact]
    public async Task Scan_Persists_Items_In_Batches()
    {
        var root = NewTree(r =>
        {
            for (var i = 0; i < 5; i++) File.WriteAllText(Path.Combine(r, $"f{i}.txt"), $"x{i}");
        });
        // Batch size 2 → the 5-file manifest needs 3 flushes; everything must
        // still be persisted exactly once with consecutive ordinals.
        using var factory = new SqliteWebApplicationFactory(Enabled(root,
            new Dictionary<string, string?> { ["AdminImport:ScanBatchSize"] = "2" }));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");

        var run = await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ordinals = await db.AdminImportItems.AsNoTracking()
            .Where(i => i.ImportRunId == run.ImportRunId)
            .OrderBy(i => i.Ordinal)
            .Select(i => i.Ordinal)
            .ToListAsync();
        Assert.Equal(Enumerable.Range(1, 5), ordinals);
        Assert.Equal("succeeded", (await GetStatusAsync(client, run.ImportRunId)).Status);
    }

    // ---- TRUE resume by item state -----------------------------------------

    [Fact]
    public async Task Paused_Run_Resumes_From_Item_State_Without_ReImporting()
    {
        var root = NewTree(r =>
        {
            for (var i = 0; i < 6; i++) File.WriteAllText(Path.Combine(r, $"f{i}.txt"), $"file-{i}");
        });
        // Budget 1 min, clock steps 20s per check → each slice imports ~3 files
        // then pauses + re-queues; the chain finishes across multiple slices.
        using var factory = new SqliteWebApplicationFactory(
            Enabled(root, new Dictionary<string, string?> { ["AdminImport:MaxRunMinutes"] = "1" }),
            clockOverride: new StepTimeProvider(20));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");

        var run = await StartRunAsync(client, targetId);
        // Drain the whole pause-requeue chain.
        for (var i = 0; i < 5; i++) await ProcessJobsAsync(factory);

        var status = await GetStatusAsync(client, run.ImportRunId);
        Assert.Equal("succeeded", status.Status);
        Assert.Equal(6, status.ImportedFiles);
        // THE slice-92 acceptance: resume skipped completed items by STATE —
        // nothing was re-walked into the already-imported bucket, and no item
        // needed a second attempt.
        Assert.Equal(0, status.AlreadyImportedFiles);
        Assert.Equal(0, status.ConflictFiles);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var items = await db.AdminImportItems.AsNoTracking()
            .Where(i => i.ImportRunId == run.ImportRunId && i.Kind == "file").ToListAsync();
        Assert.Equal(6, items.Count);
        Assert.All(items, i => Assert.Equal(1, i.Attempts));
        Assert.Equal(6, await db.FileItems.CountAsync(
            f => f.OwnerUserId == targetId && f.DeletedAt == null));
    }

    [Fact]
    public async Task Crashed_MidFile_Item_Is_Retried_And_Not_Duplicated()
    {
        var root = NewTree(r =>
        {
            File.WriteAllText(Path.Combine(r, "one.txt"), "one");
            File.WriteAllText(Path.Combine(r, "two.txt"), "two");
        });
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");

        var run = await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory);
        Assert.Equal("succeeded", (await GetStatusAsync(client, run.ImportRunId)).Status);

        // Simulate a worker that died mid-file AFTER the FileItem committed:
        // the item is frozen `importing`, the run is left non-terminal, and a
        // fresh job resumes it (exactly what lease expiry produces).
        Guid resumeJobId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var item = await db.AdminImportItems.FirstAsync(
                i => i.ImportRunId == run.ImportRunId && i.Kind == "file");
            item.Status = "importing";
            item.FileItemId = null;
            var row = await db.AdminImportRuns.FirstAsync(r => r.Id == run.ImportRunId);
            row.Status = "paused";
            row.CompletedAt = null;
            await db.SaveChangesAsync();

            var jobs = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var job = await jobs.EnqueueAsync(
                JobTypes.AdminImport, new AdminImportJobPayload(run.ImportRunId));
            resumeJobId = job.Id;
            row.JobId = resumeJobId;
            await db.SaveChangesAsync();
        }

        await ProcessJobsAsync(factory);

        var status = await GetStatusAsync(client, run.ImportRunId);
        Assert.Equal("succeeded", status.Status);
        Assert.Equal(2, status.ImportedFiles);
        // The re-run item re-detected its own FileItem (no duplicate) and is
        // classified as resume-detected, not as a true conflict.
        Assert.Equal(1, status.AlreadyImportedFiles);
        Assert.Equal(0, status.ConflictFiles);

        await using var verify = factory.Services.CreateAsyncScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await vdb.FileItems.CountAsync(
            f => f.OwnerUserId == targetId && f.DeletedAt == null));
        var retried = await vdb.AdminImportItems.AsNoTracking().FirstAsync(
            i => i.ImportRunId == run.ImportRunId && i.ConflictCategory != null);
        Assert.Equal("imported", retried.Status);
        Assert.Equal("already-imported-this-run", retried.ConflictCategory);
        Assert.Equal(2, retried.Attempts);
    }

    [Fact]
    public async Task Source_Changed_After_Scan_Is_Detected_And_Fails_Safely()
    {
        var root = NewTree(r =>
        {
            for (var i = 0; i < 6; i++) File.WriteAllText(Path.Combine(r, $"f{i}.txt"), $"file-{i}");
        });
        using var factory = new SqliteWebApplicationFactory(
            Enabled(root, new Dictionary<string, string?> { ["AdminImport:MaxRunMinutes"] = "1" }),
            clockOverride: new StepTimeProvider(20));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");

        var run = await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory, maxJobs: 1); // slice 1 → paused mid-run

        // While paused, grow a still-pending source file (size drift).
        string changedPath;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            changedPath = await db.AdminImportItems.AsNoTracking()
                .Where(i => i.ImportRunId == run.ImportRunId && i.Status == "pending" && i.Kind == "file")
                .OrderBy(i => i.Ordinal)
                .Select(i => i.RelativePath)
                .FirstAsync();
        }
        File.AppendAllText(Path.Combine(root, changedPath), "-GREW-AFTER-SCAN");

        for (var i = 0; i < 5; i++) await ProcessJobsAsync(factory);

        var status = await GetStatusAsync(client, run.ImportRunId);
        Assert.Equal("partial", status.Status); // failed file → partial
        Assert.Equal(5, status.ImportedFiles);
        Assert.Equal(1, status.FailedFiles);

        await using var verify = factory.Services.CreateAsyncScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var failed = await vdb.AdminImportItems.AsNoTracking().FirstAsync(
            i => i.ImportRunId == run.ImportRunId && i.Status == "failed");
        Assert.Equal(changedPath, failed.RelativePath);
        Assert.Equal("source_changed", failed.FailureCategory);
        Assert.DoesNotContain(root, failed.FailureMessage ?? string.Empty); // sanitized
    }

    [Fact]
    public async Task Source_Missing_After_Scan_Is_Skipped_Safely()
    {
        var root = NewTree(r =>
        {
            for (var i = 0; i < 6; i++) File.WriteAllText(Path.Combine(r, $"f{i}.txt"), $"file-{i}");
        });
        using var factory = new SqliteWebApplicationFactory(
            Enabled(root, new Dictionary<string, string?> { ["AdminImport:MaxRunMinutes"] = "1" }),
            clockOverride: new StepTimeProvider(20));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");

        var run = await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory, maxJobs: 1); // slice 1 → paused

        string missingPath;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            missingPath = await db.AdminImportItems.AsNoTracking()
                .Where(i => i.ImportRunId == run.ImportRunId && i.Status == "pending" && i.Kind == "file")
                .OrderBy(i => i.Ordinal)
                .Select(i => i.RelativePath)
                .FirstAsync();
        }
        File.Delete(Path.Combine(root, missingPath));

        for (var i = 0; i < 5; i++) await ProcessJobsAsync(factory);

        var status = await GetStatusAsync(client, run.ImportRunId);
        // A vanished source is a benign skip, not a failure.
        Assert.Equal("succeeded", status.Status);
        Assert.Equal(5, status.ImportedFiles);
        Assert.Equal(1, status.SkippedFiles);
        Assert.Equal(0, status.FailedFiles);

        await using var verify = factory.Services.CreateAsyncScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var skipped = await vdb.AdminImportItems.AsNoTracking().FirstAsync(
            i => i.ImportRunId == run.ImportRunId && i.Status == "skipped");
        Assert.Equal("source_missing", skipped.FailureCategory);
    }

    [Fact]
    public async Task Cancelling_A_Paused_Run_Freezes_Remaining_Items_As_Cancelled()
    {
        var root = NewTree(r =>
        {
            for (var i = 0; i < 6; i++) File.WriteAllText(Path.Combine(r, $"f{i}.txt"), $"file-{i}");
        });
        using var factory = new SqliteWebApplicationFactory(
            Enabled(root, new Dictionary<string, string?> { ["AdminImport:MaxRunMinutes"] = "1" }),
            clockOverride: new StepTimeProvider(20));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");

        var run = await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory, maxJobs: 1); // slice 1 → paused + requeued

        var paused = await GetStatusAsync(client, run.ImportRunId);
        Assert.Equal("paused", paused.Status);
        // The run was re-pointed at the RESUME job (cancelling a paused run
        // must flag the slice that will actually execute).
        Assert.NotEqual(run.JobId, paused.JobId);

        // Cancel the PAUSED run. Its resume job is still queued, so it will
        // never execute — the endpoint freezes the manifest remainder.
        var cancel = await (await client.PostAsync(
                $"/api/admin/import/runs/{run.ImportRunId}/cancel", null))
            .Content.ReadFromJsonAsync<AdminImportCancelResponse>();
        Assert.True(cancel!.CancellationRequested);

        await ProcessJobsAsync(factory);

        var status = await GetStatusAsync(client, run.ImportRunId);
        Assert.Equal("cancelled", status.Status);
        Assert.InRange(status.ImportedFiles, 1, 5);
        Assert.True(status.CancelledFiles >= 1);
        Assert.Equal(0, status.PendingFiles); // nothing left dangling

        await using var verify = factory.Services.CreateAsyncScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        // Exactly the imported files exist; the cancelled remainder never ran.
        Assert.Equal(status.ImportedFiles, await vdb.FileItems.CountAsync(
            f => f.OwnerUserId == targetId && f.DeletedAt == null));
        Assert.Equal(0, await vdb.AdminImportItems.CountAsync(
            i => i.ImportRunId == run.ImportRunId
                && (i.Status == "pending" || i.Status == "importing")));
    }

    // ---- conflicts stay conflicts ------------------------------------------

    [Fact]
    public async Task Preexisting_Destination_File_Is_A_Conflict_Not_A_Failure()
    {
        var root = NewTree(r => File.WriteAllText(Path.Combine(r, "dup.txt"), "incoming"));
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<NubArca.Api.Files.IFileItemService>();
            await files.CreateAsync(targetId, null, "dup.txt", "text/plain",
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes("original")));
        }

        var run = await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory);

        var status = await GetStatusAsync(client, run.ImportRunId);
        Assert.Equal("succeeded", status.Status); // conflicts are not failures
        Assert.Equal(1, status.ConflictFiles);
        Assert.Equal(0, status.FailedFiles);
        var sample = Assert.Single(status.ConflictSamples);
        Assert.Equal("dup.txt", sample.RelativePath);
        Assert.Equal("preexisting", sample.Reason);

        await using var verify = factory.Services.CreateAsyncScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var item = await vdb.AdminImportItems.AsNoTracking().FirstAsync(
            i => i.ImportRunId == run.ImportRunId && i.Kind == "file");
        Assert.Equal("conflict", item.Status);
        Assert.Equal("preexisting", item.ConflictCategory);
        Assert.Null(item.FileItemId); // the pre-existing file is NOT linked
    }

    // ---- derivative off-loading (parts B + C) ------------------------------

    [Fact]
    public async Task Import_Does_Not_Generate_Thumbnails_Inline_And_Enqueues_Backfill_Job()
    {
        var root = NewTree(r =>
        {
            File.WriteAllBytes(Path.Combine(r, "photo.png"), CreatePngBytes(64, 48));
            File.WriteAllText(Path.Combine(r, "note.txt"), "hello");
        });
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");

        var run = await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory, maxJobs: 1); // ONLY the import job

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Part B: no derivative was generated on the import critical path…
            Assert.Equal(0, await db.FileThumbnails.CountAsync());
            // …and Part C: a media.derivatives backfill job was enqueued instead.
            Assert.Equal(1, await db.BackgroundJobs.CountAsync(j =>
                j.Type == JobTypes.MediaDerivativesBackfill && j.Status == JobStatuses.Queued));
        }

        // The backfill job generates the missing image derivatives.
        await ProcessJobsAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sizes = await db.FileThumbnails.AsNoTracking().Select(t => t.Size).ToListAsync();
            Assert.Contains("small", sizes);
            Assert.Contains("medium", sizes);
            var count = sizes.Count;

            // Idempotent: re-running the backfill creates nothing new.
            var jobs = factory.Services.CreateScope().ServiceProvider.GetRequiredService<IJobQueue>();
            await jobs.EnqueueAsync(JobTypes.MediaDerivativesBackfill,
                new MediaDerivativesBackfillJobPayload());
            await ProcessJobsAsync(factory);
            Assert.Equal(count, await db.FileThumbnails.CountAsync());
        }
    }

    [Fact]
    public async Task Import_Enqueues_Profile_Keyed_Ai_Backfill_When_Enabled()
    {
        const string profileKey = "photo-siglip2-so400m-patch14-384-v2";
        var root = NewTree(r =>
            File.WriteAllBytes(Path.Combine(r, "photo.png"), CreatePngBytes(64, 48)));
        using var factory = new SqliteWebApplicationFactory(Enabled(root,
            new Dictionary<string, string?>
            {
                ["Ai:Enabled"] = "true",
                ["Ai:ImageEmbeddingsEnabled"] = "true",
                ["Ai:PhotoSimilarityProfileKey"] = profileKey,
            }));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");

        await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory, maxJobs: 1); // import only

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync(j =>
            j.Type == JobTypes.AiPhotosEmbeddingsBackfill);
        Assert.Equal(JobStatuses.Queued, job.Status);
        var payload = System.Text.Json.JsonSerializer.Deserialize<AiBackfillJobPayload>(job.PayloadJson);
        Assert.Equal(profileKey, payload?.ProfileKey);
        Assert.StartsWith("ai-photo-embeddings:import:", job.IdempotencyKey);
    }

    [Fact]
    public async Task Import_Does_Not_Enqueue_Ai_Backfill_When_Ai_Is_Disabled()
    {
        var root = NewTree(r =>
            File.WriteAllBytes(Path.Combine(r, "photo.png"), CreatePngBytes(64, 48)));
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");

        await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory, maxJobs: 1);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.BackgroundJobs.CountAsync(j =>
            j.Type == JobTypes.AiPhotosEmbeddingsBackfill));
    }

    [Fact]
    public async Task Inline_Derivatives_Config_Restores_Eager_Thumbnail_Generation()
    {
        var root = NewTree(r =>
            File.WriteAllBytes(Path.Combine(r, "photo.png"), CreatePngBytes(64, 48)));
        using var factory = new SqliteWebApplicationFactory(Enabled(root,
            new Dictionary<string, string?> { ["AdminImport:GenerateDerivativesInline"] = "true" }));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");

        var run = await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory, maxJobs: 1);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Old behaviour: the small thumbnail exists right after the import job…
        Assert.Equal(1, await db.FileThumbnails.CountAsync(t => t.Size == "small"));
        // …and no backfill job was enqueued.
        Assert.Equal(0, await db.BackgroundJobs.CountAsync(
            j => j.Type == JobTypes.MediaDerivativesBackfill));
    }

    [Fact]
    public async Task EnqueueDerivatives_Endpoint_Creates_Idempotent_Job()
    {
        var root = NewTree(r => File.WriteAllText(Path.Combine(r, "a.txt"), "a"));
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var run = await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory);

        // Unknown run → 404.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(
            $"/api/admin/import/runs/{Guid.NewGuid()}/enqueue-derivatives", null)).StatusCode);

        var first = await (await client.PostAsync(
                $"/api/admin/import/runs/{run.ImportRunId}/enqueue-derivatives", null))
            .Content.ReadFromJsonAsync<AdminImportEnqueueDerivativesResponse>();
        var second = await (await client.PostAsync(
                $"/api/admin/import/runs/{run.ImportRunId}/enqueue-derivatives", null))
            .Content.ReadFromJsonAsync<AdminImportEnqueueDerivativesResponse>();
        // While the first request's job is still queued, the second dedupes to it.
        Assert.Equal(first!.JobId, second!.JobId);
    }

    // ---- items endpoint ------------------------------------------------------

    [Fact]
    public async Task Items_Endpoint_Paginates_Filters_And_Does_Not_Leak()
    {
        var root = NewTree(r =>
        {
            for (var i = 0; i < 5; i++) File.WriteAllText(Path.Combine(r, $"f{i}.txt"), $"x{i}");
        });
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var run = await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory);

        // Page 1 of 2 + total.
        var page1 = await client.GetFromJsonAsync<AdminImportItemListResponse>(
            $"/api/admin/import/runs/{run.ImportRunId}/items?page=1&pageSize=2");
        Assert.Equal(5, page1!.Total);
        Assert.Equal(2, page1.Items.Count);

        // Status filter narrows; unknown status is a 400; unknown run a 404;
        // non-admin a 403.
        var imported = await client.GetFromJsonAsync<AdminImportItemListResponse>(
            $"/api/admin/import/runs/{run.ImportRunId}/items?status=imported&pageSize=100");
        Assert.Equal(5, imported!.Total);
        var none = await client.GetFromJsonAsync<AdminImportItemListResponse>(
            $"/api/admin/import/runs/{run.ImportRunId}/items?status=failed");
        Assert.Equal(0, none!.Total);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(
            $"/api/admin/import/runs/{run.ImportRunId}/items?status=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(
            $"/api/admin/import/runs/{Guid.NewGuid()}/items")).StatusCode);
        var (_, nonAdmin) = await factory.CreateAuthenticatedClientAsync("plain@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await nonAdmin.GetAsync(
            $"/api/admin/import/runs/{run.ImportRunId}/items")).StatusCode);

        // No-leak: the items payload carries relative paths + categories only.
        var body = await (await client.GetAsync(
                $"/api/admin/import/runs/{run.ImportRunId}/items?pageSize=100"))
            .Content.ReadAsStringAsync();
        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(root, body, StringComparison.Ordinal); // absolute server path
    }

    [Fact]
    public async Task Run_Detail_Still_Does_Not_Leak_With_Item_Derived_Fields()
    {
        var root = NewTree(r => File.WriteAllText(Path.Combine(r, "safe.txt"), "x"));
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var run = await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory);

        var bodies = new[]
        {
            await (await client.GetAsync("/api/admin/import/runs")).Content.ReadAsStringAsync(),
            await (await client.GetAsync($"/api/admin/import/runs/{run.ImportRunId}")).Content.ReadAsStringAsync(),
        };
        foreach (var body in bodies)
        {
            foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
            {
                Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
            }
            Assert.DoesNotContain(root, body, StringComparison.Ordinal);
        }
    }
}
