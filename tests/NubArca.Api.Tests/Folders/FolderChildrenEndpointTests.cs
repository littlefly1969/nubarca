using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Folders;

// End-to-end HTTP tests for GET /api/folders/children and
// GET /api/folders/{id:guid}/children.
public sealed class FolderChildrenEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FolderChildrenEndpointTests()
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

    private async Task<FileItem> SeedFileAsAsync(Guid ownerId, Guid? parentId, string name, string mime, byte[] payload)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, parentId, name, mime, new MemoryStream(payload));
    }

    private async Task SoftDeleteFolderAsync(Guid folderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tracked = await db.Folders.FirstAsync(f => f.Id == folderId);
        tracked.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Get_Root_Children_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync("/api/folders/children");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_Specific_Folder_Children_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/folders/{Guid.NewGuid()}/children");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_Root_Children_Returns_Folders_And_Files_For_Owner()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedFolderAsAsync(owner, null, "Photos");
        await SeedFolderAsAsync(owner, null, "Docs");
        await SeedFileAsAsync(owner, null, "readme.txt", "text/plain", Encoding.UTF8.GetBytes("hi"));

        var response = await client.GetAsync("/api/folders/children");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<FolderChildrenResponse>();

        Assert.NotNull(body);
        Assert.Null(body!.FolderId);
        Assert.Equal(new[] { "Docs", "Photos" }, body.Folders.Select(f => f.Name).ToArray());
        Assert.Single(body.Files);
        Assert.Equal("readme.txt", body.Files[0].Name);
        Assert.Equal("text/plain", body.Files[0].MimeType);
        Assert.Equal(2, body.Files[0].SizeBytes);
    }

    [Fact]
    public async Task Get_Specific_Folder_Children_Returns_Its_Folders_And_Files()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var parent = await SeedFolderAsAsync(owner, null, "Photos");
        await SeedFolderAsAsync(owner, parent.Id, "2026");
        await SeedFileAsAsync(owner, parent.Id, "snap.jpg", "image/jpeg", Encoding.UTF8.GetBytes("img"));
        await SeedFolderAsAsync(owner, null, "Outside"); // root, not under Photos

        var response = await client.GetAsync($"/api/folders/{parent.Id}/children");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<FolderChildrenResponse>();

        Assert.NotNull(body);
        Assert.Equal(parent.Id, body!.FolderId);
        Assert.Equal(new[] { "2026" }, body.Folders.Select(f => f.Name).ToArray());
        Assert.Equal(new[] { "snap.jpg" }, body.Files.Select(f => f.Name).ToArray());
    }

    [Fact]
    public async Task Get_Specific_Folder_Children_For_Foreign_Parent_Returns_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFolder = await SeedFolderAsAsync(alice, null, "AlicePhotos");

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var response = await bobClient.GetAsync($"/api/folders/{aliceFolder.Id}/children");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Specific_Folder_Children_For_Soft_Deleted_Parent_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "Trash");
        await SoftDeleteFolderAsync(folder.Id);

        var response = await client.GetAsync($"/api/folders/{folder.Id}/children");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Specific_Folder_Children_For_Missing_Parent_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/folders/{Guid.NewGuid()}/children");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Response_Does_Not_Leak_Storage_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "Photos");
        await SeedFolderAsAsync(owner, folder.Id, "2026");
        await SeedFileAsAsync(owner, folder.Id, "snap.jpg", "image/jpeg",
            Encoding.UTF8.GetBytes("payload-for-no-leak-check"));

        var rootResponse = await client.GetAsync("/api/folders/children");
        var folderResponse = await client.GetAsync($"/api/folders/{folder.Id}/children");

        Assert.Equal(HttpStatusCode.OK, rootResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, folderResponse.StatusCode);

        var forbidden = new[]
        {
            "BlobObjectId", "blobObjectId", "blob_object_id",
            "StorageKey", "storageKey", "storage_key",
            "OwnerUserId", "ownerUserId", "owner_user_id",
            "ParentFolderId", "parentFolderId", "parent_folder_id",
            "DeletedAt", "deletedAt", "deleted_at",
            "UpdatedAt", "updatedAt", "updated_at",
            "PasswordHash", "passwordHash", "password_hash",
            "objects/",
        };

        foreach (var response in new[] { rootResponse, folderResponse })
        {
            var body = await response.Content.ReadAsStringAsync();
            foreach (var needle in forbidden)
            {
                Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
            }

            var headers = string.Join("\n",
                response.Headers.Concat(response.Content.Headers)
                    .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));
            foreach (var needle in forbidden)
            {
                Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
            }
        }
    }
}
