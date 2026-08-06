using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Storage;

public sealed class BlobJanitorTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public BlobJanitorTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    // Video-hls slice 1: a purged blob's HLS ladder directory (keyed by the
    // blob's sha256) must be removed with the blob — it is regenerable cache
    // that would otherwise leak on disk forever.
    [Fact]
    public async Task Purge_Removes_The_Blob_Hls_Ladder_Directory()
    {
        var (id, _) = await SeedOrphanBlobAsync(referenceCount: 0, createdAtAgeMinutes: 9999);
        string sha;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            sha = (await db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == id)).Sha256;
        }
        var hls = _factory.Services.GetRequiredService<HlsDerivativeStorage>();
        var staging = hls.CreateStagingDirectory();
        File.WriteAllText(Path.Combine(staging, "master.m3u8"), "#EXTM3U");
        hls.Publish(sha, staging);
        Assert.True(hls.Exists(sha));

        var purged = await CreateJanitor(enabled: true, graceMinutes: 0).RunOnceAsync(default);

        Assert.Equal(1, purged);
        Assert.False(hls.Exists(sha));
    }

    private BlobJanitor CreateJanitor(bool enabled, int graceMinutes = 1440)
    {
        return new BlobJanitor(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new BlobJanitorOptions
            {
                Enabled = enabled,
                IntervalMinutes = 5,
                GraceMinutes = graceMinutes,
            }),
            TimeProvider.System,
            NullLogger<BlobJanitor>.Instance);
    }

    // Seeds an orphan BlobObject (no FileItem references) with the desired
    // ReferenceCount and CreatedAt age. We write the physical file via the real
    // IBlobStorage so DeleteAsync has something to remove.
    private async Task<(Guid Id, string StorageKey)> SeedOrphanBlobAsync(
        int referenceCount,
        int createdAtAgeMinutes,
        string? contentTag = null,
        int? purgeEligibleAgeMinutes = null)
    {
        contentTag ??= Guid.NewGuid().ToString("N");
        var content = Encoding.UTF8.GetBytes($"janitor-test-{contentTag}");

        var storage = _factory.Services.GetRequiredService<IBlobStorage>();
        var write = await storage.WriteAsync(new MemoryStream(content));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The physical write also implicitly creates the file, but BlobService
        // (which we did not call) is the only path that creates a BlobObject
        // row in the slice-3 flow. Insert one directly so we can dial in the
        // exact ReferenceCount and CreatedAt the test needs.
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = write.Sha256,
            SizeBytes = write.SizeBytes,
            StorageKey = write.StorageKey,
            ReferenceCount = referenceCount,
            CreatedAt = DateTime.UtcNow.AddMinutes(-createdAtAgeMinutes),
            PurgeEligibleAt = referenceCount == 0
                ? DateTime.UtcNow.AddMinutes(-(purgeEligibleAgeMinutes ?? createdAtAgeMinutes))
                : null,
        };
        db.BlobObjects.Add(blob);
        await db.SaveChangesAsync();
        return (blob.Id, blob.StorageKey);
    }

    private async Task<bool> BlobRowExistsAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BlobObjects.AnyAsync(b => b.Id == id);
    }

    [Fact]
    public async Task RunOnceAsync_When_Disabled_Does_Not_Purge()
    {
        var (id, _) = await SeedOrphanBlobAsync(referenceCount: 0, createdAtAgeMinutes: 9999);
        var janitor = CreateJanitor(enabled: false, graceMinutes: 0);

        var purged = await janitor.RunOnceAsync(default);

        Assert.Equal(0, purged);
        Assert.True(await BlobRowExistsAsync(id));
    }

    [Fact]
    public async Task RunOnceAsync_Enabled_Purges_Zero_Ref_Blob_Older_Than_Grace()
    {
        var (id, key) = await SeedOrphanBlobAsync(referenceCount: 0, createdAtAgeMinutes: 60);
        var storage = _factory.Services.GetRequiredService<IBlobStorage>();
        Assert.True(await storage.ExistsAsync(key));

        var janitor = CreateJanitor(enabled: true, graceMinutes: 30);

        var purged = await janitor.RunOnceAsync(default);

        Assert.Equal(1, purged);
        Assert.False(await BlobRowExistsAsync(id));
        Assert.False(await storage.ExistsAsync(key));
    }

    [Fact]
    public async Task RunOnceAsync_Does_Not_Purge_Blob_With_Nonzero_Reference_Count()
    {
        var (id, key) = await SeedOrphanBlobAsync(referenceCount: 1, createdAtAgeMinutes: 9999);
        var janitor = CreateJanitor(enabled: true, graceMinutes: 0);

        var purged = await janitor.RunOnceAsync(default);

        Assert.Equal(0, purged);
        Assert.True(await BlobRowExistsAsync(id));
        var storage = _factory.Services.GetRequiredService<IBlobStorage>();
        Assert.True(await storage.ExistsAsync(key));
    }

    [Fact]
    public async Task RunOnceAsync_Does_Not_Purge_Blob_Inside_Grace_Window()
    {
        // ReferenceCount=0 but created very recently — still inside grace.
        var (id, key) = await SeedOrphanBlobAsync(referenceCount: 0, createdAtAgeMinutes: 1);
        var janitor = CreateJanitor(enabled: true, graceMinutes: 60);

        var purged = await janitor.RunOnceAsync(default);

        Assert.Equal(0, purged);
        Assert.True(await BlobRowExistsAsync(id));
        var storage = _factory.Services.GetRequiredService<IBlobStorage>();
        Assert.True(await storage.ExistsAsync(key));
    }

    [Fact]
    public async Task RunOnceAsync_Uses_Purge_Eligibility_Not_Blob_Creation_Time()
    {
        // An old blob may have lost its final owner only moments ago. CreatedAt
        // must not let it bypass a fresh one-hour safety window.
        var (id, key) = await SeedOrphanBlobAsync(
            referenceCount: 0,
            createdAtAgeMinutes: 10_000,
            purgeEligibleAgeMinutes: 1);
        var janitor = CreateJanitor(enabled: true, graceMinutes: 60);

        var purged = await janitor.RunOnceAsync(default);

        Assert.Equal(0, purged);
        Assert.True(await BlobRowExistsAsync(id));
        var storage = _factory.Services.GetRequiredService<IBlobStorage>();
        Assert.True(await storage.ExistsAsync(key));
    }

    [Fact]
    public async Task RunOnceAsync_Does_Not_Purge_Zero_Ref_Blob_Without_Eligibility_Timestamp()
    {
        var (id, key) = await SeedOrphanBlobAsync(
            referenceCount: 0,
            createdAtAgeMinutes: 10_000);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BlobObjects
                .Where(b => b.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.PurgeEligibleAt, _ => null));
        }

        var purged = await CreateJanitor(enabled: true, graceMinutes: 0).RunOnceAsync(default);

        Assert.Equal(0, purged);
        Assert.True(await BlobRowExistsAsync(id));
        var storage = _factory.Services.GetRequiredService<IBlobStorage>();
        Assert.True(await storage.ExistsAsync(key));
    }

    [Fact]
    public async Task RunOnceAsync_Removes_Physical_File_And_Database_Row()
    {
        var (id, key) = await SeedOrphanBlobAsync(referenceCount: 0, createdAtAgeMinutes: 9999);
        var storage = _factory.Services.GetRequiredService<IBlobStorage>();

        await CreateJanitor(enabled: true, graceMinutes: 0).RunOnceAsync(default);

        Assert.False(await BlobRowExistsAsync(id));
        Assert.False(await storage.ExistsAsync(key));
    }

    [Fact]
    public async Task RunOnceAsync_Writes_BlobPurge_Audit_Row()
    {
        var (id, _) = await SeedOrphanBlobAsync(referenceCount: 0, createdAtAgeMinutes: 9999);

        await CreateJanitor(enabled: true, graceMinutes: 0).RunOnceAsync(default);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.BlobPurge);

        Assert.Null(audit.UserId);
        Assert.Equal(AuditEntityTypes.Blob, audit.EntityType);
        Assert.Equal(id, audit.EntityId);
    }

    [Fact]
    public async Task RunOnceAsync_Continues_On_Per_Blob_Failure()
    {
        // One purgeable blob + one blob blocked by a soft-deleted FileItem (FK
        // violation). The janitor should purge the first and skip the second
        // with only a warning.
        var (orphanId, orphanKey) = await SeedOrphanBlobAsync(
            referenceCount: 0, createdAtAgeMinutes: 9999, contentTag: "orphan");
        var (blockedId, blockedKey) = await SeedOrphanBlobAsync(
            referenceCount: 0, createdAtAgeMinutes: 9999, contentTag: "blocked");

        // Attach a soft-deleted FileItem to the "blocked" blob, plus a user to
        // satisfy the OwnerUserId FK.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = new User
            {
                Id = Guid.NewGuid(),
                Email = "blocker@example.com",
                DisplayName = "Blocker",
                CreatedAt = DateTime.UtcNow,
            };
            db.Users.Add(owner);
            db.FileItems.Add(new FileItem
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner.Id,
                ParentFolderId = null,
                BlobObjectId = blockedId,
                Name = "ghost.txt",
                MimeType = "text/plain",
                SizeBytes = 1,
                CreatedAt = DateTime.UtcNow,
                DeletedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var purged = await CreateJanitor(enabled: true, graceMinutes: 0).RunOnceAsync(default);

        Assert.Equal(1, purged);
        Assert.False(await BlobRowExistsAsync(orphanId));
        Assert.True(await BlobRowExistsAsync(blockedId));

        var storage = _factory.Services.GetRequiredService<IBlobStorage>();
        Assert.False(await storage.ExistsAsync(orphanKey));
        Assert.True(await storage.ExistsAsync(blockedKey));
    }
}
