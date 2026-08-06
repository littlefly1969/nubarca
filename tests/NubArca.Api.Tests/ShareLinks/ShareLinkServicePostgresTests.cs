using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.ShareLinks;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Integration;
using Xunit;

namespace NubArca.Api.Tests.ShareLinks;

// Real PostgreSQL via Testcontainers. Skipped when Docker is unavailable.
//
// The SQLite ShareLinkServiceTests cover sequential semantics; this class
// asserts the SQL-level atomicity guarantee that the WHERE-encoded validity
// gate in ConsumeAsync is what prevents two concurrent consumers from pushing
// DownloadCount past MaxDownloads.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class ShareLinkServicePostgresTests : IAsyncLifetime, IDisposable
{
    private readonly PostgresContainerFixture _fixture;

    private DbContextOptions<AppDbContext>? _dbOptions;
    private string _storageRoot = string.Empty;
    private LocalFileSystemBlobStorage? _storage;

    public ShareLinkServicePostgresTests(PostgresContainerFixture fixture)
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

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-pg-share-{Guid.NewGuid():N}");
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

    private ShareLinkService NewShareLinkService()
    {
        var db = new AppDbContext(_dbOptions!);
        return new ShareLinkService(db, TimeProvider.System);
    }

    [SkippableFact]
    public async Task ConsumeAsync_Concurrent_With_MaxDownloads_1_Allows_Exactly_One_Success()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        // Seed owner, file, and share link with MaxDownloads = 1.
        var ownerId = Guid.NewGuid();
        Guid fileItemId;
        string token;

        await using (var seedDb = new AppDbContext(_dbOptions!))
        {
            seedDb.Users.Add(new User
            {
                Id = ownerId,
                Email = "share-race@example.com",
                DisplayName = "Owner",
                CreatedAt = DateTime.UtcNow,
            });
            await seedDb.SaveChangesAsync();

            var blobService = new BlobService(_storage!, seedDb, TimeProvider.System);
            var thumbs = new FileThumbnailService(
                seedDb, blobService, _storage!, new SyntheticVideoPosterProvider(),
                TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileThumbnailService>.Instance,
                Microsoft.Extensions.Options.Options.Create(new ImageProcessingOptions()));
            var files = new FileItemService(seedDb, blobService, thumbs, TimeProvider.System);
            var file = await files.CreateAsync(
                ownerId, null, "race.bin", "application/octet-stream",
                new MemoryStream(Encoding.UTF8.GetBytes("share-race-payload")));
            fileItemId = file.Id;

            var shareLinks = new ShareLinkService(seedDb, TimeProvider.System);
            var created = await shareLinks.CreateAsync(
                ownerId, fileItemId, expiresAt: null, maxDownloads: 1);
            Assert.NotNull(created);
            token = created!.Token;
        }

        const int N = 10;

        // Race N independent consumers on the same token. Each uses its own
        // AppDbContext + ShareLinkService so connections don't serialise inside
        // a single tracked context.
        var tasks = Enumerable.Range(0, N)
            .Select(_ => Task.Run(async () =>
            {
                var service = NewShareLinkService();
                return await service.ConsumeAsync(token);
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        var successes = results.Where(r => r is not null).ToList();
        var failures = results.Where(r => r is null).ToList();

        Assert.Single(successes);
        Assert.Equal(N - 1, failures.Count);

        // Successful result carries the file + owner. Confirm no token / hash
        // material is reachable through it.
        var winner = successes[0]!;
        Assert.Equal(fileItemId, winner.FileItemId);
        Assert.Equal(ownerId, winner.OwnerUserId);

        var resultMembers = winner.GetType().GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("Token", resultMembers);
        Assert.DoesNotContain("TokenHash", resultMembers);

        // DB invariants after the race: DownloadCount is exactly 1, LastAccessedAt
        // is set, MaxDownloads is unchanged.
        await using var verify = new AppDbContext(_dbOptions!);
        var link = await verify.ShareLinks.AsNoTracking()
            .SingleAsync(s => s.FileItemId == fileItemId);

        Assert.Equal(1, link.DownloadCount);
        Assert.Equal(1, link.MaxDownloads);
        Assert.NotNull(link.LastAccessedAt);
        Assert.Null(link.RevokedAt);
    }
}
