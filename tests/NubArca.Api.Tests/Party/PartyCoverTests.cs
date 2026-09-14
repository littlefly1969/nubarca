using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Access;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The party's two covers: the photograph that opens the invitation, and the
/// one that takes over while the party is on.
///
/// <para>What is pinned here is the ORDER — at the party the live cover, then
/// the invitation's, then the album's; on the invitation its cover, then the
/// album's chosen one — and that the public address serves only the cover the
/// page is showing right now. Both covers are the owner's own eligible images,
/// stated whole under the party's version, and a copy of the party keeps them.</para>
/// </summary>
public sealed class PartyCoverTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyCoverTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private static readonly string[] HostPermissions =
    [
        Permissions.PartyAccess, Permissions.PartyContributions,
        Permissions.PartyGames, Permissions.PartyPrint, Permissions.PartyFaceSearch,
        Permissions.PrivateVaultAccess,
    ];

    [Fact]
    public async Task Covers_Are_Stated_Whole_Versioned_And_Previewed_For_The_Owner()
    {
        var party = await SeedPartyAsync();
        var portrait = await UploadPngAsync(party.Owner, "invito.png");
        var version = await VersionAsync(party);

        var saved = await PutCoversAsync(party, portrait, null, version);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var dto = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(portrait, dto.GetProperty("invitationCoverFileItemId").GetGuid());
        Assert.Equal(
            $"/api/files/{portrait}/thumbnail?size=medium",
            dto.GetProperty("invitationCoverUrl").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("liveCoverFileItemId").ValueKind);
        Assert.Equal(version + 1, dto.GetProperty("version").GetInt32());

        // The same choice again writes nothing and spends no version.
        var again = await (await PutCoversAsync(party, portrait, null, version + 1))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(version + 1, again.GetProperty("version").GetInt32());

        // A stale version is refused with the truth.
        var stale = await PutCoversAsync(party, null, null, version);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            "version_conflict",
            (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_Cover_Must_Be_One_Of_The_Owners_Own_Images()
    {
        var party = await SeedPartyAsync();
        var (_, stranger) = await _factory.CreatePermissionClientAsync(
            $"other-{Guid.NewGuid():N}@example.com", HostPermissions);
        var theirs = await UploadPngAsync(stranger, "theirs.png");
        var version = await VersionAsync(party);

        foreach (var file in new[] { theirs, Guid.NewGuid() })
        {
            var refused = await PutCoversAsync(party, file, null, version);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(
                "invalid_media",
                (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
        Assert.Equal(version, await VersionAsync(party));

        // Another owner's party is the generic not-found.
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync(
            $"/api/parties/{party.PartyId}/covers",
            new { invitationCoverFileItemId = (Guid?)null, liveCoverFileItemId = (Guid?)null, version }))
            .StatusCode);
    }

    [Fact]
    public async Task At_The_Party_The_Live_Cover_Comes_First_Then_The_Invitations_Then_The_Album()
    {
        var party = await SeedPartyAsync();
        var invitation = await UploadPngAsync(party.Owner, "invito.png");
        var live = await UploadPngAsync(party.Owner, "festa.png");
        var guest = _factory.CreateClient();
        var covers = $"/api/party/{party.Token}/cover";

        // On the invitation the live cover is not this page's, even when chosen.
        (await PutCoversAsync(party, invitation, live, await VersionAsync(party))).EnsureSuccessStatusCode();
        Assert.StartsWith($"{covers}/invitation/media", await CoverUrlAsync(guest, party));
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"{covers}/invitation/media")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"{covers}/live/media")).StatusCode);

        // At the party the live cover leads, and the invitation's stops answering.
        await StartLiveAsync(party);
        Assert.StartsWith($"{covers}/live/media", await CoverUrlAsync(guest, party));
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"{covers}/live/media")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"{covers}/invitation/media")).StatusCode);

        // Without a live cover, the invitation's carries on...
        (await PutCoversAsync(party, invitation, null, await VersionAsync(party))).EnsureSuccessStatusCode();
        Assert.StartsWith($"{covers}/invitation/media", await CoverUrlAsync(guest, party));

        // ...and without either, the album's cover, as every surface resolves it.
        (await PutCoversAsync(party, null, null, await VersionAsync(party))).EnsureSuccessStatusCode();
        Assert.Equal(
            $"/api/party/{party.Token}/media/{party.AlbumPhotoId}/preview",
            await CoverUrlAsync(guest, party));
    }

    [Fact]
    public async Task A_Copy_Of_The_Party_Keeps_Both_Covers()
    {
        var party = await SeedPartyAsync();
        var invitation = await UploadPngAsync(party.Owner, "invito.png");
        var live = await UploadPngAsync(party.Owner, "festa.png");
        (await PutCoversAsync(party, invitation, live, await VersionAsync(party))).EnsureSuccessStatusCode();

        var clone = await (await party.Owner.PostAsJsonAsync(
                $"/api/parties/{party.PartyId}/duplicate", new { title = (string?)null }))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(invitation, clone.GetProperty("invitationCoverFileItemId").GetGuid());
        Assert.Equal(live, clone.GetProperty("liveCoverFileItemId").GetGuid());
    }

    // --- helpers -----------------------------------------------------------

    private sealed record SeededParty(
        HttpClient Owner, Guid PartyId, Guid AlbumId, Guid AlbumPhotoId, string Token);

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
            owner, settings.GetProperty("partyId").GetGuid(), albumId, photoId,
            settings.GetProperty("partyUrl").GetString()!["/party/".Length..]);
    }

    private static async Task<int> VersionAsync(SeededParty party) =>
        (await party.Owner.GetFromJsonAsync<JsonElement>($"/api/parties/{party.PartyId}"))
            .GetProperty("version").GetInt32();

    private static Task<HttpResponseMessage> PutCoversAsync(
        SeededParty party, Guid? invitation, Guid? live, int version) =>
        party.Owner.PutAsJsonAsync($"/api/parties/{party.PartyId}/covers", new
        {
            invitationCoverFileItemId = invitation, liveCoverFileItemId = live, version,
        });

    private static async Task<string?> CoverUrlAsync(HttpClient guest, SeededParty party) =>
        (await guest.GetFromJsonAsync<JsonElement>($"/api/party/{party.Token}"))
            .GetProperty("coverUrl").GetString();

    private static async Task StartLiveAsync(SeededParty party) =>
        (await party.Owner.PostAsJsonAsync(
            $"/api/parties/{party.PartyId}/start-live",
            new { version = await VersionAsync(party) })).EnsureSuccessStatusCode();

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
