using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
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
}
