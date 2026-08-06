using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Jobs;
using NubArca.Api.Organizer;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Jobs;

// Exact-content deduplication in the Photo DateTaken Organizer.
//
// When multiple FileItems share the same BlobObjectId and their computed target
// folder is identical, the organizer keeps exactly one visible copy (the
// survivor) and soft-deletes the rest via the normal SoftDeleteAsync path so
// blob reference counts stay correct and the BlobJanitor cannot prematurely
// collect the underlying blob.
public sealed class PhotoOrganizerExactDedupeTests
{
    // ------------------------------------------------------------------ helpers

    private static async Task<PhotoOrganizerRunResponse> StartRunAsync(
        SqliteWebApplicationFactory factory, Guid owner, OrganizerOptions options)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var organizer = scope.ServiceProvider.GetRequiredService<PhotoDateTakenOrganizerService>();
        return await organizer.StartRunAsync(owner, options, default);
    }

    private static OrganizerOptions DefaultOptions() =>
        new(OrganizerScopeKind.All, null, Array.Empty<Guid>(), null, "Photos",
            OrganizerTemplate.YearDatedDay, MissingDateBehavior.Skip, ConflictPolicy.KeepBoth);

    private static async Task RunToCompletionAsync(SqliteWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(maxJobs: 20);
    }

    private static async Task SetDateTakenAsync(
        SqliteWebApplicationFactory factory, Guid blobObjectId, DateTime dateTaken)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.BlobMetadata
            .Where(m => m.BlobObjectId == blobObjectId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.DateTaken, dateTaken)
                .SetProperty(m => m.DateTakenSource, "DateTimeOriginal")
                .SetProperty(m => m.MediaCategory, "image"));
    }

    // ------------------------------------------------------------------ scenario 1
    // Two files with identical content (same BlobObjectId) and the same
    // computed DateTaken folder: exactly one survives, the other is soft-deleted.

    [Fact]
    public async Task ExactDuplicate_SameBlob_SameTargetFolder_OneSurvives()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();

        var bytes = ImageFixtures.PlainPng();
        FileItem fileA, fileB;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            fileA = await files.CreateAsync(owner, null, "photo_a.png", "image/png", new MemoryStream(bytes));
            fileB = await files.CreateAsync(owner, null, "photo_b.png", "image/png", new MemoryStream(bytes));
        }

        // Both share the same BlobObjectId (SHA-256 dedup).
        Assert.Equal(fileA.BlobObjectId, fileB.BlobObjectId);

        var dateTaken = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        await SetDateTakenAsync(factory, fileA.BlobObjectId, dateTaken);

        var run = await StartRunAsync(factory, owner, DefaultOptions());
        await RunToCompletionAsync(factory);

        await using var check = factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();

        // Exactly one file should be active in the target folder.
        var activeFiles = await db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == owner && f.DeletedAt == null)
            .ToListAsync();
        Assert.Single(activeFiles);

        // The run must report exactly one exact duplicate removed.
        var finishedRun = await db.PhotoOrganizerRuns.AsNoTracking().SingleAsync(r => r.Id == run.RunId);
        Assert.Equal(PhotoOrganizerStatuses.Succeeded, finishedRun.Status);
        Assert.Equal(1, finishedRun.ExactDuplicateRemovedCount);
    }

    // ------------------------------------------------------------------ scenario 2
    // Two files with different content (different BlobObjectId) but the same name
    // should NOT be deduplicated — they are distinct files.

    [Fact]
    public async Task DifferentBlob_SameName_NotDeduplicated()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();

        var dateTaken = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        FileItem fileA, fileB;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            // Different dimensions → different content → different SHA-256 → different BlobObjectId.
            fileA = await files.CreateAsync(owner, null, "photo_small.png", "image/png",
                new MemoryStream(ImageFixtures.PlainPng(16, 16)));
            fileB = await files.CreateAsync(owner, null, "photo_large.png", "image/png",
                new MemoryStream(ImageFixtures.PlainPng(32, 32)));
        }

        Assert.NotEqual(fileA.BlobObjectId, fileB.BlobObjectId);
        await SetDateTakenAsync(factory, fileA.BlobObjectId, dateTaken);
        await SetDateTakenAsync(factory, fileB.BlobObjectId, dateTaken);

        await StartRunAsync(factory, owner, DefaultOptions());
        await RunToCompletionAsync(factory);

        await using var check = factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();

        // Both files should still be active (one moved, one either moved with suffix or skipped by conflict policy).
        var activeCount = await db.FileItems.AsNoTracking()
            .CountAsync(f => f.OwnerUserId == owner && f.DeletedAt == null);
        Assert.Equal(2, activeCount);

        var finishedRun = await db.PhotoOrganizerRuns.AsNoTracking().SingleAsync(r => r.OwnerUserId == owner);
        Assert.Equal(0, finishedRun.ExactDuplicateRemovedCount);
    }

    // ------------------------------------------------------------------ scenario 3
    // Same blob but the two files resolve to different target folders (different
    // DateTaken): both should survive — no deduplication across folders.

    [Fact]
    public async Task SameBlob_DifferentTargetFolder_BothSurvive()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();

        var bytes = ImageFixtures.PlainPng();
        FileItem fileA, fileB;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            fileA = await files.CreateAsync(owner, null, "photo_jan.png", "image/png", new MemoryStream(bytes));
            fileB = await files.CreateAsync(owner, null, "photo_feb.png", "image/png", new MemoryStream(bytes));
        }

        Assert.Equal(fileA.BlobObjectId, fileB.BlobObjectId);

        // Give fileA an override date in January and fileB an override date in February
        // by patching the user metadata table directly.
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            // Set blob metadata for the shared blob (any date)
            await db.BlobMetadata
                .Where(m => m.BlobObjectId == fileA.BlobObjectId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.DateTaken, new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc))
                    .SetProperty(m => m.DateTakenSource, "DateTimeOriginal")
                    .SetProperty(m => m.MediaCategory, "image"));

            // Override fileB's date to February via FileItemUserMetadata
            var um = await db.FileItemUserMetadata.FirstOrDefaultAsync(u => u.FileItemId == fileB.Id);
            if (um is null)
            {
                db.FileItemUserMetadata.Add(new FileItemUserMetadata
                {
                    Id = Guid.NewGuid(),
                    FileItemId = fileB.Id,
                    DateTakenOverride = new DateTime(2024, 2, 20, 0, 0, 0, DateTimeKind.Utc),
                });
            }
            else
            {
                um.DateTakenOverride = new DateTime(2024, 2, 20, 0, 0, 0, DateTimeKind.Utc);
            }
            await db.SaveChangesAsync();
        }

        await StartRunAsync(factory, owner, DefaultOptions());
        await RunToCompletionAsync(factory);

        await using var check = factory.Services.CreateAsyncScope();
        var dbCheck = check.ServiceProvider.GetRequiredService<AppDbContext>();

        var activeFiles = await dbCheck.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == owner && f.DeletedAt == null)
            .ToListAsync();
        Assert.Equal(2, activeFiles.Count);

        var finishedRun = await dbCheck.PhotoOrganizerRuns.AsNoTracking().SingleAsync(r => r.OwnerUserId == owner);
        Assert.Equal(0, finishedRun.ExactDuplicateRemovedCount);
    }

    // ------------------------------------------------------------------ scenario 4
    // Cross-owner isolation: two users upload the same content → same BlobObjectId.
    // Running the organizer for user A must not affect user B's file.

    [Fact]
    public async Task CrossOwner_SameBlob_Isolated()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var ownerA = await factory.SeedUserAsync("a@example.com");
        var ownerB = await factory.SeedUserAsync("b@example.com");

        var bytes = ImageFixtures.PlainPng();
        var dateTaken = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        FileItem fileA, fileB;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            fileA = await files.CreateAsync(ownerA, null, "photo.png", "image/png", new MemoryStream(bytes));
            fileB = await files.CreateAsync(ownerB, null, "photo.png", "image/png", new MemoryStream(bytes));
        }

        Assert.Equal(fileA.BlobObjectId, fileB.BlobObjectId);
        await SetDateTakenAsync(factory, fileA.BlobObjectId, dateTaken);

        // Run organizer only for owner A.
        await StartRunAsync(factory, ownerA, DefaultOptions());
        await RunToCompletionAsync(factory);

        await using var check = factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();

        // Owner A's file is organised.
        var aFiles = await db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == ownerA && f.DeletedAt == null)
            .ToListAsync();
        Assert.Single(aFiles);

        // Owner B's file is untouched.
        var bFiles = await db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == ownerB && f.DeletedAt == null)
            .ToListAsync();
        Assert.Single(bFiles);
        Assert.Equal(fileB.Id, bFiles[0].Id);
        Assert.Null(bFiles[0].DeletedAt);

        var runA = await db.PhotoOrganizerRuns.AsNoTracking().SingleAsync(r => r.OwnerUserId == ownerA);
        Assert.Equal(0, runA.ExactDuplicateRemovedCount);
    }

    // ------------------------------------------------------------------ scenario 5
    // Existing-in-target survivor: when one file is already in the correct target
    // folder and a duplicate targeting the same folder is processed later, the
    // already-placed file survives and the duplicate is soft-deleted.

    [Fact]
    public async Task ExistingInTarget_IsPreferredSurvivor()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();

        var bytes = ImageFixtures.PlainPng();
        var dateTaken = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        // Create the target folder structure manually so the first file can be
        // placed into it before the organizer runs.
        Guid dayFolder;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var folders = scope.ServiceProvider.GetRequiredService<NubArca.Api.Folders.IFolderService>();

            // Ensure "Photos/2024/2024-06-15" folder chain
            var (leafId, _) = await folders.EnsureFolderPathWithCountAsync(
                owner, null, ["Photos", "2024", "2024-06-15"], default);
            dayFolder = leafId!.Value;

            // Create one file already in the correct date folder.
            var existingFile = await files.CreateAsync(owner, dayFolder, "already_there.png", "image/png", new MemoryStream(bytes));

            // Create a second file (same content) outside the target folder.
            var dupFile = await files.CreateAsync(owner, null, "elsewhere.png", "image/png", new MemoryStream(bytes));

            // Set date taken on the shared blob.
            await db.BlobMetadata
                .Where(m => m.BlobObjectId == existingFile.BlobObjectId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.DateTaken, dateTaken)
                    .SetProperty(m => m.DateTakenSource, "DateTimeOriginal")
                    .SetProperty(m => m.MediaCategory, "image"));
        }

        await StartRunAsync(factory, owner, DefaultOptions());
        await RunToCompletionAsync(factory);

        await using var check = factory.Services.CreateAsyncScope();
        var db2 = check.ServiceProvider.GetRequiredService<AppDbContext>();

        var activeFiles = await db2.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == owner && f.DeletedAt == null)
            .ToListAsync();
        Assert.Single(activeFiles);

        var finishedRun = await db2.PhotoOrganizerRuns.AsNoTracking().SingleAsync(r => r.OwnerUserId == owner);
        Assert.Equal(PhotoOrganizerStatuses.Succeeded, finishedRun.Status);
        Assert.Equal(1, finishedRun.ExactDuplicateRemovedCount);
    }

    // ------------------------------------------------------------------ scenario 5b
    // DETERMINISTIC regression for the ordering-dependent data-loss bug: the
    // candidate query processes files ordered by Id. When the out-of-target
    // duplicate sorts BEFORE the in-target survivor, the survivor must still be
    // kept. Previously the out-of-target copy removed itself + recorded a claim,
    // then the in-target copy saw that claim and removed itself too → BOTH gone
    // (0 active). We seed controlled Ids so the buggy order is forced every run.

    [Fact]
    public async Task ExistingInTarget_Survives_When_OutOfTarget_Duplicate_Sorts_First()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();

        var bytes = ImageFixtures.PlainPng();
        var dateTaken = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        // Smallest Id is processed first → force the out-of-target duplicate to
        // lead, which is exactly the order that used to delete both copies.
        var elsewhereId = new Guid("00000000-0000-0000-0000-000000000001");
        var survivorId = new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff");

        Guid dayFolder, blobId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
            var blobs = scope.ServiceProvider.GetRequiredService<IBlobService>();

            var (leafId, _) = await folders.EnsureFolderPathWithCountAsync(
                owner, null, ["Photos", "2024", "2024-06-15"], default);
            dayFolder = leafId!.Value;

            // One shared blob referenced by two FileItems (refcount = 2).
            var blob = await blobs.StoreAsync(new MemoryStream(bytes));
            await blobs.StoreAsync(new MemoryStream(bytes));
            blobId = blob.Id;

            db.BlobMetadata.Add(new BlobMetadata
            {
                Id = Guid.NewGuid(),
                BlobObjectId = blobId,
                SizeBytes = bytes.Length,
                DetectedContentType = "image/png",
                MediaCategory = MediaCategories.Image,
                DateTaken = dateTaken,
                DateTakenSource = "DateTimeOriginal",
            });

            var now = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            db.FileItems.Add(new FileItem
            {
                Id = survivorId,
                OwnerUserId = owner,
                ParentFolderId = dayFolder,
                BlobObjectId = blobId,
                Name = "already_there.png",
                MimeType = "image/png",
                SizeBytes = bytes.Length,
                CreatedAt = now,
                EffectiveDateTaken = dateTaken,
            });
            db.FileItems.Add(new FileItem
            {
                Id = elsewhereId,
                OwnerUserId = owner,
                ParentFolderId = null,
                BlobObjectId = blobId,
                Name = "elsewhere.png",
                MimeType = "image/png",
                SizeBytes = bytes.Length,
                CreatedAt = now,
                EffectiveDateTaken = dateTaken,
            });
            await db.SaveChangesAsync();
        }

        await StartRunAsync(factory, owner, DefaultOptions());
        await RunToCompletionAsync(factory);

        await using var check = factory.Services.CreateAsyncScope();
        var db2 = check.ServiceProvider.GetRequiredService<AppDbContext>();

        var active = await db2.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == owner && f.DeletedAt == null)
            .ToListAsync();
        var survivor = Assert.Single(active);
        Assert.Equal(survivorId, survivor.Id);            // the in-target copy is kept
        Assert.Equal(dayFolder, survivor.ParentFolderId);

        var run = await db2.PhotoOrganizerRuns.AsNoTracking().SingleAsync(r => r.OwnerUserId == owner);
        Assert.Equal(PhotoOrganizerStatuses.Succeeded, run.Status);
        Assert.Equal(1, run.ExactDuplicateRemovedCount);
    }

    // ------------------------------------------------------------------ scenario 6
    // Idempotency / re-run: running the organizer a second time on an already-
    // organised library must not remove anything further.

    [Fact]
    public async Task Rerun_IsIdempotent()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();

        var bytes = ImageFixtures.PlainPng();
        var dateTaken = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            await files.CreateAsync(owner, null, "photo_a.png", "image/png", new MemoryStream(bytes));
            await files.CreateAsync(owner, null, "photo_b.png", "image/png", new MemoryStream(bytes));
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var file = await db.FileItems.FirstAsync(f => f.OwnerUserId == owner);
            await SetDateTakenAsync(factory, file.BlobObjectId, dateTaken);
        }

        // First run: organises + dedupes.
        await StartRunAsync(factory, owner, DefaultOptions());
        await RunToCompletionAsync(factory);

        await using var mid = factory.Services.CreateAsyncScope();
        var midDb = mid.ServiceProvider.GetRequiredService<AppDbContext>();
        var activeAfterFirst = await midDb.FileItems.AsNoTracking()
            .CountAsync(f => f.OwnerUserId == owner && f.DeletedAt == null);
        Assert.Equal(1, activeAfterFirst);

        // Second run: nothing more to dedupe.
        await StartRunAsync(factory, owner, DefaultOptions());
        await RunToCompletionAsync(factory);

        await using var check = factory.Services.CreateAsyncScope();
        var checkDb2 = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var activeAfterSecond = await checkDb2.FileItems.AsNoTracking()
            .CountAsync(f => f.OwnerUserId == owner && f.DeletedAt == null);
        Assert.Equal(1, activeAfterSecond);

        var runs = await checkDb2.PhotoOrganizerRuns.AsNoTracking()
            .Where(r => r.OwnerUserId == owner)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();
        Assert.Equal(2, runs.Count);

        var firstRun = runs[0];
        var secondRun = runs[1];
        Assert.Equal(1, firstRun.ExactDuplicateRemovedCount);
        Assert.Equal(0, secondRun.ExactDuplicateRemovedCount); // second run finds nothing to dedupe
    }

    // ------------------------------------------------------------------ scenario 7
    // Counter correctness: with N exact duplicates the run counter equals N.

    [Fact]
    public async Task ExactDuplicateRemovedCount_MatchesActualRemovals()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();

        var bytes = ImageFixtures.PlainPng();
        var dateTaken = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        // Upload 4 copies of the same content.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            for (var i = 0; i < 4; i++)
            {
                await files.CreateAsync(owner, null, $"copy_{i}.png", "image/png", new MemoryStream(bytes));
            }
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var file = await db.FileItems.FirstAsync(f => f.OwnerUserId == owner);
            await SetDateTakenAsync(factory, file.BlobObjectId, dateTaken);
        }

        await StartRunAsync(factory, owner, DefaultOptions());
        await RunToCompletionAsync(factory);

        await using var check = factory.Services.CreateAsyncScope();
        var checkDb = check.ServiceProvider.GetRequiredService<AppDbContext>();

        var activeCount = await checkDb.FileItems.AsNoTracking()
            .CountAsync(f => f.OwnerUserId == owner && f.DeletedAt == null);
        Assert.Equal(1, activeCount); // only survivor remains

        var softDeletedCount = await checkDb.FileItems.AsNoTracking()
            .CountAsync(f => f.OwnerUserId == owner && f.DeletedAt != null);
        Assert.Equal(3, softDeletedCount);

        var finishedRun = await checkDb.PhotoOrganizerRuns.AsNoTracking().SingleAsync(r => r.OwnerUserId == owner);
        Assert.Equal(PhotoOrganizerStatuses.Succeeded, finishedRun.Status);
        Assert.Equal(3, finishedRun.ExactDuplicateRemovedCount);
    }

    // ------------------------------------------------------------------ scenario 8
    // BlobObject reference count: after deduplication the survivor still holds
    // the blob reference so the BlobJanitor cannot collect the physical file.

    [Fact]
    public async Task BlobReferenceCount_RemainsSafe_AfterDedup()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();

        var bytes = ImageFixtures.PlainPng();
        var dateTaken = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        Guid blobObjectId;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var fileA = await files.CreateAsync(owner, null, "dup_a.png", "image/png", new MemoryStream(bytes));
            await files.CreateAsync(owner, null, "dup_b.png", "image/png", new MemoryStream(bytes));
            blobObjectId = fileA.BlobObjectId;
        }

        await SetDateTakenAsync(factory, blobObjectId, dateTaken);

        var refCountBefore = await GetRefCountAsync(factory, blobObjectId);
        Assert.Equal(2L, refCountBefore); // two FileItems reference the blob

        await StartRunAsync(factory, owner, DefaultOptions());
        await RunToCompletionAsync(factory);

        var refCountAfter = await GetRefCountAsync(factory, blobObjectId);
        // SoftDeleteAsync decrements by 1; survivor still holds 1 reference.
        Assert.Equal(1L, refCountAfter);
    }

    private static async Task<long> GetRefCountAsync(SqliteWebApplicationFactory factory, Guid blobObjectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BlobObjects.AsNoTracking()
            .Where(b => b.Id == blobObjectId)
            .Select(b => b.ReferenceCount)
            .SingleAsync();
    }

    // ------------------------------------------------------------------ scenario 9
    // Privacy / no-leak: the run status response must not expose BlobObjectId
    // or any other storage internal, even when exact-duplicate data is present.

    [Fact]
    public async Task RunStatus_ExactDuplicateCount_NoBlobObjectIdInResponse()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();

        var bytes = ImageFixtures.PlainPng();
        var dateTaken = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        Guid blobObjectId;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var f = await files.CreateAsync(owner, null, "img.png", "image/png", new MemoryStream(bytes));
            await files.CreateAsync(owner, null, "img2.png", "image/png", new MemoryStream(bytes));
            blobObjectId = f.BlobObjectId;
        }

        await SetDateTakenAsync(factory, blobObjectId, dateTaken);

        var run = await StartRunAsync(factory, owner, DefaultOptions());
        await RunToCompletionAsync(factory);

        await using var readScope = factory.Services.CreateAsyncScope();
        var organizer = readScope.ServiceProvider.GetRequiredService<PhotoDateTakenOrganizerService>();
        var status = await organizer.GetRunStatusAsync(owner, run.RunId, default);
        Assert.NotNull(status);
        Assert.Equal(1, status.ExactDuplicateRemovedCount);

        // Serialize and verify no storage internals leak.
        var json = System.Text.Json.JsonSerializer.Serialize(status);
        foreach (var needle in new[]
                 {
                     "storageKey", "StorageKey",
                     "blobObjectId", "BlobObjectId",
                     "sha256", "Sha256",
                     "objects/",
                 })
        {
            Assert.DoesNotContain(needle, json, StringComparison.Ordinal);
        }

        // The counter itself must be present as a plain integer.
        Assert.Contains("ExactDuplicateRemovedCount", json, StringComparison.Ordinal);
    }
}
