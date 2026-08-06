using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tv;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Tv;

// Owner-private TV media browsing over the ShowOnTv album allowlist. The TV
// session is the limited, path-scoped /api/tv cookie; the owner controls the
// allowlist through the normal authenticated user cookie.
public sealed class TvMediaBrowsingTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public TvMediaBrowsingTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    // --- Owner-side allowlist control ---

    [Fact]
    public async Task New_Album_Defaults_To_ShowOnTv_False()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Holidays");

        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}");
        Assert.False(detail.GetProperty("showOnTv").GetBoolean());
    }

    [Fact]
    public async Task Owner_Can_Enable_And_Disable_ShowOnTv_On_Own_Album()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Holidays");

        var on = await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = true });
        on.EnsureSuccessStatusCode();
        var enabled = await on.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(enabled.GetProperty("showOnTv").GetBoolean());

        var off = await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = false });
        off.EnsureSuccessStatusCode();
        var disabled = await off.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(disabled.GetProperty("showOnTv").GetBoolean());
    }

    [Fact]
    public async Task Owner_Cannot_Set_ShowOnTv_On_Another_Owners_Album()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var aliceAlbum = await CreateAlbumAsync(alice, "Alice private");

        var response = await bob.PatchAsJsonAsync(
            $"/api/albums/{aliceAlbum}/tv-settings", new { showOnTv = true });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Set_ShowOnTv_Requires_Authentication()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Holidays");

        var anon = _factory.CreateClient();
        var response = await anon.PatchAsJsonAsync(
            $"/api/albums/{albumId}/tv-settings", new { showOnTv = true });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- TV allowlisted browsing ---

    [Fact]
    public async Task Paired_Tv_Sees_Only_ShowOnTv_Albums_For_Its_Owner()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var shown = await CreateAlbumAsync(owner, "On TV");
        var hidden = await CreateAlbumAsync(owner, "Not on TV");
        await AddPngAsync(owner, shown, "a.png");
        await AddPngAsync(owner, hidden, "b.png");
        await EnableTvAsync(owner, shown);

        var cookie = await PairTvAsync(owner);
        var albums = await TvJsonAsync(cookie, "/api/tv/albums");

        Assert.Equal(1, albums.GetArrayLength());
        Assert.Equal("On TV", albums[0].GetProperty("name").GetString());
        Assert.Equal(1, albums[0].GetProperty("itemCount").GetInt32());
        Assert.False(string.IsNullOrEmpty(albums[0].GetProperty("coverThumbnailUrl").GetString()));
    }

    [Fact]
    public async Task Paired_Tv_Cannot_See_Another_Owners_Albums()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceAlbum = await CreateAlbumAsync(alice, "Alice on TV");
        await AddPngAsync(alice, aliceAlbum, "a.png");
        await EnableTvAsync(alice, aliceAlbum);

        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var bobCookie = await PairTvAsync(bob);

        var albums = await TvJsonAsync(bobCookie, "/api/tv/albums");
        Assert.Equal(0, albums.GetArrayLength());

        // Bob's TV cannot open Alice's album by id either.
        var items = await TvSendAsync(bobCookie, HttpMethod.Get, $"/api/tv/albums/{aliceAlbum}/items");
        Assert.Equal(HttpStatusCode.NotFound, items.StatusCode);
    }

    [Fact]
    public async Task Tv_Lists_Items_Of_An_Allowlisted_Album()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        var fileId = await AddPngAsync(owner, albumId, "photo.png");
        await EnableTvAsync(owner, albumId);

        var cookie = await PairTvAsync(owner);
        var items = await TvJsonAsync(cookie, $"/api/tv/albums/{albumId}/items");

        var arr = items.GetProperty("items");
        Assert.Equal(1, arr.GetArrayLength());
        var item = arr[0];
        Assert.Equal(fileId.ToString(), item.GetProperty("id").GetString());
        Assert.Equal("image", item.GetProperty("mediaType").GetString());
        Assert.Equal($"/api/tv/media/{fileId}/thumbnail", item.GetProperty("thumbnailUrl").GetString());
        Assert.Equal($"/api/tv/media/{fileId}/preview", item.GetProperty("previewUrl").GetString());
        // Non-video items expose no poster/video URL.
        Assert.Equal(JsonValueKind.Null, item.GetProperty("posterUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("videoUrl").ValueKind);
    }

    [Fact]
    public async Task Tv_Item_Exposes_Image_Display_Dimensions()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        var fileId = await AddPngAsync(owner, albumId, "photo.png");
        await SetFileItemDimensionsAsync(fileId, 4000, 3000);
        await EnableTvAsync(owner, albumId);

        var cookie = await PairTvAsync(owner);
        var item = (await TvJsonAsync(cookie, $"/api/tv/albums/{albumId}/items"))
            .GetProperty("items")[0];
        Assert.Equal("image", item.GetProperty("mediaType").GetString());
        Assert.Equal(4000, item.GetProperty("width").GetInt32());
        Assert.Equal(3000, item.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task Tv_Item_Image_Without_Dimensions_Is_Null()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        var fileId = await AddPngAsync(owner, albumId, "photo.png");
        // Clear BOTH sources (FileItem and the blob probe) — the projection falls
        // back from FileItem dims to the blob's, so only when neither exists is the
        // DTO null.
        await SetFileItemDimensionsAsync(fileId, null, null);
        await SetBlobDimensionsAsync(fileId, null, null);
        await EnableTvAsync(owner, albumId);

        var cookie = await PairTvAsync(owner);
        var item = (await TvJsonAsync(cookie, $"/api/tv/albums/{albumId}/items"))
            .GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, item.GetProperty("width").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("height").ValueKind);
    }

    [Theory]
    // coded W, coded H, rotation, expected display W, expected display H
    [InlineData(1920, 1080, 0, 1920, 1080)]
    [InlineData(1920, 1080, 90, 1080, 1920)]
    [InlineData(1080, 1920, 270, 1920, 1080)]
    public async Task Tv_Video_Item_Uses_Rotation_Aware_Dimensions(
        int width, int height, int rotation, int expectedWidth, int expectedHeight)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        var fileId = await AddPngAsync(owner, albumId, "clip.png");
        await MakeConfirmedVideoAsync(fileId, width, height, rotation);
        await EnableTvAsync(owner, albumId);

        var cookie = await PairTvAsync(owner);
        var item = (await TvJsonAsync(cookie, $"/api/tv/albums/{albumId}/items"))
            .GetProperty("items")[0];
        Assert.Equal("video", item.GetProperty("mediaType").GetString());
        Assert.Equal(expectedWidth, item.GetProperty("width").GetInt32());
        Assert.Equal(expectedHeight, item.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task Tv_Image_Item_Uses_Exif_Oriented_Display_Dimensions()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        var fileId = await AddPngAsync(owner, albumId, "portrait.png");
        // Coded landscape + EXIF orientation 6 (90° CW) = a portrait photo on
        // screen; the DTO must report the DISPLAY dims so the tile is portrait,
        // not a landscape tile with the photo letterboxed over the backdrop.
        await SetFileItemDimensionsAsync(fileId, 4000, 3000);
        await SetImageOrientationAsync(fileId, 6);
        await EnableTvAsync(owner, albumId);

        var cookie = await PairTvAsync(owner);
        var item = (await TvJsonAsync(cookie, $"/api/tv/albums/{albumId}/items"))
            .GetProperty("items")[0];
        Assert.Equal("image", item.GetProperty("mediaType").GetString());
        Assert.Equal(3000, item.GetProperty("width").GetInt32());
        Assert.Equal(4000, item.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task Tv_Item_Order_Is_Deterministic_When_AddedAt_Ties()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        for (var i = 0; i < 5; i++)
        {
            await AddPngAsync(owner, albumId, $"p{i}.png");
        }
        // Force an AddedAt tie for every item (a bulk add) — the id tie-break
        // must still give one deterministic order, so the grid never reshuffles.
        await SetAllAlbumItemsAddedAtAsync(albumId, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await EnableTvAsync(owner, albumId);

        var cookie = await PairTvAsync(owner);
        var first = await OrderedIdsAsync(cookie, albumId);
        var second = await OrderedIdsAsync(cookie, albumId);
        Assert.Equal(5, first.Count);
        Assert.Equal(first, second);
        Assert.Equal(first.OrderBy(id => id, StringComparer.Ordinal).ToList(), first);
    }

    [Fact]
    public async Task Tv_Video_Item_Without_Dimensions_Is_Null()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        var fileId = await AddPngAsync(owner, albumId, "clip.png");
        await MakeConfirmedVideoAsync(fileId, null, null, null);
        await EnableTvAsync(owner, albumId);

        var cookie = await PairTvAsync(owner);
        var item = (await TvJsonAsync(cookie, $"/api/tv/albums/{albumId}/items"))
            .GetProperty("items")[0];
        Assert.Equal("video", item.GetProperty("mediaType").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("width").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("height").ValueKind);
    }

    [Fact]
    public async Task Disabling_ShowOnTv_Removes_Album_And_Items_From_Tv()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        var fileId = await AddPngAsync(owner, albumId, "photo.png");
        await EnableTvAsync(owner, albumId);
        var cookie = await PairTvAsync(owner);

        // Visible while enabled.
        Assert.Equal(1, (await TvJsonAsync(cookie, "/api/tv/albums")).GetArrayLength());
        Assert.Equal(HttpStatusCode.OK,
            (await TvSendAsync(cookie, HttpMethod.Get, $"/api/tv/albums/{albumId}/items")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await TvSendAsync(cookie, HttpMethod.Get, $"/api/tv/media/{fileId}/thumbnail")).StatusCode);

        // Owner disables → gone from the TV on the very next call (live re-check).
        await DisableTvAsync(owner, albumId);
        Assert.Equal(0, (await TvJsonAsync(cookie, "/api/tv/albums")).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound,
            (await TvSendAsync(cookie, HttpMethod.Get, $"/api/tv/albums/{albumId}/items")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await TvSendAsync(cookie, HttpMethod.Get, $"/api/tv/media/{fileId}/thumbnail")).StatusCode);
    }

    // --- TV media byte serving ---

    [Fact]
    public async Task Tv_Serves_Derived_Media_For_Allowlisted_File_And_404_For_Hidden()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var shown = await CreateAlbumAsync(owner, "On TV");
        var hidden = await CreateAlbumAsync(owner, "Hidden");
        var shownFile = await AddPngAsync(owner, shown, "shown.png");
        var hiddenFile = await AddPngAsync(owner, hidden, "hidden.png");
        await EnableTvAsync(owner, shown);
        var cookie = await PairTvAsync(owner);

        var thumb = await TvSendAsync(cookie, HttpMethod.Get, $"/api/tv/media/{shownFile}/thumbnail");
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.StartsWith("image/", thumb.Content.Headers.ContentType!.MediaType!);

        var preview = await TvSendAsync(cookie, HttpMethod.Get, $"/api/tv/media/{shownFile}/preview");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

        // A file only in a non-allowlisted album is invisible to the TV.
        var hiddenThumb = await TvSendAsync(cookie, HttpMethod.Get, $"/api/tv/media/{hiddenFile}/thumbnail");
        Assert.Equal(HttpStatusCode.NotFound, hiddenThumb.StatusCode);
    }

    [Fact]
    public async Task Tv_Poster_And_Video_404_For_NonVideo_Allowlisted_File()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        var fileId = await AddPngAsync(owner, albumId, "photo.png");
        await EnableTvAsync(owner, albumId);
        var cookie = await PairTvAsync(owner);

        // The image is visible, but the video/poster gate requires server-detected
        // video → 404 (no-leak parity with the /api/files/{id}/video gate).
        Assert.Equal(HttpStatusCode.NotFound,
            (await TvSendAsync(cookie, HttpMethod.Get, $"/api/tv/media/{fileId}/poster")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await TvSendAsync(cookie, HttpMethod.Get, $"/api/tv/media/{fileId}/video")).StatusCode);
    }

    // --- Authorization boundaries ---

    [Fact]
    public async Task Tv_Endpoints_Require_A_Tv_Session()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/tv/albums")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"/api/tv/albums/{Guid.NewGuid()}/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"/api/tv/media/{Guid.NewGuid()}/thumbnail")).StatusCode);
    }

    [Fact]
    public async Task Normal_User_Cookie_Cannot_Access_Tv_Endpoints()
    {
        // The owner's normal auth cookie is NOT a TV session; the TV endpoints
        // resolve only the path-scoped TV cookie, so a normal user is 401.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        await AddPngAsync(owner, albumId, "photo.png");
        await EnableTvAsync(owner, albumId);

        var response = await owner.GetAsync("/api/tv/albums");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Tv_Session_Still_Cannot_Reach_Owner_Album_Apis()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        await EnableTvAsync(owner, albumId);
        var cookie = await PairTvAsync(owner);

        // The TV cookie must not authorize the normal owner surfaces.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await TvSendAsync(cookie, HttpMethod.Get, "/api/albums")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await TvSendAsync(cookie, HttpMethod.Get, $"/api/albums/{albumId}/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await TvSendAsync(cookie, HttpMethod.Get, "/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Tv_Dtos_Do_Not_Leak_Storage_Or_Internal_Fields()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        await AddPngAsync(owner, albumId, "photo.png");
        await EnableTvAsync(owner, albumId);
        var cookie = await PairTvAsync(owner);

        var albumsRaw = await (await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/albums"))
            .Content.ReadAsStringAsync();
        var itemsRaw = await (await TvSendAsync(cookie, HttpMethod.Get, $"/api/tv/albums/{albumId}/items"))
            .Content.ReadAsStringAsync();

        foreach (var raw in new[] { albumsRaw, itemsRaw })
        {
            AssertNoInternals(raw);
        }
        // No public-share surface is introduced by this slice.
        Assert.DoesNotContain("token", itemsRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("share", itemsRaw, StringComparison.OrdinalIgnoreCase);
    }

    // --- helpers ---

    private async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();
        return detail.GetProperty("id").GetGuid();
    }

    private async Task<Guid> AddPngAsync(HttpClient owner, Guid albumId, string name)
    {
        var fileId = await UploadPngAsync(owner, name);
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
    }

    private static async Task<Guid> UploadPngAsync(HttpClient owner, string name)
    {
        using var img = new Image<Rgba32>(8, 8);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        var part = new ByteArrayContent(ms.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var resp = await owner.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return summary.GetProperty("id").GetGuid();
    }

    private async Task SetBlobDimensionsAsync(Guid fileItemId, int? width, int? height)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f = await db.FileItems.FirstAsync(x => x.Id == fileItemId);
        var meta = await db.BlobMetadata.FirstAsync(m => m.BlobObjectId == f.BlobObjectId);
        meta.Width = width;
        meta.Height = height;
        await db.SaveChangesAsync();
    }

    private async Task SetImageOrientationAsync(Guid fileItemId, int orientation)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f = await db.FileItems.FirstAsync(x => x.Id == fileItemId);
        var meta = await db.BlobMetadata.FirstAsync(m => m.BlobObjectId == f.BlobObjectId);
        meta.Orientation = orientation;
        await db.SaveChangesAsync();
    }

    private async Task SetAllAlbumItemsAddedAtAsync(Guid albumId, DateTime addedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var items = await db.AlbumItems.Where(ai => ai.AlbumId == albumId).ToListAsync();
        foreach (var item in items)
        {
            item.AddedAt = addedAt;
        }
        await db.SaveChangesAsync();
    }

    private async Task<List<string>> OrderedIdsAsync(string cookie, Guid albumId)
    {
        var arr = (await TvJsonAsync(cookie, $"/api/tv/albums/{albumId}/items")).GetProperty("items");
        return arr.EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToList();
    }

    private async Task SetFileItemDimensionsAsync(Guid fileItemId, int? width, int? height)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f = await db.FileItems.FirstAsync(x => x.Id == fileItemId);
        f.Width = width;
        f.Height = height;
        await db.SaveChangesAsync();
    }

    // Turn a seeded (uploaded PNG) file into a server-confirmed video with the
    // given probe dimensions/rotation, without needing a real ffmpeg run.
    private async Task MakeConfirmedVideoAsync(Guid fileItemId, int? width, int? height, int? rotation)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f = await db.FileItems.FirstAsync(x => x.Id == fileItemId);
        // A real video carries its dimensions on BlobMetadata (the probe), not on
        // the FileItem — clear the PNG-upload FileItem dims so the fixture matches.
        f.Width = null;
        f.Height = null;
        var meta = await db.BlobMetadata.FirstAsync(m => m.BlobObjectId == f.BlobObjectId);
        meta.MediaCategory = MediaCategories.Video;
        meta.VideoExtractionStatus = "completed";
        meta.VideoCodec = "h264";
        meta.Width = width;
        meta.Height = height;
        meta.Rotation = rotation;
        await db.SaveChangesAsync();
    }

    private static async Task EnableTvAsync(HttpClient owner, Guid albumId)
        => (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = true }))
            .EnsureSuccessStatusCode();

    private static async Task DisableTvAsync(HttpClient owner, Guid albumId)
        => (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = false }))
            .EnsureSuccessStatusCode();

    // Pairs a fresh TV client to the given already-authenticated owner and returns
    // the Set-Cookie value for the limited TV session.
    private async Task<string> PairTvAsync(HttpClient owner)
    {
        var tvClient = _factory.CreateClient();
        var start = await tvClient.PostAsync("/api/tv/pairing/start", null);
        start.EnsureSuccessStatusCode();
        var started = (await start.Content.ReadFromJsonAsync<TvPairingStartedDto>())!;

        (await owner.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                // Atomic first pairing: approval creates the owner's PIN when
                // missing; ignored for owners who already have one.
                personalPin = "123456",
                personalPinConfirmation = "123456",
            })).EnsureSuccessStatusCode();

        var pollRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        pollRequest.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var poll = await tvClient.SendAsync(pollRequest);
        poll.EnsureSuccessStatusCode();
        return poll.Headers.GetValues("Set-Cookie").Single();
    }

    private Task<HttpResponseMessage> TvSendAsync(string setCookie, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(setCookie)}");
        return _factory.CreateClient().SendAsync(request);
    }

    private async Task<JsonElement> TvJsonAsync(string setCookie, string url)
    {
        var response = await TvSendAsync(setCookie, HttpMethod.Get, url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string CookieValue(string setCookie)
    {
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }

    private static void AssertNoInternals(string raw)
    {
        string[] forbidden =
        [
            "StorageKey", "BlobObjectId", "BlobId", "sha256", "TokenHash",
            "SessionTokenHash", "PayloadJson", "PasswordHash", "OwnerUserId",
            "PrivateVaultId", "storageKey", "physicalPath", "embedding", "vector",
        ];
        foreach (var needle in forbidden)
        {
            Assert.DoesNotContain(needle, raw, StringComparison.OrdinalIgnoreCase);
        }
    }
}
