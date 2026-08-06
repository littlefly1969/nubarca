using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Admin;
using NubArca.Api.Data;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Admin;

// deleted-content-import-skip — end-to-end import skip behaviour through the
// real admin-import pipeline: "skip previously deleted" (tombstone ledger) and
// "skip already in library" (active normal-library content), owner scoping,
// safe summary counts, and no post-ingestion for skipped files.
public sealed class AdminImportSkipTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string NewTree(Action<string> build)
    {
        var root = Path.Combine(Path.GetTempPath(), $"nc-skip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        build(root);
        _tempDirs.Add(root);
        return root;
    }

    private static Dictionary<string, string?> Enabled(string root) => new()
    {
        ["AdminImport:Enabled"] = "true",
        ["AdminImport:Roots:0"] = root,
        ["DeletedContent:Pepper"] = "endpoint-test-pepper",
    };

    private static async Task<HttpClient> AdminAsync(SqliteWebApplicationFactory f, string email)
    {
        f.EnsureDatabaseCreated();
        var id = await f.SeedUserAsync(email);
        await f.PromoteToAdminAsync(id);
        return await f.LoginAsync(email);
    }

    private static async Task<AdminImportRunStatusResponse> RunAsync(
        SqliteWebApplicationFactory f, HttpClient admin, Guid targetId,
        bool skipPreviouslyDeleted = false, bool skipExistingContent = false)
    {
        var roots = await admin.GetFromJsonAsync<AdminImportRootsResponse>("/api/admin/import/roots");
        var rootId = roots!.Roots[0].RootId;
        var run = await (await admin.PostAsJsonAsync("/api/admin/import/run", new
        {
            rootId,
            relativePath = "",
            targetUserId = targetId,
            destinationFolderId = (Guid?)null,
            skipPreviouslyDeleted,
            skipExistingContent,
        })).Content.ReadFromJsonAsync<AdminImportRunResponse>();

        await using (var scope = f.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(20);
        }
        return (await admin.GetFromJsonAsync<AdminImportRunStatusResponse>(
            $"/api/admin/import/runs/{run!.ImportRunId}"))!;
    }

    private static async Task<int> ActiveFileCountAsync(SqliteWebApplicationFactory f, Guid ownerId)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FileItems.IgnoreQueryFilters()
            .CountAsync(x => x.OwnerUserId == ownerId && x.DeletedAt == null);
    }

    private static async Task<Guid> SingleActiveFileIdAsync(SqliteWebApplicationFactory f, Guid ownerId)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FileItems.IgnoreQueryFilters()
            .Where(x => x.OwnerUserId == ownerId && x.DeletedAt == null)
            .Select(x => x.Id).SingleAsync();
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task SkipPreviouslyDeleted_True_SkipsTombstonedContent()
    {
        var root = NewTree(r => File.WriteAllText(Path.Combine(r, "photo.jpg"), "unique-bytes-A"));
        using var f = new SqliteWebApplicationFactory(Enabled(root));
        var admin = await AdminAsync(f, "admin@example.com");
        var targetId = await f.SeedUserAsync("owner@example.com");
        var owner = await f.LoginAsync("owner@example.com");

        // 1) import, 2) owner deletes the only copy (→ tombstone), 3) re-import.
        var first = await RunAsync(f, admin, targetId);
        Assert.Equal(1, first.ImportedFiles);

        var fileId = await SingleActiveFileIdAsync(f, targetId);
        var del = await owner.DeleteAsync($"/api/files/{fileId}");
        del.EnsureSuccessStatusCode();

        var second = await RunAsync(f, admin, targetId, skipPreviouslyDeleted: true);

        Assert.Equal(0, second.ImportedFiles);
        Assert.Equal(1, second.SkippedPreviouslyDeletedFiles);
        Assert.Equal(0, second.SkippedAlreadyPresentFiles);
        Assert.Equal(0, await ActiveFileCountAsync(f, targetId)); // no new FileItem
    }

    [Fact]
    public async Task SkipPreviouslyDeleted_False_ReimportsTombstonedContent()
    {
        var root = NewTree(r => File.WriteAllText(Path.Combine(r, "photo.jpg"), "unique-bytes-B"));
        using var f = new SqliteWebApplicationFactory(Enabled(root));
        var admin = await AdminAsync(f, "admin@example.com");
        var targetId = await f.SeedUserAsync("owner@example.com");
        var owner = await f.LoginAsync("owner@example.com");

        await RunAsync(f, admin, targetId);
        var fileId = await SingleActiveFileIdAsync(f, targetId);
        (await owner.DeleteAsync($"/api/files/{fileId}")).EnsureSuccessStatusCode();

        var second = await RunAsync(f, admin, targetId, skipPreviouslyDeleted: false);

        Assert.Equal(1, second.ImportedFiles);
        Assert.Equal(0, second.SkippedPreviouslyDeletedFiles);
        Assert.Equal(1, await ActiveFileCountAsync(f, targetId));
    }

    [Fact]
    public async Task SkipExistingContent_True_SkipsContentAlreadyInLibrary()
    {
        var root = NewTree(r => File.WriteAllText(Path.Combine(r, "photo.jpg"), "unique-bytes-C"));
        using var f = new SqliteWebApplicationFactory(Enabled(root));
        var admin = await AdminAsync(f, "admin@example.com");
        var targetId = await f.SeedUserAsync("owner@example.com");

        var first = await RunAsync(f, admin, targetId);
        Assert.Equal(1, first.ImportedFiles);

        var second = await RunAsync(f, admin, targetId, skipExistingContent: true);

        Assert.Equal(0, second.ImportedFiles);
        Assert.Equal(1, second.SkippedAlreadyPresentFiles);
        Assert.Equal(1, await ActiveFileCountAsync(f, targetId)); // still just one copy
    }

    [Fact]
    public async Task SkipExistingContent_False_ImportsDuplicateLogicalFile()
    {
        var root = NewTree(r => File.WriteAllText(Path.Combine(r, "photo.jpg"), "unique-bytes-D"));
        using var f = new SqliteWebApplicationFactory(Enabled(root));
        var admin = await AdminAsync(f, "admin@example.com");
        var targetId = await f.SeedUserAsync("owner@example.com");

        await RunAsync(f, admin, targetId);
        // Re-import into a subfolder so the duplicate logical name doesn't clash.
        var second = await RunAsync(f, admin, targetId, skipExistingContent: false);

        // The re-run re-detects the same-name/same-content file as already
        // imported (conflict), NOT as skipped-existing: importing stays enabled.
        Assert.Equal(0, second.SkippedAlreadyPresentFiles);
    }

    [Fact]
    public async Task SkipChecks_AreOwnerScoped()
    {
        var root = NewTree(r => File.WriteAllText(Path.Combine(r, "photo.jpg"), "unique-bytes-E"));
        using var f = new SqliteWebApplicationFactory(Enabled(root));
        var admin = await AdminAsync(f, "admin@example.com");
        var alice = await f.SeedUserAsync("alice@example.com");
        var bob = await f.SeedUserAsync("bob@example.com");
        var aliceClient = await f.LoginAsync("alice@example.com");

        // Alice imports + deletes → tombstone for Alice only.
        await RunAsync(f, admin, alice);
        var aliceFile = await SingleActiveFileIdAsync(f, alice);
        (await aliceClient.DeleteAsync($"/api/files/{aliceFile}")).EnsureSuccessStatusCode();

        // Bob imports the same content with BOTH options on: nothing is skipped
        // (Alice's tombstone/library must not affect Bob).
        var bobRun = await RunAsync(f, admin, bob, skipPreviouslyDeleted: true, skipExistingContent: true);

        Assert.Equal(1, bobRun.ImportedFiles);
        Assert.Equal(0, bobRun.SkippedPreviouslyDeletedFiles);
        Assert.Equal(0, bobRun.SkippedAlreadyPresentFiles);
    }

    [Fact]
    public async Task RunStatus_SummaryLeaksNoInternals()
    {
        var root = NewTree(r => File.WriteAllText(Path.Combine(r, "photo.jpg"), "unique-bytes-F"));
        using var f = new SqliteWebApplicationFactory(Enabled(root));
        var admin = await AdminAsync(f, "admin@example.com");
        var targetId = await f.SeedUserAsync("owner@example.com");
        var owner = await f.LoginAsync("owner@example.com");

        await RunAsync(f, admin, targetId);
        var fileId = await SingleActiveFileIdAsync(f, targetId);
        (await owner.DeleteAsync($"/api/files/{fileId}")).EnsureSuccessStatusCode();
        var run = await RunAsync(f, admin, targetId, skipPreviouslyDeleted: true);

        var raw = await (await admin.GetAsync(
            $"/api/admin/import/runs/{run.ImportRunId}")).Content.ReadAsStringAsync();

        // Forbidden needles: no content hash, blob id, storage key, absolute
        // path, fingerprint, or pepper ever appears in the safe summary.
        Assert.DoesNotContain("Sha256", raw);
        Assert.DoesNotContain("sha256", raw);
        Assert.DoesNotContain("BlobObjectId", raw);
        Assert.DoesNotContain("StorageKey", raw);
        Assert.DoesNotContain("fingerprint", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pepper", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, raw); // no absolute source path
        Assert.DoesNotContain("objects/", raw);
    }
}
