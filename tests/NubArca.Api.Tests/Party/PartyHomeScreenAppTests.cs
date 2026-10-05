using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NubArca.Api.Access;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// A party kept on a guest's home screen: the app of one link, named after the
/// party, drawn from its cover, opening the link it was added from.
/// </summary>
public sealed class PartyHomeScreenAppTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyHomeScreenAppTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private static readonly string[] HostPermissions =
    [
        Permissions.PartyAccess, Permissions.PartyContributions,
        Permissions.PartyGames, Permissions.PartyPrint, Permissions.PartyFaceSearch,
    ];

    [Fact]
    public async Task A_Party_Link_Is_An_App_Named_After_The_Party_That_Opens_That_Link()
    {
        var party = await SeedPartyAsync();
        await RenameAsync(party, "50 anni di \"Cesare\"");

        var response = await _factory.CreateClient().GetAsync($"/api/party/{party.Token}/app-manifest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var manifest = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("50 anni di \"Cesare\"", manifest.GetProperty("name").GetString());
        Assert.Equal("50 anni di \"Cesare\"", manifest.GetProperty("short_name").GetString());
        // The app's identity is the party's, never the link's token; its scope
        // is its own path; it starts on the link, under that path.
        var id = manifest.GetProperty("id").GetString()!;
        Assert.Matches("^/party/app/[0-9a-f]{32}$", id);
        Assert.DoesNotContain(party.Token, id);
        Assert.Equal(NubArca.Api.Party.PartyHomeScreenApp.AppPath(party.PartyId), id);
        Assert.Equal(id + "/", manifest.GetProperty("scope").GetString());
        Assert.Equal($"{id}/party/{party.Token}", manifest.GetProperty("start_url").GetString());
        Assert.Equal("standalone", manifest.GetProperty("display").GetString());
        var version = await VersionAsync(party);
        Assert.Equal(
            [
                $"/api/party/{party.Token}/app-icon/192?v={version}|192x192|any",
                $"/api/party/{party.Token}/app-icon/512?v={version}|512x512|any",
                $"/api/party/{party.Token}/app-icon/512?v={version}&maskable=true|512x512|maskable",
            ],
            manifest.GetProperty("icons").EnumerateArray().Select(i =>
                $"{i.GetProperty("src").GetString()}|{i.GetProperty("sizes").GetString()}|{i.GetProperty("purpose").GetString()}"));
    }

    [Theory]
    [InlineData(192)]
    [InlineData(512)]
    public async Task The_Icon_Is_The_Cover_The_Page_Opens_On_Square_And_Without_Metadata(int size)
    {
        var party = await SeedPartyAsync();
        await PutInvitationCoverAsync(party, await UploadPngAsync(party.Owner, "copertina.png", 64, 40));

        var response = await _factory.CreateClient().GetAsync($"/api/party/{party.Token}/app-icon/{size}?v=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        using var icon = Image.Load(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
        Assert.Null(icon.Metadata.ExifProfile);
        Assert.Null(icon.Metadata.XmpProfile);
    }

    [Fact]
    public async Task A_Party_That_Opens_On_No_Photograph_Gets_The_Products_Icon()
    {
        // Before the party the album's photographs are not shown, and no cover
        // was chosen: the page opens on its own composition.
        var party = await SeedPartyAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var plain = await client.GetAsync($"/api/party/{party.Token}/app-icon/192");
        var maskable = await client.GetAsync($"/api/party/{party.Token}/app-icon/512?maskable=true");

        Assert.Equal(HttpStatusCode.Redirect, plain.StatusCode);
        Assert.Equal("/brand/nubarca-pwa-192.png", plain.Headers.Location!.ToString());
        Assert.Equal("/brand/nubarca-pwa-maskable-512.png", maskable.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_Link_That_Opens_No_Party_Has_No_App_And_An_Icon_Has_Two_Sizes()
    {
        var party = await SeedPartyAsync();
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/party/not-a-party-token/app-manifest")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/party/not-a-party-token/app-icon/192")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/party/{party.Token}/app-icon/100")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/party-invitations/not-an-invitation/app-manifest")).StatusCode);
    }

    [Fact]
    public async Task An_Invitation_Is_An_App_That_Opens_The_Invitation_With_The_Invitations_Cover()
    {
        var party = await SeedPartyAsync();
        await RenameAsync(party, "Matrimonio di Marta");
        await PutInvitationCoverAsync(party, await UploadPngAsync(party.Owner, "invito.png", 40, 64));
        var token = await InviteTokenAsync(party, "Sara");
        var client = _factory.CreateClient();

        var manifest = await client.GetFromJsonAsync<JsonElement>($"/api/party-invitations/{token}/app-manifest");
        Assert.Equal("Matrimonio di Marta", manifest.GetProperty("name").GetString());
        // The party's own app — same identity, same scope — starting on this
        // invitation: relaunching needs its token (it keeps no session), the
        // identity never carries it.
        var id = manifest.GetProperty("id").GetString()!;
        Assert.Equal(NubArca.Api.Party.PartyHomeScreenApp.AppPath(party.PartyId), id);
        Assert.DoesNotContain(token, id);
        Assert.Equal(id + "/", manifest.GetProperty("scope").GetString());
        Assert.Equal($"{id}/party/invite/{token}", manifest.GetProperty("start_url").GetString());
        // Never the guest's own name: the app is the party's.
        Assert.DoesNotContain("Sara", manifest.GetRawText());

        var src = manifest.GetProperty("icons")[1].GetProperty("src").GetString()!;
        Assert.StartsWith($"/api/party-invitations/{token}/app-icon/512?v=", src);
        var response = await client.GetAsync(src);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        using var icon = Image.Load(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(512, icon.Width);
        Assert.Equal(512, icon.Height);
    }

    [Fact]
    public async Task Two_Parties_Are_Two_Apps_Whose_Scopes_Never_Claim_Each_Others_Links()
    {
        // Android hands a link to the installed app whose scope contains it,
        // and judges "already installed" by it: party A's app must not contain
        // a single page of party B — its public page or any invitation to it.
        var a = await SeedPartyAsync();
        var b = await SeedPartyAsync();
        var inviteA = await InviteTokenAsync(a, "Sara");
        var inviteB = await InviteTokenAsync(b, "Luca");
        var client = _factory.CreateClient();
        async Task<(string Id, string Scope, string Start)> AppAsync(string url)
        {
            var m = await client.GetFromJsonAsync<JsonElement>(url);
            return (m.GetProperty("id").GetString()!, m.GetProperty("scope").GetString()!, m.GetProperty("start_url").GetString()!);
        }

        var partyA = await AppAsync($"/api/party/{a.Token}/app-manifest");
        var invitationA = await AppAsync($"/api/party-invitations/{inviteA}/app-manifest");
        var partyB = await AppAsync($"/api/party/{b.Token}/app-manifest");
        var invitationB = await AppAsync($"/api/party-invitations/{inviteB}/app-manifest");

        Assert.NotEqual(partyA.Id, partyB.Id);
        Assert.NotEqual(partyA.Scope, partyB.Scope);
        foreach (var (app, own, other) in new[]
        {
            (partyA, partyA.Scope, partyB.Scope), (invitationA, partyA.Scope, partyB.Scope),
            (partyB, partyB.Scope, partyA.Scope), (invitationB, partyB.Scope, partyA.Scope),
        })
        {
            Assert.Equal(own, app.Scope);
            Assert.True(InScope(app.Start, own), $"{app.Start} outside its own scope");
            Assert.False(InScope(app.Start, other), $"{app.Start} inside another party's scope");
            // Never the whole of /party/: one installed party would claim them all.
            Assert.NotEqual("/party/", app.Scope);
            Assert.Matches("^/party/app/[0-9a-f]{32}/$", app.Scope);
        }
        // Neither scope contains the other.
        Assert.False(InScope(partyA.Scope, partyB.Scope));
        Assert.False(InScope(partyB.Scope, partyA.Scope));
        // A party's page and its invitations: one app — the invitation leading
        // into the party stays inside it.
        Assert.Equal(partyA.Id, invitationA.Id);
        Assert.Equal(partyB.Id, invitationB.Id);
        // The legacy links the guests hold (QR, WhatsApp) belong to no app's
        // scope: opened in a browser, they are never another party's.
        Assert.False(InScope($"/party/{b.Token}", partyA.Scope));
        Assert.False(InScope($"/party/invite/{inviteB}", partyA.Scope));
    }

    [Fact]
    public async Task Two_Invitations_To_One_Party_Are_One_App()
    {
        var party = await SeedPartyAsync();
        var sara = await InviteTokenAsync(party, "Sara");
        var luca = await InviteTokenAsync(party, "Luca");
        var client = _factory.CreateClient();
        async Task<JsonElement> ManifestAsync(string url) => await client.GetFromJsonAsync<JsonElement>(url);

        var first = await ManifestAsync($"/api/party-invitations/{sara}/app-manifest");
        var second = await ManifestAsync($"/api/party-invitations/{luca}/app-manifest");

        Assert.Equal(first.GetProperty("id").GetString(), second.GetProperty("id").GetString());
        Assert.Equal(first.GetProperty("scope").GetString(), second.GetProperty("scope").GetString());
        // Stable: asked again, the same.
        Assert.Equal(first.GetProperty("id").GetString(),
            (await ManifestAsync($"/api/party-invitations/{sara}/app-manifest")).GetProperty("id").GetString());
        // Each starts on its own group's invitation.
        Assert.EndsWith($"/party/invite/{sara}", first.GetProperty("start_url").GetString());
        Assert.EndsWith($"/party/invite/{luca}", second.GetProperty("start_url").GetString());
    }

    /// <summary>A manifest scope's path-prefix rule.</summary>
    private static bool InScope(string url, string scope) => url.StartsWith(scope, StringComparison.Ordinal);

    [Fact]
    public async Task After_The_Party_An_Invitation_Still_Leads_Into_It()
    {
        // The invitation kept on a home screen: during the party it leads into
        // the party, and afterwards into the party's memories.
        var party = await SeedPartyAsync();
        var token = await InviteTokenAsync(party, "Sara");
        var client = _factory.CreateClient();

        await PartyInvitationTestKit.AdvanceAsync(party.Owner, party.PartyId, "start-live");
        Assert.Equal($"/party/{party.Token}", await PartyUrlAsync(client, token));

        await PartyInvitationTestKit.AdvanceAsync(party.Owner, party.PartyId, "end-live");
        var view = await client.GetFromJsonAsync<JsonElement>($"/api/party-invitations/{token}");
        Assert.Equal("after", view.GetProperty("party").GetProperty("phase").GetString());
        Assert.False(view.GetProperty("invitation").GetProperty("canCheckIn").GetBoolean());
        Assert.Equal($"/party/{party.Token}", await PartyUrlAsync(client, token));
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
        var enable = await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = true });
        enable.EnsureSuccessStatusCode();
        var settings = await enable.Content.ReadFromJsonAsync<JsonElement>();
        return new SeededParty(
            owner, settings.GetProperty("partyId").GetGuid(),
            settings.GetProperty("partyUrl").GetString()!["/party/".Length..]);
    }

    private async Task<string> InviteTokenAsync(SeededParty party, string name)
    {
        var created = await party.Owner.PostAsJsonAsync(
            $"/api/parties/{party.PartyId}/invitation-groups",
            new
            {
                label = name, recipientEmail = $"{name.ToLowerInvariant()}@example.com", phone = (string?)null,
                maxAdditionalGuests = 0, guests = new[] { new { name } }, version = 0,
            });
        created.EnsureSuccessStatusCode();
        var groupId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("groups").EnumerateArray()
            .Single(g => g.GetProperty("label").GetString() == name)
            .GetProperty("id").GetGuid();
        return PartyInvitationTestKit.CurrentToken(_factory, groupId);
    }

    private static async Task<string?> PartyUrlAsync(HttpClient client, string token) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/party-invitations/{token}"))
            .GetProperty("party").GetProperty("partyUrl").GetString();

    private static async Task<int> VersionAsync(SeededParty party) =>
        (await party.Owner.GetFromJsonAsync<JsonElement>($"/api/parties/{party.PartyId}"))
            .GetProperty("version").GetInt32();

    private static async Task RenameAsync(SeededParty party, string title) =>
        (await party.Owner.PatchAsJsonAsync($"/api/parties/{party.PartyId}", new
        {
            title, description = (string?)null, eventStartsAt = (DateTime?)null,
            guestAccessExpiresAt = (DateTime?)null, libraryAccessExpiresAt = (DateTime?)null,
            version = await VersionAsync(party),
        })).EnsureSuccessStatusCode();

    private static async Task PutInvitationCoverAsync(SeededParty party, Guid cover) =>
        (await party.Owner.PutAsJsonAsync($"/api/parties/{party.PartyId}/covers", new
        {
            invitationCoverFileItemId = cover, liveCoverFileItemId = (Guid?)null,
            version = await VersionAsync(party),
        })).EnsureSuccessStatusCode();

    private static async Task<Guid> UploadPngAsync(HttpClient owner, string name, int width = 16, int height = 16)
    {
        var part = new ByteArrayContent(ImageFixtures.PlainPng(width, height));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var upload = await owner.PostAsync("/api/files", new MultipartFormDataContent { { part, "file", name } });
        upload.EnsureSuccessStatusCode();
        return (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
