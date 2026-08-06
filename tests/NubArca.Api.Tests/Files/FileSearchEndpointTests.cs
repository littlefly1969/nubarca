using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Files;

// End-to-end HTTP tests for GET /api/search?q=...
public sealed class FileSearchEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FileSearchEndpointTests()
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
    public async Task Search_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync("/api/search?q=anything");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Search_With_Missing_Query_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/search");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_With_Whitespace_Query_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/search?q=%20%20");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_Finds_Files_By_Name()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedFileAsAsync(owner, "Report-2026.pdf", "application/pdf", "a"u8.ToArray());
        await SeedFileAsAsync(owner, "vacation.jpg", "image/jpeg", "b"u8.ToArray());

        var response = await client.GetAsync("/api/search?q=report");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<FileSummary>>();
        Assert.NotNull(results);
        Assert.Single(results!);
        Assert.Equal("Report-2026.pdf", results![0].Name);
    }

    [Fact]
    public async Task Search_Finds_Files_By_MimeType()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedFileAsAsync(owner, "alpha.jpg", "image/jpeg", "a"u8.ToArray());
        await SeedFileAsAsync(owner, "beta.jpg", "image/jpeg", "b"u8.ToArray());
        await SeedFileAsAsync(owner, "notes.txt", "text/plain", "c"u8.ToArray());

        var response = await client.GetAsync("/api/search?q=jpeg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<FileSummary>>();
        Assert.NotNull(results);
        Assert.Equal(new[] { "alpha.jpg", "beta.jpg" }, results!.Select(r => r.Name).ToArray());
    }

    [Fact]
    public async Task Search_Does_Not_Return_Foreign_Owned_Files()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        await SeedFileAsAsync(alice, "match-alice.txt", "text/plain", "a"u8.ToArray());

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var response = await bobClient.GetAsync("/api/search?q=match");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<FileSummary>>();
        Assert.NotNull(results);
        Assert.Empty(results!);
    }

    [Fact]
    public async Task Search_Orders_By_Name_Deterministically()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedFileAsAsync(owner, "zeta.txt", "text/plain", "z"u8.ToArray());
        await SeedFileAsAsync(owner, "alpha.txt", "text/plain", "a"u8.ToArray());
        await SeedFileAsAsync(owner, "mike.txt", "text/plain", "m"u8.ToArray());

        var response = await client.GetAsync("/api/search?q=txt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<FileSummary>>();
        Assert.NotNull(results);
        Assert.Equal(new[] { "alpha.txt", "mike.txt", "zeta.txt" }, results!.Select(r => r.Name).ToArray());
    }

    [Fact]
    public async Task Search_Response_Does_Not_Leak_Storage_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedFileAsAsync(owner, "report.txt", "text/plain", "no-leak"u8.ToArray());

        var response = await client.GetAsync("/api/search?q=report");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var headers = string.Join("\n",
            response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));

        var forbidden = new[]
        {
            "BlobObjectId", "blobObjectId", "blob_object_id",
            "StorageKey", "storageKey", "storage_key",
            "OwnerUserId", "ownerUserId", "owner_user_id",
            "ParentFolderId", "parentFolderId", "parent_folder_id",
            "DeletedAt", "deletedAt", "deleted_at",
            "UpdatedAt", "updatedAt", "updated_at",
            "PasswordHash", "passwordHash", "password_hash",
            "objects/",
        };

        foreach (var needle in forbidden)
        {
            Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
        }
    }
}
