using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using NubArca.Api.Tv;
using SixLabors.ImageSharp;

namespace NubArca.Api.Tests.Party;

// Anonymous party UPLOAD: guests add photos to a PartyMode album via a SEPARATE
// upload token. Read/upload tokens are independent; disabling party (or upload)
// refuses uploads immediately; uploads are image-only, owner-scoped, and only
// ever surface publicly through the safe metadata-stripped derived media path.
public sealed class PartyUploadTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyUploadTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Anonymous_Upload_Succeeds_With_Valid_Upload_Token_And_Appears_Via_Safe_Media()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);
        var uploadToken = UploadTokenFromStatus(status);
        var viewToken = ViewTokenFromStatus(status);

        var anon = _factory.CreateClient();
        var result = await UploadAsync(anon, uploadToken, ("gps.jpg", ImageFixtures.JpegWithExif(includeGps: true), "image/jpeg"));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("accepted").GetInt32());
        Assert.Equal(0, body.GetProperty("rejected").GetInt32());

        // The uploaded photo now shows up in the PUBLIC party view, and its media
        // is served through the metadata-stripped derived path (EXIF/GPS gone).
        var items = await anon.GetFromJsonAsync<JsonElement>($"/api/party/{viewToken}/items");
        Assert.Equal(1, items.GetProperty("items").GetArrayLength());
        var previewUrl = items.GetProperty("items")[0].GetProperty("previewUrl").GetString()!;
        var media = await anon.GetAsync(previewUrl);
        Assert.Equal(HttpStatusCode.OK, media.StatusCode);
        using var img = Image.Load(await media.Content.ReadAsByteArrayAsync());
        Assert.Null(img.Metadata.ExifProfile);
    }

    [Fact]
    public async Task Upload_Fails_After_Party_Disabled()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));

        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();

        var anon = _factory.CreateClient();
        var result = await UploadAsync(anon, uploadToken, ("a.png", ImageFixtures.PlainPng(), "image/png"));
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task Upload_Fails_When_Owner_Turns_Upload_Off_But_Keeps_Party()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));

        // Party stays on, upload sub-switch off. View token stays valid; upload dies.
        var status = await (await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-settings", new { enabled = true, uploadEnabled = false }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(status.GetProperty("partyMode").GetBoolean());
        Assert.False(status.GetProperty("uploadEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("uploadUrl").ValueKind);

        var anon = _factory.CreateClient();
        var result = await UploadAsync(anon, uploadToken, ("a.png", ImageFixtures.PlainPng(), "image/png"));
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);

        // View still works.
        Assert.Equal(HttpStatusCode.OK,
            (await anon.GetAsync($"/api/party/{ViewTokenFromStatus(await GetPartyAsync(owner, albumId))}")).StatusCode);
    }

    [Fact]
    public async Task Upload_Fails_With_Unknown_Or_Foreign_Token()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceAlbum = await CreateAlbumAsync(alice, "Alice party");
        await EnablePartyAsync(alice, aliceAlbum);

        var anon = _factory.CreateClient();
        var result = await UploadAsync(anon, Guid.NewGuid().ToString("N"), ("a.png", ImageFixtures.PlainPng(), "image/png"));
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task View_Token_Cannot_Upload_And_Upload_Token_Cannot_View()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        var uploadToken = UploadTokenFromStatus(status);
        Assert.NotEqual(viewToken, uploadToken);

        var anon = _factory.CreateClient();
        // View token presented to the upload endpoint → 404.
        Assert.Equal(HttpStatusCode.NotFound,
            (await UploadAsync(anon, viewToken, ("a.png", ImageFixtures.PlainPng(), "image/png"))).StatusCode);
        // Upload token presented to the view endpoints → 404.
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{uploadToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{uploadToken}/items")).StatusCode);
    }

    [Fact]
    public async Task Upload_Token_Is_Stored_As_Hash_Only()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var link = await db.PartyAlbumLinks.SingleAsync(p => p.OwnerUserId == ownerId && p.AlbumId == albumId);

        Assert.NotNull(link.UploadTokenHash);
        Assert.Equal(64, link.UploadTokenHash!.Length);
        Assert.NotEqual(uploadToken, link.UploadTokenHash);
        Assert.Equal(PartyLinkService.HashToken(uploadToken), link.UploadTokenHash);
        // The two tokens are distinct and neither equals the other's hash.
        Assert.NotEqual(link.TokenHash, link.UploadTokenHash);
    }

    [Fact]
    public async Task Dangerous_Or_NonImage_Files_Are_Rejected_And_Not_Added()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));

        var anon = _factory.CreateClient();
        // (a) A non-image declared type is rejected by the cheap pre-gate.
        var html = await UploadAsync(anon, uploadToken, ("evil.html", Encoding.UTF8.GetBytes("<script>x</script>"), "text/html"));
        var htmlBody = await html.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, htmlBody.GetProperty("accepted").GetInt32());
        Assert.Equal(1, htmlBody.GetProperty("rejected").GetInt32());

        // (b) A file that LIES (declared image/png, bytes are not an image) is
        // caught by the authoritative server-side detection gate.
        var fake = await UploadAsync(anon, uploadToken, ("fake.png", Encoding.UTF8.GetBytes("definitely not an image"), "image/png"));
        var fakeBody = await fake.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, fakeBody.GetProperty("accepted").GetInt32());
        Assert.Equal(1, fakeBody.GetProperty("rejected").GetInt32());

        // Nothing landed in the album, and the owner's active library gained no file.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.AlbumItems.CountAsync(ai => ai.AlbumId == albumId));
        Assert.Equal(0, await db.FileItems.CountAsync(f => f.OwnerUserId == ownerId && f.DeletedAt == null));
    }

    [Fact]
    public async Task Oversized_File_Is_Rejected()
    {
        using var factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Party:MaxUploadBytes"] = "16", // 16 bytes — any real image exceeds it
        });
        factory.EnsureDatabaseCreated();

        var (_, owner) = await factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));

        var anon = factory.CreateClient();
        var result = await UploadAsync(anon, uploadToken, ("big.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("accepted").GetInt32());
        Assert.Equal(1, body.GetProperty("rejected").GetInt32());
    }

    [Fact]
    public async Task Uploaded_File_Is_Owned_By_The_Album_Owner_Only()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync("owner@example.com");
        var albumId = await CreateAlbumAsync(owner, "Party");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));

        // A second, unrelated owner exists.
        var (otherId, _) = await _factory.CreateAuthenticatedClientAsync("other@example.com");

        var anon = _factory.CreateClient();
        (await UploadAsync(anon, uploadToken, ("a.jpg", ImageFixtures.JpegWithExif(), "image/jpeg")))
            .EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.SingleAsync(f => f.OwnerUserId == ownerId && f.DeletedAt == null);
        // The file belongs to the party owner, is a member of the party album, and
        // the other owner has nothing.
        Assert.True(await db.AlbumItems.AnyAsync(ai => ai.AlbumId == albumId && ai.FileItemId == file.Id));
        Assert.Equal(0, await db.FileItems.CountAsync(f => f.OwnerUserId == otherId));
    }

    [Fact]
    public async Task Upload_Response_Exposes_No_Internals()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));

        var anon = _factory.CreateClient();
        var raw = await (await UploadAsync(anon, uploadToken, ("a.jpg", ImageFixtures.JpegWithExif(), "image/jpeg")))
            .Content.ReadAsStringAsync();
        foreach (var needle in new[]
        {
            "StorageKey", "BlobObjectId", "sha256", "TokenHash", "physicalPath",
            "OwnerUserId", "fileItemId", "stack", "Exception", "PayloadJson",
        })
        {
            Assert.DoesNotContain(needle, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Tv_Album_Dto_Includes_Upload_Url_Only_When_Upload_Enabled()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "On TV");
        await AddPngAsync(owner, albumId, "a.png");
        await EnablePartyAsync(owner, albumId);
        var cookie = await PairTvAsync(owner);

        var album = (await TvJsonAsync(cookie, "/api/tv/albums"))[0];
        Assert.True(album.GetProperty("partyEnabled").GetBoolean());
        Assert.StartsWith("/party/", album.GetProperty("partyUploadUrl").GetString());
        // The upload URL is a landing path, not a token hash.
        Assert.EndsWith("/upload", album.GetProperty("partyUploadUrl").GetString());

        // Owner turns upload off → the TV upload URL disappears (view stays).
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = true, uploadEnabled = false }))
            .EnsureSuccessStatusCode();
        var after = (await TvJsonAsync(cookie, "/api/tv/albums"))[0];
        Assert.True(after.GetProperty("partyEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("partyUploadUrl").ValueKind);
        Assert.StartsWith("/party/", after.GetProperty("partyUrl").GetString());
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

    private async Task<JsonElement> GetPartyAsync(HttpClient owner, Guid albumId)
        => await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/party-settings");

    private static string ViewTokenFromStatus(JsonElement status)
        => status.GetProperty("partyUrl").GetString()!["/party/".Length..];

    private static string UploadTokenFromStatus(JsonElement status)
    {
        var url = status.GetProperty("uploadUrl").GetString()!; // "/party/{token}/upload"
        var rest = url["/party/".Length..];
        return rest[..rest.IndexOf("/upload", StringComparison.Ordinal)];
    }

    private static Task<HttpResponseMessage> UploadAsync(
        HttpClient anon, string uploadToken, params (string Name, byte[] Bytes, string ContentType)[] files)
    {
        var multipart = new MultipartFormDataContent();
        foreach (var (name, bytes, contentType) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            multipart.Add(part, "file", name);
        }
        return anon.PostAsync($"/api/party/{uploadToken}/upload", multipart);
    }

    private async Task<Guid> AddPngAsync(HttpClient owner, Guid albumId, string name)
    {
        var part = new ByteArrayContent(ImageFixtures.PlainPng());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var resp = await owner.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var fileId = summary.GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
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

    private async Task<JsonElement> TvJsonAsync(string setCookie, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(setCookie)}");
        var response = await _factory.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string CookieValue(string setCookie)
    {
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }
}
