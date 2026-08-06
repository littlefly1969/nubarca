using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Folders;

public sealed class FolderMutationEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FolderMutationEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Folder> SeedFolderAsAsync(Guid ownerId, Guid? parentId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
        return await folders.CreateAsync(ownerId, parentId, name);
    }

    private async Task<FileItem> SeedFileAsAsync(Guid ownerId, Guid? parentId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, parentId, name, "text/plain", new MemoryStream("x"u8.ToArray()));
    }

    private async Task<List<AuditLog>> ReadAuditAsync(string action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.AsNoTracking().Where(a => a.Action == action).ToListAsync();
    }

    [Fact]
    public async Task Folder_Mutations_Without_Auth_Return_401()
    {
        var anonymous = _factory.CreateClient();
        var fakeId = Guid.NewGuid();

        var rename = await anonymous.PatchAsJsonAsync($"/api/folders/{fakeId}/rename", new { name = "x" });
        var move = await anonymous.PatchAsJsonAsync($"/api/folders/{fakeId}/move", new { parentFolderId = (Guid?)null });
        var delete = await anonymous.DeleteAsync($"/api/folders/{fakeId}");

        Assert.Equal(HttpStatusCode.Unauthorized, rename.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, move.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
    }

    [Fact]
    public async Task Rename_Owned_Folder_Returns_200_With_FolderSummary_And_Writes_Audit()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "Old");

        var response = await client.PatchAsJsonAsync(
            $"/api/folders/{folder.Id}/rename", new { name = "New" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<FolderSummary>();
        Assert.NotNull(summary);
        Assert.Equal("New", summary!.Name);

        var audit = await ReadAuditAsync(AuditActions.FolderRename);
        Assert.Single(audit);
        Assert.Equal(folder.Id, audit[0].EntityId);
    }

    [Fact]
    public async Task Rename_To_Existing_Sibling_Returns_409()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var a = await SeedFolderAsAsync(owner, null, "A");
        await SeedFolderAsAsync(owner, null, "B");

        var response = await client.PatchAsJsonAsync(
            $"/api/folders/{a.Id}/rename", new { name = "B" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Move_Owned_Folder_Returns_200_And_Writes_Audit()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var parent = await SeedFolderAsAsync(owner, null, "Parent");
        var child = await SeedFolderAsAsync(owner, null, "Child");

        var response = await client.PatchAsJsonAsync(
            $"/api/folders/{child.Id}/move", new { parentFolderId = parent.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var audit = await ReadAuditAsync(AuditActions.FolderMove);
        Assert.Single(audit);
    }

    [Fact]
    public async Task Move_Into_Self_Or_Descendant_Returns_400()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var top = await SeedFolderAsAsync(owner, null, "Top");
        var mid = await SeedFolderAsAsync(owner, top.Id, "Mid");
        var leaf = await SeedFolderAsAsync(owner, mid.Id, "Leaf");

        var intoSelf = await client.PatchAsJsonAsync(
            $"/api/folders/{top.Id}/move", new { parentFolderId = top.Id });
        var intoChild = await client.PatchAsJsonAsync(
            $"/api/folders/{top.Id}/move", new { parentFolderId = mid.Id });
        var intoDescendant = await client.PatchAsJsonAsync(
            $"/api/folders/{top.Id}/move", new { parentFolderId = leaf.Id });

        Assert.Equal(HttpStatusCode.BadRequest, intoSelf.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, intoChild.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, intoDescendant.StatusCode);
    }

    [Fact]
    public async Task Delete_Empty_Folder_Returns_204_And_Writes_Audit()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "Bye");

        var response = await client.DeleteAsync($"/api/folders/{folder.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var audit = await ReadAuditAsync(AuditActions.FolderDelete);
        Assert.Single(audit);
        Assert.Equal(folder.Id, audit[0].EntityId);
    }

    [Fact]
    public async Task Delete_Folder_With_Child_Folder_Returns_409()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var parent = await SeedFolderAsAsync(owner, null, "Parent");
        await SeedFolderAsAsync(owner, parent.Id, "Child");

        var response = await client.DeleteAsync($"/api/folders/{parent.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Folder_With_Child_File_Returns_409()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var parent = await SeedFolderAsAsync(owner, null, "Parent");
        await SeedFileAsAsync(owner, parent.Id, "inside.txt");

        var response = await client.DeleteAsync($"/api/folders/{parent.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Foreign_Folder_Mutations_Return_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFolder = await SeedFolderAsAsync(alice, null, "AlicePhotos");
        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var rename = await bobClient.PatchAsJsonAsync(
            $"/api/folders/{aliceFolder.Id}/rename", new { name = "Stolen" });
        var move = await bobClient.PatchAsJsonAsync(
            $"/api/folders/{aliceFolder.Id}/move", new { parentFolderId = (Guid?)null });
        var delete = await bobClient.DeleteAsync($"/api/folders/{aliceFolder.Id}");

        Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, move.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task Folder_Mutation_Responses_Do_Not_Leak_Storage_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var parent = await SeedFolderAsAsync(owner, null, "Parent");
        var child = await SeedFolderAsAsync(owner, null, "Child");

        var rename = await client.PatchAsJsonAsync(
            $"/api/folders/{child.Id}/rename", new { name = "Renamed" });
        var move = await client.PatchAsJsonAsync(
            $"/api/folders/{child.Id}/move", new { parentFolderId = parent.Id });

        var forbidden = new[]
        {
            "BlobObjectId", "blobObjectId", "blob_object_id",
            "StorageKey", "storageKey", "storage_key",
            "OwnerUserId", "ownerUserId", "owner_user_id",
            "DeletedAt", "deletedAt", "deleted_at",
            "PasswordHash", "passwordHash", "password_hash",
            "objects/",
        };

        foreach (var response in new[] { rename, move })
        {
            var body = await response.Content.ReadAsStringAsync();
            foreach (var needle in forbidden)
            {
                Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
            }
        }
    }
}
