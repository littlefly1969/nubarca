using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Integration;
using Xunit;

namespace NubArca.Api.Tests.Storage;

// Destructive `storage reconcile` versus a writer of the SAME content, on real
// PostgreSQL — the only place the shared/exclusive advisory lock actually
// exists.
//
// Correctness here is the exclusive StorageMutationLock held from before the
// final ownership revalidation until after the unlink, NOT MinimumOrphanAge:
// every test below runs with the age policy set to zero, so nothing but the
// lock can be protecting the data. Both legal orderings are exercised, and the
// assertion in each is the same forbidden state — a live database owner whose
// physical bytes are gone.
//
// Interleavings are driven with TaskCompletionSource handoffs. No sleeps.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class ReconcileWriterOrderingTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;

    private string _storageRoot = string.Empty;
    private LocalFileSystemBlobStorage? _storage;
    private DbContextOptions<AppDbContext>? _dbOptions;

    public ReconcileWriterOrderingTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        if (!_fixture.Available)
        {
            return;
        }

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-order-{Guid.NewGuid():N}");
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

    // Age policy deliberately ZERO: if anything survives below, the exclusive
    // lock is the only thing that can have saved it.
    private static StorageReconciliationOptions Destructive() => new()
    {
        DryRun = false,
        DeleteOrphans = true,
        MinimumOrphanAge = TimeSpan.Zero,
    };

    // An unowned object physically present in the store.
    private async Task<(string Sha, string Key)> SeedUnownedObjectAsync(byte[] content)
    {
        await using var source = new MemoryStream(content, writable: false);
        var staged = await _storage!.StageAsync(source);
        var write = await _storage.PublishAsync(staged);
        return (write.Sha256, write.StorageKey);
    }

    private async Task<StorageReconciliationResult> ReconcileAsync(
        StorageReconciliationOptions options)
    {
        await using var db = NewDb();
        var svc = new StorageReconciliationService(db, _storage!);
        return await svc.RunAsync(options);
    }

    // =====================================================================
    // A) WRITER WINS
    // =====================================================================

    [SkippableFact]
    public async Task Writer_Wins_Reconcile_Waits_Revalidates_And_Does_Not_Delete()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var content = Encoding.UTF8.GetBytes("reconcile-writer-wins");
        var (sha, key) = await SeedUnownedObjectAsync(content);

        var writerHoldsLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconcileStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The writer takes the shared lock, reuses the already-published bytes,
        // and only THEN commits ownership — holding the critical section open
        // across the reconcile's entire attempt.
        var writer = Task.Run(async () =>
        {
            await using var db = NewDb();
            await using var tx = await db.Database.BeginTransactionAsync();
            await StorageMutationLock.AcquireSharedAsync(db, sha, default);

            // Publish/reuse decision, inside the lock: the bytes are already
            // there, so this is the reuse branch — the one that used to be able
            // to commit onto bytes a sweep had just removed.
            await using var source = new MemoryStream(content, writable: false);
            var staged = await _storage!.StageAsync(source);
            var write = await _storage.PublishAsync(staged);
            Assert.True(write.AlreadyExisted, "this test needs the reuse branch");

            writerHoldsLock.SetResult();
            await reconcileStarted.Task;

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

        await writerHoldsLock.Task;

        StorageReconciliationResult? result = null;
        var reconcile = Task.Run(async () =>
        {
            reconcileStarted.SetResult();
            // Blocks on the exclusive lock until the writer commits, then
            // revalidates and finds the owner.
            result = await ReconcileAsync(Destructive());
        });

        await Task.WhenAll(writer, reconcile);

        Assert.NotNull(result);
        Assert.Equal(0, result!.OrphansDeleted);
        Assert.Equal(1, result.OrphansOwnedAtRevalidation);
        // Age policy was zero, so nothing was skipped for being recent.
        Assert.Equal(0, result.RecentPhysicalObjectsSkipped);

        await using var verify = NewDb();
        var row = await verify.BlobObjects.AsNoTracking().SingleAsync(b => b.Sha256 == sha);
        Assert.True(
            File.Exists(PathOf(row.StorageKey)),
            "FORBIDDEN STATE: a live owner whose physical bytes were swept");
    }

    // =====================================================================
    // B) RECONCILE WINS
    // =====================================================================

    [SkippableFact]
    public async Task Reconcile_Wins_Then_The_Writer_Republishes_And_Commits_Over_Real_Bytes()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var content = Encoding.UTF8.GetBytes("reconcile-deleter-wins");
        var (sha, key) = await SeedUnownedObjectAsync(content);

        // Reconcile goes first and completes: genuinely unowned, so it deletes.
        var result = await ReconcileAsync(Destructive());

        Assert.Equal(1, result.OrphanPhysicalObjects);
        Assert.Equal(1, result.OrphansDeleted);
        Assert.Equal(0, result.OrphansOwnedAtRevalidation);
        Assert.False(File.Exists(PathOf(key)), "an unowned object must still be reclaimable");

        // The writer arrives afterwards. Because the object is gone, its publish
        // takes the CREATE branch and re-materialises the bytes; ownership then
        // commits over bytes that genuinely exist.
        await using var db = NewDb();
        var service = new BlobService(_storage!, db, TimeProvider.System);
        await using var ms = new MemoryStream(content, writable: false);
        var blob = await service.StoreAsync(ms);

        Assert.Equal(sha, blob.Sha256);
        Assert.Equal(key, blob.StorageKey);
        Assert.True(
            File.Exists(PathOf(blob.StorageKey)),
            "FORBIDDEN STATE: the writer committed ownership over missing bytes");

        // And the store is coherent afterwards: nothing orphaned, nothing missing.
        var after = await ReconcileAsync(new StorageReconciliationOptions { DryRun = true });
        Assert.Equal(0, after.OrphanPhysicalObjects);
        Assert.Equal(0, after.MissingPhysicalObjects);
    }

    // =====================================================================
    // Uncoordinated: whichever wins, the forbidden state must be unreachable.
    // =====================================================================

    [SkippableFact]
    public async Task Either_Ordering_Never_Leaves_A_Live_Owner_Over_Missing_Bytes()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var content = Encoding.UTF8.GetBytes("reconcile-uncoordinated-race");
        var (sha, key) = await SeedUnownedObjectAsync(content);

        var writer = Task.Run(async () =>
        {
            await using var db = NewDb();
            var service = new BlobService(_storage!, db, TimeProvider.System);
            await using var ms = new MemoryStream(content, writable: false);
            await service.StoreAsync(ms);
        });
        var reconcile = Task.Run(async () => await ReconcileAsync(Destructive()));

        await Task.WhenAll(writer, reconcile);

        await using var verify = NewDb();
        var row = await verify.BlobObjects.AsNoTracking().SingleOrDefaultAsync(b => b.Sha256 == sha);

        // The writer always ends up owning the content; the only question is
        // whether it reused the bytes or had to re-create them. Either way the
        // bytes must be there.
        Assert.NotNull(row);
        Assert.Equal(key, row!.StorageKey);
        Assert.True(
            File.Exists(PathOf(row.StorageKey)),
            "FORBIDDEN STATE: a live BlobObject owner whose physical bytes are gone");
    }

    [SkippableFact]
    public async Task Destructive_Sweep_Is_Idempotent_Across_Reruns()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var (_, key) = await SeedUnownedObjectAsync(Encoding.UTF8.GetBytes("reconcile-rerun"));

        var first = await ReconcileAsync(Destructive());
        var second = await ReconcileAsync(Destructive());

        Assert.Equal(1, first.OrphansDeleted);
        Assert.Equal(0, second.OrphansDeleted);
        Assert.Equal(0, second.OrphanPhysicalObjects);
        Assert.Equal(0, second.MissingPhysicalObjects);
        Assert.False(File.Exists(PathOf(key)));
    }

    [SkippableFact]
    public async Task Dry_Run_Takes_No_Lock_And_Mutates_Nothing()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var (_, key) = await SeedUnownedObjectAsync(Encoding.UTF8.GetBytes("reconcile-dry-run"));

        var result = await ReconcileAsync(new StorageReconciliationOptions
        {
            DryRun = true,
            MinimumOrphanAge = TimeSpan.Zero,
        });

        Assert.True(result.DryRun);
        Assert.Equal(1, result.OrphanPhysicalObjects);
        Assert.Equal(0, result.OrphansDeleted);
        Assert.Equal(0, result.OrphansOwnedAtRevalidation);
        Assert.True(File.Exists(PathOf(key)));
    }
}
