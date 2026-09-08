using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tv;

namespace NubArca.Api.Tests.Tv;

/// <summary>
/// What a paired television is FOR, which is not the same question as who it is.
///
/// Pairing is a credential and never changes; the assignment is ordinary
/// server-side state beside it. Everything here is about keeping those two
/// apart: a television moves between the general experience and a party, and
/// between two parties, without a second PIN — and no assignment change ever
/// touches the session token, the pairing, or another owner's anything.
/// </summary>
public sealed class TvDisplayAssignmentTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public TvDisplayAssignmentTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_television_paired_before_assignments_existed_is_general()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);

        // The migration's whole contract: an existing row takes the default, and
        // the default is the behaviour it already had.
        using (var scope = _factory.Services.CreateScope())
        {
            var session = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .TvSessions.SingleAsync(x => x.OwnerUserId == ownerId);
            Assert.Equal(TvDisplayAssignments.General, session.DisplayAssignment);
            Assert.Null(session.AssignedPartyAlbumLinkId);
        }

        var device = (await owner.GetFromJsonAsync<JsonElement>("/api/tv-devices"))
            .EnumerateArray().Single();
        Assert.Equal("general", device.GetProperty("assignment").GetProperty("kind").GetString());

        // And the television itself is told the same thing.
        var session2 = await TvJsonAsync("/api/tv/session", cookie);
        Assert.Equal("general", session2.GetProperty("assignment").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null,
            session2.GetProperty("assignment").GetProperty("albumId").ValueKind);
    }

    [Fact]
    public async Task One_pairing_carries_a_television_through_general_party_and_back()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var first = await PartyAlbumAsync(owner, "Festa di Anna");
        var second = await PartyAlbumAsync(owner, "Compleanno");

        // GENERAL → PARTY A. No new PIN, no new pairing: the same session token
        // is still the credential, and it still works.
        var partyA = await AssignAsync(owner, sessionId, first);
        Assert.Equal("party", partyA.GetProperty("kind").GetString());
        Assert.Equal(first, partyA.GetProperty("albumId").GetGuid());
        Assert.Equal("Festa di Anna", partyA.GetProperty("albumName").GetString());
        Assert.True(partyA.GetProperty("partyAvailable").GetBoolean());
        Assert.Equal(first, (await TvAssignmentAsync(cookie)).GetProperty("albumId").GetGuid());

        // PARTY A → PARTY B.
        var partyB = await AssignAsync(owner, sessionId, second);
        Assert.Equal(second, partyB.GetProperty("albumId").GetGuid());
        Assert.Equal(second, (await TvAssignmentAsync(cookie)).GetProperty("albumId").GetGuid());

        // PARTY B → GENERAL, which clears the link as well as the kind.
        var general = await AssignAsync(owner, sessionId, null);
        Assert.Equal("general", general.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, general.GetProperty("albumId").ValueKind);
        using var scope = _factory.Services.CreateScope();
        Assert.Null((await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .TvSessions.SingleAsync(x => x.Id == sessionId)).AssignedPartyAlbumLinkId);

        // Through all of it the pairing was never re-established.
        Assert.Equal(sessionId, await SingleSessionIdAsync(ownerId));
        Assert.Equal(HttpStatusCode.OK, (await TvGet("/api/tv/albums", cookie)).StatusCode);
    }

    [Fact]
    public async Task Setting_the_assignment_a_television_already_has_is_accepted()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyAlbumAsync(owner, "Festa");

        Assert.Equal(album, (await AssignAsync(owner, sessionId, album)).GetProperty("albumId").GetGuid());
        Assert.Equal(album, (await AssignAsync(owner, sessionId, album)).GetProperty("albumId").GetGuid());
        Assert.Equal("general", (await AssignAsync(owner, sessionId, null)).GetProperty("kind").GetString());
        Assert.Equal("general", (await AssignAsync(owner, sessionId, null)).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task An_owner_can_assign_neither_another_owners_television_nor_another_owners_party()
    {
        var (aliceId, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        await PairTvAsync(alice);
        var aliceSession = await SingleSessionIdAsync(aliceId);
        var aliceParty = await PartyAlbumAsync(alice, "Festa di Alice");

        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        await PairTvAsync(bob);
        var bobSession = await SingleSessionIdAsync(bobId);
        var bobParty = await PartyAlbumAsync(bob, "Festa di Bob");

        // Bob cannot point Alice's television anywhere — not even at his own
        // party, which is the case a check on the album alone would let through.
        Assert.Equal(HttpStatusCode.NotFound,
            (await RawAssignAsync(bob, aliceSession, bobParty)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await RawAssignAsync(bob, aliceSession, null)).StatusCode);

        // And he cannot point his own television at Alice's party. A foreign
        // party and a party that does not exist are the same answer, so this
        // cannot be used to discover that hers exists.
        var foreign = await RawAssignAsync(bob, bobSession, aliceParty);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("party_unavailable", await ErrorAsync(foreign));
        Assert.Equal(HttpStatusCode.NotFound,
            (await RawAssignAsync(bob, bobSession, Guid.NewGuid())).StatusCode);

        // Alice's television is exactly where she left it.
        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .TvSessions.SingleAsync(x => x.Id == aliceSession);
        Assert.Equal(TvDisplayAssignments.General, stored.DisplayAssignment);
        Assert.NotEqual(Guid.Empty, aliceParty);
    }

    [Fact]
    public async Task The_assignment_route_refuses_anything_but_a_party_of_this_owner()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);

        // An album with no party at all is not assignable, and says so exactly
        // as a foreign one does.
        var plain = (await (await owner.PostAsJsonAsync("/api/albums", new { name = "Senza festa" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal("party_unavailable", await ErrorAsync(await RawAssignAsync(owner, sessionId, plain)));

        // A party assignment must name an album, and a general one must not.
        Assert.Equal("album_required", await ErrorAsync(await PatchAsync(owner, sessionId,
            new { kind = "party" })));
        Assert.Equal("album_required", await ErrorAsync(await PatchAsync(owner, sessionId,
            new { kind = "general", albumId = plain })));
        Assert.Equal("unknown_assignment", await ErrorAsync(await PatchAsync(owner, sessionId,
            new { kind = "personal", albumId = (Guid?)null })));

        // A revoked television is gone for this purpose too.
        (await owner.DeleteAsync($"/api/tv-devices/{sessionId}")).EnsureSuccessStatusCode();
        var party = await PartyAlbumAsync(owner, "Festa");
        Assert.Equal(HttpStatusCode.NotFound, (await RawAssignAsync(owner, sessionId, party)).StatusCode);
    }

    [Fact]
    public async Task Assignment_needs_the_owner_cookie_and_the_TV_cookie_will_not_do()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var party = await PartyAlbumAsync(owner, "Festa");

        // Anonymous.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await RawAssignAsync(_factory.CreateClient(), sessionId, party)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync("/api/tv-devices/parties")).StatusCode);

        // And the limited TV session cannot promote itself: these routes are
        // outside /api/tv, so its path-scoped cookie is not even sent — and
        // presenting it by hand still authenticates nobody.
        var request = new HttpRequestMessage(HttpMethod.Patch,
            $"/api/tv-devices/{sessionId}/assignment")
        {
            Content = JsonContent.Create(new { kind = "party", albumId = party }),
        };
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(cookie)}");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_revoked_party_leaves_the_television_saying_so_rather_than_silently_general()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyAlbumAsync(owner, "Festa");
        await AssignAsync(owner, sessionId, album);

        // The host ends the party.
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();

        // The television is still a PARTY television, pointed at a party that is
        // over. Reporting "general" here would be a lie that quietly changes
        // what a screen in a room shows.
        var seen = await TvAssignmentAsync(cookie);
        Assert.Equal("party", seen.GetProperty("kind").GetString());
        Assert.False(seen.GetProperty("partyAvailable").GetBoolean());
        Assert.Equal(album, seen.GetProperty("albumId").GetGuid());

        var device = (await owner.GetFromJsonAsync<JsonElement>("/api/tv-devices"))
            .EnumerateArray().Single().GetProperty("assignment");
        Assert.Equal("party", device.GetProperty("kind").GetString());
        Assert.False(device.GetProperty("partyAvailable").GetBoolean());

        // It is not offered as a destination any more…
        Assert.Empty((await owner.GetFromJsonAsync<List<TvAssignablePartyDto>>("/api/tv-devices/parties"))!);

        // …and re-enabling the party mints a NEW link, so the television is not
        // silently adopted by it: a new party is a new party.
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings", new { enabled = true }))
            .EnsureSuccessStatusCode();
        Assert.False((await TvAssignmentAsync(cookie)).GetProperty("partyAvailable").GetBoolean());

        // Choosing it again resolves the new link and the television is live.
        await AssignAsync(owner, sessionId, album);
        Assert.True((await TvAssignmentAsync(cookie)).GetProperty("partyAvailable").GetBoolean());
    }

    [Fact]
    public async Task Deleting_the_album_returns_its_televisions_to_the_general_experience()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyAlbumAsync(owner, "Festa");
        await AssignAsync(owner, sessionId, album);

        // The assignment holds a restricting foreign key to the party link, so
        // an album that a television points at must still be deletable — the
        // party is what goes away, not the television.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/albums/{album}")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .TvSessions.SingleAsync(x => x.Id == sessionId);
        Assert.Equal(TvDisplayAssignments.General, stored.DisplayAssignment);
        Assert.Null(stored.AssignedPartyAlbumLinkId);
        // Still paired, still working.
        Assert.Equal(HttpStatusCode.OK, (await TvGet("/api/tv/albums", cookie)).StatusCode);
    }

    [Fact]
    public async Task The_ordinary_television_experience_is_unchanged_by_a_party_assignment()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyAlbumAsync(owner, "Festa");

        var before = await (await TvGet("/api/tv/albums", cookie)).Content.ReadAsStringAsync();
        await AssignAsync(owner, sessionId, album);
        var after = await (await TvGet("/api/tv/albums", cookie)).Content.ReadAsStringAsync();

        // This slice creates the CONCEPT of an assignment; it does not yet change
        // what the television is served. A regression here would mean a party
        // assignment had quietly become a content filter.
        Assert.Equal(before, after);
        Assert.Equal(HttpStatusCode.OK, (await TvGet("/api/tv/session/heartbeat", cookie, post: true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TvGet("/api/tv/personal/status", cookie)).StatusCode);
    }

    [Fact]
    public async Task The_assignable_party_list_is_this_owners_live_parties_and_nothing_else()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await PairTvAsync(owner);
        var withGame = await PartyAlbumAsync(owner, "Con gioco", gameEnabled: true);
        var plain = await PartyAlbumAsync(owner, "Senza gioco");
        (await owner.PostAsJsonAsync("/api/albums", new { name = "Nessuna festa" }))
            .EnsureSuccessStatusCode();

        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync("stranger@example.com");
        await PartyAlbumAsync(stranger, "Festa altrui");

        var parties = (await owner.GetFromJsonAsync<List<TvAssignablePartyDto>>("/api/tv-devices/parties"))!;
        // Ordered by album name, which is the owner's own vocabulary.
        Assert.Equal(["Con gioco", "Senza gioco"], parties.Select(p => p.AlbumName));
        Assert.Equal([withGame, plain], parties.Select(p => p.AlbumId));
        Assert.True(parties.Single(p => p.AlbumId == withGame).GameEnabled);
        Assert.False(parties.Single(p => p.AlbumId == plain).GameEnabled);
        Assert.DoesNotContain(parties, p => p.AlbumName == "Festa altrui");
        Assert.DoesNotContain(parties, p => p.AlbumName == "Nessuna festa");
    }

    [Fact]
    public async Task The_pairing_that_produced_a_device_names_it_only_to_the_owner_who_approved_it()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var tvClient = _factory.CreateClient();
        var started = (await (await tvClient.PostAsync("/api/tv/pairing/start", null))
            .Content.ReadFromJsonAsync<TvPairingStartedDto>())!;

        // Before the approval the pairing names nothing.
        Assert.Null(await PairedDeviceAsync(owner, started.PublicCode, started.PairingSecret));

        (await owner.PostAsJsonAsync($"/api/tv/pairing/{started.PublicCode}/approve", new
        {
            pairingSecret = started.PairingSecret,
            personalCode = "URDLSUDLR",
            personalCodeConfirmation = "URDLSUDLR",
        })).EnsureSuccessStatusCode();

        // Approved, but the television has not claimed it — the session does not
        // exist yet, and a wait is not an error.
        Assert.Null(await PairedDeviceAsync(owner, started.PublicCode, started.PairingSecret));

        var poll = new HttpRequestMessage(HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        poll.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        (await tvClient.SendAsync(poll)).EnsureSuccessStatusCode();

        Assert.Equal(await SingleSessionIdAsync(ownerId),
            await PairedDeviceAsync(owner, started.PublicCode, started.PairingSecret));

        // The secret is required as well as the cookie…
        Assert.Null(await PairedDeviceAsync(owner, started.PublicCode, new string('x', 40)));
        // …and so is being the owner who approved it: another account holding
        // the code and the secret is told nothing.
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync("stranger@example.com");
        Assert.Null(await PairedDeviceAsync(stranger, started.PublicCode, started.PairingSecret));
    }

    [Fact]
    public async Task An_assignment_change_is_audited_and_leaks_nothing()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyAlbumAsync(owner, "Festa");
        await AssignAsync(owner, sessionId, album);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = await db.AuditLogs.SingleAsync(
            x => x.Action == AuditActions.TvAssignmentSet && x.UserId == ownerId);
        Assert.Equal(sessionId, entry.EntityId);
        Assert.Contains("\"kind\":\"party\"", entry.MetadataJson);
        Assert.Contains(album.ToString(), entry.MetadataJson!);

        // The party's own identity — the link and its token hash — never appears.
        var link = await db.PartyAlbumLinks.AsNoTracking().SingleAsync(x => x.AlbumId == album);
        Assert.DoesNotContain(link.Id.ToString(), entry.MetadataJson!);
        Assert.DoesNotContain(link.TokenHash, entry.MetadataJson!);

        // Nor does it reach any owner-facing or device-facing response.
        var devices = await (await owner.GetAsync("/api/tv-devices")).Content.ReadAsStringAsync();
        var parties = await (await owner.GetAsync("/api/tv-devices/parties")).Content.ReadAsStringAsync();
        foreach (var body in new[] { devices, parties })
        {
            Assert.DoesNotContain(link.Id.ToString(), body);
            Assert.DoesNotContain(link.TokenHash, body);
            Assert.DoesNotContain("TokenHash", body);
            Assert.DoesNotContain("OwnerUserId", body);
        }
    }

    // --- helpers -----------------------------------------------------------

    private async Task<string> PairTvAsync(HttpClient owner)
    {
        var tvClient = _factory.CreateClient();
        var started = (await (await tvClient.PostAsync("/api/tv/pairing/start", null))
            .Content.ReadFromJsonAsync<TvPairingStartedDto>())!;
        (await owner.PostAsJsonAsync($"/api/tv/pairing/{started.PublicCode}/approve", new
        {
            pairingSecret = started.PairingSecret,
            personalCode = "URDLSUDLR",
            personalCodeConfirmation = "URDLSUDLR",
        })).EnsureSuccessStatusCode();

        var poll = new HttpRequestMessage(HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        poll.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var response = await tvClient.SendAsync(poll);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single();
    }

    /// An album with party mode on — which also makes it ShowOnTv, as the
    /// product already requires.
    private static async Task<Guid> PartyAlbumAsync(
        HttpClient owner, string name, bool gameEnabled = false)
    {
        var album = (await (await owner.PostAsJsonAsync("/api/albums", new { name }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings", new { enabled = true }))
            .EnsureSuccessStatusCode();
        if (gameEnabled)
            (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
            {
                gameEnabled = true, minChallengeIntervalSeconds = 30,
                maxChallengeIntervalSeconds = 60, votesPerGuest = 3,
                maxChallengesPerSession = (int?)null,
            })).EnsureSuccessStatusCode();
        return album;
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient owner, Guid sessionId, object body) =>
        owner.PatchAsJsonAsync($"/api/tv-devices/{sessionId}/assignment", body);

    private static Task<HttpResponseMessage> RawAssignAsync(
        HttpClient client, Guid sessionId, Guid? albumId) =>
        PatchAsync(client, sessionId, albumId is null
            ? new { kind = "general", albumId = (Guid?)null }
            : new { kind = "party", albumId });

    private static async Task<JsonElement> AssignAsync(HttpClient owner, Guid sessionId, Guid? albumId)
    {
        var response = await RawAssignAsync(owner, sessionId, albumId);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private async Task<Guid?> PairedDeviceAsync(HttpClient owner, string publicCode, string secret)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/pairing/{publicCode}/device");
        request.Headers.Add(TvPairingService.PairingSecretHeader, secret);
        var response = await owner.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("sessionId").ValueKind == JsonValueKind.Null
            ? null : body.GetProperty("sessionId").GetGuid();
    }

    private async Task<Guid> SingleSessionIdAsync(Guid ownerUserId)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .TvSessions.SingleAsync(x => x.OwnerUserId == ownerUserId)).Id;
    }

    private async Task<JsonElement> TvAssignmentAsync(string cookie) =>
        (await TvJsonAsync("/api/tv/session", cookie)).GetProperty("assignment");

    private async Task<JsonElement> TvJsonAsync(string url, string cookie)
    {
        var response = await TvGet(url, cookie);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> TvGet(string url, string setCookie, bool post = false)
    {
        var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(setCookie)}");
        return _factory.CreateClient().SendAsync(request);
    }

    private static string CookieValue(string setCookie)
    {
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }
}
