using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Albums;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Albums;

public sealed class AlbumEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public AlbumEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await work(db);
    }

    private async Task<Guid> UploadFileAsync(Guid ownerId)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        var f = await files.CreateAsync(ownerId, null, "test.txt", "text/plain",
            new MemoryStream("hello"u8.ToArray()));
        return f.Id;
    }

    // --- Auth ---

    [Fact]
    public async Task Unauthenticated_List_Returns_401()
    {
        var anon = _factory.CreateClient();
        var r = await anon.GetAsync("/api/albums");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_Create_Returns_401()
    {
        var anon = _factory.CreateClient();
        var r = await anon.PostAsJsonAsync("/api/albums", new { name = "A" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    // --- CRUD ---

    [Fact]
    public async Task Create_List_Get_Update_Delete_Album()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        // create
        var created = await client.PostAsJsonAsync("/api/albums", new { name = "Vacation 2025", description = "Summer" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = await created.Content.ReadFromJsonAsync<AlbumDetail>();
        Assert.NotNull(detail);
        Assert.Equal("Vacation 2025", detail!.Name);
        Assert.Equal("Summer", detail.Description);

        // list
        var list = await client.GetFromJsonAsync<AlbumSummary[]>("/api/albums");
        Assert.NotNull(list);
        Assert.Single(list!);
        Assert.Equal("Vacation 2025", list![0].Name);

        // get
        var gotten = await client.GetFromJsonAsync<AlbumDetail>($"/api/albums/{detail.Id}");
        Assert.Equal(detail.Id, gotten!.Id);

        // update
        var patched = await client.PatchAsJsonAsync($"/api/albums/{detail.Id}",
            new { name = "Vacation 2026", description = (string?)null });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var updated = await patched.Content.ReadFromJsonAsync<AlbumDetail>();
        Assert.Equal("Vacation 2026", updated!.Name);
        Assert.Null(updated.Description);

        // delete
        var del = await client.DeleteAsync($"/api/albums/{detail.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var afterDelete = await client.GetAsync($"/api/albums/{detail.Id}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
    }

    [Fact]
    public async Task Duplicate_Album_Name_Returns_Conflict()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await client.PostAsJsonAsync("/api/albums", new { name = "MyAlbum" });

        var r = await client.PostAsJsonAsync("/api/albums", new { name = "MyAlbum" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Empty_Name_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var r = await client.PostAsJsonAsync("/api/albums", new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // --- Owner scoping ---

    [Fact]
    public async Task User_Cannot_Access_Another_Users_Album()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var created = await alice.PostAsJsonAsync("/api/albums", new { name = "Alice private" });
        var detail = await created.Content.ReadFromJsonAsync<AlbumDetail>();

        // Bob cannot get Alice's album.
        var r = await bob.GetAsync($"/api/albums/{detail!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        // Bob cannot delete Alice's album.
        var del = await bob.DeleteAsync($"/api/albums/{detail.Id}");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
    }

    // --- Album items ---

    [Fact]
    public async Task Add_Remove_Item_And_List_Items()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadFileAsync(owner);

        var created = await client.PostAsJsonAsync("/api/albums", new { name = "Mixed" });
        var album = await created.Content.ReadFromJsonAsync<AlbumDetail>();

        // add
        var add = await client.PostAsJsonAsync($"/api/albums/{album!.Id}/items",
            new { fileItemId = fileId });
        Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);

        // list — item count in summary
        var list = await client.GetFromJsonAsync<AlbumSummary[]>("/api/albums");
        Assert.Equal(1, list![0].ItemCount);

        // list items
        var items = await client.GetFromJsonAsync<AlbumItemSummary[]>($"/api/albums/{album.Id}/items");
        Assert.Single(items!);
        Assert.Equal(fileId, items![0].FileItemId);

        // remove
        var rem = await client.DeleteAsync($"/api/albums/{album.Id}/items/{fileId}");
        Assert.Equal(HttpStatusCode.NoContent, rem.StatusCode);

        var afterRemove = await client.GetFromJsonAsync<AlbumItemSummary[]>($"/api/albums/{album.Id}/items");
        Assert.Empty(afterRemove!);
    }

    [Fact]
    public async Task Add_Same_File_Twice_Is_Idempotent()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadFileAsync(owner);
        var created = await client.PostAsJsonAsync("/api/albums", new { name = "Idempotent" });
        var album = await created.Content.ReadFromJsonAsync<AlbumDetail>();

        await client.PostAsJsonAsync($"/api/albums/{album!.Id}/items", new { fileItemId = fileId });
        var second = await client.PostAsJsonAsync($"/api/albums/{album.Id}/items", new { fileItemId = fileId });
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        var items = await client.GetFromJsonAsync<AlbumItemSummary[]>($"/api/albums/{album.Id}/items");
        Assert.Single(items!);
    }

    [Fact]
    public async Task File_Can_Belong_To_Multiple_Albums()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadFileAsync(owner);

        var a1 = await (await client.PostAsJsonAsync("/api/albums", new { name = "Alpha" }))
            .Content.ReadFromJsonAsync<AlbumDetail>();
        var a2 = await (await client.PostAsJsonAsync("/api/albums", new { name = "Beta" }))
            .Content.ReadFromJsonAsync<AlbumDetail>();

        await client.PostAsJsonAsync($"/api/albums/{a1!.Id}/items", new { fileItemId = fileId });
        await client.PostAsJsonAsync($"/api/albums/{a2!.Id}/items", new { fileItemId = fileId });

        var i1 = await client.GetFromJsonAsync<AlbumItemSummary[]>($"/api/albums/{a1.Id}/items");
        var i2 = await client.GetFromJsonAsync<AlbumItemSummary[]>($"/api/albums/{a2.Id}/items");
        Assert.Single(i1!);
        Assert.Single(i2!);
        Assert.Equal(fileId, i1![0].FileItemId);
        Assert.Equal(fileId, i2![0].FileItemId);
    }

    [Fact]
    public async Task User_Cannot_Add_Another_Users_File()
    {
        var aliceId = await _factory.SeedUserAsync("alice2@example.com");
        var aliceFileId = await UploadFileAsync(aliceId);

        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob2@example.com");
        var created = await bob.PostAsJsonAsync("/api/albums", new { name = "Bob album" });
        var album = await created.Content.ReadFromJsonAsync<AlbumDetail>();

        var r = await bob.PostAsJsonAsync($"/api/albums/{album!.Id}/items",
            new { fileItemId = aliceFileId });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    // --- Cleanup on permanent delete ---

    [Fact]
    public async Task Delete_Album_Does_Not_Delete_File()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadFileAsync(owner);

        var created = await client.PostAsJsonAsync("/api/albums", new { name = "Temp album" });
        var album = await created.Content.ReadFromJsonAsync<AlbumDetail>();
        await client.PostAsJsonAsync($"/api/albums/{album!.Id}/items", new { fileItemId = fileId });

        // Delete the album.
        await client.DeleteAsync($"/api/albums/{album.Id}");

        // File must still exist.
        var fileExists = await InDbAsync(db =>
            db.FileItems.AnyAsync(f => f.Id == fileId && f.DeletedAt == null));
        Assert.True(fileExists);
    }

    [Fact]
    public async Task Permanent_Delete_Removes_Album_Memberships()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadFileAsync(owner);

        var created = await client.PostAsJsonAsync("/api/albums", new { name = "Will be orphaned" });
        var album = await created.Content.ReadFromJsonAsync<AlbumDetail>();
        await client.PostAsJsonAsync($"/api/albums/{album!.Id}/items", new { fileItemId = fileId });

        // Soft-delete then permanently delete the file.
        await client.DeleteAsync($"/api/files/{fileId}");
        await client.DeleteAsync($"/api/trash/files/{fileId}");

        // AlbumItem must be gone.
        var membershipExists = await InDbAsync(db =>
            db.AlbumItems.AnyAsync(ai => ai.FileItemId == fileId));
        Assert.False(membershipExists);
    }

    [Fact]
    public async Task Soft_Deleted_Files_Hidden_From_Album_Items()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadFileAsync(owner);

        var created = await client.PostAsJsonAsync("/api/albums", new { name = "Sparse" });
        var album = await created.Content.ReadFromJsonAsync<AlbumDetail>();
        await client.PostAsJsonAsync($"/api/albums/{album!.Id}/items", new { fileItemId = fileId });

        // Soft-delete the file (do NOT permanently delete).
        await client.DeleteAsync($"/api/files/{fileId}");

        // Album item listing must not include the soft-deleted file.
        var items = await client.GetFromJsonAsync<AlbumItemSummary[]>($"/api/albums/{album.Id}/items");
        Assert.Empty(items!);
    }

    // --- No-leak scan ---

    [Fact]
    public async Task Album_Item_DTO_Contains_No_Internal_Fields()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadFileAsync(owner);
        var created = await client.PostAsJsonAsync("/api/albums", new { name = "NoLeak" });
        var album = await created.Content.ReadFromJsonAsync<AlbumDetail>();
        await client.PostAsJsonAsync($"/api/albums/{album!.Id}/items", new { fileItemId = fileId });

        var response = await client.GetAsync($"/api/albums/{album.Id}/items");
        var body = await response.Content.ReadAsStringAsync();

        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
        }

        // OwnerUserId must not appear in album list or detail either.
        var listBody = await (await client.GetAsync("/api/albums")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("ownerId", listBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ownerUserId", listBody, StringComparison.OrdinalIgnoreCase);
    }

    // --- Dedup-blob cross-user isolation ---

    [Fact]
    public async Task Deduped_Blob_Does_Not_Share_Album_Membership_Across_Users()
    {
        // Alice and Bob upload identical bytes → same blob, separate FileItems.
        var bytes = "hello dedup"u8.ToArray();

        var (alice, aliceClient) = await _factory.CreateAuthenticatedClientAsync("alice3@example.com");
        var (bob, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob3@example.com");

        using var scope = _factory.Services.CreateScope();
        var fileSvc = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        var aliceFile = await fileSvc.CreateAsync(alice, null, "dup.txt", "text/plain", new MemoryStream(bytes));
        var bobFile = await fileSvc.CreateAsync(bob, null, "dup.txt", "text/plain", new MemoryStream(bytes));

        // Verify dedup.
        Assert.Equal(aliceFile.BlobObjectId, bobFile.BlobObjectId);
        Assert.NotEqual(aliceFile.Id, bobFile.Id);

        // Alice adds her FileItem to her album.
        var aliceAlbum = await (await aliceClient.PostAsJsonAsync("/api/albums", new { name = "Alice Dedup" }))
            .Content.ReadFromJsonAsync<AlbumDetail>();
        await aliceClient.PostAsJsonAsync($"/api/albums/{aliceAlbum!.Id}/items",
            new { fileItemId = aliceFile.Id });

        // Bob's albums must be empty.
        var bobList = await bobClient.GetFromJsonAsync<AlbumSummary[]>("/api/albums");
        Assert.Empty(bobList!);
    }
}
