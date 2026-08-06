using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Admin;
using NubArca.Api.Data;
using NubArca.Api.Folders;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Admin;

// Slice 81 — admin server-side import. Covers auth gating, the disabled /
// unconfigured config states, path safety (traversal + symlink + internal
// storage), preview counts, the end-to-end import (structure + ownership +
// dedup + conflicts), safe user/folder data, and a no-leak response scan.
public sealed class AdminImportEndpointTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string NewImportTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nc-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "top.txt"), "top-file");
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "a.txt"), "aaa");
        File.WriteAllBytes(Path.Combine(sub, "b.bin"), new byte[] { 1, 2, 3, 4 });
        var deep = Path.Combine(sub, "deep");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "c.txt"), "cccc");
        _tempDirs.Add(root);
        return root;
    }

    private static Dictionary<string, string?> Enabled(string root) => new()
    {
        ["AdminImport:Enabled"] = "true",
        ["AdminImport:Roots:0"] = root,
    };

    private static async Task<(Guid UserId, HttpClient Client)> AdminAsync(
        SqliteWebApplicationFactory factory, string email)
    {
        factory.EnsureDatabaseCreated();
        var id = await factory.SeedUserAsync(email);
        await factory.PromoteToAdminAsync(id);
        var client = await factory.LoginAsync(email);
        return (id, client);
    }

    private static async Task<string> FirstRootIdAsync(HttpClient client)
    {
        var roots = await client.GetFromJsonAsync<AdminImportRootsResponse>("/api/admin/import/roots");
        Assert.NotNull(roots);
        Assert.True(roots!.Enabled);
        Assert.True(roots.Configured);
        return roots.Roots[0].RootId;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- auth ------------------------------------------------------------

    [Fact]
    public async Task Roots_Unauthenticated_Returns401()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewImportTree()));
        factory.EnsureDatabaseCreated();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/admin/import/roots");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Roots_NonAdmin_Returns403()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewImportTree()));
        factory.EnsureDatabaseCreated();
        var (_, client) = await factory.CreateAuthenticatedClientAsync("plain@example.com");
        var response = await client.GetAsync("/api/admin/import/roots");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- config states ---------------------------------------------------

    [Fact]
    public async Task Disabled_RootsReportsDisabled_And_PreviewConflicts()
    {
        // No AdminImport settings at all → disabled.
        using var factory = new SqliteWebApplicationFactory();
        var (targetId, client) = await AdminAsync(factory, "admin@example.com");

        var roots = await client.GetFromJsonAsync<AdminImportRootsResponse>("/api/admin/import/roots");
        Assert.NotNull(roots);
        Assert.False(roots!.Enabled);
        Assert.False(roots.Configured);

        var preview = await client.PostAsJsonAsync("/api/admin/import/preview", new
        {
            rootId = "deadbeef",
            relativePath = "",
            targetUserId = targetId,
            destinationFolderId = (Guid?)null,
        });
        Assert.Equal(HttpStatusCode.Conflict, preview.StatusCode);
    }

    [Fact]
    public async Task EnabledButNoRoots_ReportsUnconfigured_And_PreviewConflicts()
    {
        using var factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["AdminImport:Enabled"] = "true",
        });
        var (targetId, client) = await AdminAsync(factory, "admin@example.com");

        var roots = await client.GetFromJsonAsync<AdminImportRootsResponse>("/api/admin/import/roots");
        Assert.NotNull(roots);
        Assert.True(roots!.Enabled);
        Assert.False(roots.Configured);

        var preview = await client.PostAsJsonAsync("/api/admin/import/preview", new
        {
            rootId = "deadbeef",
            relativePath = "",
            targetUserId = targetId,
            destinationFolderId = (Guid?)null,
        });
        Assert.Equal(HttpStatusCode.Conflict, preview.StatusCode);
    }

    [Fact]
    public async Task StorageRootAsImportRoot_IsRejected()
    {
        // Point both the blob storage root and the only import root at the same
        // directory — the import root overlaps internal storage and is filtered.
        var shared = Path.Combine(Path.GetTempPath(), $"nc-shared-{Guid.NewGuid():N}");
        Directory.CreateDirectory(shared);
        _tempDirs.Add(shared);
        using var factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["AdminImport:Enabled"] = "true",
            ["AdminImport:Roots:0"] = shared,
            ["Storage:RootPath"] = shared,
        });
        var (_, client) = await AdminAsync(factory, "admin@example.com");

        var roots = await client.GetFromJsonAsync<AdminImportRootsResponse>("/api/admin/import/roots");
        Assert.NotNull(roots);
        Assert.True(roots!.Enabled);
        Assert.False(roots.Configured); // the internal-storage root was filtered out
    }

    // ---- roots / browse --------------------------------------------------

    [Fact]
    public async Task Admin_ListsConfiguredRoot_WithLabel()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");

        var roots = await client.GetFromJsonAsync<AdminImportRootsResponse>("/api/admin/import/roots");
        Assert.NotNull(roots);
        Assert.Single(roots!.Roots);
        Assert.Equal(Path.GetFileName(root), roots.Roots[0].Label);
        Assert.NotEmpty(roots.Roots[0].RootId);
    }

    [Fact]
    public async Task Browse_ListsDirectoriesOnly()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var rootId = await FirstRootIdAsync(client);

        var browse = await client.GetFromJsonAsync<AdminImportBrowseResponse>(
            $"/api/admin/import/browse?rootId={rootId}");
        Assert.NotNull(browse);
        Assert.Contains(browse!.Directories, d => d.Name == "sub");
        // Files are never listed by browse — only directories.
        Assert.DoesNotContain(browse.Directories, d => d.Name == "top.txt");
        var sub = browse.Directories.Single(d => d.Name == "sub");
        Assert.Equal(2, sub.FileCount); // a.txt, b.bin
        Assert.Equal(1, sub.ChildDirectoryCount); // deep
    }

    [Fact]
    public async Task Browse_RejectsTraversal()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var rootId = await FirstRootIdAsync(client);

        var response = await client.GetAsync(
            $"/api/admin/import/browse?rootId={rootId}&relativePath={Uri.EscapeDataString("../..")}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Browse_DoesNotFollowSymlinkEscape()
    {
        var root = NewImportTree();
        var outside = Path.Combine(Path.GetTempPath(), $"nc-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        _tempDirs.Add(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);

        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var rootId = await FirstRootIdAsync(client);

        // The symlinked directory is never listed (not followed).
        var browse = await client.GetFromJsonAsync<AdminImportBrowseResponse>(
            $"/api/admin/import/browse?rootId={rootId}");
        Assert.DoesNotContain(browse!.Directories, d => d.Name == "escape");

        // Trying to enter it directly is rejected.
        var into = await client.GetAsync($"/api/admin/import/browse?rootId={rootId}&relativePath=escape");
        Assert.Equal(HttpStatusCode.BadRequest, into.StatusCode);
    }

    // ---- preview ---------------------------------------------------------

    [Fact]
    public async Task Preview_CountsNestedFilesAndDirectories()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (targetId, client) = await AdminAsync(factory, "admin@example.com");
        var rootId = await FirstRootIdAsync(client);

        var response = await client.PostAsJsonAsync("/api/admin/import/preview", new
        {
            rootId,
            relativePath = "",
            targetUserId = targetId,
            destinationFolderId = (Guid?)null,
        });
        response.EnsureSuccessStatusCode();
        var preview = await response.Content.ReadFromJsonAsync<AdminImportPreviewResponse>();
        Assert.NotNull(preview);
        Assert.Equal(4, preview!.TotalFiles);
        Assert.Equal(2, preview.TotalDirectories);
        Assert.True(preview.TotalBytes > 0);
    }

    // ---- end-to-end run --------------------------------------------------

    [Fact]
    public async Task Run_ImportsPreservingStructure_OwnedByTargetUser_AndConflictsOnRerun()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, adminClient) = await AdminAsync(factory, "admin@example.com");

        // A distinct target user owns the imported files.
        var targetId = await factory.SeedUserAsync("target@example.com");
        var rootId = await FirstRootIdAsync(adminClient);

        var runResponse = await adminClient.PostAsJsonAsync("/api/admin/import/run", new
        {
            rootId,
            relativePath = "",
            targetUserId = targetId,
            destinationFolderId = (Guid?)null,
        });
        runResponse.EnsureSuccessStatusCode();
        var run = await runResponse.Content.ReadFromJsonAsync<AdminImportRunResponse>();
        Assert.NotNull(run);
        Assert.Equal("queued", run!.Status);

        // Drive the job (worker is off in tests).
        await ProcessJobsAsync(factory);

        var status = await adminClient.GetFromJsonAsync<AdminImportRunStatusResponse>(
            $"/api/admin/import/runs/{run.ImportRunId}");
        Assert.NotNull(status);
        Assert.Equal("succeeded", status!.Status);
        Assert.Equal(4, status.ImportedFiles);
        Assert.Equal(0, status.FailedFiles);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // All 4 imported files belong to the TARGET user, none to the admin.
            var owned = await db.FileItems.CountAsync(f => f.OwnerUserId == targetId && f.DeletedAt == null);
            Assert.Equal(4, owned);
            // Structure preserved as logical folders for the target user.
            var folders = await db.Folders
                .Where(f => f.OwnerUserId == targetId && f.DeletedAt == null)
                .Select(f => f.Name)
                .ToListAsync();
            Assert.Contains("sub", folders);
            Assert.Contains("deep", folders);
        }

        // Re-running the SAME import must not corrupt data: every file now
        // collides on its logical name → counted as conflicts, no new files.
        var rerun = await adminClient.PostAsJsonAsync("/api/admin/import/run", new
        {
            rootId,
            relativePath = "",
            targetUserId = targetId,
            destinationFolderId = (Guid?)null,
        });
        rerun.EnsureSuccessStatusCode();
        var rerun2 = await rerun.Content.ReadFromJsonAsync<AdminImportRunResponse>();
        await ProcessJobsAsync(factory);
        var rerunStatus = await adminClient.GetFromJsonAsync<AdminImportRunStatusResponse>(
            $"/api/admin/import/runs/{rerun2!.ImportRunId}");
        Assert.Equal(4, rerunStatus!.ConflictFiles);
        Assert.Equal(0, rerunStatus.ImportedFiles);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owned = await db.FileItems.CountAsync(f => f.OwnerUserId == targetId && f.DeletedAt == null);
            Assert.Equal(4, owned); // unchanged — no duplicates created
        }
    }

    [Fact]
    public async Task Run_DoesNotImportSymlinkedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nc-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        File.WriteAllText(Path.Combine(root, "real.txt"), "real");
        var outsideFile = Path.Combine(Path.GetTempPath(), $"nc-outsidefile-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outsideFile, "secret");
        _tempDirs.Add(outsideFile);
        File.CreateSymbolicLink(Path.Combine(root, "link.txt"), outsideFile);

        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, adminClient) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var rootId = await FirstRootIdAsync(adminClient);

        var run = await (await adminClient.PostAsJsonAsync("/api/admin/import/run", new
        {
            rootId,
            relativePath = "",
            targetUserId = targetId,
            destinationFolderId = (Guid?)null,
        })).Content.ReadFromJsonAsync<AdminImportRunResponse>();
        await ProcessJobsAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var names = await db.FileItems
            .Where(f => f.OwnerUserId == targetId && f.DeletedAt == null)
            .Select(f => f.Name)
            .ToListAsync();
        Assert.Contains("real.txt", names);
        Assert.DoesNotContain("link.txt", names); // symlink not followed
    }

    // ---- users / destination folders ------------------------------------

    [Fact]
    public async Task Users_ReturnsSafeFieldsOnly()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        await factory.SeedUserAsync("target@example.com");

        var response = await client.GetAsync("/api/admin/import/users");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("target@example.com", body);
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PasswordHash", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DestinationFolders_AreScopedToTargetUser()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var otherId = await factory.SeedUserAsync("other@example.com");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
            await folders.CreateAsync(targetId, null, "TargetFolder");
            await folders.CreateAsync(otherId, null, "OtherFolder");
        }

        var response = await client.GetFromJsonAsync<AdminImportFoldersResponse>(
            $"/api/admin/import/destination-folders?userId={targetId}");
        Assert.NotNull(response);
        Assert.Contains(response!.Folders, f => f.Name == "TargetFolder");
        Assert.DoesNotContain(response.Folders, f => f.Name == "OtherFolder");
    }

    // ---- no-leak ---------------------------------------------------------

    [Fact]
    public async Task Responses_DoNotLeakInternalsOrAbsolutePaths()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var rootId = await FirstRootIdAsync(client);

        var bodies = new List<string>
        {
            await (await client.GetAsync("/api/admin/import/roots")).Content.ReadAsStringAsync(),
            await (await client.GetAsync($"/api/admin/import/browse?rootId={rootId}")).Content.ReadAsStringAsync(),
            await (await client.PostAsJsonAsync("/api/admin/import/preview", new
            {
                rootId, relativePath = "", targetUserId = targetId, destinationFolderId = (Guid?)null,
            })).Content.ReadAsStringAsync(),
        };

        foreach (var body in bodies)
        {
            foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
            {
                Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
            }
            // The absolute server path of the configured root must never appear.
            Assert.DoesNotContain(root, body, StringComparison.Ordinal);
        }
    }

    private static async Task ProcessJobsAsync(SqliteWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
        await processor.ProcessAvailableAsync(10);
    }
}
