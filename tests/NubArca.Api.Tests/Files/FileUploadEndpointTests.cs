using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Files;

// End-to-end HTTP tests for POST /api/files and POST /api/folders/{id}/files.
public sealed class FileUploadEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FileUploadEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Folder> SeedFolderAsAsync(Guid ownerId, Guid? parentId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
        return await folders.CreateAsync(ownerId, parentId, name);
    }

    private static MultipartFormDataContent MultipartWithFile(
        byte[] payload, string filename, string contentType, string partName = "file")
    {
        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(payload);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        multipart.Add(part, partName, filename);
        return multipart;
    }

    [Fact]
    public async Task Post_Root_File_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsync("/api/files",
            MultipartWithFile("x"u8.ToArray(), "x.txt", "text/plain"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_Root_File_With_Non_Multipart_Body_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/files", new { name = "x.txt" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_Root_File_With_Missing_File_Part_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("oops"), "wrong-part-name");

        var response = await client.PostAsync("/api/files", multipart);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_Root_File_Returns_201_With_FileSummary_And_Location()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var payload = Encoding.UTF8.GetBytes("hello-upload");

        var response = await client.PostAsync("/api/files",
            MultipartWithFile(payload, "hello.txt", "text/plain"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<FileSummary>();
        Assert.NotNull(summary);
        Assert.NotEqual(Guid.Empty, summary!.Id);
        Assert.Equal("hello.txt", summary.Name);
        Assert.Equal("text/plain", summary.MimeType);
        Assert.Equal(payload.LongLength, summary.SizeBytes);

        Assert.Equal($"/api/files/{summary.Id}/content", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Post_Child_File_Returns_201_With_FileSummary_And_Location()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, null, "Photos");
        var payload = Encoding.UTF8.GetBytes("inside-folder");

        var response = await client.PostAsync($"/api/folders/{folder.Id}/files",
            MultipartWithFile(payload, "inside.txt", "text/plain"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<FileSummary>();
        Assert.NotNull(summary);
        Assert.Equal("inside.txt", summary!.Name);
        Assert.Equal($"/api/files/{summary.Id}/content", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Upload_And_Then_Get_Content_Round_Trips_Bytes()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var payload = Encoding.UTF8.GetBytes("round-trip-bytes-12345");

        var uploadResponse = await client.PostAsync("/api/files",
            MultipartWithFile(payload, "round.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);
        var summary = await uploadResponse.Content.ReadFromJsonAsync<FileSummary>();
        Assert.NotNull(summary);

        var getResponse = await client.GetAsync($"/api/files/{summary!.Id}/content");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var downloaded = await getResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(payload, downloaded);
    }

    [Fact]
    public async Task Post_Duplicate_File_Name_In_Same_Folder_Returns_409()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var first = await client.PostAsync("/api/files",
            MultipartWithFile("v1"u8.ToArray(), "report.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsync("/api/files",
            MultipartWithFile("v2"u8.ToArray(), "report.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Post_Same_Content_Different_Names_Reuses_Single_BlobObject()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var payload = Encoding.UTF8.GetBytes("dedup-bytes");

        var a = await client.PostAsync("/api/files",
            MultipartWithFile(payload, "a.txt", "text/plain"));
        var b = await client.PostAsync("/api/files",
            MultipartWithFile(payload, "b.txt", "text/plain"));

        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(2, await db.FileItems.CountAsync());
        Assert.Equal(1, await db.BlobObjects.CountAsync());
        var blob = await db.BlobObjects.AsNoTracking().SingleAsync();
        Assert.Equal(2, blob.ReferenceCount);
    }

    [Fact]
    public async Task Post_File_With_Invalid_Filename_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync("/api/files",
            MultipartWithFile("x"u8.ToArray(), "a/b.txt", "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_Child_File_Under_Missing_Parent_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync($"/api/folders/{Guid.NewGuid()}/files",
            MultipartWithFile("x"u8.ToArray(), "x.txt", "text/plain"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_Child_File_Under_Foreign_Parent_Returns_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFolder = await SeedFolderAsAsync(alice, null, "AlicePhotos");

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var response = await bobClient.PostAsync($"/api/folders/{aliceFolder.Id}/files",
            MultipartWithFile("x"u8.ToArray(), "x.txt", "text/plain"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_PNG_File_Includes_Width_And_Height_In_FileSummary()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        byte[] pngBytes;
        using (var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(160, 90))
        using (var ms = new MemoryStream())
        {
            img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
            pngBytes = ms.ToArray();
        }

        var response = await client.PostAsync("/api/files",
            MultipartWithFile(pngBytes, "image.png", "image/png"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<FileSummary>();
        Assert.NotNull(summary);
        Assert.Equal(160, summary!.Width);
        Assert.Equal(90, summary.Height);
    }

    [Fact]
    public async Task Upload_Response_Does_Not_Leak_Storage_Internals()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync("/api/files",
            MultipartWithFile("no-leak-payload"u8.ToArray(), "doc.txt", "text/plain"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

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
