using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Jobs;
using NubArca.Api.Organizer;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Organizer;

// Integration tests for the date-taken organizer: dry-run is read-only,
// execution is DB-only + idempotent + sliceable, conflicts are deterministic,
// and runs are owner-scoped. Execution is driven through the REAL job engine
// (JobProcessor) exactly as production runs it.
public sealed class PhotoOrganizerServiceTests
{
    private static readonly DateTime May17 = new(2024, 5, 17, 9, 0, 0, DateTimeKind.Utc);

    private static SqliteWebApplicationFactory NewFactory(int? sliceItemBudget = null)
        => sliceItemBudget is int b
            ? new SqliteWebApplicationFactory(new Dictionary<string, string?> { ["Jobs:MaintenanceSliceItemBudget"] = b.ToString() })
            : new SqliteWebApplicationFactory();

    private static async Task<Folder> SeedFolderAsync(SqliteWebApplicationFactory f, Guid owner, Guid? parent, string name)
    {
        await using var scope = f.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IFolderService>().CreateAsync(owner, parent, name);
    }

    private static async Task<FileItem> SeedPhotoAsync(
        SqliteWebApplicationFactory f, Guid owner, Guid? parent, string name,
        int variant, DateTime? embedded = null, string embeddedSource = "DateTimeOriginal",
        DateTime? userOverride = null, DateTime? createdAt = null)
    {
        FileItem file;
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            file = await files.CreateAsync(owner, parent, name, "image/png",
                new MemoryStream(ImageFixtures.PlainPng(width: 16 + variant, height: 16)));
        }
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (createdAt is DateTime ca)
            {
                await db.FileItems.Where(x => x.Id == file.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTime.SpecifyKind(ca, DateTimeKind.Utc)));
            }
            if (embedded is DateTime ed)
            {
                await db.BlobMetadata.Where(m => m.BlobObjectId == file.BlobObjectId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.DateTaken, DateTime.SpecifyKind(ed, DateTimeKind.Utc))
                        .SetProperty(m => m.DateTakenSource, embeddedSource)
                        .SetProperty(m => m.MediaCategory, "image"));
            }
            if (userOverride is DateTime uo)
            {
                db.FileItemUserMetadata.Add(new FileItemUserMetadata
                {
                    Id = Guid.NewGuid(),
                    FileItemId = file.Id,
                    DateTakenOverride = DateTime.SpecifyKind(uo, DateTimeKind.Utc),
                    IsFavorite = false,
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
        }
        return file;
    }

    private static async Task<PhotoOrganizerRunResponse> StartRunAsync(
        SqliteWebApplicationFactory f, Guid owner, OrganizerOptions options)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var organizer = scope.ServiceProvider.GetRequiredService<PhotoDateTakenOrganizerService>();
        return await organizer.StartRunAsync(owner, options, default);
    }

    private static async Task<PhotoOrganizerDryRunResponse> DryRunAsync(
        SqliteWebApplicationFactory f, Guid owner, OrganizerOptions options)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var organizer = scope.ServiceProvider.GetRequiredService<PhotoDateTakenOrganizerService>();
        return await organizer.DryRunAsync(owner, options, default);
    }

    private static async Task<PhotoOrganizerRun> GetRunAsync(SqliteWebApplicationFactory f, Guid runId)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PhotoOrganizerRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    // Drives the run to a terminal state through the real processor, one slice
    // per ProcessAvailableAsync call (mirrors the worker).
    private static async Task<int> RunToCompletionAsync(SqliteWebApplicationFactory f, Guid runId, int maxSlices = 50)
    {
        var slices = 0;
        for (var i = 0; i < maxSlices; i++)
        {
            int ran;
            await using (var scope = f.Services.CreateAsyncScope())
            {
                ran = await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(maxJobs: 1);
            }
            if (ran == 0) break;
            slices++;
            var run = await GetRunAsync(f, runId);
            if (PhotoOrganizerStatuses.IsTerminal(run.Status)) break;
        }
        return slices;
    }

    private static async Task<Guid?> ResolvePathAsync(SqliteWebApplicationFactory f, Guid owner, params string[] segments)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Guid? current = null;
        foreach (var seg in segments)
        {
            var id = await db.Folders.AsNoTracking()
                .Where(x => x.OwnerUserId == owner && x.ParentFolderId == current && x.DeletedAt == null && x.Name == seg)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync();
            if (id is null) return null;
            current = id;
        }
        return current;
    }

    private static async Task<FileItem> GetFileAsync(SqliteWebApplicationFactory f, Guid fileId)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FileItems.AsNoTracking().SingleAsync(x => x.Id == fileId);
    }

    private static OrganizerOptions AllOptions(
        OrganizerTemplate template = OrganizerTemplate.YearDatedDay,
        MissingDateBehavior missing = MissingDateBehavior.Skip,
        ConflictPolicy conflict = ConflictPolicy.KeepBoth,
        string? rootName = "Photos")
        => new(OrganizerScopeKind.All, null, Array.Empty<Guid>(), null, rootName, template, missing, conflict);

    // ---------------------------------------------------------------- tests

    [Fact]
    public async Task DryRun_Does_Not_Mutate()
    {
        using var f = NewFactory();
        f.EnsureDatabaseCreated();
        var owner = await f.SeedUserAsync();
        var file = await SeedPhotoAsync(f, owner, null, "IMG_1.png", variant: 1, embedded: May17);

        var dry = await DryRunAsync(f, owner, AllOptions());

        Assert.Equal(1, dry.Summary.CandidateCount);
        Assert.Equal(1, dry.Summary.ToMoveCount);
        Assert.True(dry.Summary.FoldersToCreateCount >= 1);

        // No mutation: the file did not move, no target folder exists, no run row.
        var after = await GetFileAsync(f, file.Id);
        Assert.Null(after.ParentFolderId);
        Assert.Null(await ResolvePathAsync(f, owner, "Photos"));
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.PhotoOrganizerRuns.CountAsync());
    }

    [Fact]
    public async Task Execute_Moves_Photos_Into_Date_Folders_DbOnly()
    {
        using var f = NewFactory();
        f.EnsureDatabaseCreated();
        var owner = await f.SeedUserAsync();
        var file = await SeedPhotoAsync(f, owner, null, "IMG_1.png", variant: 1, embedded: May17);
        var blobBefore = file.BlobObjectId;

        // A thumbnail row should survive the logical move untouched.
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.FileThumbnails.Add(new FileThumbnail
            {
                Id = Guid.NewGuid(), FileItemId = file.Id, BlobObjectId = blobBefore,
                Size = "organizer-test", Width = 8, Height = 8, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var run = await StartRunAsync(f, owner, AllOptions());
        await RunToCompletionAsync(f, run.RunId);

        var finished = await GetRunAsync(f, run.RunId);
        Assert.Equal(PhotoOrganizerStatuses.Succeeded, finished.Status);
        Assert.Equal(1, finished.MovedCount);

        var target = await ResolvePathAsync(f, owner, "Photos", "2024", "2024-05-17");
        Assert.NotNull(target);
        var moved = await GetFileAsync(f, file.Id);
        Assert.Equal(target, moved.ParentFolderId);
        Assert.Equal("IMG_1.png", moved.Name);

        // DB-only: same blob, thumbnail row intact + still pointing at the blob.
        Assert.Equal(blobBefore, moved.BlobObjectId);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var thumb = await db.FileThumbnails.AsNoTracking()
                .SingleAsync(t => t.FileItemId == file.Id && t.Size == "organizer-test");
            Assert.Equal(blobBefore, thumb.BlobObjectId);

            // Manifest records source → target.
            var manifest = await db.PhotoOrganizerMoves.AsNoTracking().Where(m => m.RunId == run.RunId).ToListAsync();
            var record = Assert.Single(manifest);
            Assert.Equal(file.Id, record.FileItemId);
            Assert.Null(record.SourceParentFolderId);
            Assert.Equal(target, record.TargetParentFolderId);
            Assert.Equal("IMG_1.png", record.TargetName);
        }
    }

    [Fact]
    public async Task Execute_Is_Idempotent_On_Rerun()
    {
        using var f = NewFactory();
        f.EnsureDatabaseCreated();
        var owner = await f.SeedUserAsync();
        var file = await SeedPhotoAsync(f, owner, null, "IMG_1.png", variant: 1, embedded: May17);

        var run1 = await StartRunAsync(f, owner, AllOptions());
        await RunToCompletionAsync(f, run1.RunId);
        Assert.Equal(1, (await GetRunAsync(f, run1.RunId)).MovedCount);

        var run2 = await StartRunAsync(f, owner, AllOptions());
        await RunToCompletionAsync(f, run2.RunId);
        var second = await GetRunAsync(f, run2.RunId);
        Assert.Equal(PhotoOrganizerStatuses.Succeeded, second.Status);
        Assert.Equal(0, second.MovedCount);
        Assert.Equal(1, second.AlreadyOrganizedCount);
    }

    [Fact]
    public async Task Conflict_Skip_Leaves_Second_File()
    {
        using var f = NewFactory();
        f.EnsureDatabaseCreated();
        var owner = await f.SeedUserAsync();
        var a = await SeedFolderAsync(f, owner, null, "A");
        var b = await SeedFolderAsync(f, owner, null, "B");
        // Same name + same date but distinct blobs → both target the same path/name.
        var f1 = await SeedPhotoAsync(f, owner, a.Id, "IMG.png", variant: 1, embedded: May17);
        var f2 = await SeedPhotoAsync(f, owner, b.Id, "IMG.png", variant: 2, embedded: May17);

        var run = await StartRunAsync(f, owner, AllOptions(conflict: ConflictPolicy.Skip));
        await RunToCompletionAsync(f, run.RunId);

        var finished = await GetRunAsync(f, run.RunId);
        Assert.Equal(1, finished.MovedCount);
        Assert.Equal(1, finished.SkippedConflictCount);

        var target = await ResolvePathAsync(f, owner, "Photos", "2024", "2024-05-17");
        var moved = (await GetFileAsync(f, f1.Id)).ParentFolderId == target ? f1 : f2;
        var skipped = moved.Id == f1.Id ? f2 : f1;
        Assert.Equal(target, (await GetFileAsync(f, moved.Id)).ParentFolderId);
        Assert.NotEqual(target, (await GetFileAsync(f, skipped.Id)).ParentFolderId); // untouched
    }

    [Fact]
    public async Task Conflict_KeepBoth_Suffixes_Second_File()
    {
        using var f = NewFactory();
        f.EnsureDatabaseCreated();
        var owner = await f.SeedUserAsync();
        var a = await SeedFolderAsync(f, owner, null, "A");
        var b = await SeedFolderAsync(f, owner, null, "B");
        var f1 = await SeedPhotoAsync(f, owner, a.Id, "IMG.png", variant: 1, embedded: May17);
        var f2 = await SeedPhotoAsync(f, owner, b.Id, "IMG.png", variant: 2, embedded: May17);

        var run = await StartRunAsync(f, owner, AllOptions(conflict: ConflictPolicy.KeepBoth));
        await RunToCompletionAsync(f, run.RunId);

        var finished = await GetRunAsync(f, run.RunId);
        Assert.Equal(2, finished.MovedCount);

        var target = await ResolvePathAsync(f, owner, "Photos", "2024", "2024-05-17");
        var m1 = await GetFileAsync(f, f1.Id);
        var m2 = await GetFileAsync(f, f2.Id);
        Assert.Equal(target, m1.ParentFolderId);
        Assert.Equal(target, m2.ParentFolderId);
        var names = new[] { m1.Name, m2.Name };
        Assert.Contains("IMG.png", names);
        Assert.Contains("IMG (1).png", names);
    }

    [Fact]
    public async Task Scope_Selected_Only_Moves_Selected()
    {
        using var f = NewFactory();
        f.EnsureDatabaseCreated();
        var owner = await f.SeedUserAsync();
        var f1 = await SeedPhotoAsync(f, owner, null, "IMG_1.png", variant: 1, embedded: May17);
        var f2 = await SeedPhotoAsync(f, owner, null, "IMG_2.png", variant: 2, embedded: May17);

        var options = new OrganizerOptions(
            OrganizerScopeKind.Selected, null, new[] { f1.Id }, null, "Photos",
            OrganizerTemplate.YearDatedDay, MissingDateBehavior.Skip, ConflictPolicy.KeepBoth);
        var run = await StartRunAsync(f, owner, options);
        await RunToCompletionAsync(f, run.RunId);

        Assert.Equal(1, (await GetRunAsync(f, run.RunId)).MovedCount);
        var target = await ResolvePathAsync(f, owner, "Photos", "2024", "2024-05-17");
        Assert.Equal(target, (await GetFileAsync(f, f1.Id)).ParentFolderId);
        Assert.Null((await GetFileAsync(f, f2.Id)).ParentFolderId); // not selected, untouched
    }

    [Fact]
    public async Task Scope_FolderRecursive_Moves_Descendants()
    {
        using var f = NewFactory();
        f.EnsureDatabaseCreated();
        var owner = await f.SeedUserAsync();
        var a = await SeedFolderAsync(f, owner, null, "A");
        var b = await SeedFolderAsync(f, owner, a.Id, "B");
        var outside = await SeedFolderAsync(f, owner, null, "Outside");
        var inA = await SeedPhotoAsync(f, owner, a.Id, "a.png", variant: 1, embedded: May17);
        var inB = await SeedPhotoAsync(f, owner, b.Id, "b.png", variant: 2, embedded: May17);
        var outsidePhoto = await SeedPhotoAsync(f, owner, outside.Id, "o.png", variant: 3, embedded: May17);

        var options = new OrganizerOptions(
            OrganizerScopeKind.FolderRecursive, a.Id, Array.Empty<Guid>(), null, "Photos",
            OrganizerTemplate.YearDatedDay, MissingDateBehavior.Skip, ConflictPolicy.KeepBoth);
        var run = await StartRunAsync(f, owner, options);
        await RunToCompletionAsync(f, run.RunId);

        Assert.Equal(2, (await GetRunAsync(f, run.RunId)).MovedCount);
        var target = await ResolvePathAsync(f, owner, "Photos", "2024", "2024-05-17");
        Assert.Equal(target, (await GetFileAsync(f, inA.Id)).ParentFolderId);
        Assert.Equal(target, (await GetFileAsync(f, inB.Id)).ParentFolderId);
        Assert.Equal(outside.Id, (await GetFileAsync(f, outsidePhoto.Id)).ParentFolderId); // outside scope
    }

    [Fact]
    public async Task MissingDate_Skip_Leaves_File()
    {
        using var f = NewFactory();
        f.EnsureDatabaseCreated();
        var owner = await f.SeedUserAsync();
        var file = await SeedPhotoAsync(f, owner, null, "nodate.png", variant: 1); // no embedded/override

        var run = await StartRunAsync(f, owner, AllOptions(missing: MissingDateBehavior.Skip));
        await RunToCompletionAsync(f, run.RunId);

        var finished = await GetRunAsync(f, run.RunId);
        Assert.Equal(0, finished.MovedCount);
        Assert.Equal(1, finished.SkippedMissingDateCount);
        Assert.Null((await GetFileAsync(f, file.Id)).ParentFolderId);
    }

    [Fact]
    public async Task MissingDate_FileCreated_Uses_Created_Date()
    {
        using var f = NewFactory();
        f.EnsureDatabaseCreated();
        var owner = await f.SeedUserAsync();
        var file = await SeedPhotoAsync(f, owner, null, "nodate.png", variant: 1,
            createdAt: new DateTime(2021, 3, 9, 0, 0, 0, DateTimeKind.Utc));

        var run = await StartRunAsync(f, owner, AllOptions(missing: MissingDateBehavior.FileCreated));
        await RunToCompletionAsync(f, run.RunId);

        Assert.Equal(1, (await GetRunAsync(f, run.RunId)).MovedCount);
        var target = await ResolvePathAsync(f, owner, "Photos", "2021", "2021-03-09");
        Assert.Equal(target, (await GetFileAsync(f, file.Id)).ParentFolderId);
    }

    [Fact]
    public async Task MissingDate_UnknownFolder_Routes_To_Unknown()
    {
        using var f = NewFactory();
        f.EnsureDatabaseCreated();
        var owner = await f.SeedUserAsync();
        var file = await SeedPhotoAsync(f, owner, null, "nodate.png", variant: 1);

        var run = await StartRunAsync(f, owner, AllOptions(missing: MissingDateBehavior.UnknownFolder));
        await RunToCompletionAsync(f, run.RunId);

        Assert.Equal(1, (await GetRunAsync(f, run.RunId)).MovedCount);
        var target = await ResolvePathAsync(f, owner, "Photos", "Unknown Date");
        Assert.Equal(target, (await GetFileAsync(f, file.Id)).ParentFolderId);
    }

    [Fact]
    public async Task UserOverride_Wins_Over_Embedded()
    {
        using var f = NewFactory();
        f.EnsureDatabaseCreated();
        var owner = await f.SeedUserAsync();
        var file = await SeedPhotoAsync(f, owner, null, "IMG.png", variant: 1,
            embedded: May17, userOverride: new DateTime(2019, 12, 31, 0, 0, 0, DateTimeKind.Utc));

        var run = await StartRunAsync(f, owner, AllOptions());
        await RunToCompletionAsync(f, run.RunId);

        var target = await ResolvePathAsync(f, owner, "Photos", "2019", "2019-12-31");
        Assert.Equal(target, (await GetFileAsync(f, file.Id)).ParentFolderId);
    }

    [Fact]
    public async Task Owner_Isolation_Other_Users_Files_Untouched_And_Run_Not_Visible()
    {
        using var f = NewFactory();
        f.EnsureDatabaseCreated();
        var alice = await f.SeedUserAsync("alice@example.com");
        var bob = await f.SeedUserAsync("bob@example.com");
        var aliceFile = await SeedPhotoAsync(f, alice, null, "a.png", variant: 1, embedded: May17);
        var bobFile = await SeedPhotoAsync(f, bob, null, "b.png", variant: 2, embedded: May17);

        // Bob organizes everything (his scope) + tries to target Alice's file id.
        var options = new OrganizerOptions(
            OrganizerScopeKind.Selected, null, new[] { bobFile.Id, aliceFile.Id }, null, "Photos",
            OrganizerTemplate.YearDatedDay, MissingDateBehavior.Skip, ConflictPolicy.KeepBoth);
        var run = await StartRunAsync(f, bob, options);
        await RunToCompletionAsync(f, run.RunId);

        // Only Bob's file moved; Alice's is untouched (owner filter excludes it).
        Assert.Equal(1, (await GetRunAsync(f, run.RunId)).MovedCount);
        Assert.Null((await GetFileAsync(f, aliceFile.Id)).ParentFolderId);

        // Alice cannot see Bob's run.
        await using var scope = f.Services.CreateAsyncScope();
        var organizer = scope.ServiceProvider.GetRequiredService<PhotoDateTakenOrganizerService>();
        Assert.Null(await organizer.GetRunStatusAsync(alice, run.RunId, default));
        Assert.NotNull(await organizer.GetRunStatusAsync(bob, run.RunId, default));
    }

    [Fact]
    public async Task Execution_Resumes_Across_Slices_Moving_Each_File_Once()
    {
        using var f = NewFactory(sliceItemBudget: 1); // one file per slice
        f.EnsureDatabaseCreated();
        var owner = await f.SeedUserAsync();
        for (var i = 0; i < 4; i++)
        {
            await SeedPhotoAsync(f, owner, null, $"IMG_{i}.png", variant: i, embedded: May17.AddDays(i));
        }

        var run = await StartRunAsync(f, owner, AllOptions());
        var slices = await RunToCompletionAsync(f, run.RunId);

        Assert.True(slices >= 2, $"expected multiple slices, got {slices}");
        var finished = await GetRunAsync(f, run.RunId);
        Assert.Equal(PhotoOrganizerStatuses.Succeeded, finished.Status);
        Assert.Equal(4, finished.MovedCount);

        // Each file moved exactly once → exactly 4 manifest rows, no duplicates.
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var moves = await db.PhotoOrganizerMoves.AsNoTracking().Where(m => m.RunId == run.RunId).ToListAsync();
        Assert.Equal(4, moves.Count);
        Assert.Equal(4, moves.Select(m => m.FileItemId).Distinct().Count());
    }

    [Fact]
    public async Task Cancellation_Stops_Further_Work()
    {
        using var f = NewFactory(sliceItemBudget: 1);
        f.EnsureDatabaseCreated();
        var owner = await f.SeedUserAsync();
        for (var i = 0; i < 4; i++)
        {
            await SeedPhotoAsync(f, owner, null, $"IMG_{i}.png", variant: i, embedded: May17.AddDays(i));
        }

        var run = await StartRunAsync(f, owner, AllOptions());

        // Run one slice (moves one file), then cancel and drain.
        await using (var scope = f.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(maxJobs: 1);
        }
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var jobId = (await GetRunAsync(f, run.RunId)).JobId!.Value;
            await queue.RequestCancellationAsync(jobId);
        }
        await RunToCompletionAsync(f, run.RunId);

        await using var statusScope = f.Services.CreateAsyncScope();
        var organizer = statusScope.ServiceProvider.GetRequiredService<PhotoDateTakenOrganizerService>();
        var status = await organizer.GetRunStatusAsync(owner, run.RunId, default);
        Assert.Equal(PhotoOrganizerStatuses.Cancelled, status!.Status);
        Assert.True(status.MovedCount < 4, "cancellation should stop before all files move");
    }
}
