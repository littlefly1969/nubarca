using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Domain;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;
using static NubArca.Api.Tests.Party.PartyCrewTestKit;
using static NubArca.Api.Tests.Party.PartyInvitationTestKit;

namespace NubArca.Api.Tests.Party;

// PARTY-CREW-CONTRIBUTIONS-01. Two capabilities, deliberately not one.
//
// `contributions.configure` decides WHAT THE PARTY TAKES — photographs,
// greetings, a guest book. `contributions.moderate` decides what STAYS UP
// tonight. A co-organizer holds both; a director holds only the second, because
// whether guests may contribute at all is the host's standing decision about
// their own party and their own library.
//
// What would break if this stopped holding: a role that runs the evening could
// close the host's book, and the surface would be hiding a switch the server
// still honoured — which is a permission model that lives in the UI.
public sealed class PartyCrewContributionsTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = NewFactory();

    public void Dispose() => _factory.Dispose();

    // ── Configuring ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_co_organizer_decides_what_the_party_takes()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var party = await OpenPartyAsync(owner);
        var device = await PairAsCrewAsync(owner, party.PartyId, PartyCrewRoles.CoOrganizer);

        var saved = await device.PatchAsJsonAsync(
            $"/api/party-crew/parties/{party.PartyId}/contributions",
            new { guestbookEnabled = true, slideshowMessagesEnabled = false });
        saved.EnsureSuccessStatusCode();

        // The host sees it on their own route, because there is one decision
        // and not a crew-shaped copy of it.
        var status = await owner.GetFromJsonAsync<JsonElement>(
            $"/api/albums/{party.AlbumId}/party-settings");
        Assert.True(status.GetProperty("guestbookEnabled").GetBoolean());
        Assert.False(status.GetProperty("slideshowMessagesEnabled").GetBoolean());
    }

    [Fact]
    public async Task A_director_moderates_what_guests_left_and_does_not_decide_whether_they_may_leave_it()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var device = await PairAsCrewAsync(owner, party.PartyId, PartyCrewRoles.Director);

        var capabilities = CapabilitiesOf(await SessionAsync(device, party.PartyId));
        Assert.Contains(PartyCrewCapabilities.ContributionsModerate, capabilities);
        Assert.DoesNotContain(PartyCrewCapabilities.ContributionsConfigure, capabilities);

        // Refused, and refused as the same generic nothing: the route does not
        // confirm that a switch is there to be flipped.
        AssertRefused(await device.PatchAsJsonAsync(
            $"/api/party-crew/parties/{party.PartyId}/contributions",
            new { guestbookEnabled = false }));

        // The book is still open, which is the point: nothing half-happened.
        Assert.True(
            (await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{party.AlbumId}/party-settings"))
                .GetProperty("guestbookEnabled").GetBoolean());
    }

    // ── Moderating ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_director_may_take_a_dedication_down_and_put_it_back()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var entryId = await WriteDedicationAsync(party.ViewToken, "Ada", "Evviva");
        var device = await PairAsCrewAsync(owner, party.PartyId, PartyCrewRoles.Director);

        var queue = await device.GetFromJsonAsync<JsonElement>(
            $"/api/party-crew/parties/{party.PartyId}/guestbook");
        // Authorised to moderate, and told plainly that they are not the host —
        // which is how the surface knows to hide the configuration switches.
        Assert.False(queue.GetProperty("isOwner").GetBoolean());
        Assert.Equal(1, queue.GetProperty("entries").GetArrayLength());

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await device.PostAsync(
                $"/api/party-crew/parties/{party.PartyId}/guestbook/{entryId}/hide", null)).StatusCode);
        Assert.Equal(0, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await device.PostAsync(
                $"/api/party-crew/parties/{party.PartyId}/guestbook/{entryId}/restore", null)).StatusCode);
        Assert.Equal(1, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task A_device_that_paired_with_nothing_reaches_none_of_it()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var entryId = await WriteDedicationAsync(party.ViewToken, "Ada", "Evviva");

        // No device cookie at all — a browser that simply knows the party id.
        // The id is a SELECTOR and never an authority, so every one of these is
        // the same generic nothing.
        var stranger = _factory.CreateClient();
        AssertRefused(await stranger.GetAsync($"/api/party-crew/parties/{party.PartyId}/guestbook"));
        AssertRefused(await stranger.PostAsync(
            $"/api/party-crew/parties/{party.PartyId}/guestbook/{entryId}/hide", null));
        AssertRefused(await stranger.PatchAsJsonAsync(
            $"/api/party-crew/parties/{party.PartyId}/contributions", new { guestbookEnabled = false }));

        // Untouched: the book is still open and the dedication still in it.
        Assert.Equal(1, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task The_crew_s_book_is_the_party_s_own_and_never_another_one_s()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var theirs = await OpenPartyAsync(owner, guestbook: true, albumName: "La loro");
        var other = await OpenPartyAsync(owner, guestbook: true, albumName: "L'altra");
        var entryId = await WriteDedicationAsync(other.ViewToken, null, "Dell'altra festa");
        var device = await PairAsCrewAsync(owner, theirs.PartyId, PartyCrewRoles.Director);

        // Same host, so this is a SCOPING test rather than an authorization
        // one: the party in the route selects, and the grant decides.
        AssertRefused(await device.GetAsync($"/api/party-crew/parties/{other.PartyId}/guestbook"));
        // And the entry id of a book they cannot read is nothing on a party
        // they can: the moderation query is scoped to the party in the same
        // statement that finds the row.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await device.PostAsync(
                $"/api/party-crew/parties/{theirs.PartyId}/guestbook/{entryId}/hide", null)).StatusCode);
    }

    [Fact]
    public async Task The_crew_queue_names_no_owner_no_album_and_no_participant()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var party = await OpenPartyAsync(owner, guestbook: true);
        await WriteDedicationAsync(party.ViewToken, "Ada", "Evviva");
        var device = await PairAsCrewAsync(owner, party.PartyId, PartyCrewRoles.Director);

        var raw = await device.GetStringAsync($"/api/party-crew/parties/{party.PartyId}/guestbook");
        foreach (var forbidden in new[]
                 {
                     "ownerUserId", "albumId", "partyAlbumLinkId", "partyParticipantId",
                     "moderatedByUserId", "tokenHash", "storageKey",
                 })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }

        var entry = JsonDocument.Parse(raw).RootElement.GetProperty("entries")[0];
        Assert.Equal(
            ["id", "authorDisplayName", "body", "status", "createdAt", "moderatedAt"],
            entry.EnumerateObject().Select(p => p.Name).ToArray());
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    private sealed record OpenParty(Guid PartyId, Guid AlbumId, string ViewToken);

    private async Task<OpenParty> OpenPartyAsync(
        HttpClient owner, bool guestbook = false, string albumName = "Album della festa")
    {
        var partyId = await CreatePartyAsync(owner);
        var (albumId, viewToken, _) = await OpenPublicQrAsync(owner, partyId, albumName);
        // The QR PUBLISHES the party — an invitation, which is deliberately not
        // the party itself. Guests contribute to one that has started, so this
        // starts it, exactly as a host does.
        await AdvanceAsync(owner, partyId, "start-live");
        if (guestbook)
        {
            (await owner.PatchAsJsonAsync(
                $"/api/albums/{albumId}/party-contributions", new { guestbookEnabled = true }))
                .EnsureSuccessStatusCode();
        }

        return new OpenParty(partyId, albumId, viewToken);
    }

    private async Task<HttpClient> PairAsCrewAsync(HttpClient owner, Guid partyId, string role)
    {
        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Sara", $"crew-{Guid.NewGuid():N}@example.com", role);
        return await PairAsync(_factory, invite);
    }

    private async Task<Guid> WriteDedicationAsync(string viewToken, string? author, string body)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            $"/api/party/{viewToken}/guestbook", new { authorDisplayName = author, body });
        response.EnsureSuccessStatusCode();
        var entry = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(PartyMessageStatuses.Visible, entry.GetProperty("status").GetString());
        return entry.GetProperty("id").GetGuid();
    }

    private Task<JsonElement> PublicBookAsync(string viewToken) =>
        _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/party/{viewToken}/guestbook");
}
