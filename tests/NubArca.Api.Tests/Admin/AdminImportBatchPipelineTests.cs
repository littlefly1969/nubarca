using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Admin;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.MediaLibrary;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Admin;

// Slice 98 — admin-import DB batch pipeline: staged files persist in
// sub-batches with one SHA lookup, set-based refcount increments, AddRange'd
// inserts, batched item statuses, and ONE commit per batch. These tests pin
// the correctness properties batching must not bend: content-addressed dedup
// (within and across batches, and against pre-existing blobs), refcount
// accounting (audited after every flow), conflict classification, the
// per-file fallback when a unique index fires mid-batch, metadata co-commit
// + dedup fact seeding, and media-library folder inheritance.
public sealed class AdminImportBatchPipelineTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string NewTree(Action<string>? build = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"nc-batch98-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        build?.Invoke(root);
        _tempDirs.Add(root);
        return root;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
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

    private static async Task<(Guid AdminId, Guid TargetId, HttpClient Client)> SetupAsync(
        SqliteWebApplicationFactory factory)
    {
        factory.EnsureDatabaseCreated();
        var adminId = await factory.SeedUserAsync("admin@example.com");
        await factory.PromoteToAdminAsync(adminId);
        var client = await factory.LoginAsync("admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        return (adminId, targetId, client);
    }

    private static async Task<AdminImportRunResponse> StartRunAsync(
        HttpClient client, Guid targetUserId, Guid? destinationFolderId = null)
    {
        var roots = await client.GetFromJsonAsync<AdminImportRootsResponse>("/api/admin/import/roots");
        var resp = await client.PostAsJsonAsync("/api/admin/import/run", new
        {
            rootId = roots!.Roots[0].RootId,
            relativePath = "",
            targetUserId,
            destinationFolderId,
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AdminImportRunResponse>())!;
    }

    private static async Task ProcessJobsAsync(SqliteWebApplicationFactory factory, int maxJobs = 1)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(maxJobs);
    }

    private static async Task<BlobReferenceAuditReport> AuditAsync(SqliteWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>().AuditAsync();
    }

    private static async Task AssertAuditCleanAsync(SqliteWebApplicationFactory factory)
    {
        var report = await AuditAsync(factory);
        Assert.Equal(0, report.DbRefcountTooHigh);
        Assert.Equal(0, report.DbRefcountTooLow);
        Assert.Equal(report.TotalDbReferences, report.TotalComputedReferences);
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // ---- happy path + dedup -------------------------------------------------

    [Fact]
    public async Task Batch_Import_Persists_Files_Dedups_Within_Batch_And_Audits_Clean()
    {
        var png = ImageFixtures.PlainPng(64, 64);
        var root = NewTree(r =>
        {
            Directory.CreateDirectory(Path.Combine(r, "sub"));
            File.WriteAllText(Path.Combine(r, "sub", "a.txt"), "duplicate-content");
            File.WriteAllText(Path.Combine(r, "sub", "b.txt"), "duplicate-content");
            File.WriteAllText(Path.Combine(r, "c.txt"), "unique-content");
            File.WriteAllBytes(Path.Combine(r, "photo.png"), png);
        });
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, targetId, client) = await SetupAsync(factory);

        await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Every item imported; nothing failed/skipped/conflicted.
        Assert.Equal(0, await db.AdminImportItems.CountAsync(
            i => i.Kind == "file" && i.Status != AdminImportItemStatuses.Imported));
        Assert.Equal(4, await db.FileItems.CountAsync(f => f.OwnerUserId == targetId));

        // Within-batch dedup: ONE blob for the duplicate content, refcount 2.
        var dupSha = Sha256Hex("duplicate-content"u8.ToArray());
        var dupBlob = await db.BlobObjects.AsNoTracking().SingleAsync(b => b.Sha256 == dupSha);
        Assert.Equal(2, dupBlob.ReferenceCount);
        Assert.Equal(3, await db.BlobObjects.CountAsync()); // dup + unique + png

        // Metadata co-commit: one row per blob; the image row carries the
        // detection facts and stays pending for the async backfill.
        Assert.Equal(3, await db.BlobMetadata.CountAsync());
        var pngMeta = await db.BlobMetadata.AsNoTracking()
            .SingleAsync(m => m.MediaCategory == MediaCategories.Image);
        Assert.Equal(64, pngMeta.Width);
        Assert.Equal("image/png", pngMeta.DetectedContentType);
        Assert.Equal(MetadataStatuses.Pending, pngMeta.ExtractionStatus);

        // Imported items carry their FileItemId (batched status updates).
        Assert.Equal(0, await db.AdminImportItems.CountAsync(
            i => i.Kind == "file" && i.Status == AdminImportItemStatuses.Imported && i.FileItemId == null));

        await AssertAuditCleanAsync(factory);
    }

    [Fact]
    public async Task Duplicates_Across_Batches_Increment_The_Existing_Blob()
    {
        var root = NewTree(r =>
        {
            File.WriteAllText(Path.Combine(r, "a1.txt"), "same-bytes");
            File.WriteAllText(Path.Combine(r, "b.txt"), "other-1");
            File.WriteAllText(Path.Combine(r, "c.txt"), "other-2");
            File.WriteAllText(Path.Combine(r, "d1.txt"), "same-bytes");
        });
        // DbBatchSize=2 → "same-bytes" lands once as a NEW blob (batch 1) and
        // once as a set-based increment of an EXISTING blob (batch 2).
        using var factory = new SqliteWebApplicationFactory(Enabled(root,
            new Dictionary<string, string?> { ["AdminImport:DbBatchSize"] = "2" }));
        var (_, targetId, client) = await SetupAsync(factory);

        await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sha = Sha256Hex("same-bytes"u8.ToArray());
        var blob = await db.BlobObjects.AsNoTracking().SingleAsync(b => b.Sha256 == sha);
        Assert.Equal(2, blob.ReferenceCount);
        Assert.Equal(4, await db.FileItems.CountAsync(f => f.OwnerUserId == targetId));
        await AssertAuditCleanAsync(factory);
    }

    [Fact]
    public async Task Import_Dedups_Against_A_Preexisting_Upload_And_Seeds_Dedup_Facts()
    {
        var jpeg = ImageFixtures.JpegWithExif(includeGps: true);
        var root = NewTree(r => File.WriteAllBytes(Path.Combine(r, "imported.jpg"), jpeg));
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, targetId, client) = await SetupAsync(factory);

        // Browser upload first: inline extraction populates DateTaken + GPS on
        // the shared blob metadata.
        Guid uploadedBlobId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var uploaded = await files.CreateAsync(
                targetId, null, "uploaded.jpg", "image/jpeg", new MemoryStream(jpeg));
            uploadedBlobId = uploaded.BlobObjectId;
        }

        await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory);

        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        // Dedup: same blob, refcount 1 (upload) + 1 (import) = 2.
        var blob = await db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == uploadedBlobId);
        Assert.Equal(2, blob.ReferenceCount);

        // The imported FileItem rides the EXISTING extracted metadata:
        // embedded effective date + a GPS projection row, co-seeded in batch.
        var imported = await db.FileItems.AsNoTracking().SingleAsync(f => f.Name == "imported.jpg");
        Assert.Equal(uploadedBlobId, imported.BlobObjectId);
        Assert.Equal(EffectiveDateTakenSources.Embedded, imported.EffectiveDateTakenSource);
        Assert.True(await db.FileItemLocations.AnyAsync(l => l.FileItemId == imported.Id));

        await AssertAuditCleanAsync(factory);
    }

    // ---- fallback: unique-index collision mid-batch --------------------------

    [Fact]
    public async Task Sha_Collision_During_Batch_Falls_Back_Per_File_And_Adopts_The_Winner()
    {
        var contested = "contested-bytes"u8.ToArray();
        var root = NewTree(r =>
        {
            File.WriteAllBytes(Path.Combine(r, "contested.bin"), contested);
            File.WriteAllText(Path.Combine(r, "x.txt"), "x-bytes");
            File.WriteAllText(Path.Combine(r, "y.txt"), "y-bytes");
        });
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, targetId, client) = await SetupAsync(factory);
        var run = await StartRunAsync(client, targetId);

        var sha = Sha256Hex(contested);
        Guid winnerBlobId = Guid.Empty;

        // Drive the job inside ONE scope so the test can reach the scoped
        // AdminImportService instance and arm the collision seam: a
        // "concurrent writer" inserts the same SHA between the batch's lookup
        // and its SaveChanges, so the unique index fires and the batch must
        // fall back to the per-file path (which adopts the winner's row).
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = (AdminImportService)scope.ServiceProvider.GetRequiredService<IAdminImportService>();
            service.AfterBatchLookupForTests = async () =>
            {
                service.AfterBatchLookupForTests = null; // one-shot
                await using var inner = factory.Services.CreateAsyncScope();
                var db = inner.ServiceProvider.GetRequiredService<AppDbContext>();
                var winner = new BlobObject
                {
                    Id = Guid.NewGuid(),
                    Sha256 = sha,
                    StorageKey = $"objects/{sha[..2]}/{sha[2..4]}/{sha}",
                    SizeBytes = contested.Length,
                    ReferenceCount = 0,
                    CreatedAt = DateTime.UtcNow,
                };
                db.BlobObjects.Add(winner);
                await db.SaveChangesAsync();
                winnerBlobId = winner.Id;
            };
            await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(1);
        }

        await using var verify = factory.Services.CreateAsyncScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        // The seam fired and the import still succeeded end to end.
        Assert.NotEqual(Guid.Empty, winnerBlobId);
        Assert.Equal(0, await vdb.AdminImportItems.CountAsync(
            i => i.ImportRunId == run.ImportRunId && i.Kind == "file"
                && i.Status != AdminImportItemStatuses.Imported));
        Assert.Equal(3, await vdb.FileItems.CountAsync(f => f.OwnerUserId == targetId));

        // Exactly ONE row for the contested SHA — the winner's, adopted by the
        // per-file fallback with its refcount incremented to the real owners.
        var contestedBlob = await vdb.BlobObjects.AsNoTracking().SingleAsync(b => b.Sha256 == sha);
        Assert.Equal(winnerBlobId, contestedBlob.Id);
        Assert.Equal(1, contestedBlob.ReferenceCount);

        await AssertAuditCleanAsync(factory);
    }

    // ---- conflicts ------------------------------------------------------------

    [Fact]
    public async Task Preexisting_Sibling_Is_Conflict_While_The_Rest_Of_The_Batch_Imports()
    {
        var root = NewTree(r =>
        {
            File.WriteAllText(Path.Combine(r, "taken.txt"), "import-version");
            File.WriteAllText(Path.Combine(r, "free.txt"), "free-bytes");
        });
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, targetId, client) = await SetupAsync(factory);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            await files.CreateAsync(targetId, null, "taken.txt", "text/plain",
                new MemoryStream("user-version"u8.ToArray()));
        }

        var run = await StartRunAsync(client, targetId);
        await ProcessJobsAsync(factory);

        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var taken = await db.AdminImportItems.AsNoTracking()
            .SingleAsync(i => i.ImportRunId == run.ImportRunId && i.RelativePath == "taken.txt");
        Assert.Equal(AdminImportItemStatuses.Conflict, taken.Status);
        Assert.Equal(AdminImportConflictCategories.Preexisting, taken.ConflictCategory);

        var free = await db.AdminImportItems.AsNoTracking()
            .SingleAsync(i => i.ImportRunId == run.ImportRunId && i.RelativePath == "free.txt");
        Assert.Equal(AdminImportItemStatuses.Imported, free.Status);

        // The user's bytes were never overwritten.
        var userFile = await db.FileItems.AsNoTracking().SingleAsync(f => f.Name == "taken.txt");
        var userBlob = await db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == userFile.BlobObjectId);
        Assert.Equal(Sha256Hex("user-version"u8.ToArray()), userBlob.Sha256);

        await AssertAuditCleanAsync(factory);
    }

    // ---- pause/resume across batches ------------------------------------------

    [Fact]
    public async Task Paused_Run_Flushes_Its_Batch_And_Resumes_To_A_Clean_Audit()
    {
        var root = NewTree(r =>
        {
            for (var i = 0; i < 6; i++)
            {
                File.WriteAllText(Path.Combine(r, $"f{i}.txt"), $"content-{i}");
            }
        });
        using var factory = new SqliteWebApplicationFactory(
            Enabled(root, new Dictionary<string, string?>
            {
                ["AdminImport:MaxRunMinutes"] = "1",
                ["AdminImport:DbBatchSize"] = "2",
            }),
            clockOverride: new StepTimeProvider(20));
        var (_, targetId, client) = await SetupAsync(factory);
        var run = await StartRunAsync(client, targetId);

        // First slice pauses on the wall-clock budget (flushing its staged
        // batch first); subsequent slices resume by item state until done.
        for (var i = 0; i < 8; i++)
        {
            await ProcessJobsAsync(factory);
        }

        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var runRow = await db.AdminImportRuns.AsNoTracking().SingleAsync(r => r.Id == run.ImportRunId);
        Assert.Equal(AdminImportStatuses.Succeeded, runRow.Status);
        Assert.Equal(6, await db.FileItems.CountAsync(f => f.OwnerUserId == targetId));
        Assert.Equal(0, await db.AdminImportItems.CountAsync(
            i => i.ImportRunId == run.ImportRunId && i.Kind == "file"
                && i.Status != AdminImportItemStatuses.Imported));
        await AssertAuditCleanAsync(factory);
    }

    // ---- media library --------------------------------------------------------

    [Fact]
    public async Task Folders_Created_By_A_Batched_Import_Inherit_Media_Library_Exclusion()
    {
        var root = NewTree(r =>
        {
            Directory.CreateDirectory(Path.Combine(r, "screenshots"));
            File.WriteAllBytes(Path.Combine(r, "screenshots", "shot.png"),
                ImageFixtures.PlainPng(32, 32));
        });
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, targetId, client) = await SetupAsync(factory);

        // Destination folder with an exclude-children rule, created BEFORE the
        // import — folders the import creates beneath it must inherit.
        Guid destinationId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var folders = scope.ServiceProvider.GetRequiredService<NubArca.Api.Folders.IFolderService>();
            destinationId = (await folders.EnsureFolderPathAsync(
                targetId, null, new[] { "NoGallery" }, CancellationToken.None))!.Value;
            var mediaLibrary = scope.ServiceProvider.GetRequiredService<IMediaLibraryService>();
            var rule = await mediaLibrary.SetRuleAsync(targetId, new MediaLibraryRuleRequest(
                destinationId, MediaLibraryRuleTypes.Exclude,
                AppliesToPhotos: true, AppliesToVideos: true, AppliesToChildren: true));
            Assert.NotNull(rule);
        }

        await StartRunAsync(client, targetId, destinationId);
        await ProcessJobsAsync(factory);

        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var created = await db.Folders.AsNoTracking()
            .SingleAsync(f => f.OwnerUserId == targetId && f.Name == "screenshots");
        Assert.True(created.MediaPhotosExcluded);
        Assert.True(created.MediaPhotosExcludedForChildren);

        // And the imported photo is therefore NOT gallery-eligible.
        var shot = await db.FileItems.AsNoTracking().SingleAsync(f => f.Name == "shot.png");
        var mediaLibrarySvc = verify.ServiceProvider.GetRequiredService<IMediaLibraryService>();
        Assert.False(await mediaLibrarySvc.IsEligibleAsync(targetId, shot.Id, MediaKind.Photo));

        await AssertAuditCleanAsync(factory);
    }

    // Deterministic monotonic clock: GetTimestamp advances `stepSeconds` per
    // call so the wall-clock budget trips after a fixed number of files;
    // GetUtcNow is pinned (mirrors AdminImportItemsTests).
    private sealed class StepTimeProvider : TimeProvider
    {
        private readonly long _stepTicks;
        private long _current;

        public StepTimeProvider(int stepSeconds)
        {
            _stepTicks = stepSeconds * global::System.Diagnostics.Stopwatch.Frequency;
        }

        public override long GetTimestamp() => Interlocked.Add(ref _current, _stepTicks);

        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
