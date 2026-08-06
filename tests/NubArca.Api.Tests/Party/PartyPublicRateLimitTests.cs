using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;

namespace NubArca.Api.Tests.Party;

// Focused public-party rate-limit split tests. Public JSON/actions/download stay
// on the tight party bucket; only derived thumbnail/preview routes use the
// higher party-media bucket.
public sealed class PartyPublicRateLimitTests
{
    [Fact]
    public async Task Party_Header_And_Items_Still_Use_Tight_Party_Limit()
    {
        using var f = Factory(
            ("RateLimits:Party:PermitLimit", "2"),
            ("RateLimits:Party:WindowSeconds", "60"),
            ("RateLimits:PartyMedia:PermitLimit", "20"));
        var setup = await SetupPartyAsync(f);
        var anon = f.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/party/{setup.ViewToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/party/{setup.ViewToken}/items")).StatusCode);

        var overLimit = await anon.GetAsync($"/api/party/{setup.ViewToken}");
        Assert.Equal(HttpStatusCode.TooManyRequests, overLimit.StatusCode);
    }

    [Fact]
    public async Task Party_Download_Still_Uses_Tight_Party_Limit()
    {
        using var f = Factory(
            ("RateLimits:Party:PermitLimit", "2"),
            ("RateLimits:Party:WindowSeconds", "60"),
            ("RateLimits:PartyMedia:PermitLimit", "20"));
        var setup = await SetupPartyAsync(f);
        var anon = f.CreateClient();
        var url = $"/api/party/{setup.ViewToken}/media/{setup.FileId}/download";

        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(url)).StatusCode);

        var overLimit = await anon.GetAsync(url);
        Assert.Equal(HttpStatusCode.TooManyRequests, overLimit.StatusCode);
    }

    [Fact]
    public async Task Party_Thumbnail_And_Preview_Use_Higher_Media_Limit()
    {
        using var f = Factory(
            ("RateLimits:Party:PermitLimit", "2"),
            ("RateLimits:Party:WindowSeconds", "60"),
            ("RateLimits:PartyMedia:PermitLimit", "4"),
            ("RateLimits:PartyMedia:WindowSeconds", "60"));
        var setup = await SetupPartyAsync(f);
        var anon = f.CreateClient();
        var thumb = $"/api/party/{setup.ViewToken}/media/{setup.FileId}/thumbnail";
        var preview = $"/api/party/{setup.ViewToken}/media/{setup.FileId}/preview";

        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(thumb)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(preview)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(thumb)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(preview)).StatusCode);

        var overMediaLimit = await anon.GetAsync(thumb);
        Assert.Equal(HttpStatusCode.TooManyRequests, overMediaLimit.StatusCode);
    }

    [Fact]
    public async Task Many_Thumbnail_Requests_Do_Not_429_Under_Default_Media_Limit()
    {
        using var f = Factory();
        var setup = await SetupPartyAsync(f);
        var anon = f.CreateClient();
        var url = $"/api/party/{setup.ViewToken}/media/{setup.FileId}/thumbnail";

        for (var i = 0; i < 301; i++)
        {
            var response = await anon.GetAsync(url);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Upload_And_Face_Search_Limits_Remain_Separate_From_Party_Media()
    {
        using var f = Factory(
            ("RateLimits:Party:PermitLimit", "1"),
            ("RateLimits:PartyMedia:PermitLimit", "20"),
            ("RateLimits:PartyUpload:PermitLimit", "2"),
            ("RateLimits:PartyUpload:WindowSeconds", "60"),
            ("RateLimits:PartyFaceSearch:PermitLimit", "2"),
            ("RateLimits:PartyFaceSearch:WindowSeconds", "60"));
        var setup = await SetupPartyAsync(f);
        var anon = f.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            var upload = await anon.PostAsync($"/api/party/{setup.UploadToken}/upload", null);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, upload.StatusCode);

            var faceSearch = await anon.PostAsync($"/api/party/{setup.ViewToken}/face-search", null);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, faceSearch.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await anon.PostAsync($"/api/party/{setup.UploadToken}/upload", null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await anon.PostAsync($"/api/party/{setup.ViewToken}/face-search", null)).StatusCode);
    }

    [Fact]
    public async Task Invalid_And_Missing_Token_Behavior_Stays_Generic_404()
    {
        using var f = Factory();
        f.EnsureDatabaseCreated();
        var anon = f.CreateClient();
        var token = Guid.NewGuid().ToString("N");
        var fileId = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/party/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{token}/items")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{token}/media/{fileId}/thumbnail")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{token}/media/{fileId}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{token}/media/{fileId}/download")).StatusCode);
    }

    [Fact]
    public async Task Public_Media_Remains_Derived_Metadata_Stripped_And_Not_Original()
    {
        using var f = Factory();
        var original = ImageFixtures.JpegWithExif(includeGps: true);
        var setup = await SetupPartyAsync(f, original);
        var anon = f.CreateClient();

        foreach (var variant in new[] { "thumbnail", "preview", "download" })
        {
            var response = await anon.GetAsync($"/api/party/{setup.ViewToken}/media/{setup.FileId}/{variant}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.NotEqual(original, bytes);
            using var image = Image.Load(bytes);
            Assert.Null(image.Metadata.ExifProfile);
        }

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"/api/files/{setup.FileId}/content")).StatusCode);
    }

    private static SqliteWebApplicationFactory Factory(params (string Key, string Value)[] settings)
    {
        var dict = settings.ToDictionary(s => s.Key, s => (string?)s.Value);
        var f = new SqliteWebApplicationFactory(dict);
        f.EnsureDatabaseCreated();
        return f;
    }

    private static async Task<PartySetup> SetupPartyAsync(
        SqliteWebApplicationFactory f,
        byte[]? imageBytes = null)
    {
        var (_, owner) = await f.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner);
        var (name, bytes, contentType) = imageBytes is null
            ? ("party.png", ImageFixtures.PlainPng(), "image/png")
            : ("party.jpg", imageBytes, "image/jpeg");
        var fileId = await UploadAsync(owner, name, bytes, contentType);
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        var status = await EnablePartyAsync(owner, albumId);
        return new PartySetup(
            ViewTokenFromStatus(status),
            UploadTokenFromStatus(status),
            fileId);
    }

    private static async Task<Guid> CreateAlbumAsync(HttpClient owner)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name = "Party" });
        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();
        return detail.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> EnablePartyAsync(HttpClient owner, Guid albumId)
    {
        var resp = await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = true });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
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

    private static string ViewTokenFromStatus(JsonElement status)
        => TokenFromPartyUrl(status.GetProperty("partyUrl").GetString()!);

    private static string UploadTokenFromStatus(JsonElement status)
        => TokenFromPartyUrl(status.GetProperty("uploadUrl").GetString()![..^"/upload".Length]);

    private static string TokenFromPartyUrl(string partyUrl) => partyUrl["/party/".Length..];

    private sealed record PartySetup(string ViewToken, string UploadToken, Guid FileId);
}
