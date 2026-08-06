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

// Files UI v2: sort + seek pagination + cursor validation for the directory
// listing (GET /api/folders[/{id}]/children).
public sealed class DirectoryListingEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public DirectoryListingEndpointTests()
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

    private async Task<FileItem> SeedFileAsAsync(
        Guid ownerId, Guid? parentId, string name, string mime, byte[] payload)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, parentId, name, mime, new MemoryStream(payload));
    }

    private async Task SetCreatedAtAsync(Guid fileId, DateTime createdAtUtc)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.FirstAsync(f => f.Id == fileId);
        file.CreatedAt = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
        await db.SaveChangesAsync();
    }

    private static byte[] Bytes(int n) => Enumerable.Repeat((byte)1, n).ToArray();

    [Fact]
    public async Task Default_Sort_Is_Name_Ascending()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedFileAsAsync(owner, null, "banana.txt", "text/plain", Bytes(1));
        await SeedFileAsAsync(owner, null, "apple.txt", "text/plain", Bytes(1));
        await SeedFileAsAsync(owner, null, "cherry.txt", "text/plain", Bytes(1));

        var body = await client.GetFromJsonAsync<FolderChildrenResponse>("/api/folders/children");

        Assert.NotNull(body);
        Assert.Equal(new[] { "apple.txt", "banana.txt", "cherry.txt" },
            body!.Files.Select(f => f.Name).ToArray());
    }

    [Fact]
    public async Task Sort_By_Name_Descending()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedFileAsAsync(owner, null, "apple.txt", "text/plain", Bytes(1));
        await SeedFileAsAsync(owner, null, "banana.txt", "text/plain", Bytes(1));
        await SeedFileAsAsync(owner, null, "cherry.txt", "text/plain", Bytes(1));

        var body = await client.GetFromJsonAsync<FolderChildrenResponse>(
            "/api/folders/children?sort=name&direction=desc");

        Assert.NotNull(body);
        Assert.Equal(new[] { "cherry.txt", "banana.txt", "apple.txt" },
            body!.Files.Select(f => f.Name).ToArray());
    }

    [Fact]
    public async Task Sort_By_Size_Ascending()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedFileAsAsync(owner, null, "big.bin", "application/octet-stream", Bytes(300));
        await SeedFileAsAsync(owner, null, "small.bin", "application/octet-stream", Bytes(10));
        await SeedFileAsAsync(owner, null, "mid.bin", "application/octet-stream", Bytes(100));

        var body = await client.GetFromJsonAsync<FolderChildrenResponse>(
            "/api/folders/children?sort=size&direction=asc");

        Assert.NotNull(body);
        Assert.Equal(new[] { "small.bin", "mid.bin", "big.bin" },
            body!.Files.Select(f => f.Name).ToArray());
    }

    [Fact]
    public async Task Sort_By_Type_Ascending()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedFileAsAsync(owner, null, "z.txt", "text/plain", Bytes(1));
        await SeedFileAsAsync(owner, null, "a.jpg", "image/jpeg", Bytes(1));
        await SeedFileAsAsync(owner, null, "m.pdf", "application/pdf", Bytes(1));

        var body = await client.GetFromJsonAsync<FolderChildrenResponse>(
            "/api/folders/children?sort=type&direction=asc");

        Assert.NotNull(body);
        // application/pdf < image/jpeg < text/plain (ordinal)
        Assert.Equal(new[] { "m.pdf", "a.jpg", "z.txt" },
            body!.Files.Select(f => f.Name).ToArray());
    }

    [Fact]
    public async Task Sort_By_Created_Descending_Returns_Newest_First()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var oldFile = await SeedFileAsAsync(owner, null, "old.txt", "text/plain", Bytes(1));
        var midFile = await SeedFileAsAsync(owner, null, "mid.txt", "text/plain", Bytes(1));
        var newFile = await SeedFileAsAsync(owner, null, "new.txt", "text/plain", Bytes(1));
        await SetCreatedAtAsync(oldFile.Id, new DateTime(2020, 1, 1));
        await SetCreatedAtAsync(midFile.Id, new DateTime(2022, 1, 1));
        await SetCreatedAtAsync(newFile.Id, new DateTime(2024, 1, 1));

        var body = await client.GetFromJsonAsync<FolderChildrenResponse>(
            "/api/folders/children?sort=created&direction=desc");

        Assert.NotNull(body);
        Assert.Equal(new[] { "new.txt", "mid.txt", "old.txt" },
            body!.Files.Select(f => f.Name).ToArray());
    }

    [Fact]
    public async Task Pagination_Returns_Every_File_Once_In_Order()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedFolderAsAsync(owner, null, "a-folder");
        // Seed in shuffled order to prove the server sorts.
        foreach (var name in new[] { "file-03", "file-01", "file-05", "file-02", "file-04" })
        {
            await SeedFileAsAsync(owner, null, $"{name}.txt", "text/plain", Bytes(1));
        }

        var collected = new List<string>();
        var pages = 0;
        var foldersOnFirstPageOnly = true;
        string? cursor = null;
        do
        {
            var url = "/api/folders/children?sort=name&direction=asc&limit=2"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var body = await client.GetFromJsonAsync<FolderChildrenResponse>(url);
            Assert.NotNull(body);

            if (pages == 0)
            {
                Assert.Single(body!.Folders); // folders only on the first page
            }
            else if (body!.Folders.Count != 0)
            {
                foldersOnFirstPageOnly = false;
            }

            collected.AddRange(body.Files.Select(f => f.Name));
            cursor = body.NextCursor;
            Assert.Equal(cursor is not null, body.HasMore);
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.True(foldersOnFirstPageOnly);
        Assert.Equal(3, pages); // 5 files, limit 2 → 2 + 2 + 1
        Assert.Equal(
            new[] { "file-01.txt", "file-02.txt", "file-03.txt", "file-04.txt", "file-05.txt" },
            collected.ToArray());
        Assert.Equal(collected.Count, collected.Distinct().Count()); // no duplicates
    }

    [Fact]
    public async Task Cursor_From_Another_Folder_Is_Rejected_With_400()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folderA = await SeedFolderAsAsync(owner, null, "A");
        var folderB = await SeedFolderAsAsync(owner, null, "B");
        await SeedFileAsAsync(owner, folderA.Id, "a1.txt", "text/plain", Bytes(1));
        await SeedFileAsAsync(owner, folderA.Id, "a2.txt", "text/plain", Bytes(1));

        var pageA = await client.GetFromJsonAsync<FolderChildrenResponse>(
            $"/api/folders/{folderA.Id}/children?sort=name&limit=1");
        Assert.NotNull(pageA!.NextCursor);

        // Replaying folder A's cursor against folder B must be rejected, never
        // leak A's files into B's listing.
        var response = await client.GetAsync(
            $"/api/folders/{folderB.Id}/children?sort=name&limit=1&cursor={Uri.EscapeDataString(pageA.NextCursor!)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Cursor_With_Mismatched_Sort_Is_Rejected_With_400()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedFileAsAsync(owner, null, "a.txt", "text/plain", Bytes(1));
        await SeedFileAsAsync(owner, null, "b.txt", "text/plain", Bytes(1));

        var page = await client.GetFromJsonAsync<FolderChildrenResponse>(
            "/api/folders/children?sort=name&limit=1");
        Assert.NotNull(page!.NextCursor);

        var response = await client.GetAsync(
            $"/api/folders/children?sort=size&limit=1&cursor={Uri.EscapeDataString(page.NextCursor!)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/folders/children?sort=bogus")]
    [InlineData("/api/folders/children?direction=sideways")]
    [InlineData("/api/folders/children?cursor=not-a-real-cursor")]
    public async Task Invalid_Parameters_Return_400(string url)
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_Limit_Is_Clamped_And_Still_Succeeds()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedFileAsAsync(owner, null, "only.txt", "text/plain", Bytes(1));

        var response = await client.GetAsync("/api/folders/children?limit=999999");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<FolderChildrenResponse>();
        Assert.Single(body!.Files);
        Assert.False(body.HasMore);
        Assert.Null(body.NextCursor);
    }
}
