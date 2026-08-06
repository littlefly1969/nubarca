using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Integration;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Real PostgreSQL via Testcontainers. Verifies that restore respects the
// filtered unique constraint `ux_file_items_active_sibling_name`
// (`NULLS NOT DISTINCT` on PG 15+), and that the
// catch-on-`PostgresException 23505` race net inside RestoreAsync surfaces a
// `DuplicateFileNameException` if a concurrent writer fills the slot between
// the pre-check and the UPDATE.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class FileItemRestorePostgresTests : IAsyncLifetime, IDisposable
{
    private readonly PostgresContainerFixture _fixture;

    private DbContextOptions<AppDbContext>? _dbOptions;
    private string _storageRoot = string.Empty;
    private LocalFileSystemBlobStorage? _storage;

    public FileItemRestorePostgresTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        if (!_fixture.Available)
        {
            return;
        }

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!)
            .Options;

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-pg-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);

        var blobOptions = Options.Create(new BlobStorageOptions { RootPath = _storageRoot });
        _storage = new LocalFileSystemBlobStorage(blobOptions);

        await _fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private FileItemService NewService(AppDbContext db)
    {
        var blobService = new BlobService(_storage!, db, TimeProvider.System);
        var thumbs = new FileThumbnailService(
            db, blobService, _storage!, new SyntheticVideoPosterProvider(),
            TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()));
        return new FileItemService(db, blobService, thumbs, TimeProvider.System);
    }

    [SkippableFact]
    public async Task RestoreAsync_Succeeds_Against_Real_PostgreSQL()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var ownerId = Guid.NewGuid();
        await using (var seed = new AppDbContext(_dbOptions!))
        {
            seed.Users.Add(new User
            {
                Id = ownerId,
                Email = "restore-pg@example.com",
                DisplayName = "Owner",
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        Guid fileId;
        await using (var db = new AppDbContext(_dbOptions!))
        {
            var service = NewService(db);
            var file = await service.CreateAsync(
                ownerId, null, "doc.txt", "text/plain",
                new MemoryStream(Encoding.UTF8.GetBytes("v1")));
            fileId = file.Id;
            await service.SoftDeleteAsync(ownerId, file.Id);
        }

        // Pre-state: soft-deleted, blob refcount = 0.
        await using (var verify = new AppDbContext(_dbOptions!))
        {
            var preBlob = await verify.BlobObjects.AsNoTracking().SingleAsync();
            Assert.Equal(0, preBlob.ReferenceCount);
        }

        await using (var db = new AppDbContext(_dbOptions!))
        {
            var service = NewService(db);
            var restored = await service.RestoreAsync(ownerId, fileId);
            Assert.NotNull(restored);
            Assert.Null(restored!.DeletedAt);
        }

        await using var post = new AppDbContext(_dbOptions!);
        var postBlob = await post.BlobObjects.AsNoTracking().SingleAsync();
        Assert.Equal(1, postBlob.ReferenceCount);
    }

    [SkippableFact]
    public async Task RestoreAsync_With_Active_Sibling_Conflict_Is_Rejected_On_PG()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var ownerId = Guid.NewGuid();
        await using (var seed = new AppDbContext(_dbOptions!))
        {
            seed.Users.Add(new User
            {
                Id = ownerId,
                Email = "restore-pg-dup@example.com",
                DisplayName = "Owner",
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        Guid soft;
        await using (var db = new AppDbContext(_dbOptions!))
        {
            var service = NewService(db);
            var first = await service.CreateAsync(
                ownerId, null, "race.bin", "application/octet-stream",
                new MemoryStream(Encoding.UTF8.GetBytes("v1")));
            soft = first.Id;
            await service.SoftDeleteAsync(ownerId, first.Id);

            // Occupy the slot with a brand-new active file using the same name.
            await service.CreateAsync(
                ownerId, null, "race.bin", "application/octet-stream",
                new MemoryStream(Encoding.UTF8.GetBytes("v2")));
        }

        await using (var db = new AppDbContext(_dbOptions!))
        {
            var service = NewService(db);
            await Assert.ThrowsAsync<DuplicateFileNameException>(
                () => service.RestoreAsync(ownerId, soft));
        }

        // Soft-deleted file still soft-deleted; its blob still at 0.
        await using var verify = new AppDbContext(_dbOptions!);
        var stillSoft = await verify.FileItems.AsNoTracking().FirstAsync(f => f.Id == soft);
        Assert.NotNull(stillSoft.DeletedAt);
    }
}
