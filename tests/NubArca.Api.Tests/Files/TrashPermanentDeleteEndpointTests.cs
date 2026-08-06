using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.ShareLinks;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

// HTTP tests for DELETE /api/trash/files/{id} and DELETE /api/trash/folders/{id}.
namespace NubArca.Api.Tests.Files;

public sealed class TrashPermanentDeleteEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public TrashPermanentDeleteEndpointTests()
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

    private async Task<FileItem> SeedFileAsAsync(Guid ownerId, Guid? parentId, string name,
        string mime = "text/plain", string content = "x")
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, parentId, name, mime,
            new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }

    private async Task<FileItem> SeedImageFileAsAsync(Guid ownerId, string name)
    {
        using var img = new Image<Rgba32>(200, 200);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return await SeedFileAsAsync(ownerId, null, name, "image/png", Encoding.Latin1.GetString(ms.ToArray()));
    }

    private async Task<Guid> SeedImageFileViaUploadAsync(HttpClient client, string name)
    {
        using var img = new Image<Rgba32>(200, 200);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());

        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(ms.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(part, "file", name);

        var response = await client.PostAsync("/api/files", multipart);
        response.EnsureSuccessStatusCode();
        var summary = await response.Content.ReadFromJsonAsync<FileSummary>();
        return summary!.Id;
    }

    private async Task<Guid> SeedShareLinkAsync(Guid ownerId, Guid fileId)
    {
        using var scope = _factory.Services.CreateScope();
        var shares = scope.ServiceProvider.GetRequiredService<IShareLinkService>();
        var result = await shares.CreateAsync(ownerId, fileId, null, null);
        return result!.Id;
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
    public async Task File_Trash_Delete_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.DeleteAsync($"/api/trash/files/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Folder_Trash_Delete_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.DeleteAsync($"/api/trash/folders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task File_Trash_Delete_SoftDeleted_File_Returns_204_And_Writes_Audit()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "doc.txt");
        await SoftDeleteFileAsync(owner, file.Id);

        var response = await client.DeleteAsync($"/api/trash/files/{file.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.FileItems.AnyAsync(f => f.Id == file.Id));

        var audit = await ReadAuditAsync(AuditActions.FilePermanentDelete);
        var entry = Assert.Single(audit);
        Assert.Equal(owner, entry.UserId);
        Assert.Equal(file.Id, entry.EntityId);
    }

    [Fact]
    public async Task File_Trash_Delete_Active_File_Returns_409()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "doc.txt");

        var response = await client.DeleteAsync($"/api/trash/files/{file.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.FileItems.AnyAsync(f => f.Id == file.Id));
    }

    [Fact]
    public async Task File_Trash_Delete_Missing_File_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.DeleteAsync($"/api/trash/files/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task File_Trash_Delete_Foreign_File_Returns_404()
    {
        var (alice, _) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceFile = await SeedFileAsAsync(alice, null, "alice.txt");
        await SoftDeleteFileAsync(alice, aliceFile.Id);

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var response = await bobClient.DeleteAsync($"/api/trash/files/{aliceFile.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.FileItems.AnyAsync(f => f.Id == aliceFile.Id));
    }

    [Fact]
    public async Task File_Trash_Delete_Removes_Related_ShareLinks_And_Thumbnails()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await SeedImageFileViaUploadAsync(client, "pic.png");
        var shareLinkId = await SeedShareLinkAsync(owner, fileId);

        await SoftDeleteFileAsync(owner, fileId);

        // Pre-state: thumbnail row + share link still present.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.FileThumbnails.CountAsync(t => t.FileItemId == fileId));
            Assert.Equal(1, await db.ShareLinks.CountAsync(s => s.Id == shareLinkId));
        }

        var response = await client.DeleteAsync($"/api/trash/files/{fileId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verifyDb.FileItems.AnyAsync(f => f.Id == fileId));
        Assert.False(await verifyDb.FileThumbnails.AnyAsync(t => t.FileItemId == fileId));
        Assert.False(await verifyDb.ShareLinks.AnyAsync(s => s.Id == shareLinkId));
    }

    [Fact]
    public async Task File_Trash_Delete_Disappears_From_Trash_Listing()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "doc.txt");
        await SoftDeleteFileAsync(owner, file.Id);

        var inTrash = await client.GetFromJsonAsync<TrashResponse>("/api/trash");
        Assert.Single(inTrash!.Files);

        var del = await client.DeleteAsync($"/api/trash/files/{file.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var afterTrash = await client.GetFromJsonAsync<TrashResponse>("/api/trash");
        Assert.Empty(afterTrash!.Files);
    }

    [Fact]
    public async Task File_Trash_Delete_Leaves_BlobObject_Row_With_RefCount_0()
    {
        // BlobJanitor is responsible for physical / row reclamation. Our
        // permanent-delete must leave the BlobObject row in place; soft delete
        // had already decremented its ReferenceCount to 0.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "doc.txt");

        Guid blobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            blobId = (await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == file.Id)).BlobObjectId;
        }

        await SoftDeleteFileAsync(owner, file.Id);

        var del = await client.DeleteAsync($"/api/trash/files/{file.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        using var verify = _factory.Services.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = await db2.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == blobId);
        Assert.Equal(0, blob.ReferenceCount);
    }

    [Fact]
    public async Task File_Trash_Delete_Followed_By_BlobJanitor_Reclaims_Row()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "doc.txt");
        Guid blobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            blobId = (await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == file.Id)).BlobObjectId;
        }

        await SoftDeleteFileAsync(owner, file.Id);

        var del = await client.DeleteAsync($"/api/trash/files/{file.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var janitor = new BlobJanitor(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new BlobJanitorOptions { Enabled = true, IntervalMinutes = 5, GraceMinutes = 30 }),
            TimeProvider.System,
            NullLogger<BlobJanitor>.Instance);
        var purged = await janitor.RunOnceAsync(default);
        Assert.Equal(0, purged);

        // Manual permanent delete removes the logical row immediately but
        // starts a fresh physical-blob grace window. Advance only that clock.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BlobObjects
                .Where(b => b.Id == blobId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(
                        b => b.PurgeEligibleAt,
                        _ => (DateTime?)DateTime.UtcNow.AddMinutes(-31)));
        }

        purged = await janitor.RunOnceAsync(default);

        Assert.Equal(1, purged);
        using var verify = _factory.Services.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db2.BlobObjects.AnyAsync(b => b.Id == blobId));
    }

    [Fact]
    public async Task Folder_Trash_Delete_Empty_SoftDeleted_Folder_Returns_204_And_Writes_Audit()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "Photos");
        await SoftDeleteFolderAsync(owner, folder.Id);

        var response = await client.DeleteAsync($"/api/trash/folders/{folder.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Folders.AnyAsync(f => f.Id == folder.Id));

        var audit = await ReadAuditAsync(AuditActions.FolderPermanentDelete);
        var entry = Assert.Single(audit);
        Assert.Equal(owner, entry.UserId);
        Assert.Equal(folder.Id, entry.EntityId);
    }

    [Fact]
    public async Task Folder_Trash_Delete_Active_Folder_Returns_409()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "Photos");

        var response = await client.DeleteAsync($"/api/trash/folders/{folder.Id}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Folders.AnyAsync(f => f.Id == folder.Id));
    }

    [Fact]
    public async Task Folder_Trash_Delete_Non_Empty_Folder_Returns_409()
    {
        // Soft-deleted folder still containing a soft-deleted child file:
        // permanent delete must refuse (no recursive delete in this slice).
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "Photos");
        var inside = await SeedFileAsAsync(owner, folder.Id, "kept.txt");

        await SoftDeleteFileAsync(owner, inside.Id);
        await SoftDeleteFolderAsync(owner, folder.Id);

        var response = await client.DeleteAsync($"/api/trash/folders/{folder.Id}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Folders.AnyAsync(f => f.Id == folder.Id));
        Assert.True(await db.FileItems.AnyAsync(f => f.Id == inside.Id));
    }

    [Fact]
    public async Task Folder_Trash_Delete_Becomes_Possible_After_Inner_File_Permanently_Deleted()
    {
        // End-to-end: drain children one-by-one then delete the folder.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "Photos");
        var inside = await SeedFileAsAsync(owner, folder.Id, "drop.txt");

        await SoftDeleteFileAsync(owner, inside.Id);
        await SoftDeleteFolderAsync(owner, folder.Id);

        var blockedFolder = await client.DeleteAsync($"/api/trash/folders/{folder.Id}");
        Assert.Equal(HttpStatusCode.Conflict, blockedFolder.StatusCode);

        var fileDel = await client.DeleteAsync($"/api/trash/files/{inside.Id}");
        Assert.Equal(HttpStatusCode.NoContent, fileDel.StatusCode);

        var folderDel = await client.DeleteAsync($"/api/trash/folders/{folder.Id}");
        Assert.Equal(HttpStatusCode.NoContent, folderDel.StatusCode);
    }

    [Fact]
    public async Task Folder_Trash_Delete_Missing_Folder_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.DeleteAsync($"/api/trash/folders/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Folder_Trash_Delete_Foreign_Folder_Returns_404()
    {
        var (alice, _) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceFolder = await SeedFolderAsAsync(alice, null, "AliceFolder");
        await SoftDeleteFolderAsync(alice, aliceFolder.Id);

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var response = await bobClient.DeleteAsync($"/api/trash/folders/{aliceFolder.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Trash_Delete_Response_Bodies_Have_No_Storage_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "doc.txt");
        await SoftDeleteFileAsync(owner, file.Id);
        var folder = await SeedFolderAsAsync(owner, null, "Folder");
        await SoftDeleteFolderAsync(owner, folder.Id);

        // Happy paths return 204 with empty body; conflict bodies are empty
        // too. Just check that no header carries forbidden substrings.
        var fileResp = await client.DeleteAsync($"/api/trash/files/{file.Id}");
        var folderResp = await client.DeleteAsync($"/api/trash/folders/{folder.Id}");

        string[] needles =
        {
            "StorageKey", "storageKey", "storage_key",
            "BlobObjectId", "blobObjectId", "blob_object_id",
            "OwnerUserId", "ownerUserId", "owner_user_id",
            "TokenHash", "tokenHash",
            "PasswordHash", "passwordHash",
            "objects/",
        };
        foreach (var response in new[] { fileResp, folderResp })
        {
            var headers = string.Join('\n',
                response.Headers.Concat(response.Content.Headers)
                    .Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));
            var body = await response.Content.ReadAsStringAsync();
            foreach (var needle in needles)
            {
                Assert.DoesNotContain(needle, headers);
                Assert.DoesNotContain(needle, body);
            }
        }
    }
}
