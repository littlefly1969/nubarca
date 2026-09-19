using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;
using static NubArca.Api.Tests.Party.PartyCrewTestKit;
using static NubArca.Api.Tests.Party.PartyInvitationTestKit;

namespace NubArca.Api.Tests.Party;

// PARTY-ADDRESS-SHARE-01. Telling somebody where the party is, which until now
// the product could only do by inviting them.
//
// The properties worth defending, and what would break if each stopped holding:
//   * it depends on NO guest, NO invitation group and NO RSVP — a host whose
//     list is empty has something to send, which is the whole reason it exists;
//   * it carries no bearer of any kind: no guest link, no invitation, upload,
//     print or RSVP token, no email address and no URL. An address is forwarded
//     through chats; a capability must not be forwarded with it;
//   * a party with no address says so, distinctly from a party that is not
//     yours — the first the host can fix and the surface offers to take them
//     there;
//   * on the Party Crew side it is `details.manage`: a co-organizer hands out
//     the address, a director runs the evening and does not.
public sealed class PartyAddressShareTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = NewFactory();

    public void Dispose() => _factory.Dispose();

    // ── Without a guest list ────────────────────────────────────────────────

    [Fact]
    public async Task A_host_with_nobody_on_a_list_can_still_say_where_the_party_is()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner, "Compleanno di Marta");
        await SetLocationAsync(owner, partyId, "Villa dei Fiori", "Via Roma 1, Milano", "Citofono 3");

        // NOBODY IS INVITED. No group, no guest, no RSVP — the state a host is
        // in when they decide to tell a neighbour where to come.
        var guests = await GuestListAsync(owner, partyId);
        Assert.Equal(0, guests.GetProperty("groups").GetArrayLength());

        var share = await ShareAsync(owner, partyId);
        Assert.Equal(HttpStatusCode.OK, share.StatusCode);
        var body = await share.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Compleanno di Marta", body.GetProperty("title").GetString());
        Assert.Equal("Villa dei Fiori", body.GetProperty("venueName").GetString());
        Assert.Equal("Via Roma 1, Milano", body.GetProperty("address").GetString());
        Assert.Equal("Citofono 3", body.GetProperty("note").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("eventStartsAt").ValueKind);
    }

    [Fact]
    public async Task A_section_the_guests_do_not_see_is_still_the_host_s_own_address()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await SetLocationAsync(owner, partyId, "Villa dei Fiori", "Via Roma 1, Milano", enabled: false);

        // The slot's `enabled` governs whether the GUEST PAGE draws a "where"
        // section. It is not a statement about whether the host knows their own
        // address, so it is deliberately not part of this predicate.
        var body = await (await ShareAsync(owner, partyId)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Via Roma 1, Milano", body.GetProperty("address").GetString());
    }

    [Fact]
    public async Task A_party_with_no_address_yet_says_so_rather_than_pretending()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);

        var refused = await ShareAsync(owner, partyId);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("party_address_missing", await ErrorOf(refused));

        // Told apart from "no such party" on purpose: this one the host can
        // fix, and the surface offers to take them there.
        Assert.Equal(HttpStatusCode.NotFound, (await ShareAsync(owner, Guid.NewGuid())).StatusCode);
    }

    // ── What travels, and what must not ─────────────────────────────────────

    [Fact]
    public async Task The_share_carries_no_link_no_token_and_no_email()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        // A party with EVERYTHING that could leak: a public QR with its view
        // and upload tokens, and an invited group with a personal link.
        await OpenPublicQrAsync(owner, partyId);
        var groupId = (await AddGroupAsync(owner, partyId, "Sara", "sara@example.com", 0, "Sara"))
            .GetProperty("id").GetGuid();
        await InviteAsync(_factory, owner, partyId, groupId);
        await SetLocationAsync(owner, partyId, "Villa dei Fiori", "Via Roma 1, Milano");

        var raw = await (await ShareAsync(owner, partyId)).Content.ReadAsStringAsync();
        foreach (var forbidden in new[] { "token", "http", "/party/", "@", "url", "partyId", "ownerUserId" })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }

        // Stated positively too, so a field added later has to be considered
        // rather than merely avoid five substrings.
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(
            ["title", "eventStartsAt", "venueName", "address", "note"],
            body.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task A_stranger_cannot_learn_where_somebody_else_s_party_is()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var (_, stranger) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await SetLocationAsync(owner, partyId, "Villa dei Fiori", "Via Roma 1, Milano");

        // Ownership is IN THE QUERY: somebody else's party is the same generic
        // nothing as one that does not exist, and holding the party permission
        // is not holding the party.
        Assert.Equal(HttpStatusCode.NotFound, (await ShareAsync(stranger, partyId)).StatusCode);
    }

    // ── Party Crew ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_co_organizer_may_hand_out_the_address()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner, "Matrimonio di Marta");
        await SetLocationAsync(owner, partyId, "Villa dei Fiori", "Via Roma 1, Milano");
        var device = await PairAsCrewAsync(owner, partyId, "Sara", "sara@example.com", PartyCrewRoles.CoOrganizer);

        var share = await device.PostAsync($"/api/party-crew/parties/{partyId}/address-share", null);
        share.EnsureSuccessStatusCode();
        var body = await share.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Via Roma 1, Milano", body.GetProperty("address").GetString());
        // The same restraint on the façade: a collaborator's device is the one
        // most likely to forward this into a chat.
        Assert.Equal(
            ["title", "eventStartsAt", "venueName", "address", "note"],
            body.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task A_director_runs_the_evening_and_does_not_hand_out_its_address()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await SetLocationAsync(owner, partyId, "Villa dei Fiori", "Via Roma 1, Milano");
        var device = await PairAsCrewAsync(owner, partyId, "Luca", "luca@example.com", PartyCrewRoles.Director);

        // The capability is `details.manage` — the party's own facts — and a
        // director does not hold it. The refusal is the same generic nothing
        // every capability they do not hold answers, so the route does not tell
        // them the surface exists.
        var session = await SessionAsync(device, partyId);
        Assert.DoesNotContain(PartyCrewCapabilities.DetailsManage, CapabilitiesOf(session));
        AssertRefused(await device.PostAsync($"/api/party-crew/parties/{partyId}/address-share", null));
    }

    [Fact]
    public async Task A_collaborator_on_one_party_cannot_read_another_party_s_address()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var theirs = await CreatePartyAsync(owner, "La loro");
        var other = await CreatePartyAsync(owner, "L'altra");
        await SetLocationAsync(owner, other, "Villa dei Fiori", "Via Roma 1, Milano");
        var device = await PairAsCrewAsync(
            owner, theirs, "Sara", "sara@example.com", PartyCrewRoles.CoOrganizer);

        // The party in the route is a SELECTOR, not the authority: the device's
        // grant is for one party, so naming another is nothing.
        AssertRefused(await device.PostAsync($"/api/party-crew/parties/{other}/address-share", null));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> ShareAsync(HttpClient client, Guid partyId) =>
        client.PostAsync($"/api/parties/{partyId}/address-share", null);

    private static async Task SetLocationAsync(
        HttpClient owner, Guid partyId, string venue, string address,
        string? note = null, bool enabled = true)
    {
        var response = await owner.PutAsJsonAsync(
            $"/api/parties/{partyId}/guest-content/location",
            new
            {
                enabled,
                visibleBefore = true,
                visibleLive = true,
                visibleAfter = true,
                content = new { venueName = venue, address, note },
                version = 0,
            });
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpClient> PairAsCrewAsync(
        HttpClient owner, Guid partyId, string name, string email, string role)
    {
        var (_, invite) = await AddCollaboratorAsync(owner, partyId, name, email, role);
        return await PairAsync(_factory, invite);
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("error", out var error) ? error.GetString() ?? "" : "";
    }
}
