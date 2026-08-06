using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Security;

// Slice 54.2 — downloads must not serve untrusted client MIME as authoritative,
// gallery classification must rely on server-detected content, and nosniff is
// always present.
public sealed class MimeHardeningTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public MimeHardeningTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static byte[] RealPng(int w = 16, int h = 16)
    {
        using var img = new Image<Rgba32>(w, h);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static MultipartFormDataContent Multipart(byte[] bytes, string name, string contentType)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { part, "file", name } };
    }

    private async Task<Guid> UploadAsync(HttpClient client, byte[] bytes, string name, string contentType)
    {
        var response = await client.PostAsync("/api/files", Multipart(bytes, name, contentType));
        response.EnsureSuccessStatusCode();
        var summary = await response.Content.ReadFromJsonAsync<FileSummary>();
        return summary!.Id;
    }

    [Fact]
    public async Task Html_Uploaded_As_TextHtml_Downloads_As_OctetStream_With_NoSniff()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var html = "<html><body><script>alert(1)</script></body></html>"u8.ToArray();
        var id = await UploadAsync(client, html, "page.html", "text/html");

        var response = await client.GetAsync($"/api/files/{id}/content");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The dangerous client MIME is never echoed back.
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        // Still served as an attachment, not inline.
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task Spoofed_Image_Is_Not_Listed_In_Gallery()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        // Plain text bytes uploaded with an image content type.
        await UploadAsync(client, "not an image"u8.ToArray(), "fake.png", "image/png");

        var gallery = await client.GetFromJsonAsync<ImageListResponse>("/api/images");
        Assert.NotNull(gallery);
        Assert.Empty(gallery!.Items);
    }

    [Fact]
    public async Task Real_Image_Appears_In_Gallery_And_Content_Serves_Detected_Image_Type()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadAsync(client, RealPng(), "real.png", "image/png");

        var gallery = await client.GetFromJsonAsync<ImageListResponse>("/api/images");
        Assert.NotNull(gallery);
        Assert.Contains(gallery!.Items, i => i.Id == id);

        // Detected image type is trusted, so the lightbox <img> renders.
        var content = await client.GetAsync($"/api/files/{id}/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("image/png", content.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", content.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Real_Image_Thumbnail_Still_Served_As_Jpeg()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadAsync(client, RealPng(64, 64), "thumb.png", "image/png");

        var thumb = await client.GetAsync($"/api/files/{id}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.Equal("image/jpeg", thumb.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", thumb.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Real_Image_Metadata_Extraction_Still_Completes()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadAsync(client, RealPng(), "meta.png", "image/png");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == id);
        var blobMeta = await db.BlobMetadata.AsNoTracking()
            .SingleAsync(m => m.BlobObjectId == file.BlobObjectId);

        Assert.Equal(MetadataStatuses.Completed, blobMeta.ExtractionStatus);
        Assert.Equal("image/png", blobMeta.DetectedContentType);
    }

    [Fact]
    public async Task Spoofed_Image_Still_Downloads_As_OctetStream()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadAsync(client, "<script>x</script>"u8.ToArray(), "evil.png", "image/png");

        var response = await client.GetAsync($"/api/files/{id}/content");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Not detected as a real image → octet-stream, never image/png.
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }
}
