using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Integration;
using Xunit;

namespace NubArca.Api.Tests.Storage;

// Real PostgreSQL via Testcontainers. Skipped when Docker is unavailable.
//
// These tests exercise:
//   - the atomic ExecuteUpdateAsync increment under contention
//   - the catch-on-unique-violation path when N writers concurrently insert
//     a brand-new SHA-256 (Npgsql throws PostgresException with SqlState 23505
//     on ux_blob_objects_sha256, which BlobService catches and retries as an
//     atomic increment)
//
// Each parallel Task.Run uses its own AppDbContext instance. The
// DbContextOptions is immutable and safe to share; LocalFileSystemBlobStorage
// is stateless once constructed.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class BlobServiceConcurrencyTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;

    private string _storageRoot = string.Empty;
    private LocalFileSystemBlobStorage? _storage;
    private DbContextOptions<AppDbContext>? _dbOptions;

    public BlobServiceConcurrencyTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        if (!_fixture.Available)
        {
            return;
        }

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-pg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);

        var blobOptions = Options.Create(new BlobStorageOptions { RootPath = _storageRoot });
        _storage = new LocalFileSystemBlobStorage(blobOptions);

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!)
            .Options;

        await _fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
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
            // best-effort
        }

        return Task.CompletedTask;
    }

    private BlobService NewService()
    {
        var db = new AppDbContext(_dbOptions!);
        return new BlobService(_storage!, db, TimeProvider.System);
    }

    [SkippableFact]
    public async Task ConcurrentIdenticalContent_LandsAtSingleRow_WithReferenceCountN()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        const int N = 20;
        var content = Encoding.UTF8.GetBytes("concurrent-identical-content");
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(content));

        var tasks = Enumerable.Range(0, N)
            .Select(_ => Task.Run(async () =>
            {
                var service = NewService();
                using var ms = new MemoryStream(content);
                return await service.StoreAsync(ms);
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(expectedSha, r.Sha256));

        await using var verify = new AppDbContext(_dbOptions!);
        var rows = await verify.BlobObjects.AsNoTracking().ToListAsync();

        Assert.Single(rows);
        Assert.Equal(expectedSha, rows[0].Sha256);
        Assert.Equal(N, rows[0].ReferenceCount);
        Assert.Equal(content.LongLength, rows[0].SizeBytes);

        var objectsDir = Path.Combine(_storageRoot, "objects");
        var stored = Directory.EnumerateFiles(objectsDir, "*", SearchOption.AllDirectories).ToArray();
        Assert.Single(stored);
    }

    [SkippableFact]
    public async Task ConcurrentDistinctContent_CreatesNRows_EachWithReferenceCountOne()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        const int N = 10;
        var contents = Enumerable.Range(0, N)
            .Select(i => Encoding.UTF8.GetBytes($"distinct-content-payload-{i:D2}"))
            .ToArray();

        var tasks = contents
            .Select(c => Task.Run(async () =>
            {
                var service = NewService();
                using var ms = new MemoryStream(c);
                return await service.StoreAsync(ms);
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(N, results.Select(r => r.Sha256).Distinct().Count());
        Assert.All(results, r => Assert.Equal(1, r.ReferenceCount));

        await using var verify = new AppDbContext(_dbOptions!);
        var rows = await verify.BlobObjects.AsNoTracking().ToListAsync();

        Assert.Equal(N, rows.Count);
        Assert.All(rows, r => Assert.Equal(1, r.ReferenceCount));

        var expectedSha = contents
            .Select(c => Convert.ToHexStringLower(SHA256.HashData(c)))
            .ToHashSet();
        Assert.Equal(expectedSha, rows.Select(r => r.Sha256).ToHashSet());

        var objectsDir = Path.Combine(_storageRoot, "objects");
        var stored = Directory.EnumerateFiles(objectsDir, "*", SearchOption.AllDirectories).ToArray();
        Assert.Equal(N, stored.Length);
    }
}
