using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using NubArca.Api.Tv;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Party;

// Public read-only "party mode" for ShowOnTv albums: owner enable/disable,
// scoped revocable public token (hash-only), party-safe (metadata-stripped,
// derived, never-original) public media, and the TV party-URL surfacing rule.
public sealed class PartyModeTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyModeTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    // --- Owner lifecycle ---

    [Fact]
    public async Task Owner_Can_Enable_And_Disable_Party_On_Own_Album()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");

        var status = await EnablePartyAsync(owner, albumId);
        Assert.True(status.GetProperty("partyMode").GetBoolean());
        // Party implies ShowOnTv.
        Assert.True(status.GetProperty("showOnTv").GetBoolean());
        var url = status.GetProperty("partyUrl").GetString();
        Assert.False(string.IsNullOrEmpty(url));
        Assert.StartsWith("/party/", url);

        var off = await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = false });
        off.EnsureSuccessStatusCode();
        var disabled = await off.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(disabled.GetProperty("partyMode").GetBoolean());
        Assert.Equal(JsonValueKind.Null, disabled.GetProperty("partyUrl").ValueKind);
        // Disabling party leaves ShowOnTv on (owner keeps it on their own TV).
        Assert.True(disabled.GetProperty("showOnTv").GetBoolean());
    }

    [Fact]
    public async Task Owner_Cannot_Enable_Party_On_Another_Owners_Album()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var aliceAlbum = await CreateAlbumAsync(alice, "Alice private");

        var resp = await bob.PatchAsJsonAsync($"/api/albums/{aliceAlbum}/party-settings", new { enabled = true });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var getResp = await bob.GetAsync($"/api/albums/{aliceAlbum}/party-settings");
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);
    }

    [Fact]
    public async Task Party_Endpoints_Require_Authentication()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");

        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"/api/albums/{albumId}/party-settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = true })).StatusCode);
    }

    // --- Token: hash-only, no raw persistence, no exposure ---

    [Fact]
    public async Task Public_Token_Is_Stored_As_Hash_Only_And_Never_Exposed()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);
        var token = TokenFromUrl(status.GetProperty("partyUrl").GetString()!);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var link = await db.PartyAlbumLinks.SingleAsync(p => p.OwnerUserId == ownerId && p.AlbumId == albumId);

        // Only the hash is stored — never the raw token.
        Assert.Equal(64, link.TokenHash.Length);
        Assert.NotEqual(token, link.TokenHash);
        Assert.Equal(PartyLinkService.HashToken(token), link.TokenHash);
        Assert.True(link.Enabled);

        // The owner-facing responses never carry the hash.
        var statusRaw = await (await owner.GetAsync($"/api/albums/{albumId}/party-settings")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(link.TokenHash, statusRaw);
        Assert.DoesNotContain("tokenHash", statusRaw, StringComparison.OrdinalIgnoreCase);
    }

    // --- Public access + revocation ---

    [Fact]
    public async Task Public_Party_Item_Order_Is_Deterministic_When_AddedAt_Ties()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        for (var i = 0; i < 5; i++)
        {
            await AddPngAsync(owner, albumId, $"p{i}.png");
        }
        // Force an AddedAt tie for every item (a bulk add) — without a stable
        // id tie-break the public party grid would reshuffle on each poll
        // ("a slideshow of the whole gallery").
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var members = await db.AlbumItems.Where(ai => ai.AlbumId == albumId).ToListAsync();
            foreach (var member in members)
            {
                member.AddedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            }
            await db.SaveChangesAsync();
        }
        var token = TokenFromUrl((await EnablePartyAsync(owner, albumId)).GetProperty("partyUrl").GetString()!);
        var anon = _factory.CreateClient();

        var first = await OrderedPartyIdsAsync(anon, token);
        var second = await OrderedPartyIdsAsync(anon, token);
        Assert.Equal(5, first.Count);
        Assert.Equal(first, second);
        Assert.Equal(first.OrderBy(id => id, StringComparer.Ordinal).ToList(), first);
    }

    private static async Task<List<string>> OrderedPartyIdsAsync(HttpClient anon, string token)
    {
        var resp = await anon.GetFromJsonAsync<JsonElement>($"/api/party/{token}/items");
        return resp.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString()!)
            .ToList();
    }

    [Fact]
    public async Task Public_Party_Works_While_Enabled_And_404s_After_Disable()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Summer party");
        await AddPngAsync(owner, albumId, "a.png");
        var status = await EnablePartyAsync(owner, albumId);
        var token = TokenFromUrl(status.GetProperty("partyUrl").GetString()!);

        var anon = _factory.CreateClient();
        var album = await anon.GetAsync($"/api/party/{token}");
        Assert.Equal(HttpStatusCode.OK, album.StatusCode);
        var albumJson = await album.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Summer party", albumJson.GetProperty("albumName").GetString());
        Assert.Equal(1, albumJson.GetProperty("itemCount").GetInt32());

        var items = await anon.GetFromJsonAsync<JsonElement>($"/api/party/{token}/items");
        Assert.Equal(1, items.GetProperty("items").GetArrayLength());

        // Owner disables → the token stops working on the very next request.
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{token}/items")).StatusCode);
    }

    [Fact]
    public async Task Disabling_ShowOnTv_Also_Kills_Public_Party_Access()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);
        var token = TokenFromUrl(status.GetProperty("partyUrl").GetString()!);

        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/party/{token}")).StatusCode);

        // Turning off "Show on TV" severs public party access (party ⊆ TV).
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = false }))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{token}")).StatusCode);

        // And party mode reads as off for the owner.
        var reread = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/party-settings");
        Assert.False(reread.GetProperty("partyMode").GetBoolean());
    }

    [Fact]
    public async Task Re_Enabling_Party_Rotates_The_Token()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var first = TokenFromUrl((await EnablePartyAsync(owner, albumId)).GetProperty("partyUrl").GetString()!);

        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();
        var second = TokenFromUrl((await EnablePartyAsync(owner, albumId)).GetProperty("partyUrl").GetString()!);

        Assert.NotEqual(first, second);

        // The OLD token is dead; only the new one works.
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{first}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/party/{second}")).StatusCode);
    }

    [Fact]
    public async Task Unknown_Or_Foreign_Token_Is_A_Generic_404()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceAlbum = await CreateAlbumAsync(alice, "Alice party");
        await EnablePartyAsync(alice, aliceAlbum);

        var anon = _factory.CreateClient();
        // A random token that was never issued.
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/party/{Guid.NewGuid():N}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/party/{Guid.NewGuid():N}/items")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/party/{Guid.NewGuid():N}/media/{Guid.NewGuid()}/preview")).StatusCode);
    }

    // --- Public media: derived, metadata-stripped, no originals ---

    [Fact]
    public async Task Public_Party_Media_Serves_Metadata_Stripped_Derivatives_Not_Originals()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var fileId = await AddJpegWithExifAsync(owner, albumId, "gps.jpg");
        var status = await EnablePartyAsync(owner, albumId);
        var token = TokenFromUrl(status.GetProperty("partyUrl").GetString()!);

        var anon = _factory.CreateClient();

        foreach (var variant in new[] { "thumbnail", "preview", "download" })
        {
            var resp = await anon.GetAsync($"/api/party/{token}/media/{fileId}/{variant}");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.StartsWith("image/", resp.Content.Headers.ContentType!.MediaType!);

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            using var img = Image.Load(bytes);
            // EXIF/GPS (and every other profile) is stripped from the party copy.
            Assert.Null(img.Metadata.ExifProfile);
        }

        // There is no public route to the ORIGINAL bytes: the authenticated
        // content/download endpoints are unreachable anonymously.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"/api/files/{fileId}/content")).StatusCode);
    }

    [Fact]
    public async Task Public_Party_Media_Cannot_Reach_A_File_Outside_The_Token_Album()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var partyAlbum = await CreateAlbumAsync(owner, "Party");
        var otherAlbum = await CreateAlbumAsync(owner, "Not party");
        await AddPngAsync(owner, partyAlbum, "in.png");
        var outsideFile = await AddPngAsync(owner, otherAlbum, "out.png");
        var token = TokenFromUrl((await EnablePartyAsync(owner, partyAlbum)).GetProperty("partyUrl").GetString()!);

        var anon = _factory.CreateClient();
        // A file that belongs to a DIFFERENT album is not addressable via this token.
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/party/{token}/media/{outsideFile}/preview")).StatusCode);
    }

    [Fact]
    public async Task Public_Party_Dtos_Do_Not_Leak_Internals()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        await AddJpegWithExifAsync(owner, albumId, "gps.jpg");
        var token = TokenFromUrl((await EnablePartyAsync(owner, albumId)).GetProperty("partyUrl").GetString()!);

        var anon = _factory.CreateClient();
        var albumRaw = await (await anon.GetAsync($"/api/party/{token}")).Content.ReadAsStringAsync();
        var itemsRaw = await (await anon.GetAsync($"/api/party/{token}/items")).Content.ReadAsStringAsync();

        foreach (var raw in new[] { albumRaw, itemsRaw })
        {
            AssertNoInternals(raw);
        }
    }

    // --- TV party-URL surfacing rule ---

    [Fact]
    public async Task Tv_Album_Dto_Includes_Party_Url_Only_When_Party_Enabled()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        await AddPngAsync(owner, albumId, "a.png");
        // ShowOnTv but NOT party yet.
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = true }))
            .EnsureSuccessStatusCode();
        var cookie = await PairTvAsync(owner);

        var before = (await TvJsonAsync(cookie, "/api/tv/albums"))[0];
        Assert.False(before.GetProperty("partyEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("partyUrl").ValueKind);

        // Enable party → the TV list + items now expose the party URL.
        await EnablePartyAsync(owner, albumId);
        var after = (await TvJsonAsync(cookie, "/api/tv/albums"))[0];
        Assert.True(after.GetProperty("partyEnabled").GetBoolean());
        Assert.StartsWith("/party/", after.GetProperty("partyUrl").GetString());

        var items = await TvJsonAsync(cookie, $"/api/tv/albums/{albumId}/items");
        Assert.True(items.GetProperty("partyEnabled").GetBoolean());
        Assert.StartsWith("/party/", items.GetProperty("partyUrl").GetString());

        // Disable party → the party URL disappears from the TV on the next call.
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();
        var reread = (await TvJsonAsync(cookie, "/api/tv/albums"))[0];
        Assert.False(reread.GetProperty("partyEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, reread.GetProperty("partyUrl").ValueKind);
    }

    [Fact]
    public async Task Tv_Party_Url_Is_A_Relative_Landing_Not_A_Token_Hash()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        await AddPngAsync(owner, albumId, "a.png");
        await EnablePartyAsync(owner, albumId);
        var cookie = await PairTvAsync(owner);

        var raw = await (await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/albums")).Content.ReadAsStringAsync();
        AssertNoInternals(raw);
        // The TV receives the public landing URL, never a token hash.
        Assert.DoesNotContain("tokenHash", raw, StringComparison.OrdinalIgnoreCase);
    }

    // --- helpers ---

    private async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();
        return detail.GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> EnablePartyAsync(HttpClient owner, Guid albumId)
    {
        var resp = await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = true });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string TokenFromUrl(string partyUrl) => partyUrl["/party/".Length..];

    private async Task<Guid> AddPngAsync(HttpClient owner, Guid albumId, string name)
    {
        var fileId = await UploadAsync(owner, name, ImageFixtures.PlainPng(), "image/png");
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
    }

    private async Task<Guid> AddJpegWithExifAsync(HttpClient owner, Guid albumId, string name)
    {
        var fileId = await UploadAsync(owner, name, ImageFixtures.JpegWithExif(includeGps: true), "image/jpeg");
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
    }

    private static async Task<Guid> UploadAsync(HttpClient owner, string name, byte[] bytes, string contentType)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var resp = await owner.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return summary.GetProperty("id").GetGuid();
    }

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

        var pollRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
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
            "gpsLatitude", "dateTaken", "personName", "faceId",
        ];
        foreach (var needle in forbidden)
        {
            Assert.DoesNotContain(needle, raw, StringComparison.OrdinalIgnoreCase);
        }
    }
}
