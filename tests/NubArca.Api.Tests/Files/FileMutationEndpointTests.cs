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

namespace NubArca.Api.Tests.Files;

public sealed class FileMutationEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FileMutationEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<FileItem> SeedFileAsAsync(Guid ownerId, Guid? parentId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, parentId, name, "text/plain", new MemoryStream("x"u8.ToArray()));
    }

    private async Task<Folder> SeedFolderAsAsync(Guid ownerId, string name = "Photos")
    {
        using var scope = _factory.Services.CreateScope();
        var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
        return await folders.CreateAsync(ownerId, null, name);
    }

    private async Task<List<AuditLog>> ReadAuditAsync(string action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.AsNoTracking().Where(a => a.Action == action).ToListAsync();
    }

    [Fact]
    public async Task File_Mutations_Without_Auth_Return_401()
    {
        var anonymous = _factory.CreateClient();
        var fakeId = Guid.NewGuid();

        var rename = await anonymous.PatchAsJsonAsync($"/api/files/{fakeId}/rename", new { name = "x.txt" });
        var move = await anonymous.PatchAsJsonAsync($"/api/files/{fakeId}/move", new { parentFolderId = (Guid?)null });
        var delete = await anonymous.DeleteAsync($"/api/files/{fakeId}");

        Assert.Equal(HttpStatusCode.Unauthorized, rename.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, move.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
    }

    [Fact]
    public async Task Rename_Owned_File_Returns_200_With_FileSummary_And_Writes_Audit()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "old.txt");

        var response = await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/rename", new { name = "new.txt" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<FileSummary>();
        Assert.NotNull(summary);
        Assert.Equal("new.txt", summary!.Name);

        var audit = await ReadAuditAsync(AuditActions.FileRename);
        Assert.Single(audit);
        Assert.Equal(owner, audit[0].UserId);
        Assert.Equal(file.Id, audit[0].EntityId);
    }

    [Fact]
    public async Task Rename_To_Existing_Sibling_Returns_409()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var a = await SeedFileAsAsync(owner, null, "a.txt");
        await SeedFileAsAsync(owner, null, "b.txt");

        var response = await client.PatchAsJsonAsync(
            $"/api/files/{a.Id}/rename", new { name = "b.txt" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Rename_Invalid_Name_Returns_400()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "a.txt");

        var response = await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/rename", new { name = "a/b.txt" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rename_Foreign_Or_Missing_File_Returns_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFile = await SeedFileAsAsync(alice, null, "alice.txt");
        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var foreign = await bobClient.PatchAsJsonAsync(
            $"/api/files/{aliceFile.Id}/rename", new { name = "stolen.txt" });
        var missing = await bobClient.PatchAsJsonAsync(
            $"/api/files/{Guid.NewGuid()}/rename", new { name = "ghost.txt" });

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Move_Owned_File_Returns_200_And_Writes_Audit()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner);
        var file = await SeedFileAsAsync(owner, null, "x.txt");

        var response = await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/move", new { parentFolderId = folder.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var audit = await ReadAuditAsync(AuditActions.FileMove);
        Assert.Single(audit);
        Assert.Equal(file.Id, audit[0].EntityId);
    }

    [Fact]
    public async Task Move_To_Missing_Or_Foreign_Or_Deleted_Parent_Returns_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFolder = await SeedFolderAsAsync(alice, "AliceFolder");
        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var bobFile = await SeedFileAsAsync((await ReadOwnerByEmailAsync("bob@example.com"))!.Value, null, "b.txt");

        var missing = await bobClient.PatchAsJsonAsync(
            $"/api/files/{bobFile.Id}/move", new { parentFolderId = Guid.NewGuid() });
        var foreign = await bobClient.PatchAsJsonAsync(
            $"/api/files/{bobFile.Id}/move", new { parentFolderId = aliceFolder.Id });

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Delete_Owned_File_Hides_From_All_Read_Paths_And_Writes_Audit()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "byebye.txt");

        var delete = await client.DeleteAsync($"/api/files/{file.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var get = await client.GetAsync($"/api/files/{file.Id}/content");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        var listResponse = await client.GetAsync("/api/folders/children");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<FolderChildrenResponse>();
        Assert.NotNull(list);
        Assert.DoesNotContain(list!.Files, f => f.Id == file.Id);

        var searchResponse = await client.GetAsync("/api/search?q=byebye");
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        var hits = await searchResponse.Content.ReadFromJsonAsync<List<FileSummary>>();
        Assert.NotNull(hits);
        Assert.Empty(hits!);

        var audit = await ReadAuditAsync(AuditActions.FileDelete);
        Assert.Single(audit);
    }

    [Fact]
    public async Task Delete_Foreign_File_Returns_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFile = await SeedFileAsAsync(alice, null, "a.txt");
        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var response = await bobClient.DeleteAsync($"/api/files/{aliceFile.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Mutation_Responses_Do_Not_Leak_Storage_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner);
        var file = await SeedFileAsAsync(owner, null, "doc.txt");

        var rename = await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/rename", new { name = "renamed.txt" });
        var move = await client.PatchAsJsonAsync(
            $"/api/files/{file.Id}/move", new { parentFolderId = folder.Id });

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

    private async Task<Guid?> ReadOwnerByEmailAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email);
        return user?.Id;
    }
}
