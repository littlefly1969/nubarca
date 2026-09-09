using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tv;

namespace NubArca.Api.Tests.Albums;

/// <summary>
/// Deleting an album that has played a Party Game.
///
/// The hosted game's restricting foreign keys reach further than the party
/// link, and that is the whole bug: a vote names a PARTICIPANT, a round names a
/// CHALLENGE, and the session names both the LINK and the ALBUM. So an album
/// that had ever run a game could not be deleted at all — the constraint failed,
/// in four different places, at four different steps of a delete that had
/// already committed everything before it.
///
/// These tests are about the ORDER of that delete and about it being one unit of
/// work. They deliberately drive the whole thing through the real HTTP surface,
/// because the failure was never in a single query — it was in the sequence.
/// </summary>
public sealed class AlbumDeletePartyGameTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public AlbumDeletePartyGameTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task An_album_that_never_had_a_party_still_deletes()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = (await (await owner.PostAsJsonAsync("/api/albums", new { name = "Semplice" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/albums/{album}")).StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Albums.Where(a => a.Id == album).ToListAsync());
    }

    [Fact]
    public async Task An_album_whose_game_never_started_deletes()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await PartyGameAlbumAsync(owner, "Lobby");

        // No session row at all — a game that has not begun has none. The delete
        // must not depend on one existing.
        using (var scope = _factory.Services.CreateScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .PartyGameSessions.ToListAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/albums/{album}")).StatusCode);
        await AssertNothingLeftAsync(album);
    }

    [Fact]
    public async Task An_album_with_a_finished_game_its_rounds_and_its_votes_deletes()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await PartyGameAlbumAsync(owner, "Festa");
        var token = await ViewTokenAsync(owner, album);
        var guest = _factory.CreateClient();
        await JoinAsync(guest, token);
        await PlayToTheEndAsync(owner, album, token, guest);

        // The exact shape the bug needed: a session, a round, and a vote whose
        // participant the delete is also about to remove.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Single(await db.PartyGameSessions.ToListAsync());
            Assert.NotEmpty(await db.PartyGameRounds.ToListAsync());
            Assert.NotEmpty(await db.PartyGameVotes.ToListAsync());
            Assert.NotEmpty(await db.PartyParticipants.ToListAsync());
        }

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/albums/{album}")).StatusCode);
        await AssertNothingLeftAsync(album);
    }

    [Fact]
    public async Task An_album_deletes_after_the_game_was_restarted()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await PartyGameAlbumAsync(owner, "Festa");
        var token = await ViewTokenAsync(owner, album);
        var guest = _factory.CreateClient();
        await JoinAsync(guest, token);

        var finished = await PlayToTheEndAsync(owner, album, token, guest);
        // The session SURVIVES a restart by design, so this is the state where
        // an album carries a live session with no rounds under it.
        var lobby = await CommandAsync(owner, album, "restart_game",
            finished.GetProperty("version").GetInt32());
        Assert.Equal("lobby", lobby.GetProperty("phase").GetString());
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Single(await db.PartyGameSessions.ToListAsync());
            Assert.Empty(await db.PartyGameRounds.ToListAsync());
        }

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/albums/{album}")).StatusCode);
        await AssertNothingLeftAsync(album);

        // And a second play-through then a delete, so the "restarted and played
        // again" shape is covered too rather than only the emptied one.
        var again = await PartyGameAlbumAsync(owner, "Seconda");
        var againToken = await ViewTokenAsync(owner, again);
        var againGuest = _factory.CreateClient();
        await JoinAsync(againGuest, againToken);
        var over = await PlayToTheEndAsync(owner, again, againToken, againGuest);
        var reset = await CommandAsync(owner, again, "restart_game",
            over.GetProperty("version").GetInt32());
        await CommandAsync(owner, again, "start", reset.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/albums/{again}")).StatusCode);
        await AssertNothingLeftAsync(again);
    }

    [Fact]
    public async Task A_television_assigned_to_the_deleted_party_stays_paired_and_goes_general()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);

        var doomed = await PartyGameAlbumAsync(owner, "Festa");
        var survivor = await PartyGameAlbumAsync(owner, "Altra festa");
        var token = await ViewTokenAsync(owner, doomed);
        var guest = _factory.CreateClient();
        await JoinAsync(guest, token);
        await PlayToTheEndAsync(owner, doomed, token, guest);

        // A second television, pointed at the party that is NOT going away.
        var otherCookie = await PairTvAsync(owner, "second");
        var otherSessionId = await OtherSessionIdAsync(ownerId, sessionId);
        await AssignAsync(owner, sessionId, doomed);
        await AssignAsync(owner, otherSessionId, survivor);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/albums/{doomed}")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The party is what went away, not the television: still paired, still
        // this owner's, and back on the general experience with no dangling
        // pointer at a link that no longer exists.
        var moved = await db.TvSessions.SingleAsync(x => x.Id == sessionId);
        Assert.Null(moved.RevokedAt);
        Assert.Equal(TvDisplayAssignments.General, moved.DisplayAssignment);
        Assert.Null(moved.AssignedPartyAlbumLinkId);
        Assert.Equal(HttpStatusCode.OK, (await TvGet("/api/tv/albums", cookie)).StatusCode);

        // The other television is untouched — the reset is scoped to the links
        // of the album being deleted, not to the fleet.
        var untouched = await db.TvSessions.SingleAsync(x => x.Id == otherSessionId);
        Assert.Equal(TvDisplayAssignments.Party, untouched.DisplayAssignment);
        Assert.NotNull(untouched.AssignedPartyAlbumLinkId);
        Assert.Equal(survivor, (await TvGet("/api/tv/session", otherCookie)) is var r && r.IsSuccessStatusCode
            ? (await r.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("assignment").GetProperty("albumId").GetGuid()
            : Guid.Empty);
    }

    [Fact]
    public async Task Deleting_one_album_leaves_another_owners_party_game_alone()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var aliceAlbum = await PartyGameAlbumAsync(alice, "Festa di Alice");
        var aliceToken = await ViewTokenAsync(alice, aliceAlbum);
        var aliceGuest = _factory.CreateClient();
        await JoinAsync(aliceGuest, aliceToken);
        await PlayToTheEndAsync(alice, aliceAlbum, aliceToken, aliceGuest);

        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var bobAlbum = await PartyGameAlbumAsync(bob, "Festa di Bob");
        var bobToken = await ViewTokenAsync(bob, bobAlbum);
        var bobGuest = _factory.CreateClient();
        await JoinAsync(bobGuest, bobToken);
        await PlayToTheEndAsync(bob, bobAlbum, bobToken, bobGuest);

        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/albums/{aliceAlbum}")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Bob's evening is exactly where he left it.
        var bobSession = await db.PartyGameSessions.SingleAsync();
        Assert.Equal(bobAlbum, bobSession.AlbumId);
        Assert.NotEmpty(await db.PartyGameRounds.ToListAsync());
        Assert.NotEmpty(await db.PartyGameVotes.ToListAsync());
        Assert.NotEmpty(await db.PartyParticipants.ToListAsync());
    }

    // --- helpers -----------------------------------------------------------

    /// Everything this album owned is gone, and nothing of it is orphaned.
    private async Task AssertNothingLeftAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.Albums.Where(a => a.Id == albumId).ToListAsync());
        Assert.Empty(await db.PartyGameSessions.Where(g => g.AlbumId == albumId).ToListAsync());
        Assert.Empty(await db.PartyAlbumLinks.Where(l => l.AlbumId == albumId).ToListAsync());
        Assert.Empty(await db.PartyChallenges.Where(c => c.AlbumId == albumId).ToListAsync());

        // No orphans anywhere: a round or a vote whose session is gone would be
        // a row nothing can ever reach or clean up.
        var sessionIds = await db.PartyGameSessions.Select(g => g.Id).ToListAsync();
        Assert.All(await db.PartyGameRounds.ToListAsync(),
            r => Assert.Contains(r.PartyGameSessionId, sessionIds));
        Assert.All(await db.PartyGameVotes.ToListAsync(),
            v => Assert.Contains(v.PartyGameSessionId, sessionIds));
        var linkIds = await db.PartyAlbumLinks.Select(l => l.Id).ToListAsync();
        Assert.All(await db.PartyParticipants.ToListAsync(),
            p => Assert.Contains(p.PartyAlbumLinkId, linkIds));
    }

    private async Task<Guid> PartyGameAlbumAsync(HttpClient owner, string name)
    {
        var album = (await (await owner.PostAsJsonAsync("/api/albums", new { name }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await EnableAndStartPartyAsync(owner, album);
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

    private static async Task<JsonElement> PlayToTheEndAsync(
        HttpClient owner, Guid album, string token, HttpClient guest)
    {
        var snapshot = await OwnerAsync(owner, album);
        var version = snapshot.GetProperty("version").GetInt32();
        while (snapshot.GetProperty("status").GetString() != "finished")
        {
            var command = snapshot.GetProperty("availableCommands")
                .EnumerateArray().First().GetString()!;
            snapshot = await CommandAsync(owner, album, command, version);
            version = snapshot.GetProperty("version").GetInt32();
            if (snapshot.GetProperty("phase").GetString() == "voting_open")
            {
                var round = (await GuestAsync(guest, token)).GetProperty("roundId").GetGuid();
                (await guest.PostAsJsonAsync($"/api/party/{token}/game/vote",
                    new { roundId = round, value = "yes" })).EnsureSuccessStatusCode();
            }
        }
        return snapshot;
    }

    private static async Task<JsonElement> OwnerAsync(HttpClient owner, Guid album)
    {
        var response = await owner.GetAsync($"/api/albums/{album}/party-game");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> CommandAsync(
        HttpClient owner, Guid album, string command, int expectedVersion)
    {
        var response = await owner.PostAsJsonAsync($"/api/albums/{album}/party-game/commands",
            new { command, expectedVersion });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> GuestAsync(HttpClient guest, string token)
    {
        var response = await guest.GetAsync($"/api/party/{token}/game");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task JoinAsync(HttpClient guest, string token) =>
        (await guest.PostAsync($"/api/party/{token}/game/join", null)).EnsureSuccessStatusCode();

    private static async Task<string> ViewTokenAsync(HttpClient owner, Guid album) =>
        (await (await owner.GetAsync($"/api/albums/{album}/party-settings"))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("partyUrl").GetString()!["/party/".Length..];

    private static async Task AssignAsync(HttpClient owner, Guid sessionId, Guid albumId) =>
        (await owner.PatchAsJsonAsync($"/api/tv-devices/{sessionId}/assignment",
            new { kind = "party", albumId })).EnsureSuccessStatusCode();

    private async Task<string> PairTvAsync(HttpClient owner, string? label = null)
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
        _ = label;
        return response.Headers.GetValues("Set-Cookie").Single();
    }

    private async Task<Guid> SingleSessionIdAsync(Guid ownerUserId)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .TvSessions.Where(x => x.OwnerUserId == ownerUserId)
            .OrderBy(x => x.CreatedAt).FirstAsync()).Id;
    }

    private async Task<Guid> OtherSessionIdAsync(Guid ownerUserId, Guid notThisOne)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .TvSessions.Where(x => x.OwnerUserId == ownerUserId && x.Id != notThisOne)
            .SingleAsync()).Id;
    }

    private Task<HttpResponseMessage> TvGet(string url, string setCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var value = setCookie.Split(';', 2)[0];
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={value[(value.IndexOf('=') + 1)..]}");
        return _factory.CreateClient().SendAsync(request);
    }

    // Enabling guest access PUBLISHES the party — an invitation, which is
    // deliberately not the party itself. These tests exercise the party, so they
    // start it, exactly as a host does.
    private static async Task EnableAndStartPartyAsync(HttpClient owner, Guid album)
    {
        var response = await owner.PatchAsJsonAsync(
            $"/api/albums/{album}/party-settings", new { enabled = true });
        response.EnsureSuccessStatusCode();
        await NubArca.Api.Tests.Party.PartyTestHost.StartAsync(
            owner, await response.Content.ReadFromJsonAsync<JsonElement>());
    }
}
