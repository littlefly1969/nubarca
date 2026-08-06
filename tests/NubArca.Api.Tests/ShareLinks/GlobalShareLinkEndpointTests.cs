using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.ShareLinks;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.ShareLinks;

// Slice 51 — global share-link management endpoint GET /api/share-links.
public sealed class GlobalShareLinkEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public GlobalShareLinkEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<FileItem> SeedFileAsAsync(
        Guid ownerId, string name, Guid? parentFolderId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(
            ownerId, parentFolderId, name, "text/plain", new MemoryStream("x"u8.ToArray()));
    }

    private async Task<Guid> SeedFolderAsAsync(Guid ownerId, string name, Guid? parentFolderId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
        var folder = await folders.CreateAsync(ownerId, parentFolderId, name);
        return folder.Id;
    }

    private static async Task<Guid> CreateLinkAsync(
        HttpClient client, Guid fileId, object? body = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/files/{fileId}/share-links", body ?? new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;
        return created.Id;
    }

    [Fact]
    public async Task List_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync("/api/share-links");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_With_No_Links_Returns_Empty_Envelope()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/share-links");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ShareLinkListResponse>())!;
        Assert.Empty(body.Items);
        Assert.Equal(0, body.Total);
        Assert.Equal(50, body.Limit);
        Assert.Equal(0, body.Offset);
    }

    [Fact]
    public async Task List_Returns_Only_Callers_Own_Links()
    {
        var (alice, aliceClient) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceFile = await SeedFileAsAsync(alice, "alice.txt");
        await CreateLinkAsync(aliceClient, aliceFile.Id);
        await CreateLinkAsync(aliceClient, aliceFile.Id);

        var (bob, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var bobFile = await SeedFileAsAsync(bob, "bob.txt");
        var bobLink = await CreateLinkAsync(bobClient, bobFile.Id);

        var bobResponse = await bobClient.GetAsync("/api/share-links");
        var bobBody = (await bobResponse.Content.ReadFromJsonAsync<ShareLinkListResponse>())!;

        Assert.Equal(1, bobBody.Total);
        var only = Assert.Single(bobBody.Items);
        Assert.Equal(bobLink, only.Id);
        Assert.Equal("bob.txt", only.FileName);

        // Alice still sees exactly her two — proves owner-scoping both ways.
        var aliceResponse = await aliceClient.GetAsync("/api/share-links");
        var aliceBody = (await aliceResponse.Content.ReadFromJsonAsync<ShareLinkListResponse>())!;
        Assert.Equal(2, aliceBody.Total);
        Assert.All(aliceBody.Items, i => Assert.Equal("alice.txt", i.FileName));
    }

    [Fact]
    public async Task List_Includes_FileName_And_FolderPath()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var photos = await SeedFolderAsAsync(owner, "Photos");
        var holidays = await SeedFolderAsAsync(owner, "Holidays", photos);

        var rootFile = await SeedFileAsAsync(owner, "root.txt");
        var nestedFile = await SeedFileAsAsync(owner, "img.txt", holidays);

        await CreateLinkAsync(client, rootFile.Id);
        await CreateLinkAsync(client, nestedFile.Id);

        var response = await client.GetAsync("/api/share-links");
        var body = (await response.Content.ReadFromJsonAsync<ShareLinkListResponse>())!;

        var root = Assert.Single(body.Items, i => i.FileName == "root.txt");
        Assert.Equal("/", root.FolderPath);

        var nested = Assert.Single(body.Items, i => i.FileName == "img.txt");
        Assert.Equal("/Photos/Holidays", nested.FolderPath);
    }

    [Fact]
    public async Task List_Paginates_With_Limit_And_Offset()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt");
        for (var i = 0; i < 5; i++)
        {
            await CreateLinkAsync(client, file.Id);
        }

        var firstPage = (await (await client.GetAsync("/api/share-links?limit=2&offset=0"))
            .Content.ReadFromJsonAsync<ShareLinkListResponse>())!;
        Assert.Equal(5, firstPage.Total);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(2, firstPage.Limit);
        Assert.Equal(0, firstPage.Offset);

        var lastPage = (await (await client.GetAsync("/api/share-links?limit=2&offset=4"))
            .Content.ReadFromJsonAsync<ShareLinkListResponse>())!;
        Assert.Equal(5, lastPage.Total);
        Assert.Single(lastPage.Items);
        Assert.Equal(4, lastPage.Offset);

        // Pages must not overlap — the union of ids across the three pages is
        // all five distinct links.
        var midPage = (await (await client.GetAsync("/api/share-links?limit=2&offset=2"))
            .Content.ReadFromJsonAsync<ShareLinkListResponse>())!;
        var ids = firstPage.Items.Concat(midPage.Items).Concat(lastPage.Items)
            .Select(i => i.Id)
            .ToList();
        Assert.Equal(5, ids.Distinct().Count());
    }

    [Fact]
    public async Task List_Orders_By_CreatedAt_Desc()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt");
        var first = await CreateLinkAsync(client, file.Id);
        var second = await CreateLinkAsync(client, file.Id);
        var third = await CreateLinkAsync(client, file.Id);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var baseTime = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
            (await db.ShareLinks.SingleAsync(s => s.Id == first)).CreatedAt = baseTime;
            (await db.ShareLinks.SingleAsync(s => s.Id == second)).CreatedAt = baseTime.AddMinutes(1);
            (await db.ShareLinks.SingleAsync(s => s.Id == third)).CreatedAt = baseTime.AddMinutes(2);
            await db.SaveChangesAsync();
        }

        var body = (await (await client.GetAsync("/api/share-links"))
            .Content.ReadFromJsonAsync<ShareLinkListResponse>())!;

        Assert.Equal(new[] { third, second, first }, body.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task List_Filters_By_Status_Revoked()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt");
        var active = await CreateLinkAsync(client, file.Id);
        var revoked = await CreateLinkAsync(client, file.Id);
        await client.PostAsync($"/api/share-links/{revoked}/revoke", content: null);

        var body = (await (await client.GetAsync("/api/share-links?status=revoked"))
            .Content.ReadFromJsonAsync<ShareLinkListResponse>())!;

        var only = Assert.Single(body.Items);
        Assert.Equal(revoked, only.Id);
        Assert.True(only.IsRevoked);
        Assert.Equal(1, body.Total);
        Assert.DoesNotContain(body.Items, i => i.Id == active);
    }

    [Fact]
    public async Task List_Filters_By_Status_Active()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt");
        var active = await CreateLinkAsync(client, file.Id);
        var revoked = await CreateLinkAsync(client, file.Id);
        await client.PostAsync($"/api/share-links/{revoked}/revoke", content: null);

        var body = (await (await client.GetAsync("/api/share-links?status=active"))
            .Content.ReadFromJsonAsync<ShareLinkListResponse>())!;

        var only = Assert.Single(body.Items);
        Assert.Equal(active, only.Id);
        Assert.False(only.IsRevoked);
        Assert.False(only.IsExpired);
        Assert.False(only.IsExhausted);
    }

    [Fact]
    public async Task List_Filters_By_Status_Expired()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt");
        var active = await CreateLinkAsync(client, file.Id);
        var expired = await CreateLinkAsync(
            client, file.Id, new { expiresAt = DateTime.UtcNow.AddHours(1) });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.ShareLinks.SingleAsync(s => s.Id == expired);
            row.ExpiresAt = DateTime.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        var body = (await (await client.GetAsync("/api/share-links?status=expired"))
            .Content.ReadFromJsonAsync<ShareLinkListResponse>())!;

        var only = Assert.Single(body.Items);
        Assert.Equal(expired, only.Id);
        Assert.True(only.IsExpired);
        Assert.DoesNotContain(body.Items, i => i.Id == active);
    }

    [Fact]
    public async Task List_With_Invalid_Status_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/share-links?status=bogus");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_Includes_Links_For_SoftDeleted_Files()
    {
        // A file can be soft-deleted while its share links still exist (until
        // the sweeper purges them). The global listing must still surface them
        // so the owner can revoke a link to a trashed file.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt");
        var link = await CreateLinkAsync(client, file.Id);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.FileItems.SingleAsync(f => f.Id == file.Id);
            row.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var body = (await (await client.GetAsync("/api/share-links"))
            .Content.ReadFromJsonAsync<ShareLinkListResponse>())!;

        var only = Assert.Single(body.Items);
        Assert.Equal(link, only.Id);
        Assert.Equal("doc.txt", only.FileName);
    }

    [Fact]
    public async Task List_Response_Does_Not_Leak_Internals_Or_Raw_Token()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { });
        var created = (await createResponse.Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;

        var listResponse = await client.GetAsync("/api/share-links");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var body = await listResponse.Content.ReadAsStringAsync();
        var headers = string.Join("\n",
            listResponse.Headers.Concat(listResponse.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));

        // FileName + FolderPath ARE intentionally exposed (owner's own data),
        // so they are not in the forbidden set. Everything storage-internal is.
        var forbidden = new[]
        {
            "BlobObjectId", "blobObjectId", "blob_object_id",
            "StorageKey", "storageKey", "storage_key",
            "OwnerUserId", "ownerUserId", "owner_user_id",
            "ParentFolderId", "parentFolderId", "parent_folder_id",
            "PasswordHash", "passwordHash", "password_hash",
            "TokenHash", "tokenHash", "token_hash",
            "FileItemId", "fileItemId", "file_item_id",
            "objects/",
        };
        foreach (var needle in forbidden)
        {
            Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
        }

        // The raw token is recoverable ONLY at creation time.
        Assert.DoesNotContain(created.Token, body, StringComparison.Ordinal);
    }
}
