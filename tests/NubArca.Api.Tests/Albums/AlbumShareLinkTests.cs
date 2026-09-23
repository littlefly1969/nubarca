using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Albums;

/// <summary>
/// SHARE BY LINK — an album, an address, and whoever is holding it.
///
/// What these defend:
///   * the link is INDEPENDENT of any party. It works on an album that never
///     had one, and an album that has one is reached by two different tokens
///     that open two different things — following either is never a way to the
///     other;
///   * reading is what the link is for; contributing is a second switch the
///     owner flips without invalidating an address already sent to thirty
///     people;
///   * there is NO way to delete through it. Not gated, absent;
///   * revoking is immediate, because every request re-reads the row;
///   * the ceiling belongs to the LINK and not to a person — a share has no
///     people in it — and reaching it refuses the upload while reading stays
///     open, so an owner hears it from the product and not from complaints;
///   * an original leaves only when the owner said so, because an original
///     carries the GPS the camera wrote and a link is a public share.
/// </summary>
public sealed class AlbumShareLinkTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public AlbumShareLinkTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task An_album_with_no_party_can_still_be_shared()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);

        var link = await ShareAsync(owner, album);
        var token = TokenOf(link);

        // Nothing about a party was needed to get here, and nothing about one
        // comes back.
        var view = await _factory.CreateClient()
            .GetFromJsonAsync<JsonElement>($"/api/album-share/{token}");
        Assert.Equal("Album", view.GetProperty("albumName").GetString());
        Assert.True(view.GetProperty("canUpload").GetBoolean());
        Assert.False(view.GetProperty("canDownloadOriginal").GetBoolean());
    }

    [Fact]
    public async Task The_album_link_is_not_the_party_link_and_neither_opens_the_other()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);

        // The same album, with a party on it as well.
        var settings = await owner.PatchAsJsonAsync(
            $"/api/albums/{album}/party-settings", new { enabled = true });
        settings.EnsureSuccessStatusCode();
        var status = await settings.Content.ReadFromJsonAsync<JsonElement>();
        var partyToken = status.GetProperty("partyUrl").GetString()!["/party/".Length..];

        var shareToken = TokenOf(await ShareAsync(owner, album));
        Assert.NotEqual(partyToken, shareToken);

        var anon = _factory.CreateClient();
        // Each token opens ITS OWN surface and is nothing to the other one.
        Assert.Equal(HttpStatusCode.OK,
            (await anon.GetAsync($"/api/album-share/{shareToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/album-share/{partyToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/party/{shareToken}")).StatusCode);
    }

    [Fact]
    public async Task Opening_the_panel_twice_does_not_invalidate_the_address_already_sent()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);

        var first = TokenOf(await ShareAsync(owner, album));
        var again = TokenOf(await ShareAsync(owner, album));
        Assert.Equal(first, again);

        // Rotating is the OTHER verb, and it does exactly what creating must
        // not: the old address stops opening anything.
        var rotated = await owner.PostAsync($"/api/albums/{album}/share-link/rotate", null);
        rotated.EnsureSuccessStatusCode();
        var fresh = TokenOf(await rotated.Content.ReadFromJsonAsync<JsonElement>());
        Assert.NotEqual(first, fresh);

        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/album-share/{first}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await anon.GetAsync($"/api/album-share/{fresh}")).StatusCode);
    }

    [Fact]
    public async Task Closing_contribution_leaves_the_link_open_for_reading()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);
        var token = TokenOf(await ShareAsync(owner, album));

        (await owner.PatchAsJsonAsync($"/api/albums/{album}/share-link",
            new { uploadEnabled = false })).EnsureSuccessStatusCode();

        var anon = _factory.CreateClient();
        var view = await anon.GetFromJsonAsync<JsonElement>($"/api/album-share/{token}");
        // The link still opens. Only the second switch moved.
        Assert.False(view.GetProperty("canUpload").GetBoolean());

        var refused = await anon.PostAsync($"/api/album-share/{token}/upload", OneFile());
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("uploads_disabled",
            (await refused.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("error").GetString());
    }

    [Fact]
    public async Task Revoking_closes_the_link_on_the_very_next_request()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);
        var token = TokenOf(await ShareAsync(owner, album));
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/album-share/{token}")).StatusCode);

        (await owner.DeleteAsync($"/api/albums/{album}/share-link")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/album-share/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/album-share/{token}/items")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.PostAsync($"/api/album-share/{token}/upload", OneFile())).StatusCode);

        // Revoking twice is not an error: it means the same thing.
        (await owner.DeleteAsync($"/api/albums/{album}/share-link")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_ceiling_belongs_to_the_link_and_refuses_rather_than_switching_off()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);
        var token = TokenOf(await ShareAsync(owner, album));
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/share-link",
            new { maxUploads = 1 })).EnsureSuccessStatusCode();

        var anon = _factory.CreateClient();
        var first = await anon.PostAsync($"/api/album-share/{token}/upload", OneFile());
        first.EnsureSuccessStatusCode();
        Assert.Equal(1, (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accepted").GetInt32());

        // A SECOND phone, deliberately: the ceiling is the link's, so a fresh
        // caller meets the same wall rather than getting their own allowance.
        var other = _factory.CreateClient();
        var second = await other.PostAsync($"/api/album-share/{token}/upload", OneFile());
        second.EnsureSuccessStatusCode();
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("accepted").GetInt32());
        Assert.Equal("upload_limit_reached", body.GetProperty("stopped").GetString());

        // And READING is untouched: nothing switched itself off.
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync($"/api/album-share/{token}")).StatusCode);
        var view = await other.GetFromJsonAsync<JsonElement>($"/api/album-share/{token}");
        Assert.False(view.GetProperty("canUpload").GetBoolean());
        Assert.Equal(0, view.GetProperty("uploadsRemaining").GetInt32());

        // Raising the ceiling opens it again, which is the owner's move to make.
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/share-link",
            new { maxUploads = 10 })).EnsureSuccessStatusCode();
        var third = await other.PostAsync($"/api/album-share/{token}/upload", OneFile());
        Assert.Equal(1, (await third.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accepted").GetInt32());
    }

    [Fact]
    public async Task There_is_no_way_to_delete_anything_through_a_share()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);
        var token = TokenOf(await ShareAsync(owner, album));
        var anon = _factory.CreateClient();

        // Not refused by a rule — the routes do not exist. 404 and 405 are both
        // "there is nothing here"; what must never appear is a 2xx.
        foreach (var path in new[]
        {
            $"/api/album-share/{token}/items/{Guid.NewGuid()}",
            $"/api/album-share/{token}/media/{Guid.NewGuid()}",
            $"/api/album-share/{token}",
        })
        {
            var response = await anon.DeleteAsync(path);
            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"DELETE {path} answered {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task A_stranger_and_a_foreign_album_are_the_same_nothing()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var album = await AlbumAsync(alice);

        // Bob owns nothing here, so every owner route answers the same 404 —
        // none of them can be used to learn that the album exists.
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/albums/{album}/share-link")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.PostAsync($"/api/albums/{album}/share-link", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.DeleteAsync($"/api/albums/{album}/share-link")).StatusCode);

        // And a token that never existed is the same nothing as a revoked one.
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient()
            .GetAsync("/api/album-share/not-a-real-token-at-all-0123456789")).StatusCode);
    }

    [Fact]
    public async Task The_public_view_says_what_may_be_done_and_nothing_about_the_owner()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);
        var token = TokenOf(await ShareAsync(owner, album));

        var view = await _factory.CreateClient()
            .GetFromJsonAsync<JsonElement>($"/api/album-share/{token}");

        // An allow-list, not a deny-list: any NEW field reaching this boundary
        // fails here, which is the only way a public contract stays a decision.
        string[] allowed =
        [
            "albumName", "coverUrl", "itemCount",
            "canUpload", "canDownloadOriginal", "uploadsRemaining",
        ];
        Assert.Equal(
            allowed.Order(),
            view.EnumerateObject().Select(p => p.Name).Order());
    }

    // THE CONCURRENT CREATE LIVES IN AlbumShareConcurrencyPostgresTests.
    //
    // It cannot live here. This host hands every scope ONE pooled SQLite
    // connection, so two "simultaneous" requests are serialised by the
    // transport before they reach the code — and now that the mutations open a
    // real transaction, a second one on that connection does not race, it
    // throws "cannot start a transaction within a transaction". A race that
    // cannot happen proves nothing about a race that can.

    [Fact]
    public async Task Rotating_leaves_exactly_one_live_link_behind_it()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);
        await ShareAsync(owner, album);

        (await owner.PostAsync($"/api/albums/{album}/share-link/rotate", null))
            .EnsureSuccessStatusCode();

        // The revoke and the mint are one transaction, so there is never a
        // moment with two live rows and never one with none.
        Assert.Equal(1, await _factory.CountLiveAlbumShareLinksAsync(album));
    }

    [Fact]
    public async Task An_expiry_can_be_taken_off_again()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);
        await ShareAsync(owner, album);

        var dated = await owner.PatchAsJsonAsync($"/api/albums/{album}/share-link",
            new { expiresAt = "2030-01-01T00:00:00Z" });
        dated.EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.String,
            (await dated.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("expiresAt").ValueKind);

        // "No expiry" IS null, and null already means unchanged everywhere on
        // this request — so without an explicit sentence an owner who once set
        // a date could never take it off.
        var cleared = await owner.PatchAsJsonAsync($"/api/albums/{album}/share-link",
            new { clearExpiry = true });
        cleared.EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null,
            (await cleared.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("expiresAt").ValueKind);
    }

    [Fact]
    public async Task A_share_upload_leaves_no_state_the_party_owns()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);

        // The album has a party TOO, which is the case that exposed this: the
        // ingest wrote a PartyUploadItem whatever the caller was, the party's
        // moderation queue listed rows by album, and tearing the party down
        // deleted every row for the album. A feature that advertises
        // independence must not leave state the Party owns.
        var settings = await owner.PatchAsJsonAsync(
            $"/api/albums/{album}/party-settings", new { enabled = true });
        settings.EnsureSuccessStatusCode();

        var token = TokenOf(await ShareAsync(owner, album));
        var uploaded = await _factory.CreateClient()
            .PostAsync($"/api/album-share/{token}/upload", OneFile());
        uploaded.EnsureSuccessStatusCode();
        Assert.Equal(1, (await uploaded.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accepted").GetInt32());

        // The photograph is in the album...
        var items = await _factory.CreateClient()
            .GetFromJsonAsync<JsonElement>($"/api/album-share/{token}/items");
        Assert.Equal(1, items.GetProperty("items").GetArrayLength());

        // ...and NOWHERE in the party. No provenance row, so nothing for the
        // moderation queue to list and nothing for the eraser to take away.
        Assert.Equal(0, await _factory.CountPartyUploadItemsAsync(album));

        var queue = await owner.GetFromJsonAsync<JsonElement>(
            $"/api/albums/{album}/party-uploads");
        Assert.Equal(0, queue.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Readding_a_revoked_guest_does_not_exceed_max_guests()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);
        await ShareAsync(owner, album);

        // Fill the list exactly.
        for (var i = 0; i < AlbumShareLimits.MaxGuests; i++)
        {
            (await AddGuestAsync(owner, album, $"g{i}@example.com")).EnsureSuccessStatusCode();
        }

        // THE WAY PAST THE CEILING. Remove one, add a different one — back to
        // the limit — then bring the removed one back. The count was checked
        // only when a NEW row was created, so a reactivation walked straight
        // through it: fifty, remove A, add B, re-add A, fifty-one.
        var first = await GuestIdAsync(owner, album, "g0@example.com");
        (await owner.DeleteAsync($"/api/albums/{album}/share-link/guests/{first}"))
            .EnsureSuccessStatusCode();
        (await AddGuestAsync(owner, album, "new@example.com")).EnsureSuccessStatusCode();

        var readd = await AddGuestAsync(owner, album, "g0@example.com");

        // Refused with the SAME answer a new address gets at the ceiling, and
        // the row is left revoked rather than half-restored.
        Assert.Equal(HttpStatusCode.BadRequest, readd.StatusCode);
        var link = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{album}/share-link");
        var live = link.GetProperty("guests").EnumerateArray().ToList();
        Assert.Equal(AlbumShareLimits.MaxGuests, live.Count);
        Assert.DoesNotContain(live, g => g.GetProperty("email").GetString() == "g0@example.com");
    }

    // --- helpers -----------------------------------------------------------

    private static MultipartFormDataContent OneFile()
    {
        var content = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(MinimalJpeg());
        bytes.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(bytes, "file", $"{Guid.NewGuid():N}.jpg");
        return content;
    }

    /// <summary>The smallest thing the pipeline will accept as a photograph.</summary>
    private static byte[] MinimalJpeg() =>
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43,
        0x00, .. Enumerable.Repeat((byte)0x08, 64),
        0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00,
        0xFF, 0xC4, 0x00, 0x14, 0x00, 0x01, .. Enumerable.Repeat((byte)0x00, 15), 0x00,
        0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x37, 0xFF, 0xD9,
    ];

    private static string TokenOf(JsonElement link) =>
        link.GetProperty("url").GetString()!["/album/".Length..];

    private static async Task<JsonElement> ShareAsync(HttpClient owner, Guid album)
    {
        var response = await owner.PostAsync($"/api/albums/{album}/share-link", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> AddGuestAsync(
        HttpClient owner, Guid album, string email) =>
        owner.PostAsJsonAsync($"/api/albums/{album}/share-link/guests", new { email });

    private static async Task<Guid> GuestIdAsync(HttpClient owner, Guid album, string email)
    {
        var link = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{album}/share-link");
        return link.GetProperty("guests").EnumerateArray()
            .Single(g => g.GetProperty("email").GetString() == email)
            .GetProperty("id").GetGuid();
    }

    private static async Task<Guid> AlbumAsync(HttpClient owner)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name = "Album" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
