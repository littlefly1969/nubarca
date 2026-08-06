using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Cli;
using NubArca.Api.Ai;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Storage;

// Slice 97 (bug 3) — BlobObject.ReferenceCount is derived accounting; the
// owner rows (active file_items + file_thumbnails) are the truth. These tests
// cover: healthy flows stay matched, in-process failure paths release their
// acquisition, the audit detects both drift directions (the janitor-invisible
// leak and the dangerous zero-ref-with-owners), repair corrects them without
// touching bytes, and the janitor then reclaims normally.
public sealed class BlobReferenceAuditTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public BlobReferenceAuditTests()
    {
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["BlobJanitor:Enabled"] = "true",
            ["BlobJanitor:GraceMinutes"] = "0",
        });
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<FileItem> UploadAsync(Guid ownerId, string name, byte[] bytes)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, null, name, "image/png", new MemoryStream(bytes));
    }

    private async Task<BlobReferenceAuditReport> AuditAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>().AuditAsync();
    }

    private async Task<BlobReferenceRepairResult> RepairAsync(bool dryRun)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>()
            .RepairAsync(dryRun);
    }

    // A leaked blob exactly like the field case: ReferenceCount = 1, no
    // file_items / file_thumbnails / metadata rows, no physical bytes needed.
    private async Task<Guid> SeedOrphanedBlobAsync(long refCount = 1)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = sha,
            StorageKey = $"objects/{sha[..2]}/{sha[2..4]}/{sha}",
            SizeBytes = 75597,
            ReferenceCount = refCount,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
        };
        db.BlobObjects.Add(blob);
        await db.SaveChangesAsync();
        return blob.Id;
    }

    [Fact]
    public async Task Healthy_Upload_With_Derivatives_Stays_Fully_Matched()
    {
        var owner = await _factory.SeedUserAsync();
        await UploadAsync(owner, "a.png", ImageFixtures.PlainPng(600, 600));
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MediaDerivativesBackfillService>()
                .RunAsync(new MediaDerivativesBackfillOptions { MissingOnly = true });
        }

        var report = await AuditAsync();

        // Original + small + medium each contribute an owner reference. Current
        // no-upscale geometry may make small and medium byte-identical for this
        // 600 px source, so content-addressed storage can legitimately hold
        // fewer than three physical blobs.
        Assert.True(report.TotalComputedReferences >= 3);
        Assert.Equal(report.TotalBlobs, report.MatchedReferenceCount);
        Assert.Equal(0, report.DbRefcountTooHigh);
        Assert.Equal(0, report.DbRefcountTooLow);
        Assert.Equal(report.TotalDbReferences, report.TotalComputedReferences);
    }

    [Fact]
    public async Task Duplicate_Name_Upload_Releases_Its_Acquired_Reference()
    {
        var owner = await _factory.SeedUserAsync();
        var bytes = ImageFixtures.PlainPng(600, 600);
        await UploadAsync(owner, "a.png", bytes);
        await Assert.ThrowsAsync<DuplicateFileNameException>(
            () => UploadAsync(owner, "a.png", bytes));

        var report = await AuditAsync();
        Assert.Equal(0, report.DbRefcountTooHigh);
        Assert.Equal(0, report.DbRefcountTooLow);
    }

    [Fact]
    public async Task Soft_Deleted_File_No_Longer_Counts_As_An_Owner()
    {
        var owner = await _factory.SeedUserAsync();
        var file = await UploadAsync(owner, "a.png", ImageFixtures.PlainPng(600, 600));

        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            Assert.True(await files.SoftDeleteAsync(owner, file.Id));
        }

        // Soft delete released the original's reference; the audit's owner
        // model (active files + all thumbnail rows) must agree exactly.
        var report = await AuditAsync();
        Assert.Equal(report.TotalBlobs, report.MatchedReferenceCount);
    }

    [Fact]
    public async Task Cancellation_After_Blob_Acquire_Releases_The_Reference()
    {
        // Direct-construction harness so a decorator can cancel the token
        // deterministically right after the blob reference is acquired.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var db = new AppDbContext(dbOptions);
        db.Database.EnsureCreated();
        var storageRoot = Path.Combine(Path.GetTempPath(), $"nc-cancel97-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storageRoot);
        try
        {
            var storage = new LocalFileSystemBlobStorage(
                Options.Create(new BlobStorageOptions { RootPath = storageRoot }));
            var inner = new BlobService(storage, db, TimeProvider.System);
            using var cts = new CancellationTokenSource();
            var cancelling = new CancelAfterStoreBlobService(inner, cts);
            var thumbnails = new FileThumbnailService(
                db, cancelling, storage, new SyntheticVideoPosterProvider(),
                TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
                Options.Create(new ImageProcessingOptions()));
            var service = new FileItemService(db, cancelling, thumbnails, TimeProvider.System);

            var owner = new User
            {
                Id = Guid.NewGuid(),
                Email = "owner@example.com",
                DisplayName = "Owner",
                CreatedAt = DateTime.UtcNow,
            };
            db.Users.Add(owner);
            await db.SaveChangesAsync();

            // The job token is cancelled the moment StoreAsync returns — i.e.
            // AFTER the refcount increment committed, BEFORE any owner row.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(
                owner.Id, null, "a.png", "image/png",
                new MemoryStream(ImageFixtures.PlainPng(64, 64)), cts.Token));

            // The cleanup release ran on CancellationToken.None: no leak.
            var blob = await db.BlobObjects.AsNoTracking().SingleAsync();
            Assert.Equal(0, blob.ReferenceCount);
            Assert.False(await db.FileItems.AnyAsync());

            var audit = new BlobReferenceAuditService(db, TimeProvider.System);
            var report = await audit.AuditAsync();
            Assert.Equal(0, report.OrphanedNonzeroRefcount);
        }
        finally
        {
            try { Directory.Delete(storageRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Audit_Detects_Orphan_Repair_Zeroes_It_And_Janitor_Reclaims()
    {
        var owner = await _factory.SeedUserAsync();
        await UploadAsync(owner, "a.png", ImageFixtures.PlainPng(600, 600));
        var orphanId = await SeedOrphanedBlobAsync();

        // Audit: one janitor-invisible leak, everything else matched.
        var report = await AuditAsync();
        Assert.Equal(1, report.DbRefcountTooHigh);
        Assert.Equal(1, report.OrphanedNonzeroRefcount);
        Assert.Equal(0, report.DbRefcountTooLow);

        // Dry-run reports without mutating.
        var dry = await RepairAsync(dryRun: true);
        Assert.True(dry.DryRun);
        Assert.Equal(1, dry.Mismatched);
        Assert.Equal(0, dry.Repaired);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, (await db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == orphanId)).ReferenceCount);
        }

        // Repair recomputes from the owner tables; bytes are NOT touched.
        var repair = await RepairAsync(dryRun: false);
        Assert.Equal(1, repair.Repaired);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repairedBlob = await db.BlobObjects.AsNoTracking()
                .SingleAsync(b => b.Id == orphanId);
            Assert.Equal(0, repairedBlob.ReferenceCount);
            Assert.NotNull(repairedBlob.PurgeEligibleAt);
        }
        Assert.Equal(0, (await AuditAsync()).OrphanedNonzeroRefcount);

        // The janitor can NOW reclaim it under its normal grace rules.
        var janitor = _factory.Services.GetRequiredService<BlobJanitor>();
        await janitor.RunOnceAsync(CancellationToken.None);
        await using (var verify = _factory.Services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.BlobObjects.AnyAsync(b => b.Id == orphanId));
        }
    }

    [Fact]
    public async Task Audit_Flags_Zero_Refcount_With_Live_Owners_And_Repair_Restores_It()
    {
        var owner = await _factory.SeedUserAsync();
        var file = await UploadAsync(owner, "a.png", ImageFixtures.PlainPng(600, 600));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BlobObjects.Where(b => b.Id == file.BlobObjectId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.ReferenceCount, 0L)
                    .SetProperty(
                        b => b.PurgeEligibleAt,
                        _ => (DateTime?)DateTime.UtcNow.AddDays(-1)));
        }

        // The most dangerous direction: the janitor could delete live bytes.
        var report = await AuditAsync();
        Assert.Equal(1, report.DbRefcountTooLow);
        Assert.Equal(1, report.ZeroRefWithRealReferences);

        var repair = await RepairAsync(dryRun: false);
        Assert.Equal(1, repair.Repaired);
        await using (var verify = _factory.Services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            var repairedBlob = await db.BlobObjects.AsNoTracking()
                .SingleAsync(b => b.Id == file.BlobObjectId);
            Assert.Equal(1, repairedBlob.ReferenceCount);
            Assert.Null(repairedBlob.PurgeEligibleAt);
        }
    }

    [Fact]
    public async Task Face_Preview_Is_A_Real_Owner_And_Repair_Preserves_Its_Blob()
    {
        var owner = await _factory.SeedUserAsync();
        var source = await UploadAsync(owner, "face.png", ImageFixtures.PlainPng(600, 600));
        var previewBlobId = await SeedOrphanedBlobAsync();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            await services.GetRequiredService<IAiProfileRegistry>().SeedDeterministicProfilesAsync();
            var db = services.GetRequiredService<AppDbContext>();
            var profileId = await db.AiProfiles.AsNoTracking()
                .Where(p => p.Key == "det-face-detection-v1")
                .Select(p => p.Id)
                .SingleAsync();
            var faceId = Guid.NewGuid();
            db.FaceDetections.Add(new FaceDetection
            {
                Id = faceId,
                BlobObjectId = source.BlobObjectId,
                ProfileId = profileId,
                FaceIndex = 0,
                BoundingBoxWidth = 0.5,
                BoundingBoxHeight = 0.5,
                CreatedAt = DateTime.UtcNow,
            });
            db.FacePreviews.Add(new FacePreview
            {
                Id = Guid.NewGuid(),
                FaceDetectionId = faceId,
                BlobObjectId = previewBlobId,
                Size = "small",
                Width = 160,
                Height = 160,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var healthy = await AuditAsync();
        Assert.Equal(0, healthy.DbRefcountTooHigh);
        Assert.Equal(0, healthy.DbRefcountTooLow);
        Assert.Equal(healthy.TotalDbReferences, healthy.TotalComputedReferences);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BlobObjects.Where(b => b.Id == previewBlobId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.ReferenceCount, 0L));
        }

        var drifted = await AuditAsync();
        Assert.Equal(1, drifted.DbRefcountTooLow);
        Assert.Equal(1, drifted.ZeroRefWithRealReferences);

        var repair = await RepairAsync(dryRun: false);
        Assert.Equal(1, repair.Repaired);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.BlobObjects.AsNoTracking()
                .Where(b => b.Id == previewBlobId)
                .Select(b => b.ReferenceCount)
                .SingleAsync());
        }
    }

    [Fact]
    public async Task Integrity_Check_Exposes_Reference_Integrity_Counts()
    {
        var adminId = await _factory.SeedUserAsync("admin@example.com");
        await _factory.PromoteToAdminAsync(adminId);
        var client = await _factory.LoginAsync("admin@example.com");
        await SeedOrphanedBlobAsync();

        var resp = await client.GetAsync("/api/admin/storage-stats?refresh=true&physical=true");
        resp.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var integrity = root.GetProperty("referenceIntegrity");
        Assert.Equal(1, integrity.GetProperty("refcountMismatchCount").GetInt32());
        Assert.Equal(1, integrity.GetProperty("orphanedNonzeroRefcountCount").GetInt32());
        Assert.Equal(0, integrity.GetProperty("zeroRefWithRealReferencesCount").GetInt32());

        // The fast dashboard load skips the (full-table) audit entirely.
        var fast = await client.GetAsync("/api/admin/storage-stats?refresh=true&physical=false");
        var fastRoot = JsonDocument.Parse(await fast.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Null, fastRoot.GetProperty("referenceIntegrity").ValueKind);
    }

    [Fact]
    public async Task Cli_Audit_And_Repair_Print_Counts_Only()
    {
        var owner = await _factory.SeedUserAsync();
        await UploadAsync(owner, "secret-name.png", ImageFixtures.PlainPng(600, 600));
        await SeedOrphanedBlobAsync();

        var audit = await RunCliAsync("storage", "blobs", "audit-references");
        Assert.Equal(0, audit.Exit);
        Assert.Contains("orphaned_nonzero_refcount=1", audit.Stdout);
        Assert.Contains("repair-references", audit.Stdout); // operator hint

        var dry = await RunCliAsync("storage", "blobs", "repair-references", "--dry-run");
        Assert.Equal(0, dry.Exit);
        Assert.Contains("mismatched=1", dry.Stdout);
        Assert.Contains("repaired=0", dry.Stdout);

        var repair = await RunCliAsync("storage", "blobs", "repair-references");
        Assert.Equal(0, repair.Exit);
        Assert.Contains("repaired=1", repair.Stdout);

        var after = await RunCliAsync("storage", "blobs", "audit-references");
        Assert.Contains("orphaned_nonzero_refcount=0", after.Stdout);

        foreach (var output in new[] { audit.Stdout, dry.Stdout, repair.Stdout, after.Stdout })
        {
            Assert.DoesNotContain("secret-name", output);
            Assert.DoesNotMatch(
                new Regex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"),
                output);
            Assert.DoesNotMatch(new Regex("[0-9a-f]{64}"), output);
            Assert.DoesNotContain("objects/", output);
        }
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunCliAsync(params string[] args)
    {
        using var scope = _factory.Services.CreateScope();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliEntryPoint.RunAsync(args, stdout, stderr, () => scope.ServiceProvider);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    // Cancels the linked token as soon as the blob reference has been
    // acquired — the tightest deterministic stand-in for "worker stop right
    // after the refcount increment committed".
    private sealed class CancelAfterStoreBlobService : IBlobService
    {
        private readonly IBlobService _inner;
        private readonly CancellationTokenSource _cts;

        public CancelAfterStoreBlobService(IBlobService inner, CancellationTokenSource cts)
        {
            _inner = inner;
            _cts = cts;
        }

        public async Task<BlobObject> StoreAsync(Stream content, CancellationToken cancellationToken = default)
        {
            var blob = await _inner.StoreAsync(content, cancellationToken);
            _cts.Cancel();
            return blob;
        }

        public async Task<BlobStoreResult> StoreMeasuredAsync(Stream content, CancellationToken cancellationToken = default)
        {
            var result = await _inner.StoreMeasuredAsync(content, cancellationToken);
            _cts.Cancel();
            return result;
        }

        public Task<Stream> OpenContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
            => _inner.OpenContentAsync(blobObjectId, cancellationToken);

        public Task<BlobObject> StoreDerivedAsync(Stream content, CancellationToken cancellationToken = default)
            => _inner.StoreDerivedAsync(content, cancellationToken);

        public Task<Stream?> OpenDerivedContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
            => _inner.OpenDerivedContentAsync(blobObjectId, cancellationToken);

        public Task<bool> TryRestoreDerivedFromOriginalAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
            => _inner.TryRestoreDerivedFromOriginalAsync(blobObjectId, cancellationToken);

        public Task ReleaseAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
            => _inner.ReleaseAsync(blobObjectId, cancellationToken);

        public Task MarkPurgeEligibleIfUnreferencedAsync(
            Guid blobObjectId,
            CancellationToken cancellationToken = default)
            => _inner.MarkPurgeEligibleIfUnreferencedAsync(blobObjectId, cancellationToken);

        public Task<BlobObject> AcquireExistingAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
            => _inner.AcquireExistingAsync(blobObjectId, cancellationToken);
    }
}
