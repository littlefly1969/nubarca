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
    private string WriteOrphanObject()
    {
        var sha = new string('a', 64);
        var dir = Path.Combine(_factory.StorageRoot, "objects", sha[..2], sha[2..4]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, sha);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

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
}
