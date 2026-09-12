using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tv;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// A television's permission to SHOW one party.
///
/// The whole point of this capability is what it is NOT. A display is not a
/// guest: it never holds a party token, never acquires a browser cookie, never
/// becomes a participant, and can do exactly one thing — read the party its
/// own assignment names. These tests are mostly about that boundary, because
/// the easy way to build this feature is to hand the television a guest token
/// and the easy way is the wrong one.
///
/// The second theme is that EXPIRY IS NOT THE REVOCATION BOUNDARY. The grant
/// lasts four hours; what actually decides validity is re-read on every
/// request, so un-pairing a television or pointing it somewhere else kills its
/// grant in the same instant.
/// </summary>
public sealed class PartyDisplayGrantTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyDisplayGrantTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_television_assigned_to_a_party_is_granted_exactly_that_party()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyGameAlbumAsync(owner, "Festa");
        await AssignAsync(owner, sessionId, album);

        var grant = await MintAsync(cookie);
        Assert.False(string.IsNullOrWhiteSpace(grant.Token));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.PartyDisplayGrants.SingleAsync();

        // Bound to the device and to the party, and stored as a DIGEST: a
        // stolen database is not a stolen display.
        Assert.Equal(sessionId, row.TvSessionId);
        var link = await db.PartyAlbumLinks.AsNoTracking()
            .SingleAsync(l => l.AlbumId == album && l.RevokedAt == null);
        Assert.Equal(link.Id, row.PartyAlbumLinkId);
        Assert.DoesNotContain(grant.Token, row.TokenHash);
        Assert.Equal(64, row.TokenHash.Length);
        Assert.Equal(PartyDisplayService.HashToken(grant.Token), row.TokenHash);

        // Four hours, and it is a BOUND rather than the security model.
        Assert.InRange((row.ExpiresAt - row.CreatedAt).TotalMinutes, 239, 241);

        // And it reads the party.
        var snapshot = await DisplayJsonAsync("/api/party-display/game", grant.Token);
        Assert.Equal("Festa", snapshot.GetProperty("albumName").GetString());
    }

    [Fact]
    public async Task Only_a_live_session_assigned_to_a_party_can_mint()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);

        // No cookie at all.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().PostAsync("/api/tv/party-display/grant", null)).StatusCode);
        // A cookie the server never issued.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await RawMintAsync($"x={new string('9', 43)}")).StatusCode);
        // Paired but GENERAL: nothing to show.
        Assert.Equal(HttpStatusCode.NotFound, (await RawMintAsync(cookie)).StatusCode);

        // Assigned — now it works.
        var album = await PartyGameAlbumAsync(owner, "Festa");
        await AssignAsync(owner, sessionId, album);
        Assert.Equal(HttpStatusCode.OK, (await RawMintAsync(cookie)).StatusCode);

        // Revoked session cannot mint, even though the assignment still reads
        // `party` on the row.
        (await owner.DeleteAsync($"/api/tv-devices/{sessionId}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await RawMintAsync(cookie)).StatusCode);
    }

    [Fact]
    public async Task An_expired_session_can_neither_mint_nor_present_a_grant()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyGameAlbumAsync(owner, "Festa");
        await AssignAsync(owner, sessionId, album);
        var grant = await MintAsync(cookie);

        // Age the session past its expiry.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.TvSessions.Where(x => x.Id == sessionId)
                .ExecuteUpdateAsync(u => u.SetProperty(
                    x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await RawMintAsync(cookie)).StatusCode);
        // The already-minted grant dies with the session, not at its own expiry.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await DisplayAsync("/api/party-display/game", grant.Token)).StatusCode);
    }

    [Fact]
    public async Task An_expired_grant_is_refused()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        await AssignAsync(owner, sessionId, await PartyGameAlbumAsync(owner, "Festa"));
        var grant = await MintAsync(cookie);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PartyDisplayGrants.ExecuteUpdateAsync(
                u => u.SetProperty(g => g.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await DisplayAsync("/api/party-display/game", grant.Token)).StatusCode);
    }

    [Fact]
    public async Task Changing_the_assignment_invalidates_the_grant_immediately()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var first = await PartyGameAlbumAsync(owner, "Festa di Anna");
        var second = await PartyGameAlbumAsync(owner, "Compleanno");

        await AssignAsync(owner, sessionId, first);
        var grantA = await MintAsync(cookie);
        Assert.Equal("Festa di Anna",
            (await DisplayJsonAsync("/api/party-display/game", grantA.Token))
                .GetProperty("albumName").GetString());

        // PARTY A → PARTY B. The old grant dies at once, long before its four
        // hours — and it does NOT become a grant for B.
        await AssignAsync(owner, sessionId, second);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await DisplayAsync("/api/party-display/game", grantA.Token)).StatusCode);

        var grantB = await MintAsync(cookie);
        Assert.Equal("Compleanno",
            (await DisplayJsonAsync("/api/party-display/game", grantB.Token))
                .GetProperty("albumName").GetString());
        // Still dead.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await DisplayAsync("/api/party-display/game", grantA.Token)).StatusCode);

        // PARTY → GENERAL kills the live one too.
        await AssignAsync(owner, sessionId, null);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await DisplayAsync("/api/party-display/game", grantB.Token)).StatusCode);
    }

    [Fact]
    public async Task Unpairing_the_television_invalidates_its_grant()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        await AssignAsync(owner, sessionId, await PartyGameAlbumAsync(owner, "Festa"));
        var grant = await MintAsync(cookie);
        Assert.Equal(HttpStatusCode.OK,
            (await DisplayAsync("/api/party-display/game", grant.Token)).StatusCode);

        (await owner.DeleteAsync($"/api/tv-devices/{sessionId}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await DisplayAsync("/api/party-display/game", grant.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await DisplayAsync("/api/party-display/join-qr", grant.Token)).StatusCode);
    }

    [Fact]
    public async Task A_minted_grant_supersedes_the_one_before_it()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        await AssignAsync(owner, sessionId, await PartyGameAlbumAsync(owner, "Festa"));

        var first = await MintAsync(cookie);
        var second = await MintAsync(cookie);
        Assert.NotEqual(first.Token, second.Token);

        // A remount must not leave a second usable credential behind it.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await DisplayAsync("/api/party-display/game", first.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await DisplayAsync("/api/party-display/game", second.Token)).StatusCode);
    }

    [Fact]
    public async Task One_partys_grant_never_reads_another_partys_game()
    {
        var (aliceId, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceCookie = await PairTvAsync(alice);
        var aliceSession = await SingleSessionIdAsync(aliceId);
        await AssignAsync(alice, aliceSession, await PartyGameAlbumAsync(alice, "Festa di Alice"));
        var aliceGrant = await MintAsync(aliceCookie);

        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var bobCookie = await PairTvAsync(bob);
        var bobSession = await SingleSessionIdAsync(bobId);
        await AssignAsync(bob, bobSession, await PartyGameAlbumAsync(bob, "Festa di Bob"));
        var bobGrant = await MintAsync(bobCookie);

        // Each grant reads its own party and only its own. There is no
        // parameter to change: the party comes from the grant.
        Assert.Equal("Festa di Alice",
            (await DisplayJsonAsync("/api/party-display/game", aliceGrant.Token))
                .GetProperty("albumName").GetString());
        Assert.Equal("Festa di Bob",
            (await DisplayJsonAsync("/api/party-display/game", bobGrant.Token))
                .GetProperty("albumName").GetString());
    }

    [Fact]
    public async Task A_display_reads_the_game_and_can_do_nothing_else_to_it()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyGameAlbumAsync(owner, "Festa");
        await AssignAsync(owner, sessionId, album);
        var grant = await MintAsync(cookie);

        // The guest and owner surfaces are simply not there for this credential.
        // It is not that they refuse it — the display route family has no vote,
        // no upload, no print and no command in it at all.
        foreach (var path in new[]
        {
            "/api/party-display/game/vote", "/api/party-display/vote",
            "/api/party-display/upload", "/api/party-display/print",
            "/api/party-display/commands",
        })
        {
            var post = new HttpRequestMessage(HttpMethod.Post, path);
            post.Headers.Add(PartyDisplayService.GrantHeader, grant.Token);
            var response = await _factory.CreateClient().SendAsync(post);
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{path} answered {response.StatusCode}");
        }

        // And the grant is useless as an owner credential.
        var ownerCall = new HttpRequestMessage(
            HttpMethod.Get, $"/api/albums/{album}/party-game");
        ownerCall.Headers.Add(PartyDisplayService.GrantHeader, grant.Token);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().SendAsync(ownerCall)).StatusCode);
    }

    [Fact]
    public async Task Reading_as_a_display_mints_no_participant_and_sets_no_guest_cookie()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        await AssignAsync(owner, sessionId, await PartyGameAlbumAsync(owner, "Festa"));
        var grant = await MintAsync(cookie);

        for (var i = 0; i < 3; i++)
        {
            var response = await DisplayAsync("/api/party-display/game", grant.Token);
            response.EnsureSuccessStatusCode();
            // The guest cookie is path-scoped to /api/party, so this family
            // could not set it even by accident — asserted rather than assumed.
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.PartyParticipants.ToListAsync());
        Assert.Empty(await db.PartyGameVotes.ToListAsync());
    }

    [Fact]
    public async Task A_display_read_stamps_the_screen_heartbeat()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyGameAlbumAsync(owner, "Festa");
        await AssignAsync(owner, sessionId, album);
        var grant = await MintAsync(cookie);

        using (var scope = _factory.Services.CreateScope())
            Assert.Null((await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .PartyAlbumLinks.AsNoTracking().SingleAsync(l => l.AlbumId == album))
                .LastDisplaySeenAt);

        (await DisplayAsync("/api/party-display/game", grant.Token)).EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
            Assert.NotNull((await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .PartyAlbumLinks.AsNoTracking().SingleAsync(l => l.AlbumId == album))
                .LastDisplaySeenAt);

        // The control room now says a screen is showing it.
        var control = await (await owner.GetAsync($"/api/albums/{album}/party-game"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Number, control.GetProperty("displaySeenSecondsAgo").ValueKind);
        Assert.Equal(0, control.GetProperty("guestsPresent").GetInt32());
    }

    [Fact]
    public async Task The_display_snapshot_keeps_the_result_secret_exactly_as_a_guest_screen_does()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyGameAlbumAsync(owner, "Festa");
        await AssignAsync(owner, sessionId, album);
        var grant = await MintAsync(cookie);

        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
            version = (await CommandAsync(owner, album, command, version))
                .GetProperty("version").GetInt32();

        // VOTING OPEN: participation is safe, the split is not.
        var voting = await DisplayJsonAsync("/api/party-display/game", grant.Token);
        Assert.Equal("voting_open", voting.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, voting.GetProperty("voting").GetProperty("yes").ValueKind);
        Assert.Equal(JsonValueKind.Null, voting.GetProperty("voting").GetProperty("no").ValueKind);
        Assert.Equal(JsonValueKind.Null, voting.GetProperty("voting").GetProperty("passed").ValueKind);
        Assert.Equal(JsonValueKind.Number,
            voting.GetProperty("voting").GetProperty("received").ValueKind);

        // VOTING CLOSED: the owner may know; a screen still may not.
        version = (await CommandAsync(owner, album, "close_voting", version))
            .GetProperty("version").GetInt32();
        var closed = await DisplayJsonAsync("/api/party-display/game", grant.Token);
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("voting").GetProperty("yes").ValueKind);

        // RESULT: now the room may see it.
        await CommandAsync(owner, album, "reveal_result", version);
        var result = await DisplayJsonAsync("/api/party-display/game", grant.Token);
        Assert.Equal("result", result.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Number, result.GetProperty("voting").GetProperty("yes").ValueKind);

        // And no session id, no command vocabulary, no token, ever.
        var raw = await (await DisplayAsync("/api/party-display/game", grant.Token))
            .Content.ReadAsStringAsync();
        foreach (var forbidden in new[]
            { "sessionId", "availableCommands", "tokenHash", "TokenHash", "partyUrl", "guestUrl" })
            Assert.DoesNotContain(forbidden, raw);
    }

    [Fact]
    public async Task The_media_url_a_display_is_given_stays_on_the_display_surface()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyGameAlbumAsync(owner, "Festa");
        await AssignAsync(owner, sessionId, album);
        var grant = await MintAsync(cookie);
        await CommandAsync(owner, album, "start", 0);

        var snapshot = await DisplayJsonAsync("/api/party-display/game", grant.Token);
        var challenge = snapshot.GetProperty("challenge");
        Assert.Equal(JsonValueKind.Null, challenge.GetProperty("mediaUrl").ValueKind);

        // The activity here carries no photograph, so the assertion that matters
        // is the negative one: nothing in the body addresses /api/party/{token}.
        var raw = await (await DisplayAsync("/api/party-display/game", grant.Token))
            .Content.ReadAsStringAsync();
        Assert.DoesNotContain("/api/party/", raw);
    }

    [Fact]
    public async Task The_lobby_QR_is_pixels_and_the_party_token_never_reaches_the_display()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);
        var album = await PartyGameAlbumAsync(owner, "Festa");
        await AssignAsync(owner, sessionId, album);
        var grant = await MintAsync(cookie);

        var response = await DisplayAsync("/api/party-display/join-qr", grant.Token);
        response.EnsureSuccessStatusCode();
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        var svg = await response.Content.ReadAsStringAsync();
        Assert.Contains("<svg", svg);
        Assert.Contains("</svg>", svg);

        // The room can scan it. The television cannot READ it: the guest token
        // appears nowhere in the response, so the display holds pixels rather
        // than a capability.
        var token = await ViewTokenAsync(owner, album);
        Assert.DoesNotContain(token, svg);
        Assert.DoesNotContain("/party/", svg);
    }

    // --- helpers -----------------------------------------------------------

    private sealed record Grant(string Token, DateTime ExpiresAt);

    private async Task<Grant> MintAsync(string cookie)
    {
        var response = await RawMintAsync(cookie);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Grant(body.GetProperty("grant").GetString()!,
            body.GetProperty("expiresAt").GetDateTime());
    }

    private Task<HttpResponseMessage> RawMintAsync(string setCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/tv/party-display/grant");
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(setCookie)}");
        return _factory.CreateClient().SendAsync(request);
    }

    private Task<HttpResponseMessage> DisplayAsync(string url, string grant)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(PartyDisplayService.GrantHeader, grant);
        return _factory.CreateClient().SendAsync(request);
    }

    private async Task<JsonElement> DisplayJsonAsync(string url, string grant)
    {
        var response = await DisplayAsync(url, grant);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> PairTvAsync(HttpClient owner)
    {
        var tvClient = _factory.CreateClient();
        var started = (await (await tvClient.PostAsync("/api/tv/pairing/start", null))
            .Content.ReadFromJsonAsync<TvPairingStartedDto>())!;
        (await owner.PostAsJsonAsync($"/api/tv/pairing/{started.PublicCode}/approve", new
        {
            pairingSecret = started.PairingSecret,
            personalCode = "URDLSUDLR", personalCodeConfirmation = "URDLSUDLR",
        })).EnsureSuccessStatusCode();
        var poll = new HttpRequestMessage(HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        poll.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var response = await tvClient.SendAsync(poll);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single();
    }

    /// A LIVE party with its game on: the only state in which a display grant
    /// can exist, because the grant follows the television's presentation and
    /// the game takes the screen only while the party is live.
    private static async Task<Guid> PartyGameAlbumAsync(HttpClient owner, string name)
    {
        var album = (await (await owner.PostAsJsonAsync("/api/albums", new { name }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings", new { enabled = true }))
            .EnsureSuccessStatusCode();
        var partyId = (await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{album}/party-settings"))
            .GetProperty("partyId").GetGuid();
        var party = await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}");
        (await owner.PostAsJsonAsync($"/api/parties/{partyId}/start-live",
            new { version = party.GetProperty("version").GetInt32() })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 30,
            maxChallengeIntervalSeconds = 60, votesPerGuest = 3,
            maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();
        foreach (var title in new[] { "Uno", "Due" })
            (await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
            {
                title, body = "Descrizione", kind = "dare",
                mediaFileItemId = (Guid?)null, isEnabled = true, votingMode = "binary",
            })).EnsureSuccessStatusCode();
        return album;
    }

    private static async Task<string> ViewTokenAsync(HttpClient owner, Guid album) =>
        (await (await owner.GetAsync($"/api/albums/{album}/party-settings"))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("partyUrl").GetString()!["/party/".Length..];

    private static async Task AssignAsync(HttpClient owner, Guid sessionId, Guid? albumId) =>
        (await owner.PatchAsJsonAsync($"/api/tv-devices/{sessionId}/assignment",
            albumId is null
                ? new { kind = "general", albumId = (Guid?)null }
                : new { kind = "party", albumId })).EnsureSuccessStatusCode();

    private static async Task<JsonElement> CommandAsync(
        HttpClient owner, Guid album, string command, int expectedVersion)
    {
        var response = await owner.PostAsJsonAsync($"/api/albums/{album}/party-game/commands",
            new { command, expectedVersion });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<Guid> SingleSessionIdAsync(Guid ownerUserId)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .TvSessions.SingleAsync(x => x.OwnerUserId == ownerUserId)).Id;
    }

    private static string CookieValue(string setCookie)
    {
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }
}
