using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Storage;

namespace NubArca.Api.Tests.Storage;

// These tests run against a SQLite in-memory database (in-process, no Docker).
// SQLite enforces unique constraints and supports real transactions + ExecuteUpdate,
// so the service's UPDATE-or-INSERT flow is exercised end-to-end at the SQL level.
//
// What this still does NOT cover: true concurrent writers racing on the same SHA-256.
// The catch-on-DbUpdateException branch in BlobService matches a Npgsql-specific
// PostgresException, so the race-recovery branch itself is verified only by code
// inspection here. A Testcontainers-based PostgreSQL test is the natural follow-up.
public sealed class BlobServiceTests : IDisposable
{
    private readonly string _storageRoot;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly LocalFileSystemBlobStorage _storage;
    private readonly BlobService _service;

    public BlobServiceTests()
    {
        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-svc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(dbOptions);
        _db.Database.EnsureCreated();

        var blobOptions = Options.Create(new BlobStorageOptions { RootPath = _storageRoot });
        _storage = new LocalFileSystemBlobStorage(blobOptions);

        _service = new BlobService(_storage, _db, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
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

    private static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Fact]
    public async Task StoreAsync_NewContent_Creates_BlobObject_With_ReferenceCount_One()
    {
        var content = Encoding.UTF8.GetBytes("hello-blob-service");
        var expectedSha = Sha256Hex(content);

        var result = await _service.StoreAsync(new MemoryStream(content));

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(expectedSha, result.Sha256);
        Assert.Equal(content.LongLength, result.SizeBytes);
        Assert.Equal($"objects/{expectedSha[..2]}/{expectedSha[2..4]}/{expectedSha}", result.StorageKey);
        Assert.Equal(1, result.ReferenceCount);
        Assert.NotEqual(default, result.CreatedAt);

        Assert.Single(_db.BlobObjects);
    }

    [Fact]
    public async Task StoreAsync_DuplicateContent_Increments_ReferenceCount_And_Reuses_Row()
    {
        var content = Encoding.UTF8.GetBytes("dedup-me");

        var first = await _service.StoreAsync(new MemoryStream(content));
        var second = await _service.StoreAsync(new MemoryStream(content));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.StorageKey, second.StorageKey);
        Assert.Equal(first.SizeBytes, second.SizeBytes);
        Assert.Equal(2, second.ReferenceCount);

        Assert.Single(_db.BlobObjects);
    }

    [Fact]
    public async Task StoreAsync_ThreeIdenticalCalls_LandAtReferenceCountThree()
    {
        var content = Encoding.UTF8.GetBytes("three-times");

        await _service.StoreAsync(new MemoryStream(content));
        await _service.StoreAsync(new MemoryStream(content));
        var third = await _service.StoreAsync(new MemoryStream(content));

        Assert.Equal(3, third.ReferenceCount);
        Assert.Single(_db.BlobObjects);
    }

    [Fact]
    public async Task StoreAsync_DifferentContent_Creates_Distinct_Rows()
    {
        var alpha = await _service.StoreAsync(new MemoryStream(Encoding.UTF8.GetBytes("alpha")));
        var beta = await _service.StoreAsync(new MemoryStream(Encoding.UTF8.GetBytes("beta")));

        Assert.NotEqual(alpha.Sha256, beta.Sha256);
        Assert.NotEqual(alpha.StorageKey, beta.StorageKey);
        Assert.NotEqual(alpha.Id, beta.Id);

        Assert.Equal(2, await _db.BlobObjects.CountAsync());
    }

    [Fact]
    public async Task StoreAsync_DuplicateContent_DoesNotCreateSecondPhysicalBlob()
    {
        var content = Encoding.UTF8.GetBytes("physical-dedup");

        await _service.StoreAsync(new MemoryStream(content));
        await _service.StoreAsync(new MemoryStream(content));

        var objectsDir = Path.Combine(_storageRoot, "objects");
        var stored = Directory.EnumerateFiles(objectsDir, "*", SearchOption.AllDirectories).ToArray();

        Assert.Single(stored);
    }

    [Fact]
    public async Task StoreAsync_PersistsStorageKeyAndSizeFromBlobStorage()
    {
        var content = Encoding.UTF8.GetBytes("persisted-fields");

        var result = await _service.StoreAsync(new MemoryStream(content));

        var row = await _db.BlobObjects.AsNoTracking().SingleAsync();
        Assert.Equal(result.Sha256, row.Sha256);
        Assert.Equal(result.StorageKey, row.StorageKey);
        Assert.Equal(result.SizeBytes, row.SizeBytes);
        Assert.Equal(1, row.ReferenceCount);
    }

    [Fact]
    public async Task ReleaseAsync_Decrements_ReferenceCount()
    {
        var stored = await _service.StoreAsync(new MemoryStream(Encoding.UTF8.GetBytes("a")));
        await _service.StoreAsync(new MemoryStream(Encoding.UTF8.GetBytes("a")));
        // ReferenceCount is now 2.

        await _service.ReleaseAsync(stored.Id);

        var row = await _db.BlobObjects.AsNoTracking().SingleAsync();
        Assert.Equal(1, row.ReferenceCount);
        Assert.Null(row.PurgeEligibleAt);
    }

    [Fact]
    public async Task ReleaseAsync_Stops_At_Zero_And_Never_Goes_Negative()
    {
        var stored = await _service.StoreAsync(new MemoryStream(Encoding.UTF8.GetBytes("once")));
        // ReferenceCount = 1.

        await _service.ReleaseAsync(stored.Id);
        await _service.ReleaseAsync(stored.Id);
        await _service.ReleaseAsync(stored.Id);

        var row = await _db.BlobObjects.AsNoTracking().SingleAsync();
        Assert.Equal(0, row.ReferenceCount);
        Assert.NotNull(row.PurgeEligibleAt);
    }

    [Fact]
    public async Task AcquireExistingAsync_Clears_Purge_Eligibility()
    {
        var stored = await _service.StoreAsync(new MemoryStream(Encoding.UTF8.GetBytes("reacquire")));
        await _service.ReleaseAsync(stored.Id);
        Assert.NotNull((await _db.BlobObjects.AsNoTracking().SingleAsync()).PurgeEligibleAt);

        await _service.AcquireExistingAsync(stored.Id);

        var row = await _db.BlobObjects.AsNoTracking().SingleAsync();
        Assert.Equal(1, row.ReferenceCount);
        Assert.Null(row.PurgeEligibleAt);
    }

    [Fact]
    public async Task ReleaseAsync_For_Missing_Blob_Is_NoOp()
    {
        await _service.ReleaseAsync(Guid.NewGuid());

        Assert.Equal(0, await _db.BlobObjects.CountAsync());
    }
}
