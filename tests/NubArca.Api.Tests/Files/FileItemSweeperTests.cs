using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.ShareLinks;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Files;

public sealed class FileItemSweeperTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FileItemSweeperTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private FileItemSweeper CreateSweeper(bool enabled, int graceMinutes = 1440)
    {
        return new FileItemSweeper(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new FileItemSweeperOptions
            {
                Enabled = enabled,
                IntervalMinutes = 5,
                GraceMinutes = graceMinutes,
            }),
            TimeProvider.System,
            NullLogger<FileItemSweeper>.Instance);
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

    private async Task<Guid> SeedUserAsync(string email = "owner@example.com")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = id,
            Email = email,
            DisplayName = "Owner",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<FileItem> CreateFileAsync(Guid ownerId, string name, string content)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(
            ownerId, null, name, "text/plain", new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }

    private async Task SoftDeleteAsync(Guid ownerId, Guid fileId)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        await files.SoftDeleteAsync(ownerId, fileId);
    }

    private async Task BackdateDeletedAtAsync(Guid fileId, int minutesAgo)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.FileItems.FirstAsync(f => f.Id == fileId);
        row.DeletedAt = DateTime.UtcNow.AddMinutes(-minutesAgo);
        await db.SaveChangesAsync();
    }

    private async Task<bool> FileItemExistsAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FileItems.AnyAsync(f => f.Id == id);
    }

    [Fact]
    public async Task RunOnceAsync_When_Disabled_Does_Not_Purge()
    {
        var owner = await SeedUserAsync();
        var file = await CreateFileAsync(owner, "doc.txt", "x");
        await SoftDeleteAsync(owner, file.Id);
        await BackdateDeletedAtAsync(file.Id, minutesAgo: 9999);

        var swept = await CreateSweeper(enabled: false, graceMinutes: 0).RunOnceAsync(default);

        Assert.Equal(0, swept);
        Assert.True(await FileItemExistsAsync(file.Id));
    }

    [Fact]
    public async Task RunOnceAsync_Enabled_Purges_Old_Soft_Deleted_FileItem()
    {
        var owner = await SeedUserAsync();
        var file = await CreateFileAsync(owner, "doc.txt", "x");
        await SoftDeleteAsync(owner, file.Id);
        await BackdateDeletedAtAsync(file.Id, minutesAgo: 60);

        var swept = await CreateSweeper(enabled: true, graceMinutes: 30).RunOnceAsync(default);

        Assert.Equal(1, swept);
        Assert.False(await FileItemExistsAsync(file.Id));
    }

    [Fact]
    public async Task RunOnceAsync_Does_Not_Purge_Active_FileItem()
    {
        var owner = await SeedUserAsync();
        var file = await CreateFileAsync(owner, "doc.txt", "x");

        var swept = await CreateSweeper(enabled: true, graceMinutes: 0).RunOnceAsync(default);

        Assert.Equal(0, swept);
        Assert.True(await FileItemExistsAsync(file.Id));
    }

    [Fact]
    public async Task RunOnceAsync_Does_Not_Purge_FileItem_Inside_Grace_Window()
    {
        var owner = await SeedUserAsync();
        var file = await CreateFileAsync(owner, "doc.txt", "x");
        await SoftDeleteAsync(owner, file.Id);
        await BackdateDeletedAtAsync(file.Id, minutesAgo: 1);

        var swept = await CreateSweeper(enabled: true, graceMinutes: 60).RunOnceAsync(default);

        Assert.Equal(0, swept);
        Assert.True(await FileItemExistsAsync(file.Id));
    }

    [Fact]
    public async Task RunOnceAsync_Deletes_Related_ShareLinks()
    {
        var owner = await SeedUserAsync();
        var file = await CreateFileAsync(owner, "doc.txt", "x");

        Guid shareLinkId;
        using (var scope = _factory.Services.CreateScope())
        {
            var shareService = scope.ServiceProvider.GetRequiredService<IShareLinkService>();
            var created = await shareService.CreateAsync(owner, file.Id, null, null);
            Assert.NotNull(created);
            shareLinkId = created!.Id;
        }

        await SoftDeleteAsync(owner, file.Id);
        await BackdateDeletedAtAsync(file.Id, minutesAgo: 60);

        var swept = await CreateSweeper(enabled: true, graceMinutes: 30).RunOnceAsync(default);

        Assert.Equal(1, swept);
        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.FileItems.AnyAsync(f => f.Id == file.Id));
        Assert.False(await db.ShareLinks.AnyAsync(s => s.Id == shareLinkId));
    }

    [Fact]
    public async Task RunOnceAsync_Writes_FilePurge_Audit_Row()
    {
        var owner = await SeedUserAsync();
        var file = await CreateFileAsync(owner, "doc.txt", "x");
        await SoftDeleteAsync(owner, file.Id);
        await BackdateDeletedAtAsync(file.Id, minutesAgo: 60);

        await CreateSweeper(enabled: true, graceMinutes: 30).RunOnceAsync(default);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.FilePurge);

        Assert.Equal(owner, audit.UserId);
        Assert.Equal(AuditEntityTypes.File, audit.EntityType);
        Assert.Equal(file.Id, audit.EntityId);
    }

    [Fact]
    public async Task RunOnceAsync_Hard_Purge_Of_Image_FileItem_With_Thumbnail_Does_Not_FK_Violate()
    {
        var owner = await SeedUserAsync();

        Guid fileId;
        Guid thumbBlobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(300, 300);
            using var ms = new MemoryStream();
            img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
            var file = await files.CreateAsync(owner, null, "pic.png", "image/png", new MemoryStream(ms.ToArray()));
            fileId = file.Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var thumb = await db.FileThumbnails.AsNoTracking().SingleAsync(t => t.FileItemId == fileId);
            thumbBlobId = thumb.BlobObjectId;
        }

        await SoftDeleteAsync(owner, fileId);
        await BackdateDeletedAtAsync(fileId, minutesAgo: 60);

        var swept = await CreateSweeper(enabled: true, graceMinutes: 30).RunOnceAsync(default);

        Assert.Equal(1, swept);

        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verifyDb.FileItems.AnyAsync(f => f.Id == fileId));
        Assert.False(await verifyDb.FileThumbnails.AnyAsync(t => t.FileItemId == fileId));

        // Thumbnail blob ReferenceCount has been released back to 0 so the
        // BlobJanitor can reclaim it on a subsequent tick.
        var thumbBlob = await verifyDb.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == thumbBlobId);
        Assert.Equal(0, thumbBlob.ReferenceCount);
    }

    [Fact]
    public async Task Full_Chain_Sweeper_Then_Janitor_Reclaims_Row_And_Physical_File()
    {
        var owner = await SeedUserAsync();
        var file = await CreateFileAsync(owner, "doc.txt", "reclaim-end-to-end");

        // Snapshot the blob's storage key before anything is deleted.
        string storageKey;
        Guid blobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blob = await db.BlobObjects.AsNoTracking().SingleAsync();
            storageKey = blob.StorageKey;
            blobId = blob.Id;
        }

        await SoftDeleteAsync(owner, file.Id);
        await BackdateDeletedAtAsync(file.Id, minutesAgo: 60);

        var sweptCount = await CreateSweeper(enabled: true, graceMinutes: 30).RunOnceAsync(default);
        Assert.Equal(1, sweptCount);

        var storage = _factory.Services.GetRequiredService<IBlobStorage>();
        Assert.True(await storage.ExistsAsync(storageKey)); // sweeper does not touch physical files

        // The sweeper starts a fresh grace window at hard purge time. Blob age
        // and the earlier soft-delete timestamp must not bypass it.
        var purgedCount = await CreateJanitor(enabled: true, graceMinutes: 30).RunOnceAsync(default);
        Assert.Equal(0, purgedCount);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BlobObjects
                .Where(b => b.Id == blobId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(
                        b => b.PurgeEligibleAt,
                        _ => (DateTime?)DateTime.UtcNow.AddMinutes(-31)));
        }

        purgedCount = await CreateJanitor(enabled: true, graceMinutes: 30).RunOnceAsync(default);
        Assert.Equal(1, purgedCount);

        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await verifyDb.FileItems.CountAsync());
        Assert.Equal(0, await verifyDb.BlobObjects.CountAsync());
        Assert.False(await storage.ExistsAsync(storageKey));
    }
}
