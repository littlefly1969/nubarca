using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Files;

// End-to-end HTTP tests for GET /api/trash and GET /api/trash/folders/{id}/children.
public sealed class TrashEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public TrashEndpointTests()
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

    private async Task<FileItem> SeedFileAsAsync(Guid ownerId, Guid? parentId, string name, string mime = "text/plain", string content = "x")
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, parentId, name, mime,
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));
    }

    private async Task SoftDeleteFolderAsync(Guid ownerId, Guid folderId)
    {
        using var scope = _factory.Services.CreateScope();
        var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
        await folders.SoftDeleteAsync(ownerId, folderId);
    }

    private async Task SoftDeleteFileAsync(Guid ownerId, Guid fileId)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        await files.SoftDeleteAsync(ownerId, fileId);
    }

    private async Task BackdateFileDeletedAtAsync(Guid id, int minutesAgo)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.FileItems.FirstAsync(f => f.Id == id);
        row.DeletedAt = DateTime.UtcNow.AddMinutes(-minutesAgo);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Trash_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync("/api/trash");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Trash_Folder_Children_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/trash/folders/{Guid.NewGuid()}/children");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Trash_Returns_Empty_For_User_With_No_Deletions()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/trash");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TrashResponse>();
        Assert.NotNull(body);
        Assert.Empty(body!.Folders);
        Assert.Empty(body.Files);
    }

    [Fact]
    public async Task Trash_Includes_SoftDeleted_File()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "doc.txt");
        await SoftDeleteFileAsync(owner, file.Id);

        var response = await client.GetAsync("/api/trash");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TrashResponse>();
        Assert.NotNull(body);
        var entry = Assert.Single(body!.Files);
        Assert.Equal(file.Id, entry.Id);
        Assert.Equal("doc.txt", entry.Name);
        Assert.Equal("text/plain", entry.MimeType);
        Assert.NotEqual(default, entry.DeletedAt);
    }

    [Fact]
    public async Task Trash_Includes_SoftDeleted_Folder()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "OldFolder");
        await SoftDeleteFolderAsync(owner, folder.Id);

        var response = await client.GetAsync("/api/trash");
        var body = await response.Content.ReadFromJsonAsync<TrashResponse>();
        Assert.NotNull(body);

        var entry = Assert.Single(body!.Folders);
        Assert.Equal(folder.Id, entry.Id);
        Assert.Equal("OldFolder", entry.Name);
        Assert.NotEqual(default, entry.DeletedAt);
    }

    [Fact]
    public async Task Trash_Excludes_Active_File_And_Folder()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedFileAsAsync(owner, null, "keep.txt");
        await SeedFolderAsAsync(owner, null, "KeepFolder");

        var response = await client.GetAsync("/api/trash");
        var body = await response.Content.ReadFromJsonAsync<TrashResponse>();
        Assert.NotNull(body);
        Assert.Empty(body!.Files);
        Assert.Empty(body.Folders);
    }

    [Fact]
    public async Task Trash_Excludes_Foreign_Soft_Deleted_File()
    {
        var (alice, _) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceFile = await SeedFileAsAsync(alice, null, "alice.txt");
        await SoftDeleteFileAsync(alice, aliceFile.Id);

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var response = await bobClient.GetAsync("/api/trash");
        var body = await response.Content.ReadFromJsonAsync<TrashResponse>();
        Assert.NotNull(body);
        Assert.Empty(body!.Files);
        Assert.Empty(body.Folders);
    }

    [Fact]
    public async Task Trash_Excludes_HardPurged_File()
    {
        // FileItemSweeper hard-deletes the row. After that, /api/trash must
        // not list it (the row no longer exists).
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "gone.txt");
        await SoftDeleteFileAsync(owner, file.Id);
        await BackdateFileDeletedAtAsync(file.Id, minutesAgo: 9999);

        var sweeper = new FileItemSweeper(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new FileItemSweeperOptions
            {
                Enabled = true,
                IntervalMinutes = 5,
                GraceMinutes = 30,
            }),
            TimeProvider.System,
            NullLogger<FileItemSweeper>.Instance);
        Assert.Equal(1, await sweeper.RunOnceAsync(default));

        var response = await client.GetAsync("/api/trash");
        var body = await response.Content.ReadFromJsonAsync<TrashResponse>();
        Assert.NotNull(body);
        Assert.DoesNotContain(body!.Files, f => f.Id == file.Id);
    }

    [Fact]
    public async Task Trash_Files_Are_Ordered_By_DeletedAt_Desc_Then_Name()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var older = await SeedFileAsAsync(owner, null, "older.txt");
        var newerA = await SeedFileAsAsync(owner, null, "a-newer.txt");
        var newerB = await SeedFileAsAsync(owner, null, "b-newer.txt");

        await SoftDeleteFileAsync(owner, older.Id);
        await BackdateFileDeletedAtAsync(older.Id, minutesAgo: 60);
        // Two files share (approximately) the same DeletedAt timestamp; force
        // them onto an identical instant so the secondary Name sort decides.
        await SoftDeleteFileAsync(owner, newerB.Id);
        await SoftDeleteFileAsync(owner, newerA.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sameInstant = DateTime.UtcNow;
            await db.FileItems.Where(f => f.Id == newerA.Id || f.Id == newerB.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.DeletedAt, _ => (DateTime?)sameInstant));
        }

        var response = await client.GetAsync("/api/trash");
        var body = await response.Content.ReadFromJsonAsync<TrashResponse>();
        Assert.NotNull(body);
        Assert.Equal(3, body!.Files.Count);
        Assert.Equal("a-newer.txt", body.Files[0].Name);
        Assert.Equal("b-newer.txt", body.Files[1].Name);
        Assert.Equal("older.txt", body.Files[2].Name);
    }

    [Fact]
    public async Task Trash_Includes_ParentFolderId_For_Original_Location_Context()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "Photos");
        var file = await SeedFileAsAsync(owner, folder.Id, "in-folder.txt");

        await SoftDeleteFileAsync(owner, file.Id);

        var response = await client.GetAsync("/api/trash");
        var body = await response.Content.ReadFromJsonAsync<TrashResponse>();
        Assert.NotNull(body);
        var entry = Assert.Single(body!.Files);
        Assert.Equal(folder.Id, entry.ParentFolderId);
    }

    [Fact]
    public async Task Trash_Folder_Children_Lists_Only_Deleted_Children_Of_That_Parent()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var parent = await SeedFolderAsAsync(owner, null, "Parent");
        var insideDeletedFile = await SeedFileAsAsync(owner, parent.Id, "inside.txt");
        var insideDeletedFolder = await SeedFolderAsAsync(owner, parent.Id, "innerFolder");
        var outsideDeletedFile = await SeedFileAsAsync(owner, null, "outside.txt");

        await SoftDeleteFileAsync(owner, insideDeletedFile.Id);
        await SoftDeleteFolderAsync(owner, insideDeletedFolder.Id);
        await SoftDeleteFileAsync(owner, outsideDeletedFile.Id);

        var response = await client.GetAsync($"/api/trash/folders/{parent.Id}/children");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TrashResponse>();
        Assert.NotNull(body);

        Assert.Single(body!.Files);
        Assert.Equal(insideDeletedFile.Id, body.Files[0].Id);
        Assert.Single(body.Folders);
        Assert.Equal(insideDeletedFolder.Id, body.Folders[0].Id);
    }

    [Fact]
    public async Task Trash_Folder_Children_Foreign_Parent_Returns_404()
    {
        var (alice, _) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceFolder = await SeedFolderAsAsync(alice, null, "AliceFolder");

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var response = await bobClient.GetAsync($"/api/trash/folders/{aliceFolder.Id}/children");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Trash_Folder_Children_Missing_Parent_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/trash/folders/{Guid.NewGuid()}/children");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Trash_Folder_Children_Works_For_SoftDeleted_Parent()
    {
        // A user can drill into their own deleted folder to see its deleted
        // descendants.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var parent = await SeedFolderAsAsync(owner, null, "Parent");
        var child = await SeedFileAsAsync(owner, parent.Id, "inside.txt");

        await SoftDeleteFileAsync(owner, child.Id);
        await SoftDeleteFolderAsync(owner, parent.Id);

        var response = await client.GetAsync($"/api/trash/folders/{parent.Id}/children");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TrashResponse>();
        Assert.NotNull(body);
        Assert.Single(body!.Files);
    }

    [Fact]
    public async Task Trash_Response_Has_No_Storage_Internals_Leak()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "Photos");
        var file = await SeedFileAsAsync(owner, folder.Id, "doc.txt");

        await SoftDeleteFileAsync(owner, file.Id);
        await SoftDeleteFolderAsync(owner, folder.Id);

        var response = await client.GetAsync("/api/trash");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        string[] needles =
        {
            "StorageKey", "storageKey", "storage_key",
            "BlobObjectId", "blobObjectId", "blob_object_id",
            "OwnerUserId", "ownerUserId", "owner_user_id",
            "TokenHash", "tokenHash",
            "PasswordHash", "passwordHash",
            "objects/",
        };
        foreach (var needle in needles)
        {
            Assert.DoesNotContain(needle, body);
        }

        // The DTO carries parentFolderId, deletedAt, createdAt, updatedAt by
        // design — assert they ARE present so the client can render location
        // + lifecycle context.
        var json = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("files").ValueKind);
        var firstFile = json.RootElement.GetProperty("files")[0];
        Assert.True(firstFile.TryGetProperty("parentFolderId", out _));
        Assert.True(firstFile.TryGetProperty("deletedAt", out _));
    }

    [Fact]
    public async Task Delete_Then_Trash_Then_Restore_Flow_Round_Trips_Cleanly()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "doc.txt");

        // Step 1: soft-delete via the HTTP DELETE.
        var del = await client.DeleteAsync($"/api/files/{file.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // Step 2: appears in /api/trash; absent from active listing.
        var inTrash = await client.GetFromJsonAsync<TrashResponse>("/api/trash");
        Assert.NotNull(inTrash);
        Assert.Single(inTrash!.Files);
        Assert.Equal(file.Id, inTrash.Files[0].Id);

        var activeBefore = await client.GetFromJsonAsync<FolderChildrenResponse>("/api/folders/children");
        Assert.NotNull(activeBefore);
        Assert.DoesNotContain(activeBefore!.Files, f => f.Id == file.Id);

        // Step 3: restore.
        var restore = await client.PostAsync($"/api/files/{file.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

        // Step 4: gone from trash, back in active listing.
        var afterTrash = await client.GetFromJsonAsync<TrashResponse>("/api/trash");
        Assert.NotNull(afterTrash);
        Assert.Empty(afterTrash!.Files);

        var activeAfter = await client.GetFromJsonAsync<FolderChildrenResponse>("/api/folders/children");
        Assert.NotNull(activeAfter);
        Assert.Contains(activeAfter!.Files, f => f.Id == file.Id);
    }
}
