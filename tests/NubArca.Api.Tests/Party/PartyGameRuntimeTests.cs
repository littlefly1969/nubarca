using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The runtime through its real HTTP surface: the owner commands it, a guest
/// token reads it, and neither of them can put it into a state the state machine
/// does not allow.
/// </summary>
public sealed class PartyGameRuntimeTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyGameRuntimeTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task An_unstarted_game_is_a_lobby_at_version_zero_and_reading_it_writes_nothing()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 2);

        var snapshot = await SnapshotAsync(owner, album);
        Assert.Equal("lobby", snapshot.GetProperty("status").GetString());
        Assert.Equal("lobby", snapshot.GetProperty("phase").GetString());
        Assert.Equal(0, snapshot.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("sessionId").ValueKind);
        Assert.Equal(2, snapshot.GetProperty("totalChallenges").GetInt32());
        Assert.Equal("Uno", snapshot.GetProperty("nextChallenge").GetProperty("title").GetString());
        Assert.Equal(["start", "finish"], Commands(snapshot));

        // Three reads, still no row: a television polling a party that has not
        // started must never start it.
        await SnapshotAsync(owner, album);
        await SnapshotAsync(owner, album);
        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyGameSessions.ToListAsync());
    }

    [Fact]
    public async Task A_full_round_walks_every_phase_and_the_version_moves_once_per_command()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 2);

        var s = await CommandAsync(owner, album, "start", 0);
        Assert.Equal("live", s.GetProperty("status").GetString());
        Assert.Equal("challenge_reveal", s.GetProperty("phase").GetString());
        Assert.Equal(1, s.GetProperty("version").GetInt32());
        Assert.Equal(1, s.GetProperty("roundNumber").GetInt32());
        Assert.Equal("Uno", s.GetProperty("currentChallenge").GetProperty("title").GetString());
        Assert.Equal("Due", s.GetProperty("nextChallenge").GetProperty("title").GetString());
        Assert.Equal(["start_challenge", "skip_challenge", "finish"], Commands(s));

        s = await CommandAsync(owner, album, "start_challenge", 1);
        Assert.Equal("challenge_active", s.GetProperty("phase").GetString());
        s = await CommandAsync(owner, album, "open_voting", 2);
        Assert.Equal("voting_open", s.GetProperty("phase").GetString());
        s = await CommandAsync(owner, album, "close_voting", 3);
        Assert.Equal("voting_closed", s.GetProperty("phase").GetString());
        s = await CommandAsync(owner, album, "reveal_result", 4);
        Assert.Equal("result", s.GetProperty("phase").GetString());
        Assert.Equal(["next_challenge", "finish"], Commands(s));

        s = await CommandAsync(owner, album, "next_challenge", 5);
        Assert.Equal("challenge_reveal", s.GetProperty("phase").GetString());
        Assert.Equal(2, s.GetProperty("roundNumber").GetInt32());
        Assert.Equal(1, s.GetProperty("playedRounds").GetInt32());
        Assert.Equal("Due", s.GetProperty("currentChallenge").GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("nextChallenge").ValueKind);
        Assert.Equal(6, s.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task The_last_activity_ends_the_game_by_itself()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 1);

        var version = 0;
        foreach (var command in new[]
            { "start", "start_challenge", "open_voting", "close_voting", "reveal_result" })
        {
            var step = await CommandAsync(owner, album, command, version);
            version = step.GetProperty("version").GetInt32();
        }
        var finished = await CommandAsync(owner, album, "next_challenge", version);
        Assert.Equal("finished", finished.GetProperty("status").GetString());
        Assert.Equal("finished", finished.GetProperty("phase").GetString());
        Assert.Equal(1, finished.GetProperty("playedRounds").GetInt32());
        Assert.Equal(JsonValueKind.Null, finished.GetProperty("currentChallenge").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, finished.GetProperty("finishedAt").ValueKind);
        // The only thing left to do with the evening is another one.
        Assert.Equal(["restart_game"], Commands(finished));

        // And a finished game accepts nothing else, at any version.
        var refused = await RawCommandAsync(owner, album, "start", finished.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("illegal_transition", await CodeAsync(refused));
    }

    [Fact]
    public async Task A_stale_version_is_refused_and_handed_the_current_state()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 2);
        await CommandAsync(owner, album, "start", 0);

        var stale = await RawCommandAsync(owner, album, "start_challenge", 0);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("version_conflict", body.GetProperty("code").GetString());

        // The refusal is useful: it carries the state the caller should have had.
        var current = body.GetProperty("snapshot");
        Assert.Equal(1, current.GetProperty("version").GetInt32());
        Assert.Equal("challenge_reveal", current.GetProperty("phase").GetString());

        // The game did not move.
        Assert.Equal("challenge_reveal", (await SnapshotAsync(owner, album)).GetProperty("phase").GetString());
    }

    [Fact]
    public async Task A_repeated_command_advances_the_game_exactly_once()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 3);
        await CommandAsync(owner, album, "start", 0);

        // The same tap twice — the second quotes the version the first consumed.
        await CommandAsync(owner, album, "start_challenge", 1);
        var second = await RawCommandAsync(owner, album, "start_challenge", 1);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("version_conflict", await CodeAsync(second));

        var snapshot = await SnapshotAsync(owner, album);
        Assert.Equal("challenge_active", snapshot.GetProperty("phase").GetString());
        Assert.Equal(2, snapshot.GetProperty("version").GetInt32());
        Assert.Equal(1, snapshot.GetProperty("roundNumber").GetInt32());
    }

    [Fact]
    public async Task An_illegal_transition_is_refused_without_touching_the_game()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 2);
        var started = await CommandAsync(owner, album, "start", 0);
        var version = started.GetProperty("version").GetInt32();

        foreach (var illegal in new[] { "close_voting", "reveal_result", "next_challenge", "start" })
        {
            var refused = await RawCommandAsync(owner, album, illegal, version);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("illegal_transition", await CodeAsync(refused));
        }

        var snapshot = await SnapshotAsync(owner, album);
        Assert.Equal("challenge_reveal", snapshot.GetProperty("phase").GetString());
        Assert.Equal(version, snapshot.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task A_game_with_no_activities_cannot_start()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 0);
        var refused = await RawCommandAsync(owner, album, "start", 0);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("no_challenges", await CodeAsync(refused));
    }

    [Fact]
    public async Task Skipping_abandons_the_round_and_moves_to_the_next_activity()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 2);
        await CommandAsync(owner, album, "start", 0);
        var skipped = await CommandAsync(owner, album, "skip_challenge", 1);

        Assert.Equal("challenge_reveal", skipped.GetProperty("phase").GetString());
        Assert.Equal(2, skipped.GetProperty("roundNumber").GetInt32());
        Assert.Equal("Due", skipped.GetProperty("currentChallenge").GetProperty("title").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rounds = await db.PartyGameRounds.OrderBy(x => x.Sequence).ToListAsync();
        Assert.Equal(["abandoned", "active"], rounds.Select(x => x.Status));
        Assert.NotNull(rounds[0].CompletedAt);
    }

    [Fact]
    public async Task An_activity_is_never_played_twice_in_one_game()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 3);
        var version = 0;
        var seen = new List<string>();
        while (true)
        {
            var snapshot = await SnapshotAsync(owner, album);
            if (snapshot.GetProperty("status").GetString() == "finished") break;
            if (snapshot.GetProperty("currentChallenge").ValueKind != JsonValueKind.Null)
                seen.Add(snapshot.GetProperty("currentChallenge").GetProperty("id").GetString()!);
            var command = version == 0 ? "start" : "skip_challenge";
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();
        }
        Assert.Equal(3, seen.Distinct().Count());
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task Finishing_mid_round_abandons_it_and_finishing_after_a_result_completes_it()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var abandonAlbum = await SetUpGameAsync(owner, challenges: 2, name: "Abbandonata");
        await CommandAsync(owner, abandonAlbum, "start", 0);
        await CommandAsync(owner, abandonAlbum, "start_challenge", 1);
        await CommandAsync(owner, abandonAlbum, "finish", 2);

        var completeAlbum = await SetUpGameAsync(owner, challenges: 2, name: "Completata");
        var version = 0;
        foreach (var command in new[]
            { "start", "start_challenge", "open_voting", "close_voting", "reveal_result", "finish" })
            version = (await CommandAsync(owner, completeAlbum, command, version)).GetProperty("version").GetInt32();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var abandoned = await db.PartyGameRounds
            .Where(r => db.PartyGameSessions.Any(s => s.Id == r.PartyGameSessionId && s.AlbumId == abandonAlbum))
            .SingleAsync();
        var completed = await db.PartyGameRounds
            .Where(r => db.PartyGameSessions.Any(s => s.Id == r.PartyGameSessionId && s.AlbumId == completeAlbum))
            .SingleAsync();
        Assert.Equal(PartyGameRoundStatuses.Abandoned, abandoned.Status);
        Assert.Equal(PartyGameRoundStatuses.Completed, completed.Status);
    }

    [Fact]
    public async Task Only_the_owner_reaches_the_game_and_only_while_it_is_enabled()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var album = await SetUpGameAsync(alice, challenges: 1);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/albums/{album}/party-game")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync(
            $"/api/albums/{album}/party-game/commands",
            new { command = "start", expectedVersion = 0 })).StatusCode);

        // Turning the game switch off closes the runtime on the next request.
        await SetGameEnabledAsync(alice, album, enabled: false);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/albums/{album}/party-game")).StatusCode);
        var refused = await RawCommandAsync(alice, album, "start", 0);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("game_disabled", await CodeAsync(refused));
    }

    [Fact]
    public async Task An_unknown_command_is_a_bad_request_not_a_conflict()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 1);
        Assert.Equal(HttpStatusCode.BadRequest, (await RawCommandAsync(owner, album, "reveal_challenge", 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(
            $"/api/albums/{album}/party-game/commands", new { expectedVersion = 0 })).StatusCode);
    }

    [Fact]
    public async Task A_guest_token_reads_the_public_snapshot_and_only_sees_an_activity_on_screen()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 2);
        var token = await ViewTokenAsync(owner, album);
        var guest = _factory.CreateClient();

        var lobby = await GuestSnapshotAsync(guest, token);
        Assert.Equal("lobby", lobby.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, lobby.GetProperty("challenge").ValueKind);
        Assert.Equal(2, lobby.GetProperty("totalChallenges").GetInt32());
        Assert.Equal("Festa", lobby.GetProperty("albumName").GetString());

        await CommandAsync(owner, album, "start", 0);
        var live = await GuestSnapshotAsync(guest, token);
        Assert.Equal("challenge_reveal", live.GetProperty("phase").GetString());
        Assert.Equal("Uno", live.GetProperty("challenge").GetProperty("title").GetString());
        Assert.Equal(1, live.GetProperty("roundNumber").GetInt32());

        // Nothing owner-shaped ever crosses the token boundary.
        foreach (var forbidden in new[] { "sessionId", "availableCommands", "nextChallenge", "playedRounds" })
            Assert.False(live.TryGetProperty(forbidden, out _), forbidden);
    }

    [Fact]
    public async Task The_public_snapshot_needs_a_valid_token_and_an_enabled_game()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 1);
        var token = await ViewTokenAsync(owner, album);

        Assert.Equal(HttpStatusCode.NotFound,
            (await _factory.CreateClient().GetAsync("/api/party/not-a-token/game")).StatusCode);

        await SetGameEnabledAsync(owner, album, enabled: false);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _factory.CreateClient().GetAsync($"/api/party/{token}/game")).StatusCode);
    }

    [Fact]
    public async Task An_activity_a_round_has_played_can_no_longer_be_deleted()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 2);
        var started = await CommandAsync(owner, album, "start", 0);
        var playing = started.GetProperty("currentChallenge").GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync(
            $"/api/albums/{album}/party-challenges/{playing}")).StatusCode);
    }

    [Fact]
    public async Task The_guest_hub_is_told_where_the_game_lives_or_nothing_at_all()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 1);
        var token = await ViewTokenAsync(owner, album);

        var hub = await (await _factory.CreateClient().GetAsync($"/api/party/{token}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"/party/{token}/game", hub.GetProperty("gameUrl").GetString());

        // Turning the game off removes the capability rather than disabling it:
        // a null url is the whole answer, exactly as printing works.
        await SetGameEnabledAsync(owner, album, enabled: false);
        var without = await (await _factory.CreateClient().GetAsync($"/api/party/{token}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, without.GetProperty("gameUrl").ValueKind);
    }

    [Fact]
    public async Task The_control_room_is_told_about_the_room()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 2);
        var token = await ViewTokenAsync(owner, album);

        var quiet = await SnapshotAsync(owner, album);
        Assert.Equal(0, quiet.GetProperty("guestsPresent").GetInt32());
        // Never seen is not "seen a long time ago": it is null.
        Assert.Equal(JsonValueKind.Null, quiet.GetProperty("displaySeenSecondsAgo").ValueKind);
        Assert.Equal($"/party/{token}/tv", quiet.GetProperty("tvUrl").GetString());
        Assert.Equal($"/party/{token}/game", quiet.GetProperty("guestUrl").GetString());

        // A guest joining is a guest in the room.
        var guest = _factory.CreateClient();
        (await guest.PostAsync($"/api/party/{token}/game/join", null)).EnsureSuccessStatusCode();
        Assert.Equal(1, (await SnapshotAsync(owner, album)).GetProperty("guestsPresent").GetInt32());
    }

    [Fact]
    public async Task Only_a_client_that_says_it_is_a_screen_counts_as_one()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 1);
        var token = await ViewTokenAsync(owner, album);

        // A guest polling the same endpoint is not a television, and the server
        // does not infer one from the absence of a cookie.
        var guest = _factory.CreateClient();
        (await guest.PostAsync($"/api/party/{token}/game/join", null)).EnsureSuccessStatusCode();
        (await guest.GetAsync($"/api/party/{token}/game")).EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null,
            (await SnapshotAsync(owner, album)).GetProperty("displaySeenSecondsAgo").ValueKind);

        var tv = _factory.CreateClient();
        (await tv.GetAsync($"/api/party/{token}/game?display=1")).EnsureSuccessStatusCode();
        var seen = (await SnapshotAsync(owner, album)).GetProperty("displaySeenSecondsAgo");
        Assert.Equal(JsonValueKind.Number, seen.ValueKind);
        Assert.InRange(seen.GetInt32(), 0, 30);
    }

    [Fact]
    public async Task A_screen_polling_never_contends_with_a_command()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpGameAsync(owner, challenges: 2);
        var token = await ViewTokenAsync(owner, album);
        var tv = _factory.CreateClient();

        // The heartbeat writes one column on the LINK, outside the session's
        // concurrency token, so a television polling every couple of seconds
        // cannot cost an owner a command.
        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
        {
            (await tv.GetAsync($"/api/party/{token}/game?display=1")).EnsureSuccessStatusCode();
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();
        }
        Assert.Equal(3, version);
    }

    // --- helpers -----------------------------------------------------------

    private async Task<Guid> SetUpGameAsync(HttpClient owner, int challenges, string name = "Festa")
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        var album = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings", new { enabled = true }))
            .EnsureSuccessStatusCode();
        await SetGameEnabledAsync(owner, album, enabled: true);
        var names = new[] { "Uno", "Due", "Tre", "Quattro" };
        for (var i = 0; i < challenges; i++)
            (await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
            {
                title = names[i], body = "Descrizione", kind = "dare",
                mediaFileItemId = (Guid?)null, isEnabled = true,
            })).EnsureSuccessStatusCode();
        return album;
    }

    private static async Task SetGameEnabledAsync(HttpClient owner, Guid album, bool enabled) =>
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = enabled, minChallengeIntervalSeconds = 30,
            maxChallengeIntervalSeconds = 60, votesPerGuest = 3,
            maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();

    private static async Task<string> ViewTokenAsync(HttpClient owner, Guid album)
    {
        var status = await (await owner.GetAsync($"/api/albums/{album}/party-settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        return status.GetProperty("partyUrl").GetString()!["/party/".Length..];
    }

    private static async Task<JsonElement> SnapshotAsync(HttpClient owner, Guid album)
    {
        var response = await owner.GetAsync($"/api/albums/{album}/party-game");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> GuestSnapshotAsync(HttpClient guest, string token)
    {
        var response = await guest.GetAsync($"/api/party/{token}/game");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> RawCommandAsync(
        HttpClient owner, Guid album, string command, int expectedVersion) =>
        owner.PostAsJsonAsync($"/api/albums/{album}/party-game/commands",
            new { command, expectedVersion });

    private static async Task<JsonElement> CommandAsync(
        HttpClient owner, Guid album, string command, int expectedVersion)
    {
        var response = await RawCommandAsync(owner, album, command, expectedVersion);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private static string[] Commands(JsonElement snapshot) =>
        snapshot.GetProperty("availableCommands").EnumerateArray().Select(x => x.GetString()!).ToArray();
}
