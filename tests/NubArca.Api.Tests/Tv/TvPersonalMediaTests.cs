using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tv;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Tv;

// The unified TV Personal media workspace: GET /api/tv/personal/media and
// GET /api/tv/personal/albums[/{id}[/media]].
//
// The point of these endpoints is that they are NOT a second implementation.
// They bind through the same MediaCollectionQueryBinder and run through the same
// IMediaCollectionQueryService as the web's /api/media, so the television's
// filters cannot come to mean something different from the web's. What follows
// therefore tests the two things that are genuinely TV-specific — the
// authorization gate and the URL projection — plus the query rules the TV
// INHERITS, because inheriting them is the whole design and a regression would
// be silent.
public sealed class TvPersonalMediaTests : IDisposable
{
    private const string Code = "URDLSUDLR";

    private readonly SqliteWebApplicationFactory _factory = new();

    public TvPersonalMediaTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    // ── authorization ───────────────────────────────────────────────────────

    [Fact]
    public async Task Every_Media_Route_Requires_Both_The_Session_And_The_Grant()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);

        foreach (var url in new[]
        {
            "/api/tv/personal/media",
            "/api/tv/personal/albums",
        })
        {
            // Session but no grant → locked.
            var locked = await TvSendAsync(cookie, HttpMethod.Get, url);
            Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
            Assert.Contains("locked", await locked.Content.ReadAsStringAsync());

            // No session at all → unauthorized, even with a grant-shaped header.
            var anonymous = new HttpRequestMessage(HttpMethod.Get, url);
            anonymous.Headers.Add(TvPersonalAreaService.UnlockHeader, "not-a-grant");
            var response = await _factory.CreateClient().SendAsync(anonymous);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Media_And_Album_Payloads_Are_No_Store()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(owner, "a.png");
        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        foreach (var url in new[] { "/api/tv/personal/media", "/api/tv/personal/albums" })
        {
            var response = await TvSendAsync(cookie, HttpMethod.Get, url, grant);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");
        }
    }

    [Fact]
    public async Task One_Owner_Media_Never_Appears_For_Another()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceFile = await UploadAsync(alice, "alice.png");

        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        await UploadAsync(bob, "bob.png");
        var bobCookie = await PairAsync(bob);
        var bobGrant = await UnlockTokenAsync(bobCookie);

        var page = await MediaAsync(bobCookie, bobGrant);
        var ids = page.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(aliceFile, ids);
        Assert.Single(ids);
    }

    // ── the unified projection ──────────────────────────────────────────────

    [Fact]
    public async Task All_Returns_Photos_And_Videos_In_One_Server_Ordered_Grid()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(owner, "photo.png");
        await UploadAsync(owner, "clip.mp4", Mp4Bytes(), "video/mp4");
        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        var page = await MediaAsync(cookie, grant);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        // The per-kind split lets the tabs show counts without extra requests.
        Assert.Equal(1, page.GetProperty("photoCount").GetInt32());
        Assert.Equal(1, page.GetProperty("videoCount").GetInt32());
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());

        var photo = items.Single(i => i.GetProperty("kind").GetString() == "image");
        var video = items.Single(i => i.GetProperty("kind").GetString() == "video");

        // Every URL is a grant-gated TV path — never /api/files, which the TV
        // session cannot reach at all.
        foreach (var url in new[]
        {
            photo.GetProperty("cardImageUrl").GetString()!,
            photo.GetProperty("viewerImageUrl").GetString()!,
            video.GetProperty("cardImageUrl").GetString()!,
            video.GetProperty("videoUrl").GetString()!,
        })
        {
            Assert.StartsWith("/api/tv/personal/media/", url);
        }
        // A photo card is the small thumbnail and its viewer image the medium
        // preview; a video card is the poster.
        Assert.EndsWith("/thumbnail", photo.GetProperty("cardImageUrl").GetString());
        Assert.EndsWith("/preview", photo.GetProperty("viewerImageUrl").GetString());
        Assert.EndsWith("/poster", video.GetProperty("cardImageUrl").GetString());

        // Kind-specific fields are null on the other kind.
        Assert.Equal(JsonValueKind.Null, photo.GetProperty("videoUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, photo.GetProperty("durationSeconds").ValueKind);
    }

    [Fact]
    public async Task The_Payload_Leaks_No_Storage_Or_Ai_Internals()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(owner, "leakcheck.png");
        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        var raw = await (await TvSendAsync(
            cookie, HttpMethod.Get, "/api/tv/personal/media", grant)).Content.ReadAsStringAsync();
        foreach (var forbidden in new[]
        {
            "storageKey", "blobObjectId", "blobId", "sha256", "ownerUserId",
            "parentFolderId", "deletedAt", "payloadJson", "embedding", "latitude",
        })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Photos_And_Videos_Tabs_Narrow_The_Same_Query()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(owner, "photo.png");
        await UploadAsync(owner, "clip.mp4", Mp4Bytes(), "video/mp4");
        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        var photos = await MediaAsync(cookie, grant, "?kind=image");
        Assert.All(photos.GetProperty("items").EnumerateArray(),
            i => Assert.Equal("image", i.GetProperty("kind").GetString()));

        var videos = await MediaAsync(cookie, grant, "?kind=video");
        Assert.All(videos.GetProperty("items").EnumerateArray(),
            i => Assert.Equal("video", i.GetProperty("kind").GetString()));
    }

    // ── inherited query rules ───────────────────────────────────────────────

    [Fact]
    public async Task A_Photo_Filter_On_A_Non_Photo_Tab_Is_Refused_Rather_Than_Applied()
    {
        // This is the rule that makes "no hidden filter" real: the client is
        // built never to send one, and the server refuses it if one arrives.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        foreach (var query in new[]
        {
            "?kind=all&hasGps=true",
            "?kind=video&collapseDuplicates=true",
            "?kind=all&minHeight=1080",
            "?kind=image&codec=h264",
        })
        {
            var response = await TvSendAsync(
                cookie, HttpMethod.Get, $"/api/tv/personal/media{query}", grant);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_Cursor_Cannot_Be_Replayed_Under_A_Different_Query()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        for (var i = 0; i < 3; i++) await UploadAsync(owner, $"p{i}.png");
        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        var first = await MediaAsync(cookie, grant, "?limit=1");
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        // Same query: fine.
        var next = await TvSendAsync(
            cookie, HttpMethod.Get,
            $"/api/tv/personal/media?limit=1&cursor={Uri.EscapeDataString(cursor!)}", grant);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);

        // A different sort — a page issued for one ordering must never be served
        // under another.
        var mismatched = await TvSendAsync(
            cookie, HttpMethod.Get,
            $"/api/tv/personal/media?limit=1&direction=asc&cursor={Uri.EscapeDataString(cursor!)}",
            grant);
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
    }

    [Fact]
    public async Task The_Album_Route_Rejects_Album_Membership_Because_It_Is_Meaningless_There()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Vacanze");
        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        // The parameter is not bound on this route at all, so it is silently
        // absent rather than silently applied — and the library route, where it
        // IS meaningful, accepts it.
        var inAlbum = await TvSendAsync(
            cookie, HttpMethod.Get,
            $"/api/tv/personal/albums/{albumId}/media?albumMembership=unassigned", grant);
        Assert.Equal(HttpStatusCode.OK, inAlbum.StatusCode);
        Assert.Empty(JsonDocument.Parse(await inAlbum.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items").EnumerateArray());

        var inLibrary = await TvSendAsync(
            cookie, HttpMethod.Get, "/api/tv/personal/media?albumMembership=unassigned", grant);
        Assert.Equal(HttpStatusCode.OK, inLibrary.StatusCode);
    }

    // ── personal albums ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_Owner_Sees_Their_Own_Albums_With_Counts_And_Tv_Cover_Urls()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var photo = await UploadAsync(owner, "in-album.png");
        var albumId = await CreateAlbumAsync(owner, "Vacanze");
        await AddToAlbumAsync(owner, albumId, photo);

        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        var response = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/albums", grant);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var albums = JsonDocument.Parse(raw).RootElement.EnumerateArray().ToList();
        var album = Assert.Single(albums);
        Assert.Equal("Vacanze", album.GetProperty("name").GetString());
        Assert.Equal(1, album.GetProperty("itemCount").GetInt32());

        // Cover URLs are re-pointed at the grant-gated TV routes — the web cover
        // URL addresses /api/files, which the TV session cannot reach.
        foreach (var url in album.GetProperty("coverImageUrls").EnumerateArray())
        {
            Assert.StartsWith("/api/tv/personal/media/", url.GetString());
        }
        // Party/allowlist state is not a Personal Area concern and is absent.
        Assert.DoesNotContain("showOnTv", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("partyUrl", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Personal_Albums_Are_Not_Limited_To_The_Party_Allowlist()
    {
        // Party's ShowOnTv flag is a PUBLIC-facing allowlist with a different
        // threat model. The Personal Area is gated by the session AND the grant
        // AND owner scoping, so it shows the owner every album they own — the
        // same set the web shows them.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await CreateAlbumAsync(owner, "Not on TV");
        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        var party = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/albums");
        Assert.Empty(JsonDocument.Parse(await party.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray());

        var personal = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/albums", grant);
        Assert.Single(JsonDocument.Parse(await personal.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray());
    }

    [Fact]
    public async Task A_Foreign_Album_Is_A_Generic_404_And_Never_Serves_Its_Media()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceAlbum = await CreateAlbumAsync(alice, "Alice");
        var alicePhoto = await UploadAsync(alice, "alice.png");
        await AddToAlbumAsync(alice, aliceAlbum, alicePhoto);

        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var bobCookie = await PairAsync(bob);
        var bobGrant = await UnlockTokenAsync(bobCookie);

        // Existence must not leak: the same 404 an unknown id would produce.
        var detail = await TvSendAsync(
            bobCookie, HttpMethod.Get, $"/api/tv/personal/albums/{aliceAlbum}", bobGrant);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

        var media = await TvSendAsync(
            bobCookie, HttpMethod.Get, $"/api/tv/personal/albums/{aliceAlbum}/media", bobGrant);
        Assert.Equal(HttpStatusCode.NotFound, media.StatusCode);

        var unknown = await TvSendAsync(
            bobCookie, HttpMethod.Get, $"/api/tv/personal/albums/{Guid.NewGuid()}/media", bobGrant);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task An_Album_Serves_Only_Its_Members()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var inside = await UploadAsync(owner, "inside.png");
        await UploadAsync(owner, "outside.png");
        var albumId = await CreateAlbumAsync(owner, "Vacanze");
        await AddToAlbumAsync(owner, albumId, inside);

        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        var page = await MediaAsync(cookie, grant, albumId: albumId);
        var ids = page.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([inside], ids);
    }

    [Fact]
    public async Task A_Locked_Or_Stale_Grant_Cannot_Read_An_Album()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Vacanze");
        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        // Explicit lock revokes the grant server-side.
        (await TvSendAsync(cookie, HttpMethod.Post, "/api/tv/personal/lock"))
            .EnsureSuccessStatusCode();
        var afterLock = await TvSendAsync(
            cookie, HttpMethod.Get, $"/api/tv/personal/albums/{albumId}/media", grant);
        Assert.Equal(HttpStatusCode.Forbidden, afterLock.StatusCode);

        // A changed code invalidates it with the distinct reason.
        var fresh = await UnlockTokenAsync(cookie);
        (await owner.PostAsJsonAsync("/api/tv-personal/tv-code",
            new { code = "SSSUUUDDD", confirmCode = "SSSUUUDDD" })).EnsureSuccessStatusCode();
        var afterChange = await TvSendAsync(
            cookie, HttpMethod.Get, $"/api/tv/personal/albums/{albumId}/media", fresh);
        Assert.Equal(HttpStatusCode.Forbidden, afterChange.StatusCode);
        Assert.Contains("pin_changed", await afterChange.Content.ReadAsStringAsync());
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task<JsonElement> MediaAsync(
        string cookie, string grant, string query = "", Guid? albumId = null)
    {
        var url = albumId is Guid id
            ? $"/api/tv/personal/albums/{id}/media{query}"
            : $"/api/tv/personal/media{query}";
        var response = await TvSendAsync(cookie, HttpMethod.Get, url, grant);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static byte[] PngBytes(int width = 16, int height = 16)
    {
        using var image = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    // Minimal MP4 header: enough for the server's content sniffing to classify
    // the upload as a video, which is all these tests need.
    private static byte[] Mp4Bytes()
    {
        var header = new byte[]
        {
            0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70,
            0x69, 0x73, 0x6F, 0x6D, 0x00, 0x00, 0x02, 0x00,
            0x69, 0x73, 0x6F, 0x6D, 0x69, 0x73, 0x6F, 0x32,
            0x61, 0x76, 0x63, 0x31, 0x6D, 0x70, 0x34, 0x31,
        };
        return [.. header, .. new byte[256]];
    }

    private static async Task<Guid> UploadAsync(
        HttpClient client, string name, byte[]? bytes = null, string mime = "image/png")
    {
        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes ?? PngBytes());
        part.Headers.ContentType = new MediaTypeHeaderValue(mime);
        multipart.Add(part, "file", name);
        var response = await client.PostAsync("/api/files", multipart);
        response.EnsureSuccessStatusCode();
        var summary = await response.Content.ReadFromJsonAsync<FileSummary>();
        return summary!.Id;
    }

    private static async Task<Guid> CreateAlbumAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        var dto = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return dto.GetProperty("id").GetGuid();
    }

    private static async Task AddToAlbumAsync(HttpClient client, Guid albumId, params Guid[] ids)
    {
        foreach (var fileItemId in ids)
        {
            var response = await client.PostAsJsonAsync(
                $"/api/albums/{albumId}/items", new { fileItemId });
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task<string> PairAsync(HttpClient owner)
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
                personalCode = Code,
                personalCodeConfirmation = Code,
            })).EnsureSuccessStatusCode();

        var poll = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        poll.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var response = await tvClient.SendAsync(poll);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single();
    }

    private async Task<string> UnlockTokenAsync(string cookie)
    {
        var response = await TvSendAsync(
            cookie, HttpMethod.Post, "/api/tv/personal/unlock", json: new { code = Code });
        response.EnsureSuccessStatusCode();
        var dto = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return dto.GetProperty("unlockToken").GetString()!;
    }

    private Task<HttpResponseMessage> TvSendAsync(
        string cookie, HttpMethod method, string url, string? grant = null, object? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(cookie)}");
        if (grant is not null) request.Headers.Add(TvPersonalAreaService.UnlockHeader, grant);
        if (json is not null) request.Content = JsonContent.Create(json);
        return _factory.CreateClient().SendAsync(request);
    }

    private static string CookieValue(string setCookie)
    {
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }
}
