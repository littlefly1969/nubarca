using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Files;

// End-to-end HTTP tests for GET /api/files/{id}/content.
// Uses the shared SqliteWebApplicationFactory + cookie auth (no Docker).
public sealed class FileContentEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FileContentEndpointTests()
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

    private async Task SoftDeleteFileAsync(Guid fileId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tracked = await db.FileItems.FirstAsync(f => f.Id == fileId);
        tracked.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Get_Content_Returns_Body_And_Headers_For_Owner()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var payload = Encoding.UTF8.GetBytes("hello-endpoint");
        var file = await SeedFileAsAsync(owner, "report.txt", "text/plain", payload);

        var response = await client.GetAsync($"/api/files/{file.Id}/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Slice 54.2: the untrusted client MIME ("text/plain") is NOT served as
        // authoritative — a non-image download is application/octet-stream with
        // nosniff, so spoofed text/html etc. can never be browser-rendered.
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("report.txt", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(payload, body);
    }

    [Fact]
    public async Task Get_Content_Without_Auth_Returns_401()
    {
        var owner = await _factory.SeedUserAsync();
        var file = await SeedFileAsAsync(owner, "x.txt", "text/plain", "x"u8.ToArray());

        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/files/{file.Id}/content");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_Content_For_Foreign_Owner_Returns_404_Not_403()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFile = await SeedFileAsAsync(alice, "secret.txt", "text/plain", "alice-only"u8.ToArray());

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var response = await bobClient.GetAsync($"/api/files/{aliceFile.Id}/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Content_For_Soft_Deleted_File_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "x.txt", "text/plain", "x"u8.ToArray());
        await SoftDeleteFileAsync(file.Id);

        var response = await client.GetAsync($"/api/files/{file.Id}/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Content_For_Missing_Id_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/files/{Guid.NewGuid()}/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Content_Does_Not_Leak_StorageKey_In_Headers_Or_Body()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(owner, "doc.txt", "text/plain", "no-leak-please"u8.ToArray());

        var response = await client.GetAsync($"/api/files/{file.Id}/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var allHeaderValues = string.Join("\n",
            response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));

        Assert.DoesNotContain("objects/", allHeaderValues, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StorageKey", allHeaderValues, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage_key", allHeaderValues, StringComparison.OrdinalIgnoreCase);

        var bodyText = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("objects/", bodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StorageKey", bodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage_key", bodyText, StringComparison.OrdinalIgnoreCase);
    }
}
