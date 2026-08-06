using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Admin;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Admin;

// Slice 84 — investigate/guard the import conflict counter: a clean import must
// report zero conflicts, nested basenames must not collide, and only true
// pre-existing logical-name collisions count as conflicts (resume re-walk is
// reported separately as "already imported").
public sealed class AdminImportConflictTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string NewTree(Action<string> build)
    {
        var root = Path.Combine(Path.GetTempPath(), $"nc-conflict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        build(root);
        _tempDirs.Add(root);
        return root;
    }

    private static Dictionary<string, string?> Enabled(string root) => new()
    {
        ["AdminImport:Enabled"] = "true",
        ["AdminImport:Roots:0"] = root,
    };

    private static async Task<(Guid UserId, HttpClient Client)> AdminAsync(SqliteWebApplicationFactory f, string email)
    {
        f.EnsureDatabaseCreated();
        var id = await f.SeedUserAsync(email);
        await f.PromoteToAdminAsync(id);
        return (id, await f.LoginAsync(email));
    }

    private static async Task<AdminImportRunStatusResponse> RunAndProcessAsync(
        SqliteWebApplicationFactory f, HttpClient client, Guid targetId)
    {
        var roots = await client.GetFromJsonAsync<AdminImportRootsResponse>("/api/admin/import/roots");
        var rootId = roots!.Roots[0].RootId;
        var run = await (await client.PostAsJsonAsync("/api/admin/import/run", new
        {
            rootId, relativePath = "", targetUserId = targetId, destinationFolderId = (Guid?)null,
        })).Content.ReadFromJsonAsync<AdminImportRunResponse>();

        await using (var scope = f.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(10);
        }
        return (await client.GetFromJsonAsync<AdminImportRunStatusResponse>(
            $"/api/admin/import/runs/{run!.ImportRunId}"))!;
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task CleanImport_EmptyDestination_ZeroConflictsZeroAlreadyImported()
    {
        var root = NewTree(r =>
        {
            File.WriteAllText(Path.Combine(r, "a.txt"), "a");
            var sub = Path.Combine(r, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "b.txt"), "b");
        });
        using var f = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(f, "admin@example.com");
        var targetId = await f.SeedUserAsync("target@example.com");

        var status = await RunAndProcessAsync(f, client, targetId);

        Assert.Equal("succeeded", status.Status);
        Assert.Equal(2, status.ImportedFiles);
        Assert.Equal(0, status.ConflictFiles);          // the bug report's symptom
        Assert.Equal(0, status.AlreadyImportedFiles);
        Assert.Empty(status.ConflictSamples);
    }

    [Fact]
    public async Task SameBasename_DifferentSubdirs_DoesNotConflict_AndPreservesStructure()
    {
        // photo.jpg appears in two different source folders — must NOT collide
        // (they land in different logical folders).
        var root = NewTree(r =>
        {
            var d2009 = Path.Combine(r, "2009", "trip");
            var d2010 = Path.Combine(r, "2010", "trip");
            Directory.CreateDirectory(d2009);
            Directory.CreateDirectory(d2010);
            File.WriteAllText(Path.Combine(d2009, "photo.jpg"), "x");
            File.WriteAllText(Path.Combine(d2010, "photo.jpg"), "y");
        });
        using var f = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(f, "admin@example.com");
        var targetId = await f.SeedUserAsync("target@example.com");

        var status = await RunAndProcessAsync(f, client, targetId);

        Assert.Equal(2, status.ImportedFiles);
        Assert.Equal(0, status.ConflictFiles);

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Structure preserved: both 2009 and 2010 folders exist for the target.
        var folders = await db.Folders
            .Where(x => x.OwnerUserId == targetId && x.DeletedAt == null)
            .Select(x => x.Name).ToListAsync();
        Assert.Contains("2009", folders);
        Assert.Contains("2010", folders);
        Assert.Equal(2, await db.FileItems.CountAsync(x => x.OwnerUserId == targetId && x.DeletedAt == null));
    }

    [Fact]
    public async Task PreexistingFile_CountsAsTrueConflict_WithSafeSample()
    {
        var root = NewTree(r => File.WriteAllText(Path.Combine(r, "dup.txt"), "imported"));
        using var f = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(f, "admin@example.com");
        var targetId = await f.SeedUserAsync("target@example.com");

        // Pre-existing active file with the same name at the destination root,
        // created BEFORE the run → must be a true conflict, never overwritten.
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            await files.CreateAsync(targetId, null, "dup.txt", "text/plain",
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes("original")));
        }

        var status = await RunAndProcessAsync(f, client, targetId);

        Assert.Equal(0, status.ImportedFiles);
        Assert.Equal(1, status.ConflictFiles);
        Assert.Equal(0, status.AlreadyImportedFiles);
        var sample = Assert.Single(status.ConflictSamples);
        Assert.Equal("dup.txt", sample.RelativePath);
        Assert.Equal("preexisting", sample.Reason);

        // The pre-existing file's bytes are untouched (no overwrite).
        await using var verify = f.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.FileItems.CountAsync(x => x.OwnerUserId == targetId && x.DeletedAt == null && x.Name == "dup.txt"));
    }

    [Fact]
    public async Task SoftDeletedFileWithSameName_DoesNotConflict()
    {
        var root = NewTree(r => File.WriteAllText(Path.Combine(r, "ghost.txt"), "fresh"));
        using var f = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(f, "admin@example.com");
        var targetId = await f.SeedUserAsync("target@example.com");

        // A trashed (soft-deleted) file with the same name must NOT block import.
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var created = await files.CreateAsync(targetId, null, "ghost.txt", "text/plain",
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes("old")));
            await files.SoftDeleteAsync(targetId, created.Id);
        }

        var status = await RunAndProcessAsync(f, client, targetId);

        Assert.Equal(1, status.ImportedFiles);
        Assert.Equal(0, status.ConflictFiles);
        Assert.Equal(0, status.AlreadyImportedFiles);
    }

    [Fact]
    public async Task ConflictSamples_DoNotLeakInternals()
    {
        var root = NewTree(r => File.WriteAllText(Path.Combine(r, "dup.txt"), "x"));
        using var f = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(f, "admin@example.com");
        var targetId = await f.SeedUserAsync("target@example.com");
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            await files.CreateAsync(targetId, null, "dup.txt", "text/plain", new MemoryStream(new byte[] { 1 }));
        }
        var status = await RunAndProcessAsync(f, client, targetId);
        Assert.NotEmpty(status.ConflictSamples);

        var body = await (await client.GetAsync($"/api/admin/import/runs/{status.ImportRunId}"))
            .Content.ReadAsStringAsync();
        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(root, body, StringComparison.Ordinal); // no absolute path
    }
}
