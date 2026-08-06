using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Files;

// End-to-end HTTP tests for GET /api/files/{id}/thumbnail.
public sealed class FileThumbnailEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FileThumbnailEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static byte[] CreatePngBytes(int width, int height)
    {
        using var img = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static MultipartFormDataContent MultipartWithFile(
        byte[] payload, string filename, string contentType)
    {
        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(payload);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        multipart.Add(part, "file", filename);
        return multipart;
    }

    private async Task<Guid> UploadPngAsync(HttpClient client, string name = "pic.png", int w = 400, int h = 300)
    {
        var response = await client.PostAsync("/api/files",
            MultipartWithFile(CreatePngBytes(w, h), name, "image/png"));
        response.EnsureSuccessStatusCode();
        var summary = await response.Content.ReadFromJsonAsync<FileSummary>();
        return summary!.Id;
    }

    [Fact]
    public async Task Get_Thumbnail_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/files/{Guid.NewGuid()}/thumbnail?size=small");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_Thumbnail_As_Owner_Returns_200_With_Image_Bytes()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client);

        var response = await client.GetAsync($"/api/files/{fileId}/thumbnail?size=small");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);
        using var ms = new MemoryStream(bytes);
        var info = await Image.IdentifyAsync(ms);
        Assert.NotNull(info);
        Assert.InRange(info!.Width, 1, ThumbnailSizes.GetEdge(ThumbnailSizes.Small));
        Assert.InRange(info.Height, 1, ThumbnailSizes.GetEdge(ThumbnailSizes.Small));
    }

    [Fact]
    public async Task Get_Thumbnail_Defaults_To_Small_When_Size_Omitted()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client);

        var response = await client.GetAsync($"/api/files/{fileId}/thumbnail");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Get_Thumbnail_For_Foreign_File_Returns_404()
    {
        var (_, aliceClient) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var fileId = await UploadPngAsync(aliceClient);

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var response = await bobClient.GetAsync($"/api/files/{fileId}/thumbnail?size=small");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Thumbnail_For_SoftDeleted_File_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client);

        var delete = await client.DeleteAsync($"/api/files/{fileId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var response = await client.GetAsync($"/api/files/{fileId}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Thumbnail_For_NonImage_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var upload = await client.PostAsync("/api/files",
            MultipartWithFile("hello"u8.ToArray(), "notes.txt", "text/plain"));
        upload.EnsureSuccessStatusCode();
        var summary = await upload.Content.ReadFromJsonAsync<FileSummary>();

        var response = await client.GetAsync($"/api/files/{summary!.Id}/thumbnail?size=small");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Thumbnail_For_Missing_File_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/files/{Guid.NewGuid()}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Thumbnail_With_Unknown_Size_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client);

        var response = await client.GetAsync($"/api/files/{fileId}/thumbnail?size=huge");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_Thumbnail_Response_Has_No_Storage_Internals_Leak()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadPngAsync(client);

        var response = await client.GetAsync($"/api/files/{fileId}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var allHeaders = string.Join('\n',
            response.Headers.Concat(response.Content.Headers)
                .Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));

        string[] forbiddenSubstrings =
        {
            "StorageKey", "storageKey", "storage_key",
            "BlobObjectId", "blobObjectId", "blob_object_id",
            "OwnerUserId", "ownerUserId", "owner_user_id",
            "ParentFolderId", "parentFolderId",
            "DeletedAt", "deletedAt",
            "PasswordHash", "passwordHash",
            "objects/",
        };
        foreach (var needle in forbiddenSubstrings)
        {
            Assert.DoesNotContain(needle, allHeaders);
        }

        // Body is JPEG bytes, not JSON. Still scan the binary as ASCII to make
        // sure no path / identifier accidentally got embedded.
        var raw = await response.Content.ReadAsByteArrayAsync();
        var bodyAscii = System.Text.Encoding.ASCII.GetString(raw);
        foreach (var needle in forbiddenSubstrings)
        {
            Assert.DoesNotContain(needle, bodyAscii);
        }
    }

    [Fact]
    public async Task Upload_Image_Does_Not_Block_Upload_When_Thumbnail_Disabled_Scenario()
    {
        // Regression guard: a successful upload of an image must always return
        // 201 even if no thumbnail row got created. We don't have a real failure
        // path to inject here, so we just verify upload succeeds and the file
        // is fetchable independently of the thumbnail row.
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var upload = await client.PostAsync("/api/files",
            MultipartWithFile(CreatePngBytes(50, 50), "smol.png", "image/png"));

        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var summary = await upload.Content.ReadFromJsonAsync<FileSummary>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.FileItems.AnyAsync(f => f.Id == summary!.Id));
    }
}
