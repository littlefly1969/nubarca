using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Folders;

// End-to-end HTTP tests for POST /api/folders and POST /api/folders/{id}/folders.
public sealed class FolderCreateEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FolderCreateEndpointTests()
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

    [Fact]
    public async Task Post_Root_Folder_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/folders", new { name = "Photos" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_Root_Folder_Returns_201_With_FolderSummary_And_Location()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/folders", new { name = "Photos" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<FolderSummary>();
        Assert.NotNull(summary);
        Assert.NotEqual(Guid.Empty, summary!.Id);
        Assert.Equal("Photos", summary.Name);
        Assert.NotEqual(default, summary.CreatedAt);

        Assert.Equal($"/api/folders/{summary.Id}/children", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Post_Child_Folder_Returns_201_With_FolderSummary_And_Location()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var parent = await SeedFolderAsAsync(owner, null, "Photos");

        var response = await client.PostAsJsonAsync(
            $"/api/folders/{parent.Id}/folders",
            new { name = "2026" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<FolderSummary>();
        Assert.NotNull(summary);
        Assert.Equal("2026", summary!.Name);
        Assert.Equal($"/api/folders/{summary.Id}/children", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Post_Root_Folder_With_Duplicate_Name_Returns_409()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedFolderAsAsync(owner, null, "Photos");

        var response = await client.PostAsJsonAsync("/api/folders", new { name = "Photos" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Post_Root_Folder_With_Invalid_Name_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/folders", new { name = "a/b" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_Root_Folder_With_Missing_Name_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/folders", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_Child_Folder_Under_Missing_Parent_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/folders/{Guid.NewGuid()}/folders",
            new { name = "Child" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_Child_Folder_Under_Foreign_Parent_Returns_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFolder = await SeedFolderAsAsync(alice, null, "AlicePhotos");

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var response = await bobClient.PostAsJsonAsync(
            $"/api/folders/{aliceFolder.Id}/folders",
            new { name = "Stolen" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_Folder_Response_Does_Not_Leak_Storage_Internals()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/folders", new { name = "Photos" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var headers = string.Join("\n",
            response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));

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

        foreach (var needle in forbidden)
        {
            Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
        }
    }
}
