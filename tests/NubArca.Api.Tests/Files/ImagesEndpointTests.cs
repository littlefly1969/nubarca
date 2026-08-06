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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Files;

// End-to-end tests for GET /api/images.
public sealed class ImagesEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public ImagesEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static byte[] PngBytes(int width, int height)
    {
        using var img = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static byte[] JpegBytes(int width, int height)
    {
        using var img = new Image<Rgb24>(width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder());
        return ms.ToArray();
    }

    private static MultipartFormDataContent Multipart(byte[] bytes, string filename, string contentType)
    {
        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        multipart.Add(part, "file", filename);
        return multipart;
    }

    private async Task<Guid> UploadImageAsync(HttpClient client, string name, int w = 200, int h = 200,
        Guid? folderId = null)
    {
        var url = folderId is null ? "/api/files" : $"/api/folders/{folderId}/files";
        var resp = await client.PostAsync(url, Multipart(PngBytes(w, h), name, "image/png"));
        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();
        return summary!.Id;
    }

    private async Task<Folder> SeedFolderAsAsync(Guid ownerId, string name = "Photos")
    {
        using var scope = _factory.Services.CreateScope();
        var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
        return await folders.CreateAsync(ownerId, null, name);
    }

    [Fact]
    public async Task Images_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync("/api/images");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Images_Empty_For_User_With_No_Files()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var body = await client.GetFromJsonAsync<ImageListResponse>("/api/images");

        Assert.NotNull(body);
        Assert.Empty(body!.Items);
        Assert.Equal(0, body.Count);
        Assert.Equal(50, body.Limit);
        Assert.Equal(0, body.Offset);
    }

    [Fact]
    public async Task Images_Surfaces_ServerAuthoritative_Total_On_Cursor_Path()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadImageAsync(client, "a.png");
        await UploadImageAsync(client, "b.png");
        await UploadImageAsync(client, "c.png");

        // First page of size 2: Count reflects the page (2) but Total is the
        // full server-authoritative match count (3), so a client shows the real
        // total instead of the loaded-page count.
        var body = await client.GetFromJsonAsync<ImageListResponse>("/api/images?limit=2");
        Assert.NotNull(body);
        Assert.Equal(2, body!.Count);
        Assert.Equal(3, body.Total);
        Assert.True(body.HasMore);
    }

    [Fact]
    public async Task Images_Returns_Active_Image_With_Dimensions_And_ThumbnailUrl()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadImageAsync(client, "pic.png", w: 320, h: 240);

        var body = await client.GetFromJsonAsync<ImageListResponse>("/api/images");
        Assert.NotNull(body);
        var item = Assert.Single(body!.Items);

        Assert.Equal(fileId, item.Id);
        Assert.Equal("pic.png", item.Name);
        Assert.Equal("image/png", item.MimeType);
        Assert.Equal(320, item.Width);
        Assert.Equal(240, item.Height);
        Assert.Equal($"/api/files/{fileId}/thumbnail?size=small", item.ThumbnailUrl);
    }

    [Fact]
    public async Task Images_Excludes_NonImage_Files()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var resp = await client.PostAsync("/api/files", Multipart("hello"u8.ToArray(), "notes.txt", "text/plain"));
        resp.EnsureSuccessStatusCode();

        var body = await client.GetFromJsonAsync<ImageListResponse>("/api/images");
        Assert.NotNull(body);
        Assert.Empty(body!.Items);
    }

    [Fact]
    public async Task Images_Excludes_SoftDeleted_Images()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadImageAsync(client, "pic.png");

        var del = await client.DeleteAsync($"/api/files/{fileId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var body = await client.GetFromJsonAsync<ImageListResponse>("/api/images");
        Assert.NotNull(body);
        Assert.Empty(body!.Items);
    }

    [Fact]
    public async Task Images_Excludes_Foreign_Owned_Images()
    {
        var (_, aliceClient) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        await UploadImageAsync(aliceClient, "alice.png");

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var body = await bobClient.GetFromJsonAsync<ImageListResponse>("/api/images");

        Assert.NotNull(body);
        Assert.Empty(body!.Items);
    }

    [Fact]
    public async Task Images_Returns_Newest_First_With_Id_Tiebreaker()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var a = await UploadImageAsync(client, "a.png");
        var b = await UploadImageAsync(client, "b.png");
        var c = await UploadImageAsync(client, "c.png");

        var body = await client.GetFromJsonAsync<ImageListResponse>("/api/images");
        Assert.NotNull(body);
        Assert.Equal(3, body!.Items.Count);

        // Newest first → c, b, a (uploaded in that order).
        Assert.Equal(c, body.Items[0].Id);
        Assert.Equal(b, body.Items[1].Id);
        Assert.Equal(a, body.Items[2].Id);
    }

    [Fact]
    public async Task Images_Supports_Limit_And_Offset()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        for (var i = 0; i < 5; i++)
        {
            await UploadImageAsync(client, $"pic-{i}.png");
        }

        var page1 = await client.GetFromJsonAsync<ImageListResponse>("/api/images?limit=2&offset=0");
        var page2 = await client.GetFromJsonAsync<ImageListResponse>("/api/images?limit=2&offset=2");
        var page3 = await client.GetFromJsonAsync<ImageListResponse>("/api/images?limit=2&offset=4");

        Assert.NotNull(page1);
        Assert.NotNull(page2);
        Assert.NotNull(page3);

        Assert.Equal(2, page1!.Items.Count);
        Assert.Equal(2, page2!.Items.Count);
        Assert.Single(page3!.Items);

        // No id appears twice across pages.
        var allIds = page1.Items.Concat(page2.Items).Concat(page3.Items).Select(i => i.Id).ToList();
        Assert.Equal(allIds.Count, allIds.Distinct().Count());

        Assert.Equal(2, page2.Limit);
        Assert.Equal(2, page2.Offset);
    }

    [Fact]
    public async Task Images_Limit_Is_Clamped_To_Max_200()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var body = await client.GetFromJsonAsync<ImageListResponse>("/api/images?limit=1000");
        Assert.NotNull(body);
        Assert.Equal(200, body!.Limit);
    }

    [Fact]
    public async Task Images_Limit_Below_1_Is_Clamped_To_1()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var body = await client.GetFromJsonAsync<ImageListResponse>("/api/images?limit=0");
        Assert.NotNull(body);
        Assert.Equal(1, body!.Limit);
    }

    [Fact]
    public async Task Images_Filters_By_FolderId()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var inFolder = await SeedFolderAsAsync(owner, "Photos");
        var insideId = await UploadImageAsync(client, "inside.png", folderId: inFolder.Id);
        var outsideId = await UploadImageAsync(client, "outside.png");

        var inside = await client.GetFromJsonAsync<ImageListResponse>($"/api/images?folderId={inFolder.Id}");
        Assert.NotNull(inside);
        Assert.Single(inside!.Items);
        Assert.Equal(insideId, inside.Items[0].Id);

        var all = await client.GetFromJsonAsync<ImageListResponse>("/api/images");
        Assert.NotNull(all);
        Assert.Equal(2, all!.Items.Count);
        Assert.Contains(all.Items, i => i.Id == outsideId);
    }

    [Fact]
    public async Task Images_With_Missing_FolderId_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/images?folderId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Images_With_Foreign_FolderId_Returns_404()
    {
        var (alice, _) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceFolder = await SeedFolderAsAsync(alice, "AlicePhotos");

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var response = await bobClient.GetAsync($"/api/images?folderId={aliceFolder.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Images_With_SoftDeleted_FolderId_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, "Photos");

        var del = await client.DeleteAsync($"/api/folders/{folder.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var response = await client.GetAsync($"/api/images?folderId={folder.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Images_Excludes_Spoofed_Or_Undetected_Image()
    {
        // Slice 54.2: gallery membership is based on SERVER-DETECTED image
        // content, not the client MIME. Upload a "corrupt PNG" (valid magic +
        // garbage payload) declared image/png: ImageSharp cannot identify it,
        // so BlobMetadata.DetectedContentType stays null and the row must NOT
        // appear in the gallery — a spoofed/undetectable file claiming image/*
        // is no longer trusted as an image. Upload itself still succeeds.
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var corrupt = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xff, 0xff, 0xff, 0xff };

        var upload = await client.PostAsync("/api/files", Multipart(corrupt, "broken.png", "image/png"));
        upload.EnsureSuccessStatusCode();

        var body = await client.GetFromJsonAsync<ImageListResponse>("/api/images");
        Assert.NotNull(body);
        Assert.Empty(body!.Items);
    }

    [Fact]
    public async Task Images_Returns_Mixed_Image_Mime_Types()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var pngId = await UploadImageAsync(client, "pic.png");
        var jpegResp = await client.PostAsync("/api/files",
            Multipart(JpegBytes(100, 80), "pic.jpg", "image/jpeg"));
        jpegResp.EnsureSuccessStatusCode();

        var body = await client.GetFromJsonAsync<ImageListResponse>("/api/images");
        Assert.NotNull(body);
        Assert.Equal(2, body!.Items.Count);
        Assert.Contains(body.Items, i => i.MimeType == "image/png");
        Assert.Contains(body.Items, i => i.MimeType == "image/jpeg");
    }

    [Fact]
    public async Task Images_Q_Filters_By_Name_Substring()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var sunsetId = await UploadImageAsync(client, "sunset-beach.png");
        await UploadImageAsync(client, "office.png");
        await UploadImageAsync(client, "city-skyline.png");

        var body = await client.GetFromJsonAsync<ImageListResponse>("/api/images?q=sunset");

        Assert.NotNull(body);
        var item = Assert.Single(body!.Items);
        Assert.Equal(sunsetId, item.Id);
    }

    [Fact]
    public async Task Images_Q_Is_Case_Insensitive()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadImageAsync(client, "Holiday-2024.PNG");
        await UploadImageAsync(client, "Other.png");

        var body = await client.GetFromJsonAsync<ImageListResponse>("/api/images?q=HOLIDAY");

        Assert.NotNull(body);
        var item = Assert.Single(body!.Items);
        Assert.Equal(id, item.Id);
    }

    [Fact]
    public async Task Images_Q_Does_Not_Match_Non_Image_Even_If_Name_Matches()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        // A text file whose name contains the same substring.
        var notes = await client.PostAsync("/api/files",
            Multipart("notes about sunset"u8.ToArray(), "sunset.txt", "text/plain"));
        notes.EnsureSuccessStatusCode();

        var body = await client.GetFromJsonAsync<ImageListResponse>("/api/images?q=sunset");

        Assert.NotNull(body);
        Assert.Empty(body!.Items);
    }

    [Fact]
    public async Task Images_Q_Does_Not_Match_SoftDeleted_Image()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var sunsetId = await UploadImageAsync(client, "sunset.png");

        var del = await client.DeleteAsync($"/api/files/{sunsetId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var body = await client.GetFromJsonAsync<ImageListResponse>("/api/images?q=sunset");
        Assert.NotNull(body);
        Assert.Empty(body!.Items);
    }

    [Fact]
    public async Task Images_Q_Does_Not_Match_Foreign_Image()
    {
        var (_, aliceClient) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        await UploadImageAsync(aliceClient, "alice-sunset.png");

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var body = await bobClient.GetFromJsonAsync<ImageListResponse>("/api/images?q=sunset");

        Assert.NotNull(body);
        Assert.Empty(body!.Items);
    }

    [Fact]
    public async Task Images_Q_Works_With_FolderId()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, "Photos");

        var insideMatch = await UploadImageAsync(client, "sunset-in.png", folderId: folder.Id);
        await UploadImageAsync(client, "skyline-in.png", folderId: folder.Id);
        await UploadImageAsync(client, "sunset-out.png"); // root, matches q but outside folder

        var body = await client.GetFromJsonAsync<ImageListResponse>(
            $"/api/images?folderId={folder.Id}&q=sunset");

        Assert.NotNull(body);
        var item = Assert.Single(body!.Items);
        Assert.Equal(insideMatch, item.Id);
    }

    [Fact]
    public async Task Images_Q_Works_With_Limit_And_Offset()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        for (var i = 0; i < 5; i++)
        {
            await UploadImageAsync(client, $"sunset-{i}.png");
        }
        await UploadImageAsync(client, "noise.png");

        var page1 = await client.GetFromJsonAsync<ImageListResponse>("/api/images?q=sunset&limit=2&offset=0");
        var page2 = await client.GetFromJsonAsync<ImageListResponse>("/api/images?q=sunset&limit=2&offset=2");
        var page3 = await client.GetFromJsonAsync<ImageListResponse>("/api/images?q=sunset&limit=2&offset=4");

        Assert.NotNull(page1);
        Assert.NotNull(page2);
        Assert.NotNull(page3);
        Assert.Equal(2, page1!.Items.Count);
        Assert.Equal(2, page2!.Items.Count);
        Assert.Single(page3!.Items);

        var all = page1.Items.Concat(page2.Items).Concat(page3.Items).ToList();
        Assert.Equal(5, all.Count);
        Assert.All(all, i => Assert.Contains("sunset", i.Name));
    }

    [Fact]
    public async Task Images_Whitespace_Q_Behaves_Like_No_Q()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadImageAsync(client, "a.png");
        await UploadImageAsync(client, "b.png");

        var blank = await client.GetFromJsonAsync<ImageListResponse>("/api/images?q=%20%20%20");
        var noQ = await client.GetFromJsonAsync<ImageListResponse>("/api/images");

        Assert.NotNull(blank);
        Assert.NotNull(noQ);
        Assert.Equal(noQ!.Items.Count, blank!.Items.Count);
        Assert.Equal(2, blank.Items.Count);
    }

    [Fact]
    public async Task Images_Q_Above_256_Chars_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var huge = new string('a', 257);

        var response = await client.GetAsync($"/api/images?q={huge}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Images_Q_Exactly_256_Chars_Is_Accepted()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var atLimit = new string('a', 256);

        var response = await client.GetAsync($"/api/images?q={atLimit}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Images_Q_Response_Has_No_Storage_Internals_Leak()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadImageAsync(client, "sunset.png");

        var response = await client.GetAsync("/api/images?q=sunset");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        string[] needles =
        {
            "StorageKey", "storageKey", "storage_key",
            "BlobObjectId", "blobObjectId", "blob_object_id",
            "OwnerUserId", "ownerUserId", "owner_user_id",
            "ParentFolderId", "parentFolderId",
            "DeletedAt", "deletedAt",
            "TokenHash", "tokenHash",
            "PasswordHash", "passwordHash",
            "objects/",
        };
        foreach (var needle in needles)
        {
            Assert.DoesNotContain(needle, body);
        }
    }

    [Fact]
    public async Task Images_Response_Has_No_Storage_Internals_Leak()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folder = await SeedFolderAsAsync(owner, "Photos");
        await UploadImageAsync(client, "pic.png", folderId: folder.Id);

        var response = await client.GetAsync("/api/images");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var headers = string.Join('\n',
            response.Headers.Concat(response.Content.Headers)
                .Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));

        string[] needles =
        {
            "StorageKey", "storageKey", "storage_key",
            "BlobObjectId", "blobObjectId", "blob_object_id",
            "OwnerUserId", "ownerUserId", "owner_user_id",
            "ParentFolderId", "parentFolderId",
            "DeletedAt", "deletedAt",
            "TokenHash", "tokenHash",
            "PasswordHash", "passwordHash",
            // Thumbnail URLs intentionally embed "/api/files/.../thumbnail",
            // so we cannot blanket-ban "files" or "api". But "objects/" must
            // never appear.
            "objects/",
        };
        foreach (var needle in needles)
        {
            Assert.DoesNotContain(needle, body);
            Assert.DoesNotContain(needle, headers);
        }
    }
}
