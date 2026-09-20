using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using NubArca.Api.Access;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The card a chat app draws for a party's link.
///
/// <para>What is pinned here is that it says what opening the link would show
/// and nothing more — the party's name, one line for the phase it is in, and the
/// picture its page opens on, as an address a stranger's fetcher can load — and
/// that a token which opens no party gets the plain product card, whatever the
/// reason.</para>
/// </summary>
public sealed class PartyLinkPreviewTests : IDisposable
{
    private const string Origin = "https://party.example";

    private readonly SqliteWebApplicationFactory _factory = new(new Dictionary<string, string?>
    {
        ["Mail:PublicOrigin"] = Origin,
    });

    public PartyLinkPreviewTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private static readonly string[] HostPermissions =
    [
        Permissions.PartyAccess, Permissions.PartyContributions,
        Permissions.PartyGames, Permissions.PartyPrint, Permissions.PartyFaceSearch,
        Permissions.PrivateVaultAccess,
    ];

    [Fact]
    public async Task A_Partys_Link_Is_Drawn_With_Its_Name_Its_Day_And_The_Cover_Its_Page_Opens_On()
    {
        var party = await SeedPartyAsync();
        await RenameAsync(party, "50 anni di Cesare", new DateTime(2027, 2, 10, 12, 0, 0, DateTimeKind.Utc));
        var invitation = await UploadPngAsync(party.Owner, "invito.png");
        await PutCoversAsync(party, invitation, null);

        var html = await PreviewAsync(party.Token);

        Assert.Contains("<meta property=\"og:title\" content=\"50 anni di Cesare\">", html);
        Assert.Contains("<meta property=\"og:description\" content=\"Sei invitato · 10 febbraio 2027\">", html);
        Assert.Contains($"<meta property=\"og:url\" content=\"{Origin}/party/{party.Token}\">", html);
        Assert.Contains("summary_large_image", html);
        Assert.Contains("noindex", html);

        // The picture is the invitation's cover, on an address a stranger's
        // fetcher can actually load.
        var image = Regex.Match(html, "og:image\" content=\"([^\"]+)\"").Groups[1].Value;
        Assert.StartsWith($"{Origin}/api/party/{party.Token}/cover/invitation/media", image);
        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.CreateClient().GetAsync(image[Origin.Length..])).StatusCode);
    }

    [Fact]
    public async Task At_The_Party_The_Card_Says_So_And_Shows_The_Live_Cover_In_The_Readers_Language()
    {
        var party = await SeedPartyAsync();
        var invitation = await UploadPngAsync(party.Owner, "invito.png");
        var live = await UploadPngAsync(party.Owner, "festa.png");
        await PutCoversAsync(party, invitation, live);
        (await party.Owner.PostAsJsonAsync(
                $"/api/parties/{party.PartyId}/start-live", new { version = await VersionAsync(party) }))
            .EnsureSuccessStatusCode();

        var italian = await PreviewAsync(party.Token);
        Assert.Contains("content=\"La festa è in corso\"", italian);
        Assert.Contains($"{Origin}/api/party/{party.Token}/cover/live/media", italian);

        Assert.Contains("content=\"The party is on\"", await PreviewAsync(party.Token, "en-GB,en;q=0.9"));
    }

    [Fact]
    public async Task A_Link_That_Opens_No_Party_Gets_The_Plain_Product_Card()
    {
        var html = await PreviewAsync("not-a-party-token");

        Assert.Contains("<meta property=\"og:title\" content=\"NubArca\">", html);
        Assert.Contains($"<meta property=\"og:image\" content=\"{Origin}/brand/nubarca-pwa-512.png\">", html);
        Assert.DoesNotContain("og:description", html);
        Assert.DoesNotContain("og:url", html);
    }

    [Fact]
    public async Task The_Hosts_Title_Is_Text_And_Never_Markup()
    {
        var party = await SeedPartyAsync();
        await RenameAsync(party, "Anna \"la festa\" <3 & co", null);

        var html = await PreviewAsync(party.Token);

        Assert.Contains("content=\"Anna &quot;la festa&quot; &lt;3 &amp; co\"", html);
        Assert.DoesNotContain("<3", html);
    }

    [Fact]
    public void Without_A_Configured_Public_Origin_The_Card_Carries_No_Address()
    {
        var html = PartyLinkPreview.ForParty(
            PartyLinkPreview.Origin(null), "/party/t", "Festa", "Sei invitato", "/api/party/t/cover/invitation/media");

        Assert.Contains("og:title", html);
        Assert.DoesNotContain("og:image", html);
        Assert.DoesNotContain("og:url", html);
        Assert.Null(PartyLinkPreview.Origin("not a url"));
    }

    // --- the PERSONAL invitation's card --------------------------------------

    [Fact]
    public async Task A_Shared_Invitation_Is_Drawn_With_The_Partys_Name_Its_Day_And_Its_Cover()
    {
        var party = await SeedPartyAsync();
        await RenameAsync(party, "50 Cesare", new DateTime(2027, 2, 10, 12, 0, 0, DateTimeKind.Utc));
        var invitation = await UploadPngAsync(party.Owner, "invito.png");
        await PutCoversAsync(party, invitation, null);
        var token = await InviteTokenAsync(party, "Sara", "sara@example.com");

        var html = await InvitePreviewAsync(token);

        // The party's own card, on the invitation's route: this is the link a
        // host actually forwards, and until it had a preview of its own it drew
        // the product's logo under the word "NubArca".
        Assert.Contains("<meta property=\"og:title\" content=\"50 Cesare\">", html);
        Assert.Contains("<meta property=\"og:description\" content=\"Sei invitato · 10 febbraio 2027\">", html);
        Assert.Contains($"<meta property=\"og:url\" content=\"{Origin}/party/invite/{token}\">", html);
        Assert.Contains("summary_large_image", html);
        Assert.Contains("noindex", html);

        // And the picture is fetchable by a stranger's crawler, on the
        // invitation's OWN token — the only address it holds.
        var image = Regex.Match(html, "og:image\" content=\"([^\"]+)\"").Groups[1].Value;
        Assert.StartsWith($"{Origin}/api/party-invitations/{token}/cover/invitation/media", image);
        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.CreateClient().GetAsync(image[Origin.Length..])).StatusCode);
    }

    [Fact]
    public async Task The_Invitations_Card_Describes_The_Party_And_Never_The_Guest()
    {
        var party = await SeedPartyAsync();
        await RenameAsync(party, "50 Cesare", null);
        var token = await InviteTokenAsync(party, "Sara Bianchi", "sara.bianchi@example.com");

        var html = await InvitePreviewAsync(token);

        // THE INVARIANT. A forwarded invitation is quoted and screenshotted into
        // group chats, so who it was addressed to, their address and their
        // answer must not be in the card a crawler draws. What it may say is
        // what a poster on a wall could say.
        Assert.DoesNotContain("Sara", html);
        Assert.DoesNotContain("Bianchi", html);
        Assert.DoesNotContain("example.com", html);
        Assert.DoesNotContain("rsvp", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("50 Cesare", html);
    }

    [Fact]
    public async Task A_Link_That_Opens_No_Invitation_Gets_The_Plain_Product_Card()
    {
        var html = await InvitePreviewAsync("not-an-invitation-token");

        Assert.Contains("<meta property=\"og:title\" content=\"NubArca\">", html);
        Assert.Contains($"<meta property=\"og:image\" content=\"{Origin}/brand/nubarca-pwa-512.png\">", html);
        Assert.DoesNotContain("og:description", html);
        Assert.DoesNotContain("og:url", html);
    }

    [Fact]
    public async Task Drawing_The_Card_Takes_No_Answer_And_Leaves_The_Invitation_Untouched()
    {
        var party = await SeedPartyAsync();
        var token = await InviteTokenAsync(party, "Luca", "luca@example.com");
        var before = await _factory.CreateClient()
            .GetFromJsonAsync<JsonElement>($"/api/party-invitations/{token}");

        await InvitePreviewAsync(token);

        // A crawler is not a guest. Nothing it does may count as opening the
        // invitation or change what the host sees.
        var after = await _factory.CreateClient()
            .GetFromJsonAsync<JsonElement>($"/api/party-invitations/{token}");
        Assert.Equal(
            before.GetProperty("invitation").GetProperty("version").GetInt32(),
            after.GetProperty("invitation").GetProperty("version").GetInt32());
    }

    // --- helpers -----------------------------------------------------------

    private sealed record SeededParty(HttpClient Owner, Guid PartyId, string Token);

    private async Task<SeededParty> SeedPartyAsync()
    {
        var (_, owner) = await _factory.CreatePermissionClientAsync(
            $"host-{Guid.NewGuid():N}@example.com", HostPermissions);
        var albumId = (await (await owner.PostAsJsonAsync(
            "/api/albums", new { name = $"Album {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var photoId = await UploadPngAsync(owner, "album-photo.png");
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = photoId }))
            .EnsureSuccessStatusCode();

        var enable = await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-settings", new { enabled = true });
        enable.EnsureSuccessStatusCode();
        var settings = await enable.Content.ReadFromJsonAsync<JsonElement>();
        return new SeededParty(
            owner, settings.GetProperty("partyId").GetGuid(),
            settings.GetProperty("partyUrl").GetString()!["/party/".Length..]);
    }

    private async Task<string> PreviewAsync(string token, string? acceptLanguage = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/party/{token}/link-preview");
        request.Headers.UserAgent.ParseAdd("WhatsApp/2.24.1 A");
        if (acceptLanguage is not null) request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);
        var response = await _factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<string> InvitePreviewAsync(string token, string? acceptLanguage = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/party-invitations/{token}/link-preview");
        request.Headers.UserAgent.ParseAdd("WhatsApp/2.24.1 A");
        if (acceptLanguage is not null) request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);
        var response = await _factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>A group with one named guest, and the personal token for it.</summary>
    private async Task<string> InviteTokenAsync(SeededParty party, string name, string email)
    {
        var created = await party.Owner.PostAsJsonAsync(
            $"/api/parties/{party.PartyId}/invitation-groups",
            new
            {
                label = name, recipientEmail = email, phone = (string?)null,
                maxAdditionalGuests = 0, guests = new[] { new { name } }, version = 0,
            });
        created.EnsureSuccessStatusCode();
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var groupId = body.GetProperty("groups").EnumerateArray()
            .Single(g => g.GetProperty("label").GetString() == name)
            .GetProperty("id").GetGuid();
        return PartyInvitationTestKit.CurrentToken(_factory, groupId);
    }

    private static async Task<int> VersionAsync(SeededParty party) =>
        (await party.Owner.GetFromJsonAsync<JsonElement>($"/api/parties/{party.PartyId}"))
            .GetProperty("version").GetInt32();

    private static async Task RenameAsync(SeededParty party, string title, DateTime? eventStartsAt) =>
        (await party.Owner.PatchAsJsonAsync($"/api/parties/{party.PartyId}", new
        {
            title, description = (string?)null, eventStartsAt,
            guestAccessExpiresAt = (DateTime?)null, libraryAccessExpiresAt = (DateTime?)null,
            version = await VersionAsync(party),
        })).EnsureSuccessStatusCode();

    private static async Task PutCoversAsync(SeededParty party, Guid? invitation, Guid? live) =>
        (await party.Owner.PutAsJsonAsync($"/api/parties/{party.PartyId}/covers", new
        {
            invitationCoverFileItemId = invitation, liveCoverFileItemId = live,
            version = await VersionAsync(party),
        })).EnsureSuccessStatusCode();

    private static async Task<Guid> UploadPngAsync(HttpClient owner, string name)
    {
        var part = new ByteArrayContent(ImageFixtures.PlainPng());
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var upload = await owner.PostAsync(
            "/api/files", new MultipartFormDataContent { { part, "file", name } });
        upload.EnsureSuccessStatusCode();
        return (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
