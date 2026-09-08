using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.Files;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Storage;

// Slice 65: operator reconciliation between the physical store and the
// BlobObject table. Uses the real LocalFileSystemBlobStorage rooted at the
// factory's StorageRoot so on-disk manipulation is genuine.
public sealed class StorageReconciliationServiceTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public StorageReconciliationServiceTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> UploadAsync(HttpClient client, byte[] bytes, string name)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var resp = await client.PostAsync("/api/files",
            new MultipartFormDataContent { { part, "file", name } });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<FileSummary>())!.Id;
    }

    // Writes a stray object file under objects/{a}/{b}/{sha} that has no
    // BlobObject row — an orphan from the reconciler's point of view.
    // `ageHours` backdates the object's write time. The sweep refuses to delete
    // anything younger than StorageReconciliationOptions.MinimumOrphanAge, so a
    // test that wants a genuinely stale orphan has to say so — a freshly written
    // object is treated as possibly in-flight, which is the whole point.
    private string WriteOrphanObject(char fill = 'a', double ageHours = 48)
    {
        var sha = new string(fill, 64);
        var dir = Path.Combine(_factory.StorageRoot, "objects", sha[..2], sha[2..4]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, sha);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        Backdate(path, ageHours);
        return path;
    }

    private static void Backdate(string path, double ageHours) =>
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-ageHours));

    private async Task<StorageReconciliationResult> RunAsync(StorageReconciliationOptions options)
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<StorageReconciliationService>();
        return await svc.RunAsync(options);
    }

    [Fact]
    public async Task DryRun_Reports_Orphan_Without_Deleting()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "real.bin"); // one legit blob
        var orphanPath = WriteOrphanObject();

        var result = await RunAsync(new StorageReconciliationOptions { DryRun = true });

        Assert.True(result.DryRun);
        Assert.Equal(1, result.OrphanPhysicalObjects);
        Assert.Equal(0, result.OrphansDeleted);
        Assert.True(File.Exists(orphanPath)); // dry-run never deletes
    }

    [Fact]
    public async Task Delete_Orphans_Removes_Only_The_Orphan()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "real.bin");
        var orphanPath = WriteOrphanObject();

        var result = await RunAsync(new StorageReconciliationOptions
        {
            DryRun = false,
            DeleteOrphans = true,
        });

        Assert.Equal(1, result.OrphanPhysicalObjects);
        Assert.Equal(1, result.OrphansDeleted);
        Assert.False(File.Exists(orphanPath));

        // The legit blob row still has its physical object (not missing).
        var after = await RunAsync(new StorageReconciliationOptions { DryRun = true });
        Assert.Equal(0, after.OrphanPhysicalObjects);
        Assert.Equal(0, after.MissingPhysicalObjects);
    }

    [Fact]
    public async Task Detects_BlobObject_Row_With_Missing_Physical_File()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[128], "vanishing.bin");

        // Delete the physical object out from under the DB row.
        string storageKey;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            storageKey = await db.BlobObjects.AsNoTracking().Select(b => b.StorageKey).SingleAsync();
        }
        var physicalPath = Path.Combine(
            _factory.StorageRoot, storageKey.Replace('/', Path.DirectorySeparatorChar));
        File.Delete(physicalPath);

        var result = await RunAsync(new StorageReconciliationOptions { DryRun = true });

        Assert.Equal(1, result.BlobObjectRows);
        Assert.Equal(1, result.MissingPhysicalObjects);
        Assert.Equal(0, result.OrphanPhysicalObjects);
    }

    [Fact]
    public async Task Clean_Store_Reports_No_Orphans_Or_Missing()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "a.bin");
        await UploadAsync(client, new byte[128], "b.bin");

        var result = await RunAsync(new StorageReconciliationOptions { DryRun = true });

        Assert.Equal(0, result.OrphanPhysicalObjects);
        Assert.Equal(0, result.MissingPhysicalObjects);
        Assert.Equal(0, result.OrphansDeleted);
    }

    [Fact]
    public async Task Report_Line_Contains_No_Storage_Key_Or_Path()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "a.bin");
        WriteOrphanObject();

        string? line = null;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<StorageReconciliationService>();
            await svc.RunAsync(new StorageReconciliationOptions { DryRun = true }, l => line = l);
        }

        Assert.NotNull(line);
        Assert.DoesNotContain("objects/", line!, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 64), line!, StringComparison.Ordinal);
        Assert.DoesNotContain(_factory.StorageRoot, line!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Print artifacts own physical objects WITHOUT a BlobObject row. They must
    // never be classified as orphans, and --delete-orphans must never remove
    // them: that would delete a queued job's rendered image out from under the
    // Print Agent.
    // ---------------------------------------------------------------------

    // Renders bytes through the SAME store the print services use, then points
    // a PrintJob at the resulting key — exactly what PrintStationService and
    // PartyPrintSubmissionService do. No BlobObject row is created, by design.
    private async Task<(Guid JobId, string Path)> SeedPrintArtifactAsync(
        string kind, byte[] bytes)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var artifacts = scope.ServiceProvider.GetRequiredService<IDerivedBlobStorage>();

        await using var source = new MemoryStream(bytes, writable: false);
        var stored = await artifacts.WriteAsync(source);

        var ownerId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = ownerId,
            Email = $"print-{ownerId:N}@example.com",
            DisplayName = "Print Owner",
            CreatedAt = DateTime.UtcNow,
        });
        db.PrintStations.Add(new PrintStation
        {
            Id = stationId,
            OwnerUserId = ownerId,
            Name = "Station",
            CreatedAt = DateTime.UtcNow,
        });
        db.PrinterDevices.Add(new PrinterDevice
        {
            Id = deviceId,
            PrintStationId = stationId,
            DeviceKey = $"dev-{deviceId:N}",
            DisplayName = "Printer",
            AdapterKind = "test",
            LastSeenAt = DateTime.UtcNow,
        });
        var jobId = Guid.NewGuid();
        db.PrintJobs.Add(new PrintJob
        {
            Id = jobId,
            OwnerUserId = ownerId,
            PrintStationId = stationId,
            PrinterDeviceId = deviceId,
            Kind = kind,
            Format = PrintFormats.Photo10x15,
            State = PrintJobStates.Ready,
            ArtifactStorageKey = stored.StorageKey,
            ArtifactContentType = "image/png",
            ArtifactByteLength = stored.SizeBytes,
            CreatedAt = DateTime.UtcNow,
            RenderedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var key = stored.StorageKey;
        var path = Path.Combine(
            _factory.StorageRoot, key.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), "the seeded print artifact must be on disk");
        return (jobId, path);
    }

    [Fact]
    public async Task Live_Print_Artifact_Is_Not_Classified_As_Orphan()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "real.bin");
        var (_, artifactPath) = await SeedPrintArtifactAsync(
            PrintJobKinds.OwnerPhoto, new byte[] { 9, 9, 9, 9 });

        var result = await RunAsync(new StorageReconciliationOptions { DryRun = true });

        Assert.Equal(0, result.OrphanPhysicalObjects);
        Assert.Equal(1, result.ProtectedNonBlobObjects);
        Assert.True(File.Exists(artifactPath));
    }

    [Fact]
    public async Task Live_Print_Artifact_Survives_Destructive_Delete_Orphans()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "real.bin");
        var (jobId, artifactPath) = await SeedPrintArtifactAsync(
            PrintJobKinds.OwnerPhoto, new byte[] { 7, 7, 7, 7 });
        var orphanPath = WriteOrphanObject();

        var result = await RunAsync(new StorageReconciliationOptions
        {
            DryRun = false,
            DeleteOrphans = true,
        });

        // The genuine orphan goes; the print artifact stays.
        Assert.Equal(1, result.OrphanPhysicalObjects);
        Assert.Equal(1, result.OrphansDeleted);
        Assert.Equal(1, result.ProtectedNonBlobObjects);
        Assert.False(File.Exists(orphanPath));
        Assert.True(File.Exists(artifactPath), "a live print artifact must never be deleted");

        // No Print regression: the job can still resolve its artifact.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.PrintJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.NotNull(job.ArtifactStorageKey);
        var artifacts = scope.ServiceProvider.GetRequiredService<IDerivedBlobStorage>();
        Assert.True(await artifacts.ExistsAsync(job.ArtifactStorageKey!));
    }

    [Fact]
    public async Task Party_Print_Artifact_Is_Protected_By_The_Same_Rule()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "real.bin");
        var (_, artifactPath) = await SeedPrintArtifactAsync(
            PrintJobKinds.PartyStrip4, new byte[] { 5, 5, 5, 5 });

        var result = await RunAsync(new StorageReconciliationOptions
        {
            DryRun = false,
            DeleteOrphans = true,
        });

        // Party prints reach storage through the same column, so they are
        // covered by the same set — not a second implementation.
        Assert.Equal(0, result.OrphanPhysicalObjects);
        Assert.Equal(1, result.ProtectedNonBlobObjects);
        Assert.True(File.Exists(artifactPath));
    }

    [Fact]
    public async Task Two_Jobs_Sharing_One_Artifact_Are_Counted_Once()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "real.bin");

        // Identical bytes are content-addressed to the SAME physical object,
        // so two jobs legitimately share one artifact.
        var identical = new byte[] { 4, 2, 4, 2 };
        var (_, firstPath) = await SeedPrintArtifactAsync(PrintJobKinds.OwnerPhoto, identical);
        var (_, secondPath) = await SeedPrintArtifactAsync(PrintJobKinds.PartyPhoto, identical);
        Assert.Equal(firstPath, secondPath);

        var result = await RunAsync(new StorageReconciliationOptions { DryRun = true });

        Assert.Equal(0, result.OrphanPhysicalObjects);
        Assert.Equal(1, result.ProtectedNonBlobObjects);
        Assert.True(File.Exists(firstPath));
    }

    [Fact]
    public async Task Print_Artifact_Protection_Is_Stable_Across_Repeated_Runs()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "real.bin");
        var (_, artifactPath) = await SeedPrintArtifactAsync(
            PrintJobKinds.OwnerPhoto, new byte[] { 3, 3, 3, 3 });

        var first = await RunAsync(new StorageReconciliationOptions
        {
            DryRun = false,
            DeleteOrphans = true,
        });
        var second = await RunAsync(new StorageReconciliationOptions
        {
            DryRun = false,
            DeleteOrphans = true,
        });

        Assert.Equal(first.ProtectedNonBlobObjects, second.ProtectedNonBlobObjects);
        Assert.Equal(first.OrphanPhysicalObjects, second.OrphanPhysicalObjects);
        Assert.Equal(0, second.OrphansDeleted);
        Assert.True(File.Exists(artifactPath));
    }

    [Fact]
    public async Task Print_Artifact_In_A_Separate_Derived_Root_Is_Protected()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "real.bin");

        // Split roots: the artifact lives ONLY in the derived root, which the
        // single-root default never exercises.
        var derivedRoot = Path.Combine(
            Path.GetTempPath(), $"nubarca-derived-{Guid.NewGuid():N}");
        Directory.CreateDirectory(derivedRoot);
        try
        {
            var derived = new DerivedFsBlobStorage(derivedRoot, 64 * 1024 * 1024);
            await using var source = new MemoryStream(new byte[] { 8, 8, 8, 8 }, writable: false);
            var stored = await derived.WriteAsync(source);
            var artifactPath = Path.Combine(
                derivedRoot, stored.StorageKey.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(artifactPath));

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ownerId = Guid.NewGuid();
            var stationId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            db.Users.Add(new User
            {
                Id = ownerId,
                Email = $"split-{ownerId:N}@example.com",
                DisplayName = "Split Owner",
                CreatedAt = DateTime.UtcNow,
            });
            db.PrintStations.Add(new PrintStation
            {
                Id = stationId,
                OwnerUserId = ownerId,
                Name = "Station",
                CreatedAt = DateTime.UtcNow,
            });
            db.PrinterDevices.Add(new PrinterDevice
            {
                Id = deviceId,
                PrintStationId = stationId,
                DeviceKey = $"dev-{deviceId:N}",
                DisplayName = "Printer",
                AdapterKind = "test",
                LastSeenAt = DateTime.UtcNow,
            });
            db.PrintJobs.Add(new PrintJob
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerId,
                PrintStationId = stationId,
                PrinterDeviceId = deviceId,
                Kind = PrintJobKinds.OwnerPhoto,
                Format = PrintFormats.Photo10x15,
                State = PrintJobStates.Ready,
                ArtifactStorageKey = stored.StorageKey,
                ArtifactContentType = "image/png",
                ArtifactByteLength = stored.SizeBytes,
                CreatedAt = DateTime.UtcNow,
                RenderedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            var original = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
            var svc = new StorageReconciliationService(db, original, derived);
            var result = await svc.RunAsync(new StorageReconciliationOptions
            {
                DryRun = false,
                DeleteOrphans = true,
            });

            Assert.Equal(1, result.ProtectedNonBlobObjects);
            Assert.Equal(0, result.OrphanPhysicalObjects);
            Assert.True(File.Exists(artifactPath), "split-root print artifact must survive");
        }
        finally
        {
            try { Directory.Delete(derivedRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Print_Artifact_Protection_Leaks_No_Storage_Key_Or_Path()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "a.bin");
        var (_, artifactPath) = await SeedPrintArtifactAsync(
            PrintJobKinds.OwnerPhoto, new byte[] { 6, 6, 6, 6 });
        var artifactSha = Path.GetFileName(artifactPath);

        string? line = null;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<StorageReconciliationService>();
            await svc.RunAsync(new StorageReconciliationOptions { DryRun = true }, l => line = l);
        }

        Assert.NotNull(line);
        Assert.DoesNotContain("objects/", line!, StringComparison.Ordinal);
        Assert.DoesNotContain(artifactSha, line!, StringComparison.Ordinal);
        Assert.DoesNotContain(_factory.StorageRoot, line!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Write-before-commit. Destructive reconciliation is a conservative
    // mark-and-sweep: an object may only be deleted when it has been unowned
    // for a safety interval AND is still unowned when re-checked immediately
    // before deletion. These tests drive the two races deterministically by
    // hooking the storage layer at the exact point of interest — no sleeps,
    // no timing assumptions.
    // ---------------------------------------------------------------------

    // Wraps a real store and fires callbacks at the two moments that matter:
    // when the scan begins (the ownership snapshot has just been taken) and
    // when an object's age is queried (the scan is over, revalidation has not
    // happened yet).
    private sealed class HookedBlobStorage(IBlobStorage inner) : IBlobStorage
    {
        public Func<Task>? OnEnumerationStarting { get; set; }
        public Func<string, Task>? OnAgeQueried { get; set; }

        public Task<BlobWriteResult> WriteAsync(Stream c, CancellationToken ct = default)
            => inner.WriteAsync(c, ct);
        public Task<Stream> OpenReadAsync(string k, CancellationToken ct = default)
            => inner.OpenReadAsync(k, ct);
        public Task<bool> ExistsAsync(string k, CancellationToken ct = default)
            => inner.ExistsAsync(k, ct);
        public Task DeleteAsync(string k, CancellationToken ct = default)
            => inner.DeleteAsync(k, ct);

        public async Task<DateTimeOffset?> GetLastWriteTimeUtcAsync(
            string k, CancellationToken ct = default)
        {
            if (OnAgeQueried is not null)
            {
                await OnAgeQueried(k);
            }
            return await inner.GetLastWriteTimeUtcAsync(k, ct);
        }

        public async IAsyncEnumerable<string> EnumerateStorageKeysAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (OnEnumerationStarting is not null)
            {
                await OnEnumerationStarting();
            }
            await foreach (var key in inner.EnumerateStorageKeysAsync(ct))
            {
                yield return key;
            }
        }
    }

    // A PrintJob adopting an arbitrary existing key — ownership that never
    // passes through blob_objects.
    private async Task SeedPrintJobForKeyAsync(string storageKey, long sizeBytes)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ownerId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = ownerId,
            Email = $"adopt-{ownerId:N}@example.com",
            DisplayName = "Adopter",
            CreatedAt = DateTime.UtcNow,
        });
        db.PrintStations.Add(new PrintStation
        {
            Id = stationId,
            OwnerUserId = ownerId,
            Name = "Station",
            CreatedAt = DateTime.UtcNow,
        });
        db.PrinterDevices.Add(new PrinterDevice
        {
            Id = deviceId,
            PrintStationId = stationId,
            DeviceKey = $"dev-{deviceId:N}",
            DisplayName = "Printer",
            AdapterKind = "test",
            LastSeenAt = DateTime.UtcNow,
        });
        db.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerId,
            PrintStationId = stationId,
            PrinterDeviceId = deviceId,
            Kind = PrintJobKinds.OwnerPhoto,
            Format = PrintFormats.Photo10x15,
            State = PrintJobStates.Ready,
            ArtifactStorageKey = storageKey,
            ArtifactContentType = "image/png",
            ArtifactByteLength = sizeBytes,
            CreatedAt = DateTime.UtcNow,
            RenderedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static string ShaOf(string storageKey) => storageKey[(storageKey.LastIndexOf('/') + 1)..];

    // Commits ownership the way a real writer would: its own scope, its own
    // transaction, after the bytes are already on disk.
    private async Task CommitBlobOwnershipAsync(string storageKey, long sizeBytes)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.BlobObjects.Add(new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = ShaOf(storageKey),
            SizeBytes = sizeBytes,
            StorageKey = storageKey,
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<StorageReconciliationResult> RunHookedAsync(
        HookedBlobStorage storage, StorageReconciliationOptions options)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = new StorageReconciliationService(db, storage);
        return await svc.RunAsync(options);
    }

    [Fact]
    public async Task In_Flight_Object_Written_After_The_Ownership_Snapshot_Survives()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "real.bin");

        var real = _factory.Services.GetRequiredService<IBlobStorage>();
        var hooked = new HookedBlobStorage(real);

        // The writer stages its bytes AFTER reconciliation has read the owner
        // tables and BEFORE it commits the row that will own them. This is the
        // exact interleaving that used to lose data.
        string? inFlightKey = null;
        long inFlightSize = 0;
        hooked.OnEnumerationStarting = async () =>
        {
            if (inFlightKey is not null) return;
            await using var bytes = new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes("bytes-on-disk-owner-not-committed-yet"));
            var written = await real.WriteAsync(bytes);
            inFlightKey = written.StorageKey;
            inFlightSize = written.SizeBytes;
        };

        var result = await RunHookedAsync(hooked, new StorageReconciliationOptions
        {
            DryRun = false,
            DeleteOrphans = true,
        });

        Assert.NotNull(inFlightKey);
        var inFlightPath = Path.Combine(
            _factory.StorageRoot, inFlightKey!.Replace('/', Path.DirectorySeparatorChar));

        // It was seen as unowned, but it was young, so the sweep left it alone.
        Assert.Equal(1, result.OrphanPhysicalObjects);
        Assert.Equal(1, result.RecentPhysicalObjectsSkipped);
        Assert.Equal(0, result.OrphansDeleted);
        Assert.True(File.Exists(inFlightPath), "in-flight bytes must survive the sweep");

        // The writer now commits. The end state is a live row over live bytes.
        await CommitBlobOwnershipAsync(inFlightKey!, inFlightSize);

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.BlobObjects.AnyAsync(b => b.StorageKey == inFlightKey));
        Assert.True(File.Exists(inFlightPath));

        // And a later run sees a healthy store: nothing orphaned, nothing missing.
        var after = await RunAsync(new StorageReconciliationOptions { DryRun = true });
        Assert.Equal(0, after.OrphanPhysicalObjects);
        Assert.Equal(0, after.MissingPhysicalObjects);
    }

    [Fact]
    public async Task Object_Owned_Between_Scan_And_Revalidation_Survives()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "real.bin");

        // Old enough to clear the age gate, so ONLY the final revalidation can
        // save it — which is the point: age alone is not sufficient, because a
        // dedup re-upload can adopt ancient bytes without ever rewriting them.
        var orphanPath = WriteOrphanObject('b', ageHours: 72);
        var orphanKey = $"objects/bb/bb/{new string('b', 64)}";

        var real = _factory.Services.GetRequiredService<IBlobStorage>();
        var hooked = new HookedBlobStorage(real);
        hooked.OnAgeQueried = async key =>
        {
            if (key != orphanKey) return;
            hooked.OnAgeQueried = null; // once
            await CommitBlobOwnershipAsync(orphanKey, 3);
        };

        var result = await RunHookedAsync(hooked, new StorageReconciliationOptions
        {
            DryRun = false,
            DeleteOrphans = true,
        });

        Assert.Equal(1, result.OrphanPhysicalObjects);
        Assert.Equal(1, result.OrphansOwnedAtRevalidation);
        Assert.Equal(0, result.OrphansDeleted);
        Assert.True(File.Exists(orphanPath), "an object that gained an owner must survive");
    }

    [Fact]
    public async Task Print_Owner_Appearing_Between_Scan_And_Revalidation_Survives()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "real.bin");

        var orphanPath = WriteOrphanObject('c', ageHours: 72);
        var orphanKey = $"objects/cc/cc/{new string('c', 64)}";

        var real = _factory.Services.GetRequiredService<IBlobStorage>();
        var hooked = new HookedBlobStorage(real);
        hooked.OnAgeQueried = async key =>
        {
            if (key != orphanKey) return;
            hooked.OnAgeQueried = null;
            // A print job adopts the object — ownership WITHOUT a BlobObject
            // row, so only a revalidation that knows about non-blob owners can
            // see it.
            await SeedPrintJobForKeyAsync(orphanKey, 3);
        };

        var result = await RunHookedAsync(hooked, new StorageReconciliationOptions
        {
            DryRun = false,
            DeleteOrphans = true,
        });

        Assert.Equal(1, result.OrphansOwnedAtRevalidation);
        Assert.Equal(0, result.OrphansDeleted);
        Assert.True(File.Exists(orphanPath), "a print-owned object must survive");
    }

    [Fact]
    public async Task Recently_Written_Unowned_Object_Is_Not_Deleted()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "real.bin");
        var freshPath = WriteOrphanObject('d', ageHours: 0);

        var result = await RunAsync(new StorageReconciliationOptions
        {
            DryRun = false,
            DeleteOrphans = true,
        });

        Assert.Equal(1, result.OrphanPhysicalObjects);
        Assert.Equal(1, result.RecentPhysicalObjectsSkipped);
        Assert.Equal(0, result.OrphansDeleted);
        Assert.True(File.Exists(freshPath));
    }

    [Fact]
    public async Task Genuinely_Old_Unowned_Object_Is_Still_Deleted()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "real.bin");
        var stalePath = WriteOrphanObject('e', ageHours: 72);

        var result = await RunAsync(new StorageReconciliationOptions
        {
            DryRun = false,
            DeleteOrphans = true,
        });

        // The hardening must not turn the tool into a no-op.
        Assert.Equal(1, result.OrphanPhysicalObjects);
        Assert.Equal(0, result.RecentPhysicalObjectsSkipped);
        Assert.Equal(0, result.OrphansOwnedAtRevalidation);
        Assert.Equal(1, result.OrphansDeleted);
        Assert.False(File.Exists(stalePath));
    }

    [Fact]
    public async Task Repeated_Destructive_Reconcile_Is_Idempotent()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "real.bin");
        WriteOrphanObject('e', ageHours: 72);
        var freshPath = WriteOrphanObject('f', ageHours: 0);

        var destructive = new StorageReconciliationOptions
        {
            DryRun = false,
            DeleteOrphans = true,
        };
        var first = await RunAsync(destructive);
        var second = await RunAsync(destructive);

        Assert.Equal(1, first.OrphansDeleted);
        Assert.Equal(0, second.OrphansDeleted);
        // The young object is still protected on the second pass, not quietly
        // reclassified.
        Assert.Equal(1, second.RecentPhysicalObjectsSkipped);
        Assert.Equal(0, second.MissingPhysicalObjects);
        Assert.True(File.Exists(freshPath));
    }

    [Fact]
    public async Task DryRun_Never_Deletes_Even_A_Genuinely_Old_Orphan()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "real.bin");
        var stalePath = WriteOrphanObject('e', ageHours: 72);

        var result = await RunAsync(new StorageReconciliationOptions { DryRun = true });

        Assert.True(result.DryRun);
        Assert.Equal(1, result.OrphanPhysicalObjects);
        Assert.Equal(0, result.OrphansDeleted);
        // Dry-run does not sweep at all, so the sweep-only counters stay clean.
        Assert.Equal(0, result.RecentPhysicalObjectsSkipped);
        Assert.Equal(0, result.OrphansOwnedAtRevalidation);
        Assert.True(File.Exists(stalePath));
    }

    [Fact]
    public async Task Sweep_Counters_Leak_No_Storage_Key_Or_Path()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(client, new byte[64], "a.bin");
        WriteOrphanObject('e', ageHours: 72);
        WriteOrphanObject('f', ageHours: 0);

        string? line = null;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<StorageReconciliationService>();
            await svc.RunAsync(
                new StorageReconciliationOptions { DryRun = false, DeleteOrphans = true },
                l => line = l);
        }

        Assert.NotNull(line);
        Assert.DoesNotContain("objects/", line!, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('e', 64), line!, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('f', 64), line!, StringComparison.Ordinal);
        Assert.DoesNotContain(_factory.StorageRoot, line!, StringComparison.Ordinal);
    }
}
