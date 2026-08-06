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
using NubArca.Api.ShareLinks;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Files;

// End-to-end HTTP tests for DELETE /api/trash (bulk empty-trash).
public sealed class EmptyTrashEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public EmptyTrashEndpointTests()
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

    // Stamp DeletedAt directly so we can simulate a state the public soft-
    // delete API forbids (non-empty folder marked deleted). PermanentDelete's
    // empty check is defensive against this state regardless of how it was
    // reached.
    private async Task ForceSoftDeleteFolderAsync(Guid folderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Folders
            .Where(f => f.Id == folderId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.DeletedAt, _ => (DateTime?)DateTime.UtcNow));
    }

    private async Task<List<AuditLog>> ReadAuditAsync(string action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.AsNoTracking().Where(a => a.Action == action).ToListAsync();
    }

    [Fact]
    public async Task Empty_Trash_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.DeleteAsync("/api/trash");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Empty_Trash_When_Trash_Is_Empty_Returns_Zero_Counts_And_Writes_Audit()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.DeleteAsync("/api/trash");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<EmptyTrashResult>();
        Assert.NotNull(body);
        Assert.Equal(0, body!.DeletedFiles);
        Assert.Equal(0, body.DeletedFolders);
        Assert.Equal(0, body.Conflicts);
        Assert.Equal(0, body.Errors);
        Assert.Empty(body.Failures);

        var audit = await ReadAuditAsync(AuditActions.TrashEmpty);
        var entry = Assert.Single(audit);
        Assert.Equal(owner, entry.UserId);
        Assert.Equal(AuditEntityTypes.Trash, entry.EntityType);
        Assert.Null(entry.EntityId);
    }

    [Fact]
    public async Task Empty_Trash_Permanently_Deletes_All_SoftDeleted_Files()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();

        var file1 = await SeedFileAsAsync(owner, null, "a.txt");
        var file2 = await SeedFileAsAsync(owner, null, "b.txt");
        await SoftDeleteFileAsync(owner, file1.Id);
        await SoftDeleteFileAsync(owner, file2.Id);

        var response = await client.DeleteAsync("/api/trash");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<EmptyTrashResult>();
        Assert.NotNull(body);
        Assert.Equal(2, body!.DeletedFiles);
        Assert.Equal(0, body.DeletedFolders);
        Assert.Equal(0, body.Conflicts);
        Assert.Equal(0, body.Errors);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.FileItems.CountAsync());
    }

    [Fact]
    public async Task Empty_Trash_Permanently_Deletes_Empty_SoftDeleted_Folders()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var f1 = await SeedFolderAsAsync(owner, null, "Folder1");
        var f2 = await SeedFolderAsAsync(owner, null, "Folder2");
        await SoftDeleteFolderAsync(owner, f1.Id);
        await SoftDeleteFolderAsync(owner, f2.Id);

        var response = await client.DeleteAsync("/api/trash");
        var body = await response.Content.ReadFromJsonAsync<EmptyTrashResult>();
        Assert.NotNull(body);
        Assert.Equal(2, body!.DeletedFolders);
        Assert.Equal(0, body.Conflicts);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Folders.CountAsync());
    }

    [Fact]
    public async Task Empty_Trash_Leaves_Active_Files_And_Folders_Untouched()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var keepFolder = await SeedFolderAsAsync(owner, null, "Keep");
        var keepFile = await SeedFileAsAsync(owner, null, "keep.txt");
        var deleteFile = await SeedFileAsAsync(owner, null, "delete.txt");
        await SoftDeleteFileAsync(owner, deleteFile.Id);

        var response = await client.DeleteAsync("/api/trash");
        var body = await response.Content.ReadFromJsonAsync<EmptyTrashResult>();
        Assert.NotNull(body);
        Assert.Equal(1, body!.DeletedFiles);
        Assert.Equal(0, body.DeletedFolders);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.FileItems.AnyAsync(f => f.Id == keepFile.Id));
        Assert.True(await db.Folders.AnyAsync(f => f.Id == keepFolder.Id));
    }

    [Fact]
    public async Task Empty_Trash_Does_Not_Touch_Foreign_SoftDeleted_Items()
    {
        var (alice, aliceClient) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceFile = await SeedFileAsAsync(alice, null, "alice.txt");
        await SoftDeleteFileAsync(alice, aliceFile.Id);
        var aliceFolder = await SeedFolderAsAsync(alice, null, "AliceFolder");
        await SoftDeleteFolderAsync(alice, aliceFolder.Id);

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var response = await bobClient.DeleteAsync("/api/trash");
        var body = await response.Content.ReadFromJsonAsync<EmptyTrashResult>();
        Assert.NotNull(body);
        Assert.Equal(0, body!.DeletedFiles);
        Assert.Equal(0, body.DeletedFolders);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.FileItems.AnyAsync(f => f.Id == aliceFile.Id));
        Assert.True(await db.Folders.AnyAsync(f => f.Id == aliceFolder.Id));
    }

    [Fact]
    public async Task Empty_Trash_Processes_Files_Before_Folders_So_Parent_Folder_Becomes_Empty()
    {
        // The folder is soft-deleted AFTER its child file. The file lives at
        // ParentFolderId = folder.Id. With file-first ordering, the file is
        // purged first, leaving the folder empty, so it can also be purged
        // in the same bulk call.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "Photos");
        var file = await SeedFileAsAsync(owner, folder.Id, "inside.txt");

        await SoftDeleteFileAsync(owner, file.Id);
        await SoftDeleteFolderAsync(owner, folder.Id);

        var response = await client.DeleteAsync("/api/trash");
        var body = await response.Content.ReadFromJsonAsync<EmptyTrashResult>();
        Assert.NotNull(body);
        Assert.Equal(1, body!.DeletedFiles);
        Assert.Equal(1, body.DeletedFolders);
        Assert.Equal(0, body.Conflicts);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.FileItems.CountAsync());
        Assert.Equal(0, await db.Folders.CountAsync());
    }

    [Fact]
    public async Task Empty_Trash_Multi_Pass_Drains_Nested_SoftDeleted_Folders()
    {
        // outer/inner — both soft-deleted, no files. Outer cannot be purged
        // until inner is gone, so we need at least two folder passes.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var outer = await SeedFolderAsAsync(owner, null, "Outer");
        var inner = await SeedFolderAsAsync(owner, outer.Id, "Inner");

        await SoftDeleteFolderAsync(owner, inner.Id);
        await SoftDeleteFolderAsync(owner, outer.Id);

        var response = await client.DeleteAsync("/api/trash");
        var body = await response.Content.ReadFromJsonAsync<EmptyTrashResult>();
        Assert.NotNull(body);
        Assert.Equal(2, body!.DeletedFolders);
        Assert.Equal(0, body.Conflicts);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Folders.CountAsync());
    }

    [Fact]
    public async Task Empty_Trash_Reports_Conflict_For_Folder_With_Active_Child()
    {
        // The public SoftDeleteAsync refuses non-empty folders, but the
        // PermanentDeleteAsync empty check defends against the state anyway.
        // Force-stamp DeletedAt to reach this state and verify the bulk
        // operation reports it as `not_empty` without touching the active
        // child or the foreign-untouched siblings.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "Photos");
        var activeChild = await SeedFileAsAsync(owner, folder.Id, "live.txt");
        await ForceSoftDeleteFolderAsync(folder.Id);

        var response = await client.DeleteAsync("/api/trash");
        var body = await response.Content.ReadFromJsonAsync<EmptyTrashResult>();
        Assert.NotNull(body);
        Assert.Equal(0, body!.DeletedFolders);
        Assert.Equal(1, body.Conflicts);
        var failure = Assert.Single(body.Failures);
        Assert.Equal(folder.Id, failure.Id);
        Assert.Equal("folder", failure.Type);
        Assert.Equal("not_empty", failure.Reason);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Folders.AnyAsync(f => f.Id == folder.Id));
        Assert.True(await db.FileItems.AnyAsync(f => f.Id == activeChild.Id));
    }

    [Fact]
    public async Task Empty_Trash_Removes_ShareLinks_And_Thumbnails_For_Deleted_Files()
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

        var response = await client.DeleteAsync("/api/trash");
        var body = await response.Content.ReadFromJsonAsync<EmptyTrashResult>();
        Assert.NotNull(body);
        Assert.Equal(1, body!.DeletedFiles);

        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verifyDb.FileItems.AnyAsync(f => f.Id == fileId));
        Assert.False(await verifyDb.FileThumbnails.AnyAsync(t => t.FileItemId == fileId));
        Assert.False(await verifyDb.ShareLinks.AnyAsync(s => s.Id == shareLinkId));
    }

    [Fact]
    public async Task Empty_Trash_Leaves_BlobObject_Rows_For_BlobJanitor()
    {
        // Permanent delete must NOT touch the BlobObject row. BlobJanitor
        // reclaims it later when ReferenceCount has been 0 long enough.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "doc.txt");
        Guid blobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            blobId = (await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == file.Id)).BlobObjectId;
        }

        await SoftDeleteFileAsync(owner, file.Id);

        var response = await client.DeleteAsync("/api/trash");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = await verifyDb.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == blobId);
        Assert.Equal(0, blob.ReferenceCount);
    }

    [Fact]
    public async Task Empty_Trash_Audit_Metadata_Has_Safe_Aggregate_Counts()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "doc.txt");
        var folder = await SeedFolderAsAsync(owner, null, "Folder");
        await SoftDeleteFileAsync(owner, file.Id);
        await SoftDeleteFolderAsync(owner, folder.Id);

        var response = await client.DeleteAsync("/api/trash");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var audit = await ReadAuditAsync(AuditActions.TrashEmpty);
        var entry = Assert.Single(audit);
        Assert.NotNull(entry.MetadataJson);
        Assert.Contains("\"deletedFiles\":1", entry.MetadataJson!);
        Assert.Contains("\"deletedFolders\":1", entry.MetadataJson!);
        Assert.Contains("\"conflicts\":0", entry.MetadataJson!);
        Assert.Contains("\"errors\":0", entry.MetadataJson!);

        // No storage internals in the audit metadata.
        string[] needles = { "StorageKey", "BlobObjectId", "TokenHash", "objects/" };
        foreach (var needle in needles)
        {
            Assert.DoesNotContain(needle, entry.MetadataJson!);
        }
    }

    [Fact]
    public async Task Empty_Trash_Response_Has_No_Storage_Internals_Leak()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, null, "doc.txt");
        var folder = await SeedFolderAsAsync(owner, null, "Folder");
        var activeChild = await SeedFileAsAsync(owner, folder.Id, "stuck.txt"); // keeps folder non-empty
        await SoftDeleteFileAsync(owner, file.Id);
        await ForceSoftDeleteFolderAsync(folder.Id); // public soft-delete refuses non-empty folders

        var response = await client.DeleteAsync("/api/trash");
        var body = await response.Content.ReadAsStringAsync();
        var headers = string.Join('\n',
            response.Headers.Concat(response.Content.Headers)
                .Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));

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
            Assert.DoesNotContain(needle, headers);
        }
    }
}
