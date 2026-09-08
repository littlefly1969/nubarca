using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Integration;
using Xunit;

namespace NubArca.Api.Tests.Storage;

// The cross-process half of the storage-mutation invariant, on real PostgreSQL:
// advisory locks do not exist on SQLite, so only here can the shared/exclusive
// semantics actually be proven.
//
// The invariant under test:
//
//   No destructive physical delete may overlap the interval in which a writer
//   publishes (or reuses) those bytes and establishes durable ownership.
//
// Every test drives the interleaving deterministically with TaskCompletionSource
// handoffs — never a sleep — and asserts the one outcome that must be
// impossible: a live database owner pointing at bytes that are gone.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class StorageMutationLockConcurrencyTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;

    private string _storageRoot = string.Empty;
    private LocalFileSystemBlobStorage? _storage;
    private DbContextOptions<AppDbContext>? _dbOptions;

    public StorageMutationLockConcurrencyTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        if (!_fixture.Available)
        {
            return;
        }

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);
        _storage = new LocalFileSystemBlobStorage(
            Options.Create(new BlobStorageOptions { RootPath = _storageRoot }));
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

    private AppDbContext NewDb() => new(_dbOptions!);

    private string PathOf(string storageKey) =>
        Path.Combine(_storageRoot, storageKey.Replace('/', Path.DirectorySeparatorChar));

    private static string ShaOf(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static string KeyOf(string sha) => $"objects/{sha[..2]}/{sha[2..4]}/{sha}";

    // Bytes present on disk with NO owner and a committed purge intent — the
    // state BlobJanitor retries from.
    private async Task<(string Sha, string Key)> SeedUnownedWithPendingPurgeAsync(byte[] content)
    {
        await using var db = NewDb();
        await using var source = new MemoryStream(content, writable: false);
        var staged = await _storage!.StageAsync(source);
        var write = await _storage.PublishAsync(staged);

        db.PendingBlobPurges.Add(new PendingBlobPurge
        {
            BlobObjectId = Guid.NewGuid(),
            StorageKey = write.StorageKey,
            Sha256 = write.Sha256,
            CreatedAt = DateTime.UtcNow,
            AttemptCount = 0,
        });
        await db.SaveChangesAsync();
        return (write.Sha256, write.StorageKey);
    }

    private BlobJanitor NewJanitor(IServiceScopeFactory scopes) => new(
        scopes,
        Options.Create(new BlobJanitorOptions { Enabled = true, IntervalMinutes = 5, GraceMinutes = 0 }),
        TimeProvider.System,
        NullLogger<BlobJanitor>.Instance);

    // Minimal scope factory over a fresh AppDbContext + the real store, so the
    // janitor runs exactly as it does in production but against this fixture.
    private sealed class Scopes(
        DbContextOptions<AppDbContext> options, LocalFileSystemBlobStorage storage)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        private AppDbContext? _db;
        public IServiceScope CreateScope() =>
            new Scopes(options, storage);
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(AppDbContext)) return _db ??= new AppDbContext(options);
            if (serviceType == typeof(IBlobStorage)) return storage;
            if (serviceType == typeof(IDerivedBlobStorage)) return null;
            if (serviceType == typeof(HlsDerivativeStorage)) return null;
            if (serviceType == typeof(IBlobService))
                return new BlobService(storage, _db ??= new AppDbContext(options), TimeProvider.System);
            if (serviceType == typeof(NubArca.Api.Audit.IAuditLogger))
                return new NubArca.Api.Audit.AuditLogger(
                    _db ??= new AppDbContext(options),
                    TimeProvider.System,
                    NullLogger<NubArca.Api.Audit.AuditLogger>.Instance);
            return null;
        }
        public void Dispose() => _db?.Dispose();
    }

    // ---------------------------------------------------------------

    [SkippableFact]
    public async Task Writer_Holding_Shared_Lock_Blocks_The_Purge_And_Keeps_Its_Bytes()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var content = Encoding.UTF8.GetBytes("writer-wins-the-race");
        var (sha, key) = await SeedUnownedWithPendingPurgeAsync(content);

        var writerHasLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var janitorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Writer: takes the shared lock, then waits — deliberately holding the
        // critical section open across the janitor's entire attempt — before
        // committing ownership.
        var writer = Task.Run(async () =>
        {
            await using var db = NewDb();
            await using var tx = await db.Database.BeginTransactionAsync();
            await StorageMutationLock.AcquireSharedAsync(db, sha, default);
            writerHasLock.SetResult();

            await janitorStarted.Task;

            db.BlobObjects.Add(new BlobObject
            {
                Id = Guid.NewGuid(),
                Sha256 = sha,
                SizeBytes = content.LongLength,
                StorageKey = key,
                ReferenceCount = 1,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        });

        await writerHasLock.Task;

        var janitor = Task.Run(async () =>
        {
            janitorStarted.SetResult();
            // Blocks on the exclusive lock until the writer commits, then
            // revalidates and finds the new owner.
            await NewJanitor(new Scopes(_dbOptions!, _storage!)).RunOnceAsync(default);
        });

        await Task.WhenAll(writer, janitor);

        // The owner is live AND its bytes are there. The pending intent, now
        // obsolete, is gone rather than lying in wait for the next tick.
        await using var verify = NewDb();
        Assert.True(await verify.BlobObjects.AnyAsync(b => b.Sha256 == sha));
        Assert.True(File.Exists(PathOf(key)), "bytes of a live owner must survive");
        Assert.False(await verify.PendingBlobPurges.AnyAsync());
    }

    [SkippableFact]
    public async Task Deleter_Winning_Leaves_The_Writer_Free_To_Republish_Safely()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var content = Encoding.UTF8.GetBytes("deleter-wins-the-race");
        var (sha, key) = await SeedUnownedWithPendingPurgeAsync(content);

        // Deleter goes first, uncontended: unowned, so the bytes go.
        await NewJanitor(new Scopes(_dbOptions!, _storage!)).RunOnceAsync(default);
        Assert.False(File.Exists(PathOf(key)));
        await using (var afterPurge = NewDb())
        {
            Assert.False(await afterPurge.PendingBlobPurges.AnyAsync());
        }

        // The writer now arrives. Because the object is gone, its publish takes
        // the create branch and re-materialises the bytes; ownership then
        // commits over bytes that genuinely exist.
        await using var db = NewDb();
        var service = new BlobService(_storage!, db, TimeProvider.System);
        await using var ms = new MemoryStream(content, writable: false);
        var blob = await service.StoreAsync(ms);

        Assert.Equal(sha, blob.Sha256);
        Assert.True(File.Exists(PathOf(key)), "the writer must end up with real bytes");

        await using var verify = NewDb();
        var row = await verify.BlobObjects.AsNoTracking().SingleAsync(b => b.Sha256 == sha);
        Assert.Equal(key, row.StorageKey);
        Assert.True(File.Exists(PathOf(row.StorageKey)));
    }

    [SkippableFact]
    public async Task Pending_Retry_Is_Cancelled_When_A_BlobObject_Owns_The_Content_Again()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var content = Encoding.UTF8.GetBytes("adopted-by-a-new-blob-row");
        var (sha, key) = await SeedUnownedWithPendingPurgeAsync(content);

        // A NEW BlobObject id for the same content — which is exactly why the
        // revalidation matches on storage key and sha, not on the id the
        // pending record was created with.
        await using (var owner = NewDb())
        {
            owner.BlobObjects.Add(new BlobObject
            {
                Id = Guid.NewGuid(),
                Sha256 = sha,
                SizeBytes = content.LongLength,
                StorageKey = key,
                ReferenceCount = 1,
                CreatedAt = DateTime.UtcNow,
            });
            await owner.SaveChangesAsync();
        }

        await NewJanitor(new Scopes(_dbOptions!, _storage!)).RunOnceAsync(default);

        await using var verify = NewDb();
        Assert.True(File.Exists(PathOf(key)), "bytes owned again must survive");
        Assert.False(
            await verify.PendingBlobPurges.AnyAsync(),
            "the obsolete intent must be retired, not left to attack every tick");
    }

    [SkippableFact]
    public async Task Pending_Retry_Is_Cancelled_When_A_PrintJob_Owns_The_Artifact()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var content = Encoding.UTF8.GetBytes("adopted-by-a-print-job");
        var (_, key) = await SeedUnownedWithPendingPurgeAsync(content);

        await using (var owner = NewDb())
        {
            var ownerId = Guid.NewGuid();
            var stationId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            owner.Users.Add(new User
            {
                Id = ownerId,
                Email = $"print-{ownerId:N}@example.com",
                DisplayName = "Owner",
                CreatedAt = DateTime.UtcNow,
            });
            owner.PrintStations.Add(new PrintStation
            {
                Id = stationId,
                OwnerUserId = ownerId,
                Name = "Station",
                CreatedAt = DateTime.UtcNow,
            });
            owner.PrinterDevices.Add(new PrinterDevice
            {
                Id = deviceId,
                PrintStationId = stationId,
                DeviceKey = $"dev-{deviceId:N}",
                DisplayName = "Printer",
                AdapterKind = "test",
                LastSeenAt = DateTime.UtcNow,
            });
            owner.PrintJobs.Add(new PrintJob
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerId,
                PrintStationId = stationId,
                PrinterDeviceId = deviceId,
                Kind = PrintJobKinds.OwnerPhoto,
                Format = PrintFormats.Photo10x15,
                State = PrintJobStates.Ready,
                ArtifactStorageKey = key,
                ArtifactContentType = "image/png",
                ArtifactByteLength = content.LongLength,
                CreatedAt = DateTime.UtcNow,
                RenderedAt = DateTime.UtcNow,
            });
            await owner.SaveChangesAsync();
        }

        await NewJanitor(new Scopes(_dbOptions!, _storage!)).RunOnceAsync(default);

        await using var verify = NewDb();
        Assert.True(File.Exists(PathOf(key)), "a print-owned artifact must survive the retry");
        Assert.False(await verify.PendingBlobPurges.AnyAsync());
    }

    [SkippableFact]
    public async Task Owner_Arriving_During_The_Critical_Section_Yields_A_Safe_Serialised_Outcome()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var content = Encoding.UTF8.GetBytes("arrives-mid-critical-section");
        var (sha, key) = await SeedUnownedWithPendingPurgeAsync(content);

        // Both run at once, with no coordination beyond the lock itself. Either
        // ordering is legal; the illegal state is a live row over missing bytes.
        var writer = Task.Run(async () =>
        {
            await using var db = NewDb();
            var service = new BlobService(_storage!, db, TimeProvider.System);
            await using var ms = new MemoryStream(content, writable: false);
            await service.StoreAsync(ms);
        });
        var janitor = Task.Run(async () =>
            await NewJanitor(new Scopes(_dbOptions!, _storage!)).RunOnceAsync(default));

        await Task.WhenAll(writer, janitor);

        await using var verify = NewDb();
        var row = await verify.BlobObjects.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Sha256 == sha);

        Assert.NotNull(row);
        Assert.True(
            File.Exists(PathOf(row!.StorageKey)),
            "FORBIDDEN STATE: a live BlobObject owner whose physical bytes are gone");
        Assert.Equal(key, row.StorageKey);
    }

    [SkippableFact]
    public async Task Genuinely_Unowned_Pending_Purge_Still_Completes_And_Is_Idempotent()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var content = Encoding.UTF8.GetBytes("nobody-owns-me");
        var (_, key) = await SeedUnownedWithPendingPurgeAsync(content);

        var first = NewJanitor(new Scopes(_dbOptions!, _storage!));
        await first.RunOnceAsync(default);

        Assert.False(File.Exists(PathOf(key)), "an unowned object must still be reclaimed");
        await using (var afterFirst = NewDb())
        {
            Assert.False(await afterFirst.PendingBlobPurges.AnyAsync());
        }

        // Running again changes nothing and throws nothing.
        await NewJanitor(new Scopes(_dbOptions!, _storage!)).RunOnceAsync(default);
        await using var verify = NewDb();
        Assert.False(await verify.PendingBlobPurges.AnyAsync());
        Assert.False(File.Exists(PathOf(key)));
    }

    [SkippableFact]
    public async Task Concurrent_Writers_Of_The_Same_Content_Do_Not_Serialise_On_Each_Other()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        // Shared locks must not block shared locks: two writers of identical
        // content both hold the lock at once and both complete.
        var content = Encoding.UTF8.GetBytes("shared-locks-coexist");
        var sha = ShaOf(content);

        var firstHasLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHasLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Hold(TaskCompletionSource mine, Task theirs)
        {
            await using var db = NewDb();
            await using var tx = await db.Database.BeginTransactionAsync();
            await StorageMutationLock.AcquireSharedAsync(db, sha, default);
            mine.SetResult();
            // Completes only if the other writer also got the lock — proving
            // they are not mutually exclusive.
            await theirs;
            await tx.CommitAsync();
        }

        var a = Hold(firstHasLock, secondHasLock.Task);
        var b = Hold(secondHasLock, firstHasLock.Task);

        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(30));
    }
}
