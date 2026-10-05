using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using NubArca.Api.Albums.Sharing;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;

namespace NubArca.Api.Tests.Albums;

/// <summary>
/// An album shared by link, kept on a visitor's home screen: ONE ALBUM, ONE
/// APP, ITS OWN SCOPE.
///
/// What these defend:
///   * the app's id is the ALBUM's, never the link's: a rotated link is the
///     same app, two albums are two apps, and no token is in it;
///   * scopes are disjoint — album from album, album from party — so no
///     installed app claims another's links;
///   * the app opens the link and nothing more: a revoked or rotated link
///     closes it, and a protected album still asks for its second factor,
///     without the manifest or the icon saying what the factor protects.
/// </summary>
public sealed class AlbumShareHomeScreenAppTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public AlbumShareHomeScreenAppTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private const string Listed = "zia@example.com";
    private static readonly Regex AppId = new("^/album/app/[0-9a-f]{32}$");

    [Fact]
    public async Task A_Shared_Album_Is_An_App_Named_After_The_Album_That_Opens_Its_Link()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner, "Gita al lago");
        var token = await ShareAsync(owner, album);

        var response = await _factory.CreateClient().GetAsync($"/api/album-share/{token}/app-manifest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var text = await response.Content.ReadAsStringAsync();
        var manifest = JsonDocument.Parse(text).RootElement;
        var id = manifest.GetProperty("id").GetString()!;
        Assert.Matches(AppId, id);
        Assert.Equal(id + "/", manifest.GetProperty("scope").GetString());
        Assert.Equal($"{id}/open/{token}", manifest.GetProperty("start_url").GetString());
        Assert.Equal("Gita al lago", manifest.GetProperty("name").GetString());
        Assert.Equal("Gita al lago", manifest.GetProperty("short_name").GetString());
        Assert.Equal("standalone", manifest.GetProperty("display").GetString());
        Assert.Equal("#0a0f1a", manifest.GetProperty("background_color").GetString());
        Assert.Equal("#0a0f1a", manifest.GetProperty("theme_color").GetString());
        Assert.Equal(
            [
                $"/api/album-share/{token}/app-icon/192?v=brand|192x192|any",
                $"/api/album-share/{token}/app-icon/512?v=brand|512x512|any",
                $"/api/album-share/{token}/app-icon/512?v=brand&maskable=true|512x512|maskable",
            ],
            manifest.GetProperty("icons").EnumerateArray().Select(i =>
                $"{i.GetProperty("src").GetString()}|{i.GetProperty("sizes").GetString()}|{i.GetProperty("purpose").GetString()}"));
        // The token is in the start URL and the icons, never in the identity,
        // and the album's own id is in nothing at all.
        Assert.DoesNotContain(token, id);
        Assert.DoesNotContain(album.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(album.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_Identity_Is_A_Digest_Of_The_Album_And_The_Partys_Is_Unchanged()
    {
        var album = Guid.NewGuid();
        Assert.Equal("/album/app/" + Digest($"nubarca-album-app:{album:N}"), AlbumShareHomeScreenApp.AppPath(album));
        Assert.Equal(AlbumShareHomeScreenApp.AppPath(album), AlbumShareHomeScreenApp.AppPath(album));
        Assert.NotEqual(AlbumShareHomeScreenApp.AppPath(album), AlbumShareHomeScreenApp.AppPath(Guid.NewGuid()));
        Assert.DoesNotContain(album.ToString("N"), AlbumShareHomeScreenApp.AppPath(album));

        // A party already on a phone keeps its id: moving the builder must not
        // have changed a byte of it.
        var party = Guid.NewGuid();
        Assert.Equal("/party/app/" + Digest($"nubarca-party-app:{party:N}"), PartyHomeScreenApp.AppPath(party));
        // The same id under the two purposes is two different apps.
        Assert.NotEqual(AlbumShareHomeScreenApp.AppPath(album)[^32..], PartyHomeScreenApp.AppPath(album)[^32..]);
    }

    [Fact]
    public async Task A_Rotated_Link_Is_The_Same_App_And_The_Old_Link_Opens_Nothing()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner, "Album");
        var first = await ShareAsync(owner, album);
        var anon = _factory.CreateClient();
        var before = await ManifestAsync(anon, first);

        var rotated = await owner.PostAsync($"/api/albums/{album}/share-link/rotate", null);
        rotated.EnsureSuccessStatusCode();
        var fresh = (await rotated.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("url").GetString()!["/album/".Length..];
        var after = await ManifestAsync(anon, fresh);

        Assert.Equal(before.GetProperty("id").GetString(), after.GetProperty("id").GetString());
        Assert.Equal(before.GetProperty("scope").GetString(), after.GetProperty("scope").GetString());
        Assert.Equal($"{after.GetProperty("id").GetString()}/open/{fresh}", after.GetProperty("start_url").GetString());
        // The app installed from the old link launches into an address that no
        // longer opens anything: rotation is not worked around.
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/album-share/{first}/app-manifest")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/album-share/{first}")).StatusCode);
    }

    [Fact]
    public async Task A_Revoked_Link_Has_No_App_And_No_Icon()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner, "Album");
        var token = await ShareAsync(owner, album);
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/album-share/{token}/app-manifest")).StatusCode);

        (await owner.DeleteAsync($"/api/albums/{album}/share-link")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/album-share/{token}/app-manifest")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/album-share/{token}/app-icon/192")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/album-share/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/album-share/not-a-real-token/app-manifest")).StatusCode);
    }

    [Fact]
    public async Task Two_Albums_And_A_Party_Are_Three_Apps_Whose_Scopes_Never_Claim_Each_Others_Links()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumA = await AlbumAsync(owner, "A");
        var albumB = await AlbumAsync(owner, "B");
        var anon = _factory.CreateClient();
        var a = await ManifestAsync(anon, await ShareAsync(owner, albumA));
        var b = await ManifestAsync(anon, await ShareAsync(owner, albumB));

        // A party on album A itself: the closest an album and a party can be.
        var settings = await owner.PatchAsJsonAsync($"/api/albums/{albumA}/party-settings", new { enabled = true });
        settings.EnsureSuccessStatusCode();
        var partyToken = (await settings.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("partyUrl").GetString()!["/party/".Length..];
        var party = await anon.GetFromJsonAsync<JsonElement>($"/api/party/{partyToken}/app-manifest");

        var apps = new[] { a, b, party };
        Assert.Equal(3, apps.Select(m => m.GetProperty("id").GetString()).Distinct().Count());
        Assert.Equal(3, apps.Select(m => m.GetProperty("scope").GetString()).Distinct().Count());
        for (var i = 0; i < apps.Length; i++)
        {
            var scope = apps[i].GetProperty("scope").GetString()!;
            Assert.DoesNotContain(scope, new[] { "/", "/album/", "/party/", "/album/app/", "/party/app/" });
            for (var j = 0; j < apps.Length; j++)
            {
                var start = apps[j].GetProperty("start_url").GetString()!;
                // Each app's own start URL is in its own scope, and in no other.
                Assert.Equal(i == j, InScope(start, scope));
                if (i != j)
                {
                    var otherScope = apps[j].GetProperty("scope").GetString()!;
                    Assert.False(InScope(otherScope, scope), $"{scope} contains {otherScope}");
                }
            }
        }
        // And the links visitors hold are in no app's scope at all.
        foreach (var app in apps)
        {
            var scope = app.GetProperty("scope").GetString()!;
            Assert.False(InScope($"/party/{partyToken}", scope));
            Assert.False(InScope("/album/some-link-token-0123456789abcdefghijklmnopqrstuv", scope));
        }
    }

    [Theory]
    [InlineData(192)]
    [InlineData(512)]
    public async Task The_Icon_Is_The_Albums_Cover_Square_Without_Metadata_And_Not_Kept(int size)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner, "Album");
        await AddPhotoAsync(owner, album, 64, 40);
        var token = await ShareAsync(owner, album);
        var anon = _factory.CreateClient();

        var manifest = await ManifestAsync(anon, token);
        var src = manifest.GetProperty("icons")[0].GetProperty("src").GetString()!;
        Assert.DoesNotContain("v=brand", src);

        var response = await anon.GetAsync($"/api/album-share/{token}/app-icon/{size}?v=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        // A revoke takes effect on the next request, the icon included.
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        using var icon = Image.Load(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
        Assert.Null(icon.Metadata.ExifProfile);
        Assert.Null(icon.Metadata.XmpProfile);
    }

    [Fact]
    public async Task An_Album_With_No_Picture_Gets_The_Products_Icon_In_Two_Sizes_Only()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var token = await ShareAsync(owner, await AlbumAsync(owner, "Album"));
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var plain = await client.GetAsync($"/api/album-share/{token}/app-icon/192");
        var maskable = await client.GetAsync($"/api/album-share/{token}/app-icon/512?maskable=true");

        Assert.Equal(HttpStatusCode.Redirect, plain.StatusCode);
        Assert.Equal("/brand/nubarca-pwa-192.png", plain.Headers.Location!.ToString());
        Assert.Equal("/brand/nubarca-pwa-maskable-512.png", maskable.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/album-share/{token}/app-icon/100")).StatusCode);
    }

    [Fact]
    public async Task A_Protected_Album_Is_An_App_Only_On_Its_Verified_Device()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner, "Segreto di famiglia");
        await AddPhotoAsync(owner, album, 40, 64);
        var token = await ShareAsync(owner, album);
        (await owner.PostAsJsonAsync($"/api/albums/{album}/share-link/guests",
            new { email = Listed, displayName = "La zia" })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/share-link",
            new { requireSecondFactor = true })).EnsureSuccessStatusCode();

        var visitor = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Not yet verified: the answer the page itself gets — and no name.
        var locked = await visitor.GetAsync($"/api/album-share/{token}/app-manifest");
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        var lockedBody = await locked.Content.ReadAsStringAsync();
        Assert.Contains("second_factor_required", lockedBody);
        Assert.DoesNotContain("Segreto", lockedBody);
        // The icon is the product's, not the cover the factor protects.
        var lockedIcon = await visitor.GetAsync($"/api/album-share/{token}/app-icon/192");
        Assert.Equal(HttpStatusCode.Redirect, lockedIcon.StatusCode);
        Assert.Equal("/brand/nubarca-pwa-192.png", lockedIcon.Headers.Location!.ToString());

        (await visitor.PostAsJsonAsync($"/api/album-share/{token}/challenge", new { email = Listed }))
            .EnsureSuccessStatusCode();
        (await visitor.PostAsJsonAsync($"/api/album-share/{token}/verify",
            new { email = Listed, code = await LastCodeAsync() })).EnsureSuccessStatusCode();

        // The device cookie, pathed to this link, reaches the manifest and the icon.
        var open = await visitor.GetAsync($"/api/album-share/{token}/app-manifest");
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal("Segreto di famiglia",
            (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        var icon = await visitor.GetAsync($"/api/album-share/{token}/app-icon/192");
        Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        Assert.Equal("image/png", icon.Content.Headers.ContentType!.MediaType);

        // Another visitor — a phone that never verified — is still asked.
        var stranger = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await stranger.GetAsync($"/api/album-share/{token}/app-manifest")).StatusCode);
    }

    // --- helpers -----------------------------------------------------------

    private static bool InScope(string url, string scope) => url.StartsWith(scope, StringComparison.Ordinal);

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    private static async Task<JsonElement> ManifestAsync(HttpClient client, string token)
    {
        var response = await client.GetAsync($"/api/album-share/{token}/app-manifest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> AlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<string> ShareAsync(HttpClient owner, Guid album)
    {
        var response = await owner.PostAsync($"/api/albums/{album}/share-link", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("url").GetString()!["/album/".Length..];
    }

    private static async Task AddPhotoAsync(HttpClient owner, Guid album, int width, int height)
    {
        var part = new ByteArrayContent(ImageFixtures.PlainPng(width, height));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var upload = await owner.PostAsync("/api/files",
            new MultipartFormDataContent { { part, "file", $"{Guid.NewGuid():N}.png" } });
        upload.EnsureSuccessStatusCode();
        var photo = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync($"/api/albums/{album}/items", new { fileItemId = photo }))
            .EnsureSuccessStatusCode();
    }

    private async Task<string> LastCodeAsync()
    {
        await _factory.WaitForShareCodesAsync(1);
        var body = _factory.EmailSender.Messages[^1].TextBody;
        var match = Regex.Match(body, @"\b(\d{6})\b");
        Assert.True(match.Success, "the message carried no six-digit code");
        return match.Groups[1].Value;
    }
}
