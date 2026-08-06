using Microsoft.Extensions.Options;
using NubArca.Api.Admin;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace NubArca.Api.Tests.Admin;

// Slice 78.1 — real PostgreSQL regression for the admin Storage Stats 22P02
// failure. RawMetadataJson is a `jsonb` column; the previous
// `RawMetadataJson != ""` predicate compiled to `... <> ''`, and `''` is not
// valid JSON, so PostgreSQL raised "22P02: invalid input syntax for type json"
// and the endpoint 500'd. SQLite stores jsonb as dynamic text so the bug was
// invisible there — this test runs against a real Postgres container.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class StorageStatsPostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    public StorageStatsPostgresTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        if (!_fixture.Available) return;
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!)
            .Options;
        await _fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private StorageStatsService NewService(AppDbContext db) =>
        new(
            db,
            TimeProvider.System,
            new StaticOptionsMonitor<FileItemSweeperOptions>(new()),
            new StaticOptionsMonitor<BlobJanitorOptions>(new()),
            new StaticOptionsMonitor<BlobStorageOptions>(new() { RootPath = "/tmp/nc-stats-test" }));

    [SkippableFact]
    public async Task GetAsync_With_Jsonb_RawMetadata_Does_Not_Throw_22P02()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using (var seed = new AppDbContext(_dbOptions!))
        {
            var blob = new BlobObject
            {
                Id = Guid.NewGuid(),
                Sha256 = new string('a', 64),
                SizeBytes = 123,
                StorageKey = "objects/aa/aa/" + new string('a', 64),
                ReferenceCount = 1,
                CreatedAt = DateTime.UtcNow,
            };
            seed.BlobObjects.Add(blob);
            seed.BlobMetadata.Add(new BlobMetadata
            {
                Id = Guid.NewGuid(),
                BlobObjectId = blob.Id,
                SizeBytes = 123,
                MediaCategory = MediaCategories.Image,
                ThumbnailStatus = MetadataStatuses.Completed,
                ExtractionStatus = MetadataStatuses.Completed,
                // A valid jsonb document — the production scenario that broke
                // the old `!= ""` comparison.
                RawMetadataJson = "{\"camera\":\"NanoCam\",\"iso\":100}",
            });
            await seed.SaveChangesAsync();
        }

        await using var db = new AppDbContext(_dbOptions!);
        var service = NewService(db);

        // Must NOT throw 22P02 (the regression). Before the fix this line threw
        // Npgsql.PostgresException and the endpoint returned 500.
        var stats = await service.GetAsync();

        Assert.NotNull(stats);
        Assert.Equal(1, stats.SensitiveAggregates.BlobsWithRawDocument);
        // Core counts present + correct.
        Assert.Equal(1, stats.Blobs.Total);
    }

    [SkippableFact]
    public async Task GetAsync_With_Null_RawMetadata_Counts_Zero()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using (var seed = new AppDbContext(_dbOptions!))
        {
            var blob = new BlobObject
            {
                Id = Guid.NewGuid(),
                Sha256 = new string('b', 64),
                SizeBytes = 5,
                StorageKey = "objects/bb/bb/" + new string('b', 64),
                ReferenceCount = 1,
                CreatedAt = DateTime.UtcNow,
            };
            seed.BlobObjects.Add(blob);
            seed.BlobMetadata.Add(new BlobMetadata
            {
                Id = Guid.NewGuid(),
                BlobObjectId = blob.Id,
                SizeBytes = 5,
                MediaCategory = MediaCategories.Other,
                ThumbnailStatus = MetadataStatuses.Skipped,
                ExtractionStatus = MetadataStatuses.Skipped,
                RawMetadataJson = null, // no raw doc
            });
            await seed.SaveChangesAsync();
        }

        await using var db = new AppDbContext(_dbOptions!);
        var stats = await NewService(db).GetAsync();

        Assert.NotNull(stats);
        Assert.Equal(0, stats.SensitiveAggregates.BlobsWithRawDocument);
    }
}

// Minimal IOptionsMonitor for tests: always returns the same value.
file sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
