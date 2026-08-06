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
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Files;

// Slice: effective display names in the photo and video galleries.
//
// A user title REPLACES the file name in the gallery projections
// (ImageItem/VideoItem.DisplayName) and becomes the sort=name ordering key,
// while FileItem.Name stays the logical file name. These tests pin the DTO
// contract, the ordering, and the cursor pagination that seeks on that key.
public sealed class MediaDisplayNameTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public MediaDisplayNameTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static byte[] PngBytes(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private async Task<Guid> UploadImageAsync(HttpClient client, string name, int dim = 48)
    {
        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(PngBytes(dim));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(part, "file", name);
        var resp = await client.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();
        return summary!.Id;
    }

    private async Task<Guid> UploadVideoAsync(Guid ownerId, string name, string signature)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        var file = await files.CreateAsync(
            ownerId, null, name, "video/mp4", new MemoryStream(ImageFixtures.MinimalMp4(signature)));
        return file.Id;
    }

    // Sets the owner title through the real metadata service, so normalisation
    // (trim, whitespace-only → null) is exactly what production does.
    private async Task SetTitleAsync(Guid ownerId, Guid fileId, string? title)
    {
        using var scope = _factory.Services.CreateScope();
        var metadata = scope.ServiceProvider.GetRequiredService<IMetadataService>();
        var result = await metadata.UpdateUserMetadataAsync(
            ownerId, fileId, new UpdateFileMetadataRequest(
                Title: title, Description: null, Tags: null, Rating: null,
                Favorite: null, DateTakenOverride: null, LocationOverride: null));
        Assert.NotNull(result);
    }

    private static async Task<List<ImageItem>> ImagesAsync(HttpClient client, string url)
    {
        var body = await client.GetFromJsonAsync<ImageListResponse>(url);
        Assert.NotNull(body);
        return body!.Items.ToList();
    }

    // ---------------------------------------------------------------- DTO shape

    [Fact]
    public async Task Image_Without_Title_Falls_Back_To_File_Name()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadImageAsync(client, "beach.png");

        var item = Assert.Single(await ImagesAsync(client, "/api/images"));
        Assert.Null(item.Title);
        Assert.Equal("beach.png", item.Name);
        Assert.Equal("beach.png", item.DisplayName);
    }

    [Fact]
    public async Task Image_With_Title_Shows_Title_And_Keeps_File_Name()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadImageAsync(client, "IMG_0042.png");
        await SetTitleAsync(owner, id, "Sunset over the lake");

        var item = Assert.Single(await ImagesAsync(client, "/api/images"));
        Assert.Equal("Sunset over the lake", item.Title);
        Assert.Equal("Sunset over the lake", item.DisplayName);
        // The logical file name is never rewritten by a title.
        Assert.Equal("IMG_0042.png", item.Name);
    }

    [Fact]
    public async Task Clearing_The_Title_Restores_The_File_Name()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadImageAsync(client, "IMG_0042.png");
        await SetTitleAsync(owner, id, "Sunset");
        Assert.Equal("Sunset", (await ImagesAsync(client, "/api/images"))[0].DisplayName);

        await SetTitleAsync(owner, id, null);

        var item = Assert.Single(await ImagesAsync(client, "/api/images"));
        Assert.Null(item.Title);
        Assert.Equal("IMG_0042.png", item.DisplayName);
    }

    [Fact]
    public async Task Whitespace_Only_Title_Is_Treated_As_No_Title()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadImageAsync(client, "IMG_0042.png");
        await SetTitleAsync(owner, id, "   ");

        var item = Assert.Single(await ImagesAsync(client, "/api/images"));
        Assert.Null(item.Title);
        Assert.Equal("IMG_0042.png", item.DisplayName);
    }

    [Fact]
    public async Task Video_Without_Title_Falls_Back_And_With_Title_Shows_Title()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var plain = await UploadVideoAsync(owner, "plain.mp4", "pl01");
        var titled = await UploadVideoAsync(owner, "MVI_9001.mp4", "ti01");
        await SetTitleAsync(owner, titled, "Birthday party");

        var page = await client.GetFromJsonAsync<VideoListResponse>("/api/videos");
        var byId = page!.Items.ToDictionary(v => v.Id);

        Assert.Null(byId[plain].Title);
        Assert.Equal("plain.mp4", byId[plain].DisplayName);
        Assert.Equal("plain.mp4", byId[plain].Name);

        Assert.Equal("Birthday party", byId[titled].Title);
        Assert.Equal("Birthday party", byId[titled].DisplayName);
        Assert.Equal("MVI_9001.mp4", byId[titled].Name);
    }

    [Fact]
    public async Task Titles_Never_Leak_Storage_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadImageAsync(client, "a.png");
        await SetTitleAsync(owner, id, "Titled");

        var body = await (await client.GetAsync("/api/images")).Content.ReadAsStringAsync();
        foreach (var needle in new[]
        {
            "storageKey", "StorageKey", "blobObjectId", "BlobObjectId",
            "ownerUserId", "OwnerUserId", "objects/", "sha256",
        })
        {
            Assert.DoesNotContain(needle, body);
        }
    }

    // ------------------------------------------------------------- sort=name

    [Fact]
    public async Task Sort_By_Name_Orders_By_Display_Name_Ascending()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        // File names deliberately in the OPPOSITE order to the titles, so a
        // filename-based sort and a display-name sort cannot both pass.
        var zeta = await UploadImageAsync(client, "zeta.png");
        var alpha = await UploadImageAsync(client, "alpha.png");
        await SetTitleAsync(owner, zeta, "Aardvark");
        await SetTitleAsync(owner, alpha, "Zulu");

        var items = await ImagesAsync(client, "/api/images?sort=name&direction=asc");
        Assert.Equal(new[] { zeta, alpha }, items.Select(i => i.Id).ToArray());
        Assert.Equal(new[] { "Aardvark", "Zulu" }, items.Select(i => i.DisplayName).ToArray());
    }

    [Fact]
    public async Task Sort_By_Name_Orders_By_Display_Name_Descending()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var zeta = await UploadImageAsync(client, "zeta.png");
        var alpha = await UploadImageAsync(client, "alpha.png");
        await SetTitleAsync(owner, zeta, "Aardvark");
        await SetTitleAsync(owner, alpha, "Zulu");

        var items = await ImagesAsync(client, "/api/images?sort=name&direction=desc");
        Assert.Equal(new[] { alpha, zeta }, items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Sort_By_Name_Mixes_Titled_And_Untitled_Files_In_One_Order()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var untitledB = await UploadImageAsync(client, "banana.png");
        var titledA = await UploadImageAsync(client, "zzz.png");
        var untitledC = await UploadImageAsync(client, "cherry.png");
        await SetTitleAsync(owner, titledA, "apple");

        var items = await ImagesAsync(client, "/api/images?sort=name&direction=asc");
        // apple (title) < banana (name) < cherry (name)
        Assert.Equal(new[] { titledA, untitledB, untitledC }, items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Sort_By_Display_Name_Is_Case_Insensitive()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var upper = await UploadImageAsync(client, "one.png");
        var lower = await UploadImageAsync(client, "two.png");
        await SetTitleAsync(owner, upper, "ZEBRA");
        await SetTitleAsync(owner, lower, "apple");

        var items = await ImagesAsync(client, "/api/images?sort=name&direction=asc");
        // Case-sensitive binary ordering would put "ZEBRA" first.
        Assert.Equal(new[] { lower, upper }, items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task A_Title_Equal_To_Another_Files_Name_Sorts_Together_Deterministically()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var named = await UploadImageAsync(client, "shared.png", dim: 40);
        var titled = await UploadImageAsync(client, "other.png", dim: 41);
        await SetTitleAsync(owner, titled, "shared.png");

        var page1 = await ImagesAsync(client, "/api/images?sort=name&direction=asc&limit=1");
        var all = await ImagesAsync(client, "/api/images?sort=name&direction=asc");

        Assert.Equal(2, all.Count);
        Assert.All(all, i => Assert.Equal("shared.png", i.DisplayName));
        // Colliding keys are broken by Id, so the first page is stable.
        Assert.Equal(all[0].Id, page1[0].Id);
        Assert.Contains(all[0].Id, new[] { named, titled });
    }

    [Fact]
    public async Task Duplicate_Titles_Paginate_Without_Duplicates_Or_Gaps()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var expected = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var id = await UploadImageAsync(client, $"file-{i}.png", dim: 40 + i);
            // Every file gets the SAME title: the Id tiebreaker is the only
            // thing keeping seek pagination coherent.
            await SetTitleAsync(owner, id, "Holiday");
            expected.Add(id);
        }

        var seen = await PageThroughAsync(client, "/api/images?sort=name&direction=asc&limit=2");

        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
        Assert.Equal(expected.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public async Task Display_Name_Cursor_Pagination_Covers_Every_Item_In_Both_Directions()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var expected = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            var id = await UploadImageAsync(client, $"raw-{i}.png", dim: 40 + i);
            // Titles run counter to the file names for half the set.
            if (i % 2 == 0) await SetTitleAsync(owner, id, $"title-{6 - i}");
            expected.Add(id);
        }

        var asc = await PageThroughAsync(client, "/api/images?sort=name&direction=asc&limit=2");
        var desc = await PageThroughAsync(client, "/api/images?sort=name&direction=desc&limit=3");

        Assert.Equal(7, asc.Distinct().Count());
        Assert.Equal(7, desc.Distinct().Count());
        Assert.Equal(expected.OrderBy(x => x), asc.OrderBy(x => x));
        Assert.Equal(asc, Enumerable.Reverse(desc).ToList());
    }

    [Fact]
    public async Task Videos_Sort_By_Display_Name_Too()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var zeta = await UploadVideoAsync(owner, "zeta.mp4", "vz01");
        var alpha = await UploadVideoAsync(owner, "alpha.mp4", "va01");
        await SetTitleAsync(owner, zeta, "Aardvark");
        await SetTitleAsync(owner, alpha, "Zulu");

        var page = await client.GetFromJsonAsync<VideoListResponse>(
            "/api/videos?sort=name&direction=asc");
        Assert.Equal(new[] { zeta, alpha }, page!.Items.Select(v => v.Id).ToArray());
    }

    // Changing a title mid-pagination is allowed to move that row; what must NOT
    // happen is a crash or a corrupted page. The cursor stays bound to the same
    // filter set, so the next page is still served.
    [Fact]
    public async Task Changing_A_Title_Mid_Pagination_Still_Serves_The_Next_Page()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        for (var i = 0; i < 5; i++)
        {
            await UploadImageAsync(client, $"m-{i}.png", dim: 40 + i);
        }

        var first = await client.GetFromJsonAsync<ImageListResponse>(
            "/api/images?sort=name&direction=asc&limit=2");
        Assert.NotNull(first!.NextCursor);

        var lastOfFirstPage = first.Items[^1].Id;
        await SetTitleAsync(owner, lastOfFirstPage, "zzz-moved-to-the-end");

        var second = await client.GetAsync(
            $"/api/images?sort=name&direction=asc&limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<ImageListResponse>();
        Assert.NotNull(body);
        Assert.DoesNotContain(body!.Items, i => i.Id == first.Items[0].Id);
    }

    private static async Task<List<Guid>> PageThroughAsync(HttpClient client, string baseUrl)
    {
        var seen = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 25; guard++)
        {
            var url = cursor is null ? baseUrl : $"{baseUrl}&cursor={Uri.EscapeDataString(cursor)}";
            var body = await client.GetFromJsonAsync<ImageListResponse>(url);
            Assert.NotNull(body);
            seen.AddRange(body!.Items.Select(i => i.Id));
            cursor = body.NextCursor;
            if (cursor is null) return seen;
        }
        Assert.Fail("pagination did not terminate");
        return seen;
    }
}
