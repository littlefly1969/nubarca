using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Folders;

// Slice 77 — recursive folder soft-delete.
public sealed class RecursiveFolderDeleteTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public RecursiveFolderDeleteTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Folder> CreateFolderAsync(Guid ownerId, Guid? parentId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
        return await folders.CreateAsync(ownerId, parentId, name);
    }

    private async Task<FileItem> UploadFileAsync(Guid ownerId, Guid? folderId, string name = "f.txt")
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, folderId, name, "text/plain",
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(name)));
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    // ---- existing empty-folder behavior preserved ----

    [Fact]
    public async Task Delete_Empty_Folder_Returns_204_No_Content()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync("rfd-empty@example.com");
        var folder = await CreateFolderAsync(owner, null, "empty");

        var resp = await client.DeleteAsync($"/api/folders/{folder.Id}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_NonEmpty_Folder_Without_Recursive_Returns_409()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync("rfd-notrec@example.com");
        var folder = await CreateFolderAsync(owner, null, "nonempty");
        await UploadFileAsync(owner, folder.Id);

        var resp = await client.DeleteAsync($"/api/folders/{folder.Id}");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        // File still active.
        Assert.Equal(1, await InDbAsync(db => db.FileItems.CountAsync(f => f.DeletedAt == null)));
    }

    // ---- recursive delete ----

    [Fact]
    public async Task Recursive_Delete_NonEmpty_Folder_Returns_200_With_Counts()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync("rfd-01@example.com");
        var folder = await CreateFolderAsync(owner, null, "parent");
        await UploadFileAsync(owner, folder.Id, "a.txt");
        await UploadFileAsync(owner, folder.Id, "b.txt");

        var resp = await client.DeleteAsync($"/api/folders/{folder.Id}?recursive=true");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<RecursiveDeleteResultDto>();
        Assert.Equal(2, result!.DeletedFileCount);
        Assert.Equal(1, result.DeletedFolderCount); // the root folder itself
    }

    [Fact]
    public async Task Recursive_Delete_Preserves_Nested_Structure()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync("rfd-02@example.com");
        var root = await CreateFolderAsync(owner, null, "root");
        var sub = await CreateFolderAsync(owner, root.Id, "sub");
        await UploadFileAsync(owner, root.Id, "root.txt");
        await UploadFileAsync(owner, sub.Id, "sub.txt");

        var resp = await client.DeleteAsync($"/api/folders/{root.Id}?recursive=true");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<RecursiveDeleteResultDto>();
        Assert.Equal(2, result!.DeletedFileCount);
        Assert.Equal(2, result.DeletedFolderCount); // root + sub

        // All folders and files are soft-deleted.
        Assert.Equal(0, await InDbAsync(db => db.Folders.CountAsync(f => f.DeletedAt == null)));
        Assert.Equal(0, await InDbAsync(db => db.FileItems.CountAsync(f => f.DeletedAt == null)));
    }

    [Fact]
    public async Task Recursive_Delete_Missing_Folder_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("rfd-03@example.com");
        var resp = await client.DeleteAsync($"/api/folders/{Guid.NewGuid()}?recursive=true");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- owner scoping ----

    [Fact]
    public async Task Recursive_Delete_Cannot_Affect_Another_Users_Data()
    {
        var (alice, aliceClient) = await _factory.CreateAuthenticatedClientAsync("alice-rd@example.com");
        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob-rd@example.com");

        var aliceFolder = await CreateFolderAsync(alice, null, "alice-folder");
        await UploadFileAsync(alice, aliceFolder.Id, "private.txt");

        // Bob tries to recursively delete Alice's folder.
        var resp = await bobClient.DeleteAsync($"/api/folders/{aliceFolder.Id}?recursive=true");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        // Alice's data untouched.
        Assert.Equal(1, await InDbAsync(db => db.Folders.CountAsync(f => f.DeletedAt == null)));
        Assert.Equal(1, await InDbAsync(db => db.FileItems.CountAsync(f => f.DeletedAt == null)));
    }

    // ---- blob dedup safety ----

    [Fact]
    public async Task Recursive_Delete_Does_Not_Delete_Blob_Referenced_By_Other_FileItem()
    {
        // Upload the same bytes twice: one in the folder to delete, one elsewhere.
        var bytes = "shared-blob-content"u8.ToArray();
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync("rfd-04@example.com");
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();

        var folder = await CreateFolderAsync(owner, null, "to-delete");
        await files.CreateAsync(owner, folder.Id, "inside.txt", "text/plain", new MemoryStream(bytes));
        await files.CreateAsync(owner, null, "outside.txt", "text/plain", new MemoryStream(bytes));

        // Same bytes → one BlobObject shared by two FileItems.
        Assert.Equal(1, await InDbAsync(db => db.BlobObjects.CountAsync()));
        var blobId = await InDbAsync(db => db.BlobObjects.Select(b => b.Id).SingleAsync());

        var resp = await client.DeleteAsync($"/api/folders/{folder.Id}?recursive=true");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The blob row still exists (reference count decremented but still > 0).
        Assert.True(await InDbAsync(db =>
            db.BlobObjects.AnyAsync(b => b.Id == blobId && b.ReferenceCount > 0)));
        // The outside file is still active.
        Assert.Equal(1, await InDbAsync(db => db.FileItems.CountAsync(f => f.DeletedAt == null)));
    }

    // ---- delete-preview endpoint ----

    [Fact]
    public async Task Delete_Preview_Returns_Correct_Counts()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync("rfd-05@example.com");
        var root = await CreateFolderAsync(owner, null, "root");
        var sub = await CreateFolderAsync(owner, root.Id, "sub");
        await UploadFileAsync(owner, root.Id, "a.txt");
        await UploadFileAsync(owner, sub.Id, "b.txt");

        var resp = await client.GetAsync($"/api/folders/{root.Id}/delete-preview");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var preview = await resp.Content.ReadFromJsonAsync<FolderDeletePreviewDto>();
        Assert.Equal(2, preview!.FileCount);
        Assert.Equal(1, preview.FolderCount); // sub only (root excluded from count)
    }

    [Fact]
    public async Task Delete_Preview_Missing_Folder_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("rfd-06@example.com");
        var resp = await client.GetAsync($"/api/folders/{Guid.NewGuid()}/delete-preview");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- no-leak scan ----

    [Fact]
    public async Task Recursive_Delete_Response_Has_No_Internal_Fields()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync("rfd-07@example.com");
        var folder = await CreateFolderAsync(owner, null, "scan");
        await UploadFileAsync(owner, folder.Id);

        var resp = await client.DeleteAsync($"/api/folders/{folder.Id}?recursive=true");
        var body = await resp.Content.ReadAsStringAsync();

        foreach (var needle in new[] { "storageKey", "sha256", "blobObjectId", "objects/" })
        {
            Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
        }
    }
}

// Local DTO mirrors — used only in this test file.
file sealed class RecursiveDeleteResultDto
{
    public int DeletedFileCount { get; set; }
    public int DeletedFolderCount { get; set; }
}

file sealed class FolderDeletePreviewDto
{
    public int FileCount { get; set; }
    public int FolderCount { get; set; }
}
