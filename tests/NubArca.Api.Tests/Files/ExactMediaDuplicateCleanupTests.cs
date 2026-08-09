using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

public sealed class ExactMediaDuplicateCleanupTests
{
    private static async Task<FileItem> UploadAsync(
        SqliteWebApplicationFactory factory,
        Guid owner,
        string name,
        byte[] bytes,
        string mimeType,
        Guid? parentFolderId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IFileItemService>()
            .CreateAsync(owner, parentFolderId, name, mimeType, new MemoryStream(bytes));
    }

    private static async Task RunAsync(SqliteWebApplicationFactory factory, Guid owner)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ExactMediaDuplicateCleanupService>()
                .StartAsync(owner, default);
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobProcessor>()
                .ProcessAvailableAsync(maxJobs: 20);
        }
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("video/mp4")]
    public async Task SameCanonicalHash_ForDetectedMedia_RemovesNewerDuplicate(string mimeType)
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        var bytes = mimeType == "image/png" ? ImageFixtures.PlainPng() : ImageFixtures.MinimalMp4();
        var older = await UploadAsync(factory, owner, "older", bytes, mimeType);
        var newer = await UploadAsync(factory, owner, "newer", bytes, mimeType);

        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.FileItems.Where(f => f.Id == older.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.CreatedAt,
                    new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            await db.FileItems.Where(f => f.Id == newer.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.CreatedAt,
                    new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        }

        await RunAsync(factory, owner);

        await using var check = factory.Services.CreateAsyncScope();
        var dbCheck = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null((await dbCheck.FileItems.IgnoreQueryFilters().SingleAsync(f => f.Id == older.Id)).DeletedAt);
        Assert.NotNull((await dbCheck.FileItems.IgnoreQueryFilters().SingleAsync(f => f.Id == newer.Id)).DeletedAt);
        var run = await dbCheck.MediaDuplicateCleanupRuns.SingleAsync();
        Assert.Equal(1, run.DuplicateGroupCount);
        Assert.Equal(1, run.FilesRemovedCount);
        Assert.Equal(1, run.FilesRetainedCount);
    }

    [Fact]
    public async Task SameSizeDifferentHash_AndUnclassifiedSameHash_AreNotProcessed()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();

        var firstVideo = await UploadAsync(
            factory, owner, "first.mp4", ImageFixtures.MinimalMp4("isom"), "video/mp4");
        var secondVideo = await UploadAsync(
            factory, owner, "second.mp4", ImageFixtures.MinimalMp4("mp42"), "video/mp4");
        Assert.Equal(firstVideo.SizeBytes, secondVideo.SizeBytes);
        Assert.NotEqual(firstVideo.BlobObjectId, secondVideo.BlobObjectId);

        var unknownBytes = "identical unknown content"u8.ToArray();
        var unknownA = await UploadAsync(factory, owner, "unknown-a.bin", unknownBytes, "application/octet-stream");
        var unknownB = await UploadAsync(factory, owner, "unknown-b.bin", unknownBytes, "application/octet-stream");
        Assert.Equal(unknownA.BlobObjectId, unknownB.BlobObjectId);

        await RunAsync(factory, owner);

        await using var check = factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(4, await db.FileItems.CountAsync(f => f.OwnerUserId == owner && f.DeletedAt == null));
        Assert.Equal(0, (await db.MediaDuplicateCleanupRuns.SingleAsync()).FilesRemovedCount);
    }

    [Fact]
    public async Task EqualTimestamp_UsesFullPathThenId_AndThreeCopiesLeaveOne()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        Guid folderA;
        Guid folderZ;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
            folderZ = (await folders.CreateAsync(owner, null, "Zeta")).Id;
            folderA = (await folders.CreateAsync(owner, null, "Alpha")).Id;
        }

        var bytes = ImageFixtures.PlainPng();
        var z = await UploadAsync(factory, owner, "same.png", bytes, "image/png", folderZ);
        var a = await UploadAsync(factory, owner, "same.png", bytes, "image/png", folderA);
        var root = await UploadAsync(factory, owner, "zzz.png", bytes, "image/png");
        var tied = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.FileItems.Where(f => f.Id == z.Id || f.Id == a.Id || f.Id == root.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.CreatedAt, tied));
        }

        await RunAsync(factory, owner);

        await using var check = factory.Services.CreateAsyncScope();
        var dbCheck = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await dbCheck.FileItems.IgnoreQueryFilters()
            .Where(f => f.Id == z.Id || f.Id == a.Id || f.Id == root.Id)
            .ToListAsync();
        Assert.Equal(a.Id, Assert.Single(rows, f => f.DeletedAt == null).Id);
        Assert.Equal(2, rows.Count(f => f.DeletedAt != null));
        var run = await dbCheck.MediaDuplicateCleanupRuns.SingleAsync();
        Assert.Equal(1, run.DuplicateGroupCount);
        Assert.Equal(2, run.FilesRemovedCount);
        Assert.Equal(1, run.FilesRetainedCount);

        var blob = await dbCheck.BlobObjects.SingleAsync(b => b.Id == a.BlobObjectId);
        Assert.Equal(1, blob.ReferenceCount);
        Assert.Null(blob.PurgeEligibleAt);
    }

    [Fact]
    public async Task SameHashAcrossOwners_NeverCrossDeletes()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var alice = await factory.SeedUserAsync("alice@example.com");
        var bob = await factory.SeedUserAsync("bob@example.com");
        var bytes = ImageFixtures.PlainPng();
        var aliceOld = await UploadAsync(factory, alice, "a-old.png", bytes, "image/png");
        var aliceNew = await UploadAsync(factory, alice, "a-new.png", bytes, "image/png");
        var bobFile = await UploadAsync(factory, bob, "b.png", bytes, "image/png");
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var setupDb = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            await setupDb.FileItems.Where(f => f.Id == aliceOld.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.CreatedAt,
                    new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            await setupDb.FileItems.Where(f => f.Id == aliceNew.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.CreatedAt,
                    new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        }

        await RunAsync(factory, alice);

        await using var check = factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await db.FileItems.Where(f => f.OwnerUserId == alice && f.DeletedAt == null).ToListAsync());
        Assert.Null((await db.FileItems.SingleAsync(f => f.Id == bobFile.Id)).DeletedAt);
        Assert.Contains(aliceOld.Id, await db.FileItems.Where(f => f.OwnerUserId == alice && f.DeletedAt == null).Select(f => f.Id).ToListAsync());
        Assert.NotNull((await db.FileItems.IgnoreQueryFilters().SingleAsync(f => f.Id == aliceNew.Id)).DeletedAt);
    }

    [Fact]
    public async Task GuestPartyUpload_IsOutsideCleanupScope()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        var bytes = ImageFixtures.PlainPng();
        var libraryFile = await UploadAsync(factory, owner, "library.png", bytes, "image/png");
        var partyFile = await UploadAsync(factory, owner, "party.png", bytes, "image/png");
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var albumId = Guid.NewGuid();
            db.Albums.Add(new Album
            {
                Id = albumId, OwnerUserId = owner, Name = "Party", CreatedAt = now, UpdatedAt = now,
            });
            db.PartyUploadItems.Add(new PartyUploadItem
            {
                Id = Guid.NewGuid(), OwnerUserId = owner, AlbumId = albumId,
                FileItemId = partyFile.Id, Status = PartyUploadStatuses.Approved, UploadedAt = now,
            });
            await db.SaveChangesAsync();
        }

        await RunAsync(factory, owner);

        await using var check = factory.Services.CreateAsyncScope();
        var dbCheck = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null((await dbCheck.FileItems.SingleAsync(f => f.Id == libraryFile.Id)).DeletedAt);
        Assert.Null((await dbCheck.FileItems.SingleAsync(f => f.Id == partyFile.Id)).DeletedAt);
        Assert.Equal(0, (await dbCheck.MediaDuplicateCleanupRuns.SingleAsync()).FilesRemovedCount);
    }

    [Fact]
    public async Task GuardedDelete_RefusesWhenSurvivorDisappeared()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        var bytes = ImageFixtures.PlainPng();
        var survivor = await UploadAsync(factory, owner, "survivor.png", bytes, "image/png");
        var redundant = await UploadAsync(factory, owner, "redundant.png", bytes, "image/png");

        await using var scope = factory.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        Assert.True(await files.SoftDeleteAsync(owner, survivor.Id));
        Assert.False(await files.SoftDeleteExactMediaDuplicateAsync(owner, redundant.Id, survivor.Id));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null((await db.FileItems.SingleAsync(f => f.Id == redundant.Id)).DeletedAt);
    }

    [Fact]
    public async Task EndpointRequiresPermission_IsOwnerScoped_AndLeaksNoHashOrBlobId()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var anonymous = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync("/api/cloud-functions/media-duplicates/exact/runs", null)).StatusCode);

        var (_, ownerClient) = await factory.CreateAuthenticatedClientAsync("owner@example.com");
        var started = await ownerClient.PostAsync("/api/cloud-functions/media-duplicates/exact/runs", null);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var body = await started.Content.ReadFromJsonAsync<StartResponse>();
        Assert.NotNull(body);

        var status = await ownerClient.GetAsync(
            $"/api/cloud-functions/media-duplicates/exact/runs/{body!.RunId}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var json = await status.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sha", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blob", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ownerUserId", json, StringComparison.OrdinalIgnoreCase);

        var (_, otherClient) = await factory.CreateAuthenticatedClientAsync("other@example.com");
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await otherClient.GetAsync(
                $"/api/cloud-functions/media-duplicates/exact/runs/{body.RunId}")).StatusCode);
    }

    private sealed record StartResponse(Guid RunId, Guid JobId, string Status);
}
