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

namespace NubArca.Api.Tests.Files;

// Slice 76 — folder upload with directory-structure preservation via the
// optional `relativePath` form field on POST /api/files.
public sealed class FolderUploadEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FolderUploadEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static MultipartFormDataContent Multipart(
        byte[] payload, string filename, string? relativePath = null, string contentType = "text/plain")
    {
        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(payload);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        multipart.Add(part, "file", filename);
        if (relativePath is not null)
        {
            multipart.Add(new StringContent(relativePath), "relativePath");
        }
        return multipart;
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await work(db);
    }

    // Resolves the logical folder path (root → leaf) of a FileItem by walking
    // ParentFolderId links. Returns segments joined with "/", file name last.
    private async Task<string> LogicalPathAsync(Guid ownerId, Guid fileItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == fileItemId);
        var parts = new List<string> { file.Name };
        var pid = file.ParentFolderId;
        while (pid is Guid id)
        {
            var folder = await db.Folders.AsNoTracking().SingleAsync(f => f.Id == id);
            Assert.Equal(ownerId, folder.OwnerUserId); // owner-scoped chain
            parts.Insert(0, folder.Name);
            pid = folder.ParentFolderId;
        }
        return string.Join('/', parts);
    }

    // ---- happy paths ----

    [Fact]
    public async Task Normal_Upload_Without_RelativePath_Still_Works()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var resp = await client.PostAsync("/api/files", Multipart("hello"u8.ToArray(), "plain.txt"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();

        Assert.Equal("plain.txt", summary!.Name);
        var path = await LogicalPathAsync(owner, summary.Id);
        Assert.Equal("plain.txt", path); // no folders created
        Assert.Equal(0, await InDbAsync(db => db.Folders.CountAsync()));
    }

    [Fact]
    public async Task Folder_Upload_Creates_FileItem_With_Correct_Logical_Path()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var resp = await client.PostAsync("/api/files",
            Multipart("data"u8.ToArray(), "photo.jpg", relativePath: "Holiday/photo.jpg"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();

        Assert.Equal("photo.jpg", summary!.Name);
        Assert.Equal("Holiday/photo.jpg", await LogicalPathAsync(owner, summary.Id));
    }

    [Fact]
    public async Task Nested_Folders_Are_Preserved()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var resp = await client.PostAsync("/api/files",
            Multipart("x"u8.ToArray(), "f.txt", relativePath: "a/b/c/f.txt"));
        var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();

        Assert.Equal("a/b/c/f.txt", await LogicalPathAsync(owner, summary!.Id));
        // Exactly three folders created: a, b, c.
        Assert.Equal(3, await InDbAsync(db => db.Folders.CountAsync()));
    }

    [Fact]
    public async Task Shared_Directory_Is_Reused_Across_Files()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await client.PostAsync("/api/files",
            Multipart("1"u8.ToArray(), "one.txt", relativePath: "shared/one.txt"));
        await client.PostAsync("/api/files",
            Multipart("2"u8.ToArray(), "two.txt", relativePath: "shared/two.txt"));

        // Only one "shared" folder; both files live under it.
        Assert.Equal(1, await InDbAsync(db => db.Folders.CountAsync(f => f.Name == "shared")));
        Assert.Equal(2, await InDbAsync(db => db.FileItems.CountAsync()));
    }

    [Fact]
    public async Task Identical_Bytes_Still_Dedupe_To_One_Blob()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var bytes = "duplicate-content"u8.ToArray();
        await client.PostAsync("/api/files",
            Multipart(bytes, "a.txt", relativePath: "dir1/a.txt"));
        await client.PostAsync("/api/files",
            Multipart(bytes, "b.txt", relativePath: "dir2/b.txt"));

        // Two FileItems, ONE BlobObject (content-addressed dedup unchanged).
        Assert.Equal(2, await InDbAsync(db => db.FileItems.CountAsync()));
        Assert.Equal(1, await InDbAsync(db => db.BlobObjects.CountAsync()));
    }

    [Fact]
    public async Task Folder_Upload_Into_Existing_Parent_Folder()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        Guid parentId;
        using (var scope = _factory.Services.CreateScope())
        {
            var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
            parentId = (await folders.CreateAsync(owner, null, "Root")).Id;
        }

        var resp = await client.PostAsync($"/api/folders/{parentId}/files",
            Multipart("x"u8.ToArray(), "g.txt", relativePath: "sub/g.txt"));
        var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();

        Assert.Equal("Root/sub/g.txt", await LogicalPathAsync(owner, summary!.Id));
    }

    // ---- rejection / security ----

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../etc/passwd")]
    [InlineData("a/./b.txt")]
    [InlineData("/absolute/x.txt")]
    [InlineData("C:/Windows/x.txt")]
    [InlineData("a//b.txt")]
    public async Task Unsafe_Relative_Paths_Are_Rejected_400(string relativePath)
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var resp = await client.PostAsync("/api/files",
            Multipart("x"u8.ToArray(), "x.txt", relativePath: relativePath));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        // Nothing was created.
        Assert.Equal(0, await InDbAsync(db => db.FileItems.CountAsync()));
        Assert.Equal(0, await InDbAsync(db => db.Folders.CountAsync()));
    }

    [Fact]
    public async Task Backslash_Paths_Are_Normalized_To_Forward_Slash()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        // A spoofed Windows-style separator that is NOT absolute → normalised.
        var resp = await client.PostAsync("/api/files",
            Multipart("x"u8.ToArray(), "w.txt", relativePath: "win\\sub\\w.txt"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();
        Assert.Equal("win/sub/w.txt", await LogicalPathAsync(owner, summary!.Id));
    }

    [Fact]
    public async Task Duplicate_Path_Conflicts_With_409()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var first = await client.PostAsync("/api/files",
            Multipart("1"u8.ToArray(), "dup.txt", relativePath: "d/dup.txt"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Same logical path again → conflict (existing reject-by-name behaviour).
        var second = await client.PostAsync("/api/files",
            Multipart("2"u8.ToArray(), "dup.txt", relativePath: "d/dup.txt"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Cross_User_Namespace_Is_Isolated()
    {
        var (alice, aliceClient) = await _factory.CreateAuthenticatedClientAsync("alice-fu@example.com");
        var (bob, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob-fu@example.com");

        await aliceClient.PostAsync("/api/files",
            Multipart("a"u8.ToArray(), "a.txt", relativePath: "shared/a.txt"));
        await bobClient.PostAsync("/api/files",
            Multipart("b"u8.ToArray(), "b.txt", relativePath: "shared/b.txt"));

        // Each user has their OWN "shared" folder — two distinct rows, one per owner.
        var aliceFolders = await InDbAsync(db =>
            db.Folders.CountAsync(f => f.OwnerUserId == alice && f.Name == "shared"));
        var bobFolders = await InDbAsync(db =>
            db.Folders.CountAsync(f => f.OwnerUserId == bob && f.Name == "shared"));
        Assert.Equal(1, aliceFolders);
        Assert.Equal(1, bobFolders);

        // Alice's file is parented under Alice's folder only.
        var aliceFileParent = await InDbAsync(db => db.FileItems
            .Where(f => f.OwnerUserId == alice).Select(f => f.ParentFolderId).SingleAsync());
        var aliceOwnsParent = await InDbAsync(db => db.Folders
            .AnyAsync(f => f.Id == aliceFileParent && f.OwnerUserId == alice));
        Assert.True(aliceOwnsParent);
    }
}
