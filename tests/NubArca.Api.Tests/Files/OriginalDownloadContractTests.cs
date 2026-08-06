using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Regression tests documenting the contract the frontend's "Download original"
// action depends on: GET /api/files/{id}/content streams the IMMUTABLE ORIGINAL
// blob, byte-for-byte, under the original file name — never a derivative.
//
// The frontend has exactly one place that builds this URL
// (api-client `originalDownloadUrl`), and these tests pin what that URL must
// return. FileContentEndpointTests already covers auth, ownership, 404 and
// no-leak; this file covers only "it is the original, not a derived artifact".
public sealed class OriginalDownloadContractTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public OriginalDownloadContractTests()
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

    [Fact]
    public async Task Content_Returns_The_Original_Bytes_Verbatim()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var original = ImageFixtures.JpegWithExif(includeGps: true);
        var file = await UploadAsync(owner, original, "IMG_1248.JPG", "image/jpeg");

        var response = await client.GetAsync($"/api/files/{file.Id}/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();

        // Byte-for-byte identity is the whole point: a re-encode, a resize or a
        // metadata rewrite would all change these bytes.
        Assert.Equal(original, body);
        Assert.Equal(original.Length, body.Length);
    }

    [Fact]
    public async Task Content_Preserves_Embedded_Metadata_So_It_Is_Not_The_Stripped_Copy()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var original = ImageFixtures.JpegWithExif(includeGps: true);
        var file = await UploadAsync(owner, original, "IMG_1248.JPG", "image/jpeg");

        var originalResponse = await client.GetAsync($"/api/files/{file.Id}/content");
        var strippedResponse = await client.GetAsync($"/api/files/{file.Id}/content/privacy-safe");

        Assert.Equal(HttpStatusCode.OK, originalResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, strippedResponse.StatusCode);

        var originalBody = await originalResponse.Content.ReadAsByteArrayAsync();
        var strippedBody = await strippedResponse.Content.ReadAsByteArrayAsync();

        // The two endpoints must not be confusable: /content keeps the EXIF the
        // camera wrote, /content/privacy-safe re-encodes it away.
        Assert.NotEqual(originalBody, strippedBody);
        Assert.Equal(original, originalBody);
    }

    [Fact]
    public async Task Content_Carries_The_Original_File_Name_As_An_Attachment()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "Holiday Photo.JPG", "image/jpeg");

        var response = await client.GetAsync($"/api/files/{file.Id}/content");

        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.Equal("Holiday Photo.JPG", disposition.FileName?.Trim('"'));
    }

    [Fact]
    public async Task Content_Is_Not_The_Thumbnail_Or_The_Preview()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var original = ImageFixtures.JpegWithExif();
        var file = await UploadAsync(owner, original, "IMG_1248.JPG", "image/jpeg");

        // Materialize the derivatives first, so any confusion between the
        // original and a cached derived artifact would show up here.
        var thumbnail = await client.GetAsync($"/api/files/{file.Id}/thumbnail?size=small");
        var preview = await client.GetAsync($"/api/files/{file.Id}/preview");
        var content = await client.GetAsync($"/api/files/{file.Id}/content");

        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        var contentBody = await content.Content.ReadAsByteArrayAsync();

        Assert.Equal(original, contentBody);

        if (thumbnail.StatusCode == HttpStatusCode.OK)
        {
            Assert.NotEqual(await thumbnail.Content.ReadAsByteArrayAsync(), contentBody);
        }

        if (preview.StatusCode == HttpStatusCode.OK)
        {
            Assert.NotEqual(await preview.Content.ReadAsByteArrayAsync(), contentBody);
        }
    }

    [Fact]
    public async Task Repeated_Downloads_Return_Identical_Immutable_Bytes()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var original = ImageFixtures.JpegWithExif();
        var file = await UploadAsync(owner, original, "IMG_1248.JPG", "image/jpeg");

        var first = await (await client.GetAsync($"/api/files/{file.Id}/content")).Content.ReadAsByteArrayAsync();
        var second = await (await client.GetAsync($"/api/files/{file.Id}/content")).Content.ReadAsByteArrayAsync();

        Assert.Equal(original, first);
        Assert.Equal(first, second);
    }
}
