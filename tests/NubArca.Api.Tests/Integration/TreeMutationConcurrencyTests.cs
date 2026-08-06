using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Storage;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// Real PostgreSQL via Testcontainers. Proves the per-owner advisory-lock
// strategy (`TreeMutationLock.AcquireAsync` inside every dangerous mutation)
// holds the tree invariants under genuinely-concurrent writers:
//   * no active child under a soft-deleted parent;
//   * no folder cycle from reciprocal moves;
//   * no restore-into-deleted-parent slip;
//   * tree stays queryable, no orphans, after every race.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class TreeMutationConcurrencyTests : IAsyncLifetime, IDisposable
{
    private readonly PostgresContainerFixture _fixture;

    private DbContextOptions<AppDbContext>? _dbOptions;
    private string _storageRoot = string.Empty;
    private LocalFileSystemBlobStorage? _storage;

    public TreeMutationConcurrencyTests(PostgresContainerFixture fixture)
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

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-pg-tree-{Guid.NewGuid():N}");
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

    // ---- service builders -------------------------------------------------

    private AppDbContext NewDb() => new(_dbOptions!);

    private FolderService NewFolderService(AppDbContext db) => new(db, TimeProvider.System);

    private FileItemService NewFileService(AppDbContext db)
    {
        var blob = new BlobService(_storage!, db, TimeProvider.System);
        var thumbs = new FileThumbnailService(
            db, blob, _storage!, new SyntheticVideoPosterProvider(),
            TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()));
        return new FileItemService(db, blob, thumbs, TimeProvider.System);
    }

    // ---- seeding ----------------------------------------------------------

    private async Task<Guid> SeedOwnerAsync(string email)
    {
        var id = Guid.NewGuid();
        await using var db = NewDb();
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

    private async Task<Guid> SeedFolderAsync(Guid ownerId, Guid? parentId, string name)
    {
        await using var db = NewDb();
        var folders = NewFolderService(db);
        var folder = await folders.CreateAsync(ownerId, parentId, name);
        return folder.Id;
    }

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    private static async Task<(int Successes, int Failures)> TallyAsync(IEnumerable<Task<bool>> tasks)
    {
        var results = await Task.WhenAll(tasks);
        return (results.Count(r => r), results.Count(r => !r));
    }

    // ---- 1. SoftDelete(folder) vs CreateFile(parent=folder) ---------------

    [SkippableFact]
    public async Task SoftDelete_Folder_Vs_CreateFile_Cannot_Leave_Active_File_Under_Deleted_Parent()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var owner = await SeedOwnerAsync("race-1@example.com");
        var folder = await SeedFolderAsync(owner, null, "Photos");

        // Repeat the race a handful of times to give the scheduler a chance
        // to interleave; each iteration uses a fresh folder + file pair.
        for (var iter = 0; iter < 6; iter++)
        {
            var name = $"r1-{iter}.txt";

            var deleteTask = Task.Run(async () =>
            {
                await using var db = NewDb();
                try { await NewFolderService(db).SoftDeleteAsync(owner, folder); return true; }
                catch (FolderNotEmptyException) { return false; }
            });

            var createTask = Task.Run(async () =>
            {
                await using var db = NewDb();
                try
                {
                    await NewFileService(db).CreateAsync(
                        owner, folder, name, "text/plain", Bytes($"hello-{iter}"));
                    return true;
                }
                catch (FolderNotFoundException) { return false; }
            });

            var deleteOk = await deleteTask;
            var createOk = await createTask;

            // Exactly one or the other can succeed; the lock guarantees they
            // never both win, because either:
            //   * delete won first → file create sees DeletedAt and 404s;
            //   * create won first → delete sees a child and FolderNotEmptys.
            Assert.True(deleteOk ^ createOk,
                $"Iter {iter}: delete={deleteOk}, create={createOk}. Exactly one must win.");

            // Invariant: no active file under a soft-deleted folder.
            await using var verify = NewDb();
            var orphan = await verify.FileItems.AsNoTracking()
                .Join(verify.Folders.AsNoTracking(),
                    f => f.ParentFolderId, d => d.Id, (f, d) => new { f, d })
                .Where(x => x.d.OwnerUserId == owner
                    && x.d.DeletedAt != null
                    && x.f.DeletedAt == null)
                .AnyAsync();
            Assert.False(orphan, "Invariant broken: active file under a soft-deleted folder.");

            // Reset for the next iteration: restore (or recreate) the folder.
            if (deleteOk)
            {
                await using var db = NewDb();
                await NewFolderService(db).RestoreAsync(owner, folder);
            }
            else
            {
                // delete failed, create succeeded — clear the active child to
                // reset to "empty folder" so the next iteration's delete can
                // race fairly.
                await using var db = NewDb();
                var fileId = await db.FileItems.AsNoTracking()
                    .Where(f => f.OwnerUserId == owner && f.Name == name)
                    .Select(f => f.Id)
                    .FirstAsync();
                await NewFileService(db).SoftDeleteAsync(owner, fileId);
            }
        }
    }

    // ---- 2. SoftDelete(folder) vs MoveFileInto(folder) --------------------

    [SkippableFact]
    public async Task SoftDelete_Folder_Vs_MoveFileInto_Cannot_Leave_Active_File_Under_Deleted_Parent()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var owner = await SeedOwnerAsync("race-2@example.com");

        for (var iter = 0; iter < 6; iter++)
        {
            var folder = await SeedFolderAsync(owner, null, $"Target-{iter}");

            Guid fileId;
            await using (var seed = NewDb())
            {
                var file = await NewFileService(seed).CreateAsync(
                    owner, null, $"f-{iter}.txt", "text/plain", Bytes("payload"));
                fileId = file.Id;
            }

            var deleteTask = Task.Run(async () =>
            {
                await using var db = NewDb();
                try { await NewFolderService(db).SoftDeleteAsync(owner, folder); return true; }
                catch (FolderNotEmptyException) { return false; }
            });

            var moveTask = Task.Run(async () =>
            {
                await using var db = NewDb();
                try
                {
                    var moved = await NewFileService(db).MoveAsync(owner, fileId, folder);
                    return moved is not null;
                }
                catch (FolderNotFoundException) { return false; }
            });

            var deleteOk = await deleteTask;
            var moveOk = await moveTask;
            Assert.True(deleteOk ^ moveOk,
                $"Iter {iter}: delete={deleteOk}, move={moveOk}. Exactly one must win.");

            await using var verify = NewDb();
            var orphan = await verify.FileItems.AsNoTracking()
                .Join(verify.Folders.AsNoTracking(),
                    f => f.ParentFolderId, d => d.Id, (f, d) => new { f, d })
                .Where(x => x.d.OwnerUserId == owner
                    && x.d.DeletedAt != null
                    && x.f.DeletedAt == null)
                .AnyAsync();
            Assert.False(orphan);
        }
    }

    // ---- 3. SoftDelete(folder) vs CreateFolder(parent=folder) -------------

    [SkippableFact]
    public async Task SoftDelete_Folder_Vs_CreateChildFolder_Cannot_Leave_Active_Child_Under_Deleted_Parent()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var owner = await SeedOwnerAsync("race-3@example.com");

        for (var iter = 0; iter < 6; iter++)
        {
            var parent = await SeedFolderAsync(owner, null, $"Parent-{iter}");

            var deleteTask = Task.Run(async () =>
            {
                await using var db = NewDb();
                try { await NewFolderService(db).SoftDeleteAsync(owner, parent); return true; }
                catch (FolderNotEmptyException) { return false; }
            });

            var createTask = Task.Run(async () =>
            {
                await using var db = NewDb();
                try
                {
                    await NewFolderService(db).CreateAsync(owner, parent, $"Child-{iter}");
                    return true;
                }
                catch (FolderNotFoundException) { return false; }
            });

            var deleteOk = await deleteTask;
            var createOk = await createTask;
            Assert.True(deleteOk ^ createOk,
                $"Iter {iter}: delete={deleteOk}, create={createOk}. Exactly one must win.");

            await using var verify = NewDb();
            var orphan = await verify.Folders.AsNoTracking()
                .Where(child => child.ParentFolderId != null
                    && child.DeletedAt == null
                    && verify.Folders.Any(p => p.Id == child.ParentFolderId
                        && p.OwnerUserId == owner
                        && p.DeletedAt != null))
                .AnyAsync();
            Assert.False(orphan, "Invariant broken: active folder under a soft-deleted parent folder.");
        }
    }

    // ---- 4. Reciprocal MoveFolder(A→B) vs MoveFolder(B→A) -----------------

    [SkippableFact]
    public async Task Reciprocal_Folder_Moves_Cannot_Create_A_Cycle()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var owner = await SeedOwnerAsync("race-4@example.com");

        for (var iter = 0; iter < 6; iter++)
        {
            var a = await SeedFolderAsync(owner, null, $"A-{iter}");
            var b = await SeedFolderAsync(owner, null, $"B-{iter}");

            var aIntoB = Task.Run(async () =>
            {
                await using var db = NewDb();
                try
                {
                    var moved = await NewFolderService(db).MoveAsync(owner, a, b);
                    return moved is not null;
                }
                catch (Exception) { return false; }
            });

            var bIntoA = Task.Run(async () =>
            {
                await using var db = NewDb();
                try
                {
                    var moved = await NewFolderService(db).MoveAsync(owner, b, a);
                    return moved is not null;
                }
                catch (Exception) { return false; }
            });

            var aOk = await aIntoB;
            var bOk = await bIntoA;

            // Exactly one move can succeed without creating a cycle.
            Assert.True(aOk ^ bOk,
                $"Iter {iter}: a→b={aOk}, b→a={bOk}. Exactly one must win.");

            // Invariant: walk parent chain from every folder; no cycles.
            await using var verify = NewDb();
            var folders = await verify.Folders.AsNoTracking()
                .Where(f => f.OwnerUserId == owner)
                .Select(f => new { f.Id, f.ParentFolderId })
                .ToListAsync();
            var byId = folders.ToDictionary(f => f.Id, f => f.ParentFolderId);
            foreach (var f in folders)
            {
                Guid? cursor = f.ParentFolderId;
                var seen = new HashSet<Guid> { f.Id };
                while (cursor is Guid id)
                {
                    Assert.True(seen.Add(id), $"Cycle detected from {f.Id} through {id}.");
                    cursor = byId.TryGetValue(id, out var next) ? next : null;
                }
            }

            // Clean up for the next iteration: leave both folders detached.
            await using var reset = NewDb();
            await reset.Folders
                .Where(f => f.Id == a || f.Id == b)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.ParentFolderId, _ => (Guid?)null));
        }
    }

    // ---- 5. Restore(child) while parent is being soft-deleted -------------

    [SkippableFact]
    public async Task Restore_Child_Versus_SoftDelete_Parent_Fails_Safely()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        var owner = await SeedOwnerAsync("race-5@example.com");

        for (var iter = 0; iter < 6; iter++)
        {
            var parent = await SeedFolderAsync(owner, null, $"P-{iter}");
            var child = await SeedFolderAsync(owner, parent, $"C-{iter}");

            // Soft-delete the child first so we have something to restore.
            await using (var db = NewDb())
            {
                await NewFolderService(db).SoftDeleteAsync(owner, child);
            }

            var deleteParent = Task.Run(async () =>
            {
                await using var db = NewDb();
                try { await NewFolderService(db).SoftDeleteAsync(owner, parent); return true; }
                catch (FolderNotEmptyException) { return false; }
            });

            var restoreChild = Task.Run(async () =>
            {
                await using var db = NewDb();
                try
                {
                    var restored = await NewFolderService(db).RestoreAsync(owner, child);
                    return restored is not null && restored.DeletedAt is null;
                }
                catch (RestoreParentDeletedException) { return false; }
            });

            var deleteOk = await deleteParent;
            var restoreOk = await restoreChild;

            // Exactly one wins: either parent is now deleted (and the child
            // restore raised RestoreParentDeletedException), or the child is
            // restored (and the parent delete saw a live child and raised
            // FolderNotEmptyException).
            Assert.True(deleteOk ^ restoreOk,
                $"Iter {iter}: delete={deleteOk}, restore={restoreOk}. Exactly one must win.");

            // Invariant: if child is active, parent is active too.
            await using var verify = NewDb();
            var orphanRestore = await verify.Folders.AsNoTracking()
                .Where(c => c.Id == child
                    && c.DeletedAt == null
                    && verify.Folders.Any(p => p.Id == c.ParentFolderId && p.DeletedAt != null))
                .AnyAsync();
            Assert.False(orphanRestore, "Invariant broken: child restored under a soft-deleted parent.");
        }
    }

    // ---- 6. Post-race global queryability + invariants --------------------

    [SkippableFact]
    public async Task After_All_Race_Scenarios_Tree_Remains_Queryable_And_Invariant_Holds()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        // Run a mixed workload: N owners, each doing several concurrent
        // races. The point is that the global tree is internally consistent
        // afterwards (no orphans, no cycles, no exceptions escape).
        const int Owners = 4;
        var owners = new Guid[Owners];
        for (var i = 0; i < Owners; i++)
        {
            owners[i] = await SeedOwnerAsync($"race-mix-{i}@example.com");
        }

        var workloads = new List<Task>();
        for (var i = 0; i < Owners; i++)
        {
            var owner = owners[i];
            workloads.Add(Task.Run(async () =>
            {
                var folder = await SeedFolderAsync(owner, null, "Photos");

                // Triple race: delete folder, create child folder, create file.
                var t1 = Task.Run(async () =>
                {
                    await using var db = NewDb();
                    try { await NewFolderService(db).SoftDeleteAsync(owner, folder); }
                    catch (FolderNotEmptyException) { /* expected race loser */ }
                });
                var t2 = Task.Run(async () =>
                {
                    await using var db = NewDb();
                    try { await NewFolderService(db).CreateAsync(owner, folder, "Child"); }
                    catch (FolderNotFoundException) { /* expected race loser */ }
                });
                var t3 = Task.Run(async () =>
                {
                    await using var db = NewDb();
                    try
                    {
                        await NewFileService(db).CreateAsync(
                            owner, folder, "f.txt", "text/plain", Bytes("hi"));
                    }
                    catch (FolderNotFoundException) { /* expected race loser */ }
                });
                await Task.WhenAll(t1, t2, t3);
            }));
        }

        await Task.WhenAll(workloads);

        // Global invariants across every owner.
        await using var verify = NewDb();

        // (a) No active file under a soft-deleted folder.
        var orphanFiles = await verify.FileItems.AsNoTracking()
            .Join(verify.Folders.AsNoTracking(),
                f => f.ParentFolderId, d => d.Id, (f, d) => new { f, d })
            .Where(x => x.d.DeletedAt != null && x.f.DeletedAt == null)
            .CountAsync();
        Assert.Equal(0, orphanFiles);

        // (b) No active folder under a soft-deleted folder.
        var orphanFolders = await verify.Folders.AsNoTracking()
            .Where(child => child.ParentFolderId != null
                && child.DeletedAt == null
                && verify.Folders.Any(p => p.Id == child.ParentFolderId && p.DeletedAt != null))
            .CountAsync();
        Assert.Equal(0, orphanFolders);

        // (c) No folder cycles.
        var folders = await verify.Folders.AsNoTracking()
            .Select(f => new { f.Id, f.ParentFolderId })
            .ToListAsync();
        var byId = folders.ToDictionary(f => f.Id, f => f.ParentFolderId);
        foreach (var f in folders)
        {
            Guid? cursor = f.ParentFolderId;
            var seen = new HashSet<Guid> { f.Id };
            while (cursor is Guid id)
            {
                Assert.True(seen.Add(id), $"Cycle detected from {f.Id} through {id}.");
                cursor = byId.TryGetValue(id, out var next) ? next : null;
            }
        }
    }
}
