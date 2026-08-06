using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Integration;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Real PostgreSQL via Testcontainers. Skipped when Docker is unavailable.
//
// Covers the SQL-level race that the SQLite unit tests cannot reach:
// concurrent CreateAsync calls hitting the ux_file_items_active_sibling_name
// unique constraint. Tests both the non-null parent case and the null parent
// case (which exercises the NULLS NOT DISTINCT annotation on PostgreSQL 15+).
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class FileItemServicePostgresTests : IAsyncLifetime, IDisposable
{
    private readonly PostgresContainerFixture _fixture;

    private DbContextOptions<AppDbContext>? _dbOptions;
    private string _storageRoot = string.Empty;
    private LocalFileSystemBlobStorage? _storage;

    public FileItemServicePostgresTests(PostgresContainerFixture fixture)
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

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-pg-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);

        var blobOptions = Options.Create(new BlobStorageOptions { RootPath = _storageRoot });
        _storage = new LocalFileSystemBlobStorage(blobOptions);

        await _fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, recursive: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    private FileItemService NewService()
    {
        var db = new AppDbContext(_dbOptions!);
        var blobService = new BlobService(_storage!, db, TimeProvider.System);
        var thumbs = new FileThumbnailService(
            db, blobService, _storage!, new SyntheticVideoPosterProvider(),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FileThumbnailService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new ImageProcessingOptions()));
        return new FileItemService(db, blobService, thumbs, TimeProvider.System);
    }

    private async Task<Guid> SeedOwnerAsync(string email = "owner@example.com")
    {
        var id = Guid.NewGuid();
        await using var db = new AppDbContext(_dbOptions!);
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

    private async Task<Guid> SeedFolderAsync(Guid ownerId, string name = "Parent")
    {
        var id = Guid.NewGuid();
        await using var db = new AppDbContext(_dbOptions!);
        db.Folders.Add(new Folder
        {
            Id = id,
            OwnerUserId = ownerId,
            ParentFolderId = null,
            Name = name,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static (int Successes, int Duplicates, int Other) Tally<TResult>(
        IEnumerable<(bool Success, Exception? Exception)> results)
    {
        var successes = results.Count(r => r.Success);
        var duplicates = results.Count(r => r.Exception is DuplicateFileNameException);
        var other = results.Count() - successes - duplicates;
        return (successes, duplicates, other);
    }

    [SkippableFact]
    public async Task CreateAsync_Concurrent_Same_Name_Under_Same_Folder_Allows_Exactly_One()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var ownerId = await SeedOwnerAsync("nested@example.com");
        var folderId = await SeedFolderAsync(ownerId, "Photos");

        const int N = 10;
        const string name = "race.jpg";

        var tasks = Enumerable.Range(0, N)
            .Select(i => Task.Run<(bool Success, Exception? Exception)>(async () =>
            {
                try
                {
                    var service = NewService();
                    var bytes = Encoding.UTF8.GetBytes($"payload-{i:D2}");
                    await service.CreateAsync(ownerId, folderId, name, "image/jpeg", new MemoryStream(bytes));
                    return (true, null);
                }
                catch (Exception ex)
                {
                    return (false, ex);
                }
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var tally = Tally<object>(results);

        Assert.Equal(1, tally.Successes);
        Assert.Equal(N - 1, tally.Duplicates);
        Assert.Equal(0, tally.Other);

        await using var verify = new AppDbContext(_dbOptions!);
        Assert.Equal(1, await verify.FileItems.CountAsync(f => f.Name == name && f.ParentFolderId == folderId));
    }

    [SkippableFact]
    public async Task CreateAsync_Race_Losers_Release_Their_Blob_References()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var ownerId = await SeedOwnerAsync("refcount-race@example.com");
        var folderId = await SeedFolderAsync(ownerId, "Race");

        const int N = 8;
        const string name = "ref-race.bin";

        // Each worker writes distinct content, so each spawns a NEW BlobObject
        // with ReferenceCount = 1. Only one wins the FileItem insert; the N-1
        // losers must release their freshly-created blob back to ReferenceCount = 0.
        var tasks = Enumerable.Range(0, N)
            .Select(i => Task.Run<(bool Success, Exception? Exception)>(async () =>
            {
                try
                {
                    var service = NewService();
                    var bytes = Encoding.UTF8.GetBytes($"distinct-payload-{i:D2}");
                    await service.CreateAsync(ownerId, folderId, name, "application/octet-stream", new MemoryStream(bytes));
                    return (true, null);
                }
                catch (Exception ex)
                {
                    return (false, ex);
                }
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var tally = Tally<object>(results);
        Assert.Equal(1, tally.Successes);
        Assert.Equal(N - 1, tally.Duplicates);
        Assert.Equal(0, tally.Other);

        await using var verify = new AppDbContext(_dbOptions!);

        var fileItems = await verify.FileItems.AsNoTracking().Where(f => f.Name == name).ToListAsync();
        Assert.Single(fileItems);
        var winnerBlobId = fileItems[0].BlobObjectId;

        var allBlobs = await verify.BlobObjects.AsNoTracking().ToListAsync();
        Assert.Equal(N, allBlobs.Count);

        var winner = Assert.Single(allBlobs, b => b.Id == winnerBlobId);
        Assert.Equal(1, winner.ReferenceCount);

        // Every losing-race blob has been released back to zero.
        var losers = allBlobs.Where(b => b.Id != winnerBlobId).ToList();
        Assert.Equal(N - 1, losers.Count);
        Assert.All(losers, b => Assert.Equal(0, b.ReferenceCount));
    }

    [SkippableFact]
    public async Task CreateAsync_Concurrent_Same_Name_At_Root_Allows_Exactly_One()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var ownerId = await SeedOwnerAsync("root@example.com");

        const int N = 10;
        const string name = "root-race.txt";

        var tasks = Enumerable.Range(0, N)
            .Select(i => Task.Run<(bool Success, Exception? Exception)>(async () =>
            {
                try
                {
                    var service = NewService();
                    var bytes = Encoding.UTF8.GetBytes($"root-payload-{i:D2}");
                    await service.CreateAsync(ownerId, parentFolderId: null, name, "text/plain", new MemoryStream(bytes));
                    return (true, null);
                }
                catch (Exception ex)
                {
                    return (false, ex);
                }
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var tally = Tally<object>(results);

        Assert.Equal(1, tally.Successes);
        Assert.Equal(N - 1, tally.Duplicates);
        Assert.Equal(0, tally.Other);

        await using var verify = new AppDbContext(_dbOptions!);
        Assert.Equal(1, await verify.FileItems.CountAsync(f => f.Name == name && f.ParentFolderId == null));
    }
}
