using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 66 — GET /api/files/{id}/content/privacy-safe. Streams a stripped
// copy without mutating the FileItem or creating a new blob.
public sealed class PrivacySafeDownloadEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public PrivacySafeDownloadEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<FileItem> UploadAsync(Guid ownerId, byte[] bytes, string name, string mime)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, null, name, mime, new MemoryStream(bytes));
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await work(db);
    }

    private const string Url = "/content/privacy-safe";

    [Fact]
    public async Task PrivacySafe_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var resp = await anonymous.GetAsync($"/api/files/{Guid.NewGuid()}{Url}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task PrivacySafe_Missing_File_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var resp = await client.GetAsync($"/api/files/{Guid.NewGuid()}{Url}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PrivacySafe_Foreign_File_Returns_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFile = await UploadAsync(alice, ImageFixtures.JpegWithExif(), "a.jpg", "image/jpeg");
        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var resp = await bobClient.GetAsync($"/api/files/{aliceFile.Id}{Url}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PrivacySafe_NonImage_Returns_415()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, "plain text"u8.ToArray(), "n.txt", "text/plain");

        var resp = await client.GetAsync($"/api/files/{file.Id}{Url}");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode);
    }

    [Fact]
    public async Task PrivacySafe_Strips_Embedded_Markers_From_Returned_Bytes()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "geo.jpg", "image/jpeg");

        var resp = await client.GetAsync($"/api/files/{file.Id}{Url}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var ascii = Encoding.ASCII.GetString(bytes);
        Assert.DoesNotContain(ImageFixtures.BodySerial, ascii, StringComparison.Ordinal);
        Assert.DoesNotContain(ImageFixtures.LensSerial, ascii, StringComparison.Ordinal);
        Assert.DoesNotContain(ImageFixtures.CameraMake, ascii, StringComparison.Ordinal);
        // Still a JPEG (starts with SOI marker FF D8).
        Assert.True(bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8);
    }

    [Fact]
    public async Task PrivacySafe_Does_Not_Mutate_FileItem_Or_Create_New_Blob()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");
        var oldBlobId = file.BlobObjectId;
        var blobCountBefore = await InDbAsync(db => db.BlobObjects.AsNoTracking().CountAsync());

        var resp = await client.GetAsync($"/api/files/{file.Id}{Url}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var fileNow = await InDbAsync(db => db.FileItems.AsNoTracking().SingleAsync(f => f.Id == file.Id));
        var blobCountAfter = await InDbAsync(db => db.BlobObjects.AsNoTracking().CountAsync());
        Assert.Equal(oldBlobId, fileNow.BlobObjectId);   // FileItem unchanged
        Assert.Equal(blobCountBefore, blobCountAfter);   // no new blob created
    }

    [Fact]
    public async Task PrivacySafe_Writes_Audit_Row()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");

        var resp = await client.GetAsync($"/api/files/{file.Id}{Url}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var count = await InDbAsync(db => db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.Action == "file.download_privacy_safe" && a.EntityId == file.Id));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PrivacySafe_Response_Headers_Do_Not_Leak_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");

        var resp = await client.GetAsync($"/api/files/{file.Id}{Url}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var headers = string.Join("\n",
            resp.Headers.Concat(resp.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));
        foreach (var needle in new[] { "StorageKey", "storageKey", "objects/", "BlobObjectId", "blobObjectId" })
        {
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
        }
    }
}
