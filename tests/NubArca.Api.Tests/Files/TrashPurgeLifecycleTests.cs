using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.Files;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Files;

// The TRASHED -> PERMANENT PURGE contract.
//
// The three triggers (individual permanent delete, Empty Trash, automatic
// retention expiry) must converge on ONE canonical purge, and the physical
// half must be retry-safe: no state in which the database has forgotten a
// storage key whose bytes are still on disk.
public sealed class TrashPurgeLifecycleTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public TrashPurgeLifecycleTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    // ---------- infrastructure ----------

    private FileItemSweeper CreateSweeper(int graceMinutes = 30, bool enabled = true) => new(
        _factory.Services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new FileItemSweeperOptions
        {
            Enabled = enabled,
            IntervalMinutes = 5,
            GraceMinutes = graceMinutes,
        }),
        TimeProvider.System,
        NullLogger<FileItemSweeper>.Instance);

    private BlobJanitor CreateJanitor(int graceMinutes = 30, IBlobStorage? storageOverride = null) => new(
        storageOverride is null
            ? _factory.Services.GetRequiredService<IServiceScopeFactory>()
            : new OverridingScopeFactory(
                _factory.Services.GetRequiredService<IServiceScopeFactory>(),
                typeof(IBlobStorage),
                storageOverride),
        Options.Create(new BlobJanitorOptions
        {
            Enabled = true,
            IntervalMinutes = 5,
            GraceMinutes = graceMinutes,
        }),
        TimeProvider.System,
        NullLogger<BlobJanitor>.Instance);

    // Swaps ONE service for the janitor's scope without disturbing the shared
    // test host, so a storage-failure test does not leak into other tests.
    private sealed class OverridingScopeFactory(
        IServiceScopeFactory inner, Type serviceType, object replacement) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(inner.CreateScope(), serviceType, replacement);

        private sealed class Scope(IServiceScope inner, Type serviceType, object replacement)
            : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type type) =>
                type == serviceType ? replacement : inner.ServiceProvider.GetService(type);
            public void Dispose() => inner.Dispose();
        }
    }

    // Fails every unlink. Models a read-only mount / permission problem.
    private sealed class FailingBlobStorage(IBlobStorage inner) : IBlobStorage
    {
        public Task<BlobWriteResult> WriteAsync(Stream c, CancellationToken ct = default) =>
            inner.WriteAsync(c, ct);
        public Task<StagedBlobWrite> StageAsync(Stream c, CancellationToken ct = default) =>
            inner.StageAsync(c, ct);
        public Task<BlobWriteResult> PublishAsync(
            StagedBlobWrite staged, CancellationToken ct = default) =>
            inner.PublishAsync(staged, ct);
        public Task<Stream> OpenReadAsync(string k, CancellationToken ct = default) =>
            inner.OpenReadAsync(k, ct);
        public Task<bool> ExistsAsync(string k, CancellationToken ct = default) =>
            inner.ExistsAsync(k, ct);
        public IAsyncEnumerable<string> EnumerateStorageKeysAsync(CancellationToken ct = default) =>
            inner.EnumerateStorageKeysAsync(ct);
        public Task DeleteAsync(string k, CancellationToken ct = default) =>
            throw new IOException("storage unavailable");
    }

    // ---------- seeding ----------

    private async Task<Guid> SeedUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = id,
            Email = $"owner-{id:N}@example.com",
            DisplayName = "Owner",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<FileItem> CreateTextFileAsync(Guid ownerId, string name, string content)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(
            ownerId, null, name, "text/plain", new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }

    // An image so the file owns real derived artifacts (thumbnail blobs).
    private async Task<FileItem> CreateImageFileAsync(Guid ownerId, string name, int seed)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        using var img = new Image<Rgba32>(320, 240);
        img[seed % 320, 0] = new Rgba32((byte)(seed % 255), 40, 80, 255);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return await files.CreateAsync(
            ownerId, null, name, "image/png", new MemoryStream(ms.ToArray()));
    }

    private async Task TrashAsync(Guid ownerId, Guid fileId)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        Assert.True(await files.SoftDeleteAsync(ownerId, fileId));
    }

    private async Task BackdateTrashAsync(Guid fileId, int minutesAgo)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.FileItems.IgnoreQueryFilters().Where(f => f.Id == fileId).ExecuteUpdateAsync(
            s => s.SetProperty(f => f.DeletedAt, _ => (DateTime?)DateTime.UtcNow.AddMinutes(-minutesAgo)));
    }

    // Every storage key this file owns: its original blob plus each derived
    // thumbnail/preview/poster blob.
    private async Task<(Guid BlobId, List<string> Keys)> OwnedStorageKeysAsync(Guid fileId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blobId = await db.FileItems.IgnoreQueryFilters()
            .Where(f => f.Id == fileId).Select(f => f.BlobObjectId).SingleAsync();
        var ids = new List<Guid> { blobId };
        ids.AddRange(await db.FileThumbnails
            .Where(t => t.FileItemId == fileId).Select(t => t.BlobObjectId).ToListAsync());
        var keys = await db.BlobObjects.AsNoTracking()
            .Where(b => ids.Contains(b.Id)).Select(b => b.StorageKey).ToListAsync();
        return (blobId, keys);
    }

    private async Task<bool> AnyBytesPresentAsync(IEnumerable<string> keys)
    {
        var storage = _factory.Services.GetRequiredService<IBlobStorage>();
        var derived = _factory.Services.GetService<IDerivedBlobStorage>();
        foreach (var key in keys)
        {
            if (await storage.ExistsAsync(key)) return true;
            if (derived is not null && await derived.ExistsAsync(key)) return true;
        }
        return false;
    }

    // Drive the physical half to completion: the janitor starts a fresh grace
    // window at hard-purge time, so make everything eligible and tick.
    private async Task<int> RunJanitorToCompletionAsync(IBlobStorage? storageOverride = null)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BlobObjects.Where(b => b.PurgeEligibleAt != null).ExecuteUpdateAsync(
                s => s.SetProperty(b => b.PurgeEligibleAt, _ => (DateTime?)DateTime.UtcNow.AddMinutes(-120)));
        }

        var total = 0;
        // Two ticks: releasing a derived blob (e.g. a face preview) can make a
        // further blob eligible only after the first tick committed.
        for (var i = 0; i < 2; i++)
        {
            total += await CreateJanitor(storageOverride: storageOverride).RunOnceAsync(default);
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BlobObjects.Where(b => b.PurgeEligibleAt != null).ExecuteUpdateAsync(
                s => s.SetProperty(b => b.PurgeEligibleAt, _ => (DateTime?)DateTime.UtcNow.AddMinutes(-120)));
        }
        return total;
    }

    private async Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = _factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    // ================= ACTIVE -> TRASHED =================

    [Fact]
    public async Task Moving_To_Trash_Does_Not_Delete_The_Blob()
    {
        var owner = await SeedUserAsync();
        var file = await CreateImageFileAsync(owner, "keep.png", 1);
        var (blobId, keys) = await OwnedStorageKeysAsync(file.Id);

        await TrashAsync(owner, file.Id);
        // Even a janitor tick must not touch restorable bytes.
        await RunJanitorToCompletionAsync();

        Assert.True(await QueryAsync(db => db.BlobObjects.AnyAsync(b => b.Id == blobId)));
        foreach (var key in keys)
        {
            Assert.True(await AnyBytesPresentAsync(new[] { key }), $"bytes for {key} must survive Trash");
        }
    }

    [Fact]
    public async Task Trashed_File_Remains_Restorable_Before_Expiry()
    {
        var owner = await SeedUserAsync();
        var file = await CreateTextFileAsync(owner, "doc.txt", "restore-me");
        await TrashAsync(owner, file.Id);
        await BackdateTrashAsync(file.Id, minutesAgo: 5);

        // Inside the retention window: retention must leave it alone.
        var swept = await CreateSweeper(graceMinutes: 60).RunOnceAsync(default);
        Assert.Equal(0, swept);

        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        var restored = await files.RestoreAsync(owner, file.Id);

        Assert.NotNull(restored);
        Assert.Null(restored!.DeletedAt);
    }

    [Fact]
    public async Task Restoring_Does_Not_Lose_Its_Physical_Object()
    {
        var owner = await SeedUserAsync();
        var file = await CreateImageFileAsync(owner, "round-trip.png", 2);
        var (blobId, keys) = await OwnedStorageKeysAsync(file.Id);

        await TrashAsync(owner, file.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            Assert.NotNull(await files.RestoreAsync(owner, file.Id));
        }

        await RunJanitorToCompletionAsync();

        var blob = await QueryAsync(db => db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == blobId));
        Assert.True(blob.ReferenceCount > 0);
        Assert.Null(blob.PurgeEligibleAt);
        foreach (var key in keys)
        {
            Assert.True(await AnyBytesPresentAsync(new[] { key }));
        }

        // And the content is still readable through the service.
        using var verify = _factory.Services.CreateScope();
        var blobs = verify.ServiceProvider.GetRequiredService<IBlobService>();
        await using var stream = await blobs.OpenContentAsync(blobId);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public async Task Non_Expired_Trash_Items_Are_Untouched()
    {
        var owner = await SeedUserAsync();
        var fresh = await CreateTextFileAsync(owner, "fresh.txt", "fresh");
        var stale = await CreateTextFileAsync(owner, "stale.txt", "stale");
        var (_, freshKeys) = await OwnedStorageKeysAsync(fresh.Id);

        await TrashAsync(owner, fresh.Id);
        await TrashAsync(owner, stale.Id);
        await BackdateTrashAsync(fresh.Id, minutesAgo: 5);
        await BackdateTrashAsync(stale.Id, minutesAgo: 600);

        var swept = await CreateSweeper(graceMinutes: 60).RunOnceAsync(default);
        await RunJanitorToCompletionAsync();

        Assert.Equal(1, swept);
        Assert.True(await QueryAsync(db =>
            db.FileItems.IgnoreQueryFilters().AnyAsync(f => f.Id == fresh.Id)));
        Assert.False(await QueryAsync(db =>
            db.FileItems.IgnoreQueryFilters().AnyAsync(f => f.Id == stale.Id)));
        Assert.True(await AnyBytesPresentAsync(freshKeys));
    }

    // ================= the three triggers converge =================

    [Fact]
    public async Task Individual_Permanent_Delete_Removes_Its_Physical_Objects()
    {
        var owner = await SeedUserAsync();
        var file = await CreateImageFileAsync(owner, "individual.png", 3);
        var (blobId, keys) = await OwnedStorageKeysAsync(file.Id);
        Assert.True(keys.Count >= 2, "image should own its original plus derived artifacts");

        await TrashAsync(owner, file.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            Assert.True(await files.PermanentDeleteAsync(owner, file.Id));
        }
        await RunJanitorToCompletionAsync();

        Assert.False(await QueryAsync(db => db.BlobObjects.AnyAsync(b => b.Id == blobId)));
        Assert.False(await AnyBytesPresentAsync(keys));
        Assert.False(await QueryAsync(db => db.PendingBlobPurges.AnyAsync()));
    }

    [Fact]
    public async Task Empty_Trash_Removes_Its_Physical_Objects()
    {
        var owner = await SeedUserAsync();
        var file = await CreateImageFileAsync(owner, "bulk.png", 4);
        var (blobId, keys) = await OwnedStorageKeysAsync(file.Id);

        await TrashAsync(owner, file.Id);

        // Empty Trash is the endpoint looping the canonical per-file purge.
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            foreach (var trashed in await files.ListTrashAsync(owner, null))
            {
                Assert.True(await files.PermanentDeleteAsync(owner, trashed.Id));
            }
        }
        await RunJanitorToCompletionAsync();

        Assert.False(await QueryAsync(db => db.BlobObjects.AnyAsync(b => b.Id == blobId)));
        Assert.False(await AnyBytesPresentAsync(keys));
    }

    [Fact]
    public async Task Retention_Expiry_Removes_Exactly_The_Same_Physical_Objects()
    {
        var owner = await SeedUserAsync();

        // Two byte-identical-shaped files; one purged manually, one by
        // retention. Distinct content so they do not dedup onto one blob.
        var manual = await CreateImageFileAsync(owner, "manual.png", 5);
        var expired = await CreateImageFileAsync(owner, "expired.png", 6);
        var (manualBlob, manualKeys) = await OwnedStorageKeysAsync(manual.Id);
        var (expiredBlob, expiredKeys) = await OwnedStorageKeysAsync(expired.Id);
        Assert.Equal(manualKeys.Count, expiredKeys.Count);

        await TrashAsync(owner, manual.Id);
        await TrashAsync(owner, expired.Id);

        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            Assert.True(await files.PermanentDeleteAsync(owner, manual.Id));
        }

        await BackdateTrashAsync(expired.Id, minutesAgo: 600);
        Assert.Equal(1, await CreateSweeper(graceMinutes: 30).RunOnceAsync(default));

        await RunJanitorToCompletionAsync();

        // Identical end state for both routes.
        Assert.False(await QueryAsync(db => db.BlobObjects.AnyAsync(b => b.Id == manualBlob)));
        Assert.False(await QueryAsync(db => db.BlobObjects.AnyAsync(b => b.Id == expiredBlob)));
        Assert.False(await AnyBytesPresentAsync(manualKeys));
        Assert.False(await AnyBytesPresentAsync(expiredKeys));
        Assert.False(await QueryAsync(db => db.FileThumbnails.AnyAsync()));
        Assert.False(await QueryAsync(db => db.PendingBlobPurges.AnyAsync()));
    }

    // ================= root cause: append-only Restrict FKs =================

    [Fact]
    public async Task Photo_Exported_File_Is_Purgeable_By_Retention_Expiry()
    {
        var owner = await SeedUserAsync();
        var file = await CreateTextFileAsync(owner, "exported.txt", "exported");
        var (blobId, keys) = await OwnedStorageKeysAsync(file.Id);

        var sessionId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PhotoExportSessions.Add(new PhotoExportSession
            {
                Id = sessionId,
                OwnerUserId = owner,
                TokenHash = new string('a', 64),
                Status = PhotoExportStatuses.Ready,
                CreatedAt = DateTime.UtcNow.AddDays(-30),
                ExpiresAt = DateTime.UtcNow.AddDays(-29),
                UpdatedAt = DateTime.UtcNow.AddDays(-30),
            });
            db.PhotoExportEntries.Add(new PhotoExportEntry
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                FileItemId = file.Id,
                RelativePath = "exported.txt",
                Name = "exported.txt",
                SizeBytes = 8,
            });
            await db.SaveChangesAsync();
        }

        await TrashAsync(owner, file.Id);
        await BackdateTrashAsync(file.Id, minutesAgo: 600);

        var swept = await CreateSweeper(graceMinutes: 30).RunOnceAsync(default);
        await RunJanitorToCompletionAsync();

        Assert.Equal(1, swept);
        Assert.False(await QueryAsync(db => db.PhotoExportEntries.AnyAsync(e => e.FileItemId == file.Id)));
        Assert.False(await QueryAsync(db => db.BlobObjects.AnyAsync(b => b.Id == blobId)));
        Assert.False(await AnyBytesPresentAsync(keys));
        // The session row itself is not the file's to delete.
        Assert.True(await QueryAsync(db => db.PhotoExportSessions.AnyAsync(s => s.Id == sessionId)));
    }

    [Fact]
    public async Task Printed_File_Is_Purgeable_And_The_Print_Job_Survives()
    {
        var owner = await SeedUserAsync();
        var file = await CreateTextFileAsync(owner, "printed.txt", "printed");
        var (blobId, keys) = await OwnedStorageKeysAsync(file.Id);

        var jobId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stationId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            db.PrintStations.Add(new PrintStation
            {
                Id = stationId,
                OwnerUserId = owner,
                Name = "Station",
                CreatedAt = DateTime.UtcNow.AddDays(-40),
            });
            db.PrinterDevices.Add(new PrinterDevice
            {
                Id = deviceId,
                PrintStationId = stationId,
                DeviceKey = "dev-1",
                DisplayName = "Printer",
                AdapterKind = "test",
                LastSeenAt = DateTime.UtcNow.AddDays(-40),
            });
            db.PrintJobs.Add(new PrintJob
            {
                Id = jobId,
                OwnerUserId = owner,
                PrintStationId = stationId,
                PrinterDeviceId = deviceId,
                FileItemId = file.Id,
                Kind = PrintJobKinds.OwnerPhoto,
                State = PrintJobStates.Completed,
                CreatedAt = DateTime.UtcNow.AddDays(-30),
                CompletedAt = DateTime.UtcNow.AddDays(-30),
            });
            db.PrintJobSources.Add(PrintJobSource.Full(jobId, 0, file.Id));
            await db.SaveChangesAsync();
        }

        await TrashAsync(owner, file.Id);
        await BackdateTrashAsync(file.Id, minutesAgo: 600);

        var swept = await CreateSweeper(graceMinutes: 30).RunOnceAsync(default);
        await RunJanitorToCompletionAsync();

        Assert.Equal(1, swept);
        Assert.False(await QueryAsync(db => db.PrintJobSources.AnyAsync(s => s.FileItemId == file.Id)));
        Assert.False(await AnyBytesPresentAsync(keys));
        Assert.False(await QueryAsync(db => db.BlobObjects.AnyAsync(b => b.Id == blobId)));

        // History survives, with the dead file reference cleared rather than
        // the job destroyed — that is what the nullable column is for.
        var job = await QueryAsync(db => db.PrintJobs.AsNoTracking().SingleAsync(j => j.Id == jobId));
        Assert.Null(job.FileItemId);
        Assert.Equal(PrintJobStates.Completed, job.State);
    }

    // ================= shared / referenced objects =================

    [Fact]
    public async Task Shared_Physical_Object_Is_Not_Deleted_While_Still_Referenced()
    {
        var owner = await SeedUserAsync();
        // Identical bytes dedup onto ONE content-addressed blob.
        var first = await CreateTextFileAsync(owner, "a.txt", "same-content");
        var second = await CreateTextFileAsync(owner, "b.txt", "same-content");

        var (firstBlob, keys) = await OwnedStorageKeysAsync(first.Id);
        var (secondBlob, _) = await OwnedStorageKeysAsync(second.Id);
        Assert.Equal(firstBlob, secondBlob);

        await TrashAsync(owner, first.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            Assert.True(await files.PermanentDeleteAsync(owner, first.Id));
        }
        await RunJanitorToCompletionAsync();

        // The surviving file still holds a reference, so the bytes stay.
        Assert.True(await QueryAsync(db => db.BlobObjects.AnyAsync(b => b.Id == firstBlob)));
        Assert.True(await AnyBytesPresentAsync(keys));
        Assert.False(await QueryAsync(db => db.PendingBlobPurges.AnyAsync()));

        // Purging the LAST owner does release them.
        await TrashAsync(owner, second.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            Assert.True(await files.PermanentDeleteAsync(owner, second.Id));
        }
        await RunJanitorToCompletionAsync();

        Assert.False(await QueryAsync(db => db.BlobObjects.AnyAsync(b => b.Id == firstBlob)));
        Assert.False(await AnyBytesPresentAsync(keys));
    }

    // ================= failure semantics =================

    [Fact]
    public async Task Storage_Object_Already_Absent_Is_Handled_Idempotently()
    {
        var owner = await SeedUserAsync();
        var file = await CreateTextFileAsync(owner, "vanished.txt", "vanished");
        var (blobId, keys) = await OwnedStorageKeysAsync(file.Id);

        await TrashAsync(owner, file.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            Assert.True(await files.PermanentDeleteAsync(owner, file.Id));
        }

        // Remove the bytes out of band BEFORE the janitor runs.
        var storage = _factory.Services.GetRequiredService<IBlobStorage>();
        foreach (var key in keys)
        {
            await storage.DeleteAsync(key);
        }
        Assert.False(await AnyBytesPresentAsync(keys));

        var purged = await RunJanitorToCompletionAsync();

        // "Not found" is success: the row is reclaimed and nothing is pending.
        Assert.True(purged >= 1);
        Assert.False(await QueryAsync(db => db.BlobObjects.AnyAsync(b => b.Id == blobId)));
        Assert.False(await QueryAsync(db => db.PendingBlobPurges.AnyAsync()));
    }

    [Fact]
    public async Task Storage_Delete_Failure_Leaves_A_Retryable_State_And_Retry_Completes()
    {
        var owner = await SeedUserAsync();
        var file = await CreateTextFileAsync(owner, "stubborn.txt", "stubborn");
        var (blobId, keys) = await OwnedStorageKeysAsync(file.Id);

        await TrashAsync(owner, file.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            Assert.True(await files.PermanentDeleteAsync(owner, file.Id));
        }

        var realStorage = _factory.Services.GetRequiredService<IBlobStorage>();

        // Tick 1: the unlink fails.
        await RunJanitorToCompletionAsync(storageOverride: new FailingBlobStorage(realStorage));

        // The blob row is gone, but the storage key was NOT forgotten and the
        // bytes are still there — the state the contract requires.
        Assert.False(await QueryAsync(db => db.BlobObjects.AnyAsync(b => b.Id == blobId)));
        Assert.True(await AnyBytesPresentAsync(keys));

        var pendingRow = await QueryAsync(db =>
            db.PendingBlobPurges.AsNoTracking().SingleAsync(p => p.BlobObjectId == blobId));
        Assert.Contains(pendingRow.StorageKey, keys);
        Assert.True(pendingRow.AttemptCount >= 1);
        Assert.NotNull(pendingRow.LastAttemptAt);

        // Tick 2 with storage healthy: the retry completes.
        await RunJanitorToCompletionAsync();

        Assert.False(await AnyBytesPresentAsync(keys));
        Assert.False(await QueryAsync(db => db.PendingBlobPurges.AnyAsync()));
    }

    // ================= concurrency =================

    [Fact]
    public async Task Restore_Winning_The_Race_Deterministically_Blocks_Retention_Purge()
    {
        var owner = await SeedUserAsync();
        var file = await CreateImageFileAsync(owner, "contended.png", 7);
        var (blobId, keys) = await OwnedStorageKeysAsync(file.Id);

        await TrashAsync(owner, file.Id);
        await BackdateTrashAsync(file.Id, minutesAgo: 600);

        // The sweeper has already SELECTED this file as expired; the restore
        // commits before the purge gate runs.
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            Assert.NotNull(await files.RestoreAsync(owner, file.Id));
        }

        var swept = await CreateSweeper(graceMinutes: 30).RunOnceAsync(default);
        await RunJanitorToCompletionAsync();

        // Restore wins outright: nothing purged, and the physical object the UI
        // now claims is restored is genuinely still there.
        Assert.Equal(0, swept);
        var live = await QueryAsync(db =>
            db.FileItems.AsNoTracking().SingleAsync(f => f.Id == file.Id));
        Assert.Null(live.DeletedAt);
        Assert.True(await QueryAsync(db => db.BlobObjects.AnyAsync(b => b.Id == blobId)));
        foreach (var key in keys)
        {
            Assert.True(await AnyBytesPresentAsync(new[] { key }));
        }
        Assert.False(await QueryAsync(db => db.PendingBlobPurges.AnyAsync()));
    }

    [Fact]
    public async Task Purge_Winning_The_Race_Makes_Restore_Fail_Rather_Than_Lie()
    {
        var owner = await SeedUserAsync();
        var file = await CreateTextFileAsync(owner, "gone.txt", "gone");

        await TrashAsync(owner, file.Id);
        await BackdateTrashAsync(file.Id, minutesAgo: 600);

        Assert.Equal(1, await CreateSweeper(graceMinutes: 30).RunOnceAsync(default));

        // The other ordering: the purge committed first, so a restore arriving
        // afterwards must report failure. It must never report success for a
        // file whose bytes are scheduled for deletion.
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        Assert.Null(await files.RestoreAsync(owner, file.Id));
    }

    [Fact]
    public async Task Purge_Is_Idempotent_When_Repeated()
    {
        var owner = await SeedUserAsync();
        var file = await CreateTextFileAsync(owner, "twice.txt", "twice");

        await TrashAsync(owner, file.Id);
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();

        Assert.True(await files.PermanentDeleteAsync(owner, file.Id));
        // Second call: already gone -> false (404), never an exception.
        Assert.False(await files.PermanentDeleteAsync(owner, file.Id));
    }
}
