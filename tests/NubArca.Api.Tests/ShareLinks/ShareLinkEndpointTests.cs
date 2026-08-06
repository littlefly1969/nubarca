using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.ShareLinks;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.ShareLinks;

public sealed class ShareLinkEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public ShareLinkEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<FileItem> SeedFileAsAsync(Guid ownerId, string name, string mime, byte[] payload)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, null, name, mime, new MemoryStream(payload));
    }

    [Fact]
    public async Task Create_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync(
            $"/api/files/{Guid.NewGuid()}/share-links", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsync(
            $"/api/share-links/{Guid.NewGuid()}/revoke", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_For_Owned_File_Returns_201_With_Token_And_Url()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "x"u8.ToArray());

        var response = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ShareLinkCreatedResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.Id);
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
        Assert.Equal($"/s/{body.Token}", body.Url);
        Assert.Null(body.ExpiresAt);
        Assert.Null(body.MaxDownloads);
        Assert.Equal($"/api/share-links/{body.Id}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Create_Stores_Hash_Not_Raw_Token()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "x"u8.ToArray());

        var response = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ShareLinkCreatedResponse>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ShareLinks.AsNoTracking().SingleAsync();

        Assert.NotEqual(body!.Token, row.TokenHash);
        Assert.DoesNotContain(body.Token, row.TokenHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_For_Foreign_File_Returns_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFile = await SeedFileAsAsync(alice, "doc.txt", "text/plain", "x"u8.ToArray());

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var response = await bobClient.PostAsJsonAsync(
            $"/api/files/{aliceFile.Id}/share-links", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_For_Missing_File_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/files/{Guid.NewGuid()}/share-links", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_With_Past_ExpiresAt_Returns_400()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "x"u8.ToArray());

        var response = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links",
            new { expiresAt = DateTime.UtcNow.AddHours(-1) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_With_NonPositive_MaxDownloads_Returns_400()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "x"u8.ToArray());

        var zero = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { maxDownloads = 0 });
        var negative = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { maxDownloads = -3 });

        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
    }

    [Fact]
    public async Task Revoke_Owned_Link_Returns_204()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "x"u8.ToArray());
        var created = (await (await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { }))
            .Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;

        var response = await client.PostAsync(
            $"/api/share-links/{created.Id}/revoke", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_Foreign_Link_Returns_404()
    {
        var (alice, aliceClient) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceFile = await SeedFileAsAsync(alice, "doc.txt", "text/plain", "x"u8.ToArray());
        var aliceLink = (await (await aliceClient.PostAsJsonAsync(
            $"/api/files/{aliceFile.Id}/share-links", new { }))
            .Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var response = await bobClient.PostAsync(
            $"/api/share-links/{aliceLink.Id}/revoke", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_Missing_Link_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync(
            $"/api/share-links/{Guid.NewGuid()}/revoke", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Public_Get_With_Valid_Token_Downloads_Original_Bytes()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var payload = Encoding.UTF8.GetBytes("public-download-bytes");
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", payload);
        var created = (await (await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { }))
            .Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;

        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(created.Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var downloaded = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(payload, downloaded);
    }

    [Fact]
    public async Task Public_Get_With_Invalid_Token_Returns_404()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync("/s/this-is-definitely-not-a-real-token");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Public_Get_With_Revoked_Token_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "x"u8.ToArray());
        var created = (await (await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { }))
            .Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;
        await client.PostAsync($"/api/share-links/{created.Id}/revoke", content: null);

        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(created.Url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Public_Get_With_Expired_Token_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "x"u8.ToArray());
        var created = (await (await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links",
            new { expiresAt = DateTime.UtcNow.AddHours(1) }))
            .Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;

        // Move expiration into the past via direct DB update.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.ShareLinks.FirstAsync();
            row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(created.Url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Public_Get_Enforces_MaxDownloads_And_Increments_Counters()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "x"u8.ToArray());
        var created = (await (await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links",
            new { maxDownloads = 2 }))
            .Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;

        var anonymous = _factory.CreateClient();
        var first = await anonymous.GetAsync(created.Url);
        var second = await anonymous.GetAsync(created.Url);
        var third = await anonymous.GetAsync(created.Url);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, third.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ShareLinks.AsNoTracking().SingleAsync();
        Assert.Equal(2, row.DownloadCount);
        Assert.NotNull(row.LastAccessedAt);
    }

    [Fact]
    public async Task Create_And_Public_Response_Do_Not_Leak_Storage_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "no-leak-payload"u8.ToArray());

        var createResponse = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { });
        var created = (await createResponse.Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;

        var anonymous = _factory.CreateClient();
        var downloadResponse = await anonymous.GetAsync(created.Url);

        var forbidden = new[]
        {
            "BlobObjectId", "blobObjectId", "blob_object_id",
            "StorageKey", "storageKey", "storage_key",
            "OwnerUserId", "ownerUserId", "owner_user_id",
            "ParentFolderId", "parentFolderId", "parent_folder_id",
            "DeletedAt", "deletedAt", "deleted_at",
            "UpdatedAt", "updatedAt", "updated_at",
            "PasswordHash", "passwordHash", "password_hash",
            "TokenHash", "tokenHash", "token_hash",
            "FileItemId", "fileItemId", "file_item_id",
            "objects/",
        };

        foreach (var response in new[] { createResponse, downloadResponse })
        {
            var body = await response.Content.ReadAsStringAsync();
            var headers = string.Join("\n",
                response.Headers.Concat(response.Content.Headers)
                    .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));

            foreach (var needle in forbidden)
            {
                Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
                Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
            }
        }
    }

    // -- Listing share links for a single file -------------------------------

    private async Task<Guid> CreateShareLinkViaHttpAsync(HttpClient client, Guid fileId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/files/{fileId}/share-links", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ShareLinkCreatedResponse>();
        return created!.Id;
    }

    [Fact]
    public async Task List_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(
            $"/api/files/{Guid.NewGuid()}/share-links");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_For_Missing_File_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync(
            $"/api/files/{Guid.NewGuid()}/share-links");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_For_Foreign_File_Returns_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFile = await SeedFileAsAsync(alice, "doc.txt", "text/plain", "x"u8.ToArray());

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var response = await bobClient.GetAsync(
            $"/api/files/{aliceFile.Id}/share-links");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_For_SoftDeleted_File_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "x"u8.ToArray());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.FileItems.SingleAsync(f => f.Id == file.Id);
            row.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync(
            $"/api/files/{file.Id}/share-links");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_For_Owned_File_With_No_Links_Returns_Empty_Array()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "x"u8.ToArray());

        var response = await client.GetAsync(
            $"/api/files/{file.Id}/share-links");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summaries = await response.Content.ReadFromJsonAsync<ShareLinkSummary[]>();
        Assert.NotNull(summaries);
        Assert.Empty(summaries!);
    }

    [Fact]
    public async Task List_Returns_Items_Ordered_By_CreatedAt_Desc()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "x"u8.ToArray());

        var first = await CreateShareLinkViaHttpAsync(client, file.Id);
        var second = await CreateShareLinkViaHttpAsync(client, file.Id);
        var third = await CreateShareLinkViaHttpAsync(client, file.Id);

        // Stamp deterministic CreatedAt values so ordering is unambiguous on
        // fast machines where back-to-back inserts can collide on the clock.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var baseTime = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
            (await db.ShareLinks.SingleAsync(s => s.Id == first)).CreatedAt = baseTime;
            (await db.ShareLinks.SingleAsync(s => s.Id == second)).CreatedAt = baseTime.AddMinutes(1);
            (await db.ShareLinks.SingleAsync(s => s.Id == third)).CreatedAt = baseTime.AddMinutes(2);
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync(
            $"/api/files/{file.Id}/share-links");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summaries = (await response.Content.ReadFromJsonAsync<ShareLinkSummary[]>())!;

        Assert.Equal(3, summaries.Length);
        Assert.Equal(third, summaries[0].Id);
        Assert.Equal(second, summaries[1].Id);
        Assert.Equal(first, summaries[2].Id);
    }

    [Fact]
    public async Task List_Includes_Revoked_Link_With_IsRevoked_True()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "x"u8.ToArray());
        var id = await CreateShareLinkViaHttpAsync(client, file.Id);

        await client.PostAsync($"/api/share-links/{id}/revoke", content: null);

        var response = await client.GetAsync(
            $"/api/files/{file.Id}/share-links");
        var summaries = (await response.Content.ReadFromJsonAsync<ShareLinkSummary[]>())!;
        var summary = Assert.Single(summaries);

        Assert.True(summary.IsRevoked);
        Assert.NotNull(summary.RevokedAt);
        Assert.False(summary.IsExpired);
        Assert.False(summary.IsExhausted);
    }

    [Fact]
    public async Task List_Includes_Expired_Link_With_IsExpired_True()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "x"u8.ToArray());
        var id = await CreateShareLinkViaHttpAsync(client, file.Id);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.ShareLinks.SingleAsync(s => s.Id == id);
            row.ExpiresAt = DateTime.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync(
            $"/api/files/{file.Id}/share-links");
        var summaries = (await response.Content.ReadFromJsonAsync<ShareLinkSummary[]>())!;
        var summary = Assert.Single(summaries);

        Assert.True(summary.IsExpired);
        Assert.NotNull(summary.ExpiresAt);
        Assert.False(summary.IsRevoked);
        Assert.False(summary.IsExhausted);
    }

    [Fact]
    public async Task List_Includes_Exhausted_Link_With_IsExhausted_True()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "x"u8.ToArray());
        // Create with maxDownloads = 1 and exhaust it via the public path.
        var createResponse = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { maxDownloads = 1 });
        var created = (await createResponse.Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;

        var anonymous = _factory.CreateClient();
        var first = await anonymous.GetAsync(created.Url);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await anonymous.GetAsync(created.Url);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        var response = await client.GetAsync(
            $"/api/files/{file.Id}/share-links");
        var summaries = (await response.Content.ReadFromJsonAsync<ShareLinkSummary[]>())!;
        var summary = Assert.Single(summaries);

        Assert.True(summary.IsExhausted);
        Assert.Equal(1, summary.MaxDownloads);
        Assert.Equal(1, summary.DownloadCount);
        Assert.NotNull(summary.LastAccessedAt);
        Assert.False(summary.IsRevoked);
        Assert.False(summary.IsExpired);
    }

    [Fact]
    public async Task List_Response_Does_Not_Leak_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "x"u8.ToArray());
        var createResponse = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { });
        var created = (await createResponse.Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;

        var listResponse = await client.GetAsync(
            $"/api/files/{file.Id}/share-links");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var body = await listResponse.Content.ReadAsStringAsync();
        var headers = string.Join("\n",
            listResponse.Headers.Concat(listResponse.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));

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

        // The raw token is recoverable ONLY at creation time. The listing must
        // not echo it back even though we just created the link in this test.
        Assert.DoesNotContain(created.Token, body, StringComparison.Ordinal);
    }
}
