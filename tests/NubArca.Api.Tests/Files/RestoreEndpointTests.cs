using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Files;

// HTTP tests for POST /api/files/{id}/restore and POST /api/folders/{id}/restore.
public sealed class RestoreEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public RestoreEndpointTests()
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

    private async Task SoftDeleteFileAsync(Guid ownerId, Guid fileId)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        await files.SoftDeleteAsync(ownerId, fileId);
    }

    private async Task SoftDeleteFolderAsync(Guid ownerId, Guid folderId)
    {
        using var scope = _factory.Services.CreateScope();
        var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
        await folders.SoftDeleteAsync(ownerId, folderId);
    }

    private async Task<List<AuditLog>> ReadAuditAsync(string action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.AsNoTracking().Where(a => a.Action == action).ToListAsync();
    }

    [Fact]
    public async Task File_Restore_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsync($"/api/files/{Guid.NewGuid()}/restore", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Folder_Restore_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsync($"/api/folders/{Guid.NewGuid()}/restore", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task File_Restore_Owned_SoftDeleted_Returns_200_With_FileSummary_And_Writes_Audit()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "doc.txt");
        await SoftDeleteFileAsync(owner, file.Id);

        var response = await client.PostAsync($"/api/files/{file.Id}/restore", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<FileSummary>();
        Assert.NotNull(summary);
        Assert.Equal(file.Id, summary!.Id);
        Assert.Equal("doc.txt", summary.Name);

        var audit = await ReadAuditAsync(AuditActions.FileRestore);
        Assert.Single(audit);
        Assert.Equal(owner, audit[0].UserId);
        Assert.Equal(file.Id, audit[0].EntityId);
    }

    [Fact]
    public async Task Folder_Restore_Owned_SoftDeleted_Returns_200_With_FolderSummary_And_Writes_Audit()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "Photos");
        await SoftDeleteFolderAsync(owner, folder.Id);

        var response = await client.PostAsync($"/api/folders/{folder.Id}/restore", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<FolderSummary>();
        Assert.NotNull(summary);
        Assert.Equal(folder.Id, summary!.Id);
        Assert.Equal("Photos", summary.Name);

        var audit = await ReadAuditAsync(AuditActions.FolderRestore);
        Assert.Single(audit);
        Assert.Equal(owner, audit[0].UserId);
        Assert.Equal(folder.Id, audit[0].EntityId);
    }

    [Fact]
    public async Task File_Restore_Missing_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsync($"/api/files/{Guid.NewGuid()}/restore", content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task File_Restore_Foreign_Returns_404()
    {
        var (alice, _) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceFile = await SeedFileAsAsync(alice, null, "alice.txt");
        await SoftDeleteFileAsync(alice, aliceFile.Id);

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var response = await bobClient.PostAsync($"/api/files/{aliceFile.Id}/restore", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Folder_Restore_Missing_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsync($"/api/folders/{Guid.NewGuid()}/restore", content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Folder_Restore_Foreign_Returns_404()
    {
        var (alice, _) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceFolder = await SeedFolderAsAsync(alice, null, "AliceFolder");
        await SoftDeleteFolderAsync(alice, aliceFolder.Id);

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var response = await bobClient.PostAsync($"/api/folders/{aliceFolder.Id}/restore", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task File_Restore_With_Sibling_Name_Conflict_Returns_409()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var first = await SeedFileAsAsync(owner, null, "doc.txt");
        await SoftDeleteFileAsync(owner, first.Id);
        // Occupy the name with a new active file.
        await SeedFileAsAsync(owner, null, "doc.txt");

        var response = await client.PostAsync($"/api/files/{first.Id}/restore", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Folder_Restore_With_Sibling_Name_Conflict_Returns_409()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var first = await SeedFolderAsAsync(owner, null, "Photos");
        await SoftDeleteFolderAsync(owner, first.Id);
        await SeedFolderAsAsync(owner, null, "Photos");

        var response = await client.PostAsync($"/api/folders/{first.Id}/restore", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task File_Restore_With_SoftDeleted_Parent_Returns_409()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "Photos");
        var file = await SeedFileAsAsync(owner, folder.Id, "snap.jpg");

        await SoftDeleteFileAsync(owner, file.Id);
        await SoftDeleteFolderAsync(owner, folder.Id);

        var response = await client.PostAsync($"/api/files/{file.Id}/restore", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Folder_Restore_With_SoftDeleted_Parent_Returns_409()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var parent = await SeedFolderAsAsync(owner, null, "Outer");
        var child = await SeedFolderAsAsync(owner, parent.Id, "Inner");

        await SoftDeleteFolderAsync(owner, child.Id);
        await SoftDeleteFolderAsync(owner, parent.Id);

        var response = await client.PostAsync($"/api/folders/{child.Id}/restore", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task File_Restore_Response_Has_No_Storage_Internals_Leak()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "doc.txt");
        await SoftDeleteFileAsync(owner, file.Id);

        var response = await client.PostAsync($"/api/files/{file.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var headers = string.Join('\n',
            response.Headers.Concat(response.Content.Headers)
                .Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));

        string[] needles =
        {
            "StorageKey", "storageKey", "storage_key",
            "BlobObjectId", "blobObjectId", "blob_object_id",
            "OwnerUserId", "ownerUserId", "owner_user_id",
            "ParentFolderId", "parentFolderId",
            "DeletedAt", "deletedAt",
            "TokenHash", "tokenHash",
            "PasswordHash", "passwordHash",
            "objects/",
        };
        foreach (var needle in needles)
        {
            Assert.DoesNotContain(needle, body);
            Assert.DoesNotContain(needle, headers);
        }
    }

    [Fact]
    public async Task Restored_Image_File_Thumbnail_Endpoint_Works_Again()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();

        // Upload a real PNG so the thumbnail row is generated.
        using var img = new Image<Rgba32>(200, 200);
        using var pngBytes = new MemoryStream();
        img.Save(pngBytes, new PngEncoder());

        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(pngBytes.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(part, "file", "pic.png");

        var upload = await client.PostAsync("/api/files", multipart);
        upload.EnsureSuccessStatusCode();
        var summary = await upload.Content.ReadFromJsonAsync<FileSummary>();
        var fileId = summary!.Id;

        // Sanity: thumbnail visible before delete.
        var before = await client.GetAsync($"/api/files/{fileId}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // Soft-delete the file; thumbnail must hide.
        var delete = await client.DeleteAsync($"/api/files/{fileId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var hidden = await client.GetAsync($"/api/files/{fileId}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        // Restore: thumbnail visible again, no duplicate row.
        var restore = await client.PostAsync($"/api/files/{fileId}/restore", content: null);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

        var after = await client.GetAsync($"/api/files/{fileId}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.FileThumbnails.CountAsync(t => t.FileItemId == fileId));
    }

    [Fact]
    public async Task Share_Link_For_Restored_File_Works_Again()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "shared.txt");

        // Create a share link.
        var createShare = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { });
        createShare.EnsureSuccessStatusCode();
        var shareBody = await createShare.Content.ReadFromJsonAsync<ShareProbe>();
        var rawToken = shareBody!.Token;

        // Sanity: public URL works.
        var anonymous = _factory.CreateClient();
        var beforeDel = await anonymous.GetAsync($"/s/{rawToken}");
        Assert.Equal(HttpStatusCode.OK, beforeDel.StatusCode);

        // Soft-delete file; public URL now returns 404 (file invisible).
        await SoftDeleteFileAsync(owner, file.Id);
        var afterDel = await anonymous.GetAsync($"/s/{rawToken}");
        Assert.Equal(HttpStatusCode.NotFound, afterDel.StatusCode);

        // Restore file; share link still exists so public URL works again.
        var restore = await client.PostAsync($"/api/files/{file.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var afterRestore = await anonymous.GetAsync($"/s/{rawToken}");
        Assert.Equal(HttpStatusCode.OK, afterRestore.StatusCode);
    }

    private sealed record ShareProbe(Guid Id, string Token, string Url);
}
