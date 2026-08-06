using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 59 — medium preview endpoint for the gallery lightbox.
// The endpoint generates and persists a second FileThumbnail row
// (Size = "medium") on demand, then opens it. Behaviour mirrors the small
// thumbnail endpoint: owner-scoped, soft-delete-aware, safe content type
// (JPEG via FileThumbnailService), and a private Cache-Control header so
// the lightbox can re-open without a round-trip.
public sealed class FilePreviewEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FilePreviewEndpointTests()
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

    [Fact]
    public async Task Preview_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(
            $"/api/files/{Guid.NewGuid()}/preview");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Preview_Missing_File_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/files/{Guid.NewGuid()}/preview");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Preview_Foreign_File_Returns_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFile = await UploadAsync(
            alice, ImageFixtures.JpegWithExif(), "a.jpg", "image/jpeg");

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var response = await bobClient.GetAsync($"/api/files/{aliceFile.Id}/preview");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Preview_NonImage_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, "plain notes"u8.ToArray(), "notes.txt", "text/plain");

        var response = await client.GetAsync($"/api/files/{file.Id}/preview");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Preview_Image_Generates_Medium_FileThumbnail_And_Serves_Jpeg_With_Cache_Header()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(), "photo.jpg", "image/jpeg");

        var response = await client.GetAsync($"/api/files/{file.Id}/preview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FileThumbnailService.ThumbnailMimeType,
            response.Content.Headers.ContentType?.ToString());

        // Private cache header for repeat lightbox opens.
        Assert.Contains("private", response.Headers.CacheControl?.ToString() ?? "");

        // nosniff is set globally by slice 54.2's middleware on every response.
        Assert.Contains("nosniff",
            string.Join(",", response.Headers.GetValues("X-Content-Type-Options")));

        // The first call persisted a medium FileThumbnail row.
        var hasMedium = await InDbAsync(db => db.FileThumbnails.AsNoTracking()
            .AnyAsync(t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Medium));
        Assert.True(hasMedium);
    }

    [Fact]
    public async Task Preview_Second_Call_Reuses_Persisted_FileThumbnail()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(), "photo.jpg", "image/jpeg");

        // First call generates the row.
        var first = await client.GetAsync($"/api/files/{file.Id}/preview");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstRowCount = await InDbAsync(db => db.FileThumbnails.AsNoTracking()
            .CountAsync(t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Medium));

        // Second call must not insert a duplicate row.
        var second = await client.GetAsync($"/api/files/{file.Id}/preview");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondRowCount = await InDbAsync(db => db.FileThumbnails.AsNoTracking()
            .CountAsync(t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Medium));

        Assert.Equal(1, firstRowCount);
        Assert.Equal(1, secondRowCount);
    }

    [Fact]
    public async Task Preview_Soft_Deleted_File_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(), "photo.jpg", "image/jpeg");

        var del = await client.DeleteAsync($"/api/files/{file.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var response = await client.GetAsync($"/api/files/{file.Id}/preview");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Preview_Response_Does_Not_Leak_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(includeGps: true), "leak.jpg", "image/jpeg");

        var response = await client.GetAsync($"/api/files/{file.Id}/preview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var headers = string.Join("\n",
            response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));

        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
        }
        // Literal sensitive strings from the fixture must not surface either.
        Assert.DoesNotContain(ImageFixtures.BodySerial, headers, StringComparison.Ordinal);
        Assert.DoesNotContain(ImageFixtures.LensSerial, headers, StringComparison.Ordinal);
        Assert.DoesNotContain(ImageFixtures.Software, headers, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Thumbnail_Endpoint_Adds_Private_Cache_Header_Too()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, ImageFixtures.JpegWithExif(), "photo.jpg", "image/jpeg");

        var response = await client.GetAsync($"/api/files/{file.Id}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("private", response.Headers.CacheControl?.ToString() ?? "");
    }
}
