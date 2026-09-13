using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tv;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The host conducting the evening: what is still to come, in what order, and
/// the pause that gives the room back to the party between two activities.
///
/// <para>Two rules run through every test here. THE PLAN DESCRIBES THE FUTURE —
/// a played round is what the room experienced and a current one is on the
/// screen, and neither is something planning may rewrite. And AN INTERMISSION IS
/// NOT AN ENDING: the rounds, the plan and the preferences all survive it, and
/// the television goes back to the slideshow because the presentation projection
/// reads the phase, never because the game stopped.</para>
/// </summary>
public sealed class PartyGamePlanningTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyGamePlanningTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_control_room_sees_the_order_the_preferences_and_what_is_left()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo", "Brindisi"]);
        var token = await ViewTokenAsync(owner, album);

        var items = (await JoinAsync(_factory.CreateClient(), token))
            .GetProperty("preferences").GetProperty("items").EnumerateArray().ToArray();
        var ballo = items[1].GetProperty("id").GetGuid();
        // Two guests ask for the second activity.
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var guest = _factory.CreateClient();
            await JoinAsync(guest, token);
            await ChooseAsync(guest, token, ballo, true);
        }

        var version = (await CommandAsync(owner, album, "start", 0)).GetProperty("version").GetInt32();
        var plan = Plan(await OwnerAsync(owner, album));

        Assert.Equal(3, plan.Length);
        // What is on the screen, what is still to come, and where each sits.
        Assert.Equal("current", plan[0].GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, plan[0].GetProperty("position").ValueKind);
        Assert.Equal("remaining", plan[1].GetProperty("state").GetString());
        Assert.Equal(1, plan[1].GetProperty("position").GetInt32());
        Assert.Equal(2, plan[2].GetProperty("position").GetInt32());
        // The room's preferences, beside the activity they were cast for.
        Assert.Equal(2, plan[1].GetProperty("preferenceVotes").GetInt32());
        Assert.Equal(0, plan[2].GetProperty("preferenceVotes").GetInt32());
        Assert.False(plan[1].GetProperty("excluded").GetBoolean());

        // Playing it moves it into the past, and the past keeps its votes.
        foreach (var command in new[] { "start_challenge", "open_voting", "close_voting", "reveal_result", "next_challenge" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();
        var after = Plan(await OwnerAsync(owner, album));
        Assert.Equal("played", after[0].GetProperty("state").GetString());
        Assert.Equal("current", after[1].GetProperty("state").GetString());
        Assert.Equal(2, after[1].GetProperty("preferenceVotes").GetInt32());
    }

    [Fact]
    public async Task The_host_reorders_what_is_left_and_cannot_touch_the_past_or_the_present()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo", "Brindisi"]);
        var version = (await CommandAsync(owner, album, "start", 0)).GetProperty("version").GetInt32();

        var plan = Plan(await OwnerAsync(owner, album));
        var current = plan[0].GetProperty("id").GetGuid();
        var brindisi = plan[2].GetProperty("id").GetGuid();

        // "Play this next" is a move to the front of the remaining queue.
        var moved = await PlanAsync(owner, album, "move", brindisi, version, position: 0);
        version = moved.GetProperty("version").GetInt32();
        var reordered = Plan(moved);
        Assert.Equal("Brindisi", reordered[1].GetProperty("title").GetString());
        Assert.Equal(1, reordered[1].GetProperty("position").GetInt32());
        Assert.Equal("Canta", reordered[0].GetProperty("title").GetString());
        Assert.Equal("current", reordered[0].GetProperty("state").GetString());
        // The server's own answer to "what is next" agrees with the plan.
        Assert.Equal("Brindisi", moved.GetProperty("nextChallenge").GetProperty("title").GetString());

        // The activity on screen cannot be planned around. Refused out loud,
        // never silently reordered past.
        var refused = await owner.PostAsJsonAsync($"/api/albums/{album}/party-game/plan",
            new { action = "move", challengeId = current, expectedVersion = version, position = 1 });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("invalid_plan",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // And so does a round the room has already seen.
        foreach (var command in new[] { "start_challenge", "open_voting", "close_voting", "reveal_result", "next_challenge" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();
        var played = await owner.PostAsJsonAsync($"/api/albums/{album}/party-game/plan",
            new { action = "move", challengeId = current, expectedVersion = version, position = 0 });
        Assert.Equal(HttpStatusCode.Conflict, played.StatusCode);
    }

    [Fact]
    public async Task Excluding_an_activity_takes_it_out_of_the_match_and_keeps_its_preferences()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"]);
        var token = await ViewTokenAsync(owner, album);

        var guest = _factory.CreateClient();
        var items = (await JoinAsync(guest, token)).GetProperty("preferences")
            .GetProperty("items").EnumerateArray().ToArray();
        var ballo = items[1].GetProperty("id").GetGuid();
        await ChooseAsync(guest, token, ballo, true);

        var version = (await CommandAsync(owner, album, "start", 0)).GetProperty("version").GetInt32();
        var excluded = await PlanAsync(owner, album, "exclude", ballo, version);
        version = excluded.GetProperty("version").GetInt32();

        var entry = Plan(excluded).Single(x => x.GetProperty("id").GetGuid() == ballo);
        Assert.True(entry.GetProperty("excluded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("position").ValueKind);
        // THE PREFERENCES SURVIVE. "The room wanted this and there was no time"
        // is information; deleting it would destroy the evidence the host's own
        // decision was about.
        Assert.Equal(1, entry.GetProperty("preferenceVotes").GetInt32());
        Assert.Equal(JsonValueKind.Null, excluded.GetProperty("nextChallenge").ValueKind);

        using (var scope = _factory.Services.CreateScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .PartyChallengeVotes.CountAsync());
        }

        // An excluded activity is never played: the deck is spent after the one
        // that is running.
        foreach (var command in new[] { "start_challenge", "open_voting", "close_voting", "reveal_result", "next_challenge" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();
        Assert.Equal("finished", (await OwnerAsync(owner, album)).GetProperty("status").GetString());

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.PartyGameRounds.CountAsync());
        Assert.Equal(1, await db.PartyChallengeVotes.CountAsync());
    }

    [Fact]
    public async Task Putting_an_excluded_activity_back_returns_it_to_the_queue()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"]);
        var version = (await CommandAsync(owner, album, "start", 0)).GetProperty("version").GetInt32();
        var ballo = Plan(await OwnerAsync(owner, album))[1].GetProperty("id").GetGuid();

        var excluded = await PlanAsync(owner, album, "exclude", ballo, version);
        var included = await PlanAsync(owner, album, "include", ballo,
            excluded.GetProperty("version").GetInt32());
        var entry = Plan(included).Single(x => x.GetProperty("id").GetGuid() == ballo);
        Assert.False(entry.GetProperty("excluded").GetBoolean());
        Assert.Equal(1, entry.GetProperty("position").GetInt32());
        Assert.Equal("Ballo", included.GetProperty("nextChallenge").GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_plan_edit_quoting_a_spent_version_is_refused_with_the_truth()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo", "Brindisi"]);
        var version = (await CommandAsync(owner, album, "start", 0)).GetProperty("version").GetInt32();
        var plan = Plan(await OwnerAsync(owner, album));
        var ballo = plan[1].GetProperty("id").GetGuid();
        var brindisi = plan[2].GetProperty("id").GetGuid();

        // Two control-room surfaces read the same version. One acts.
        var moved = await PlanAsync(owner, album, "move", brindisi, version, position: 0);
        Assert.Equal(version + 1, moved.GetProperty("version").GetInt32());

        // The other is still holding the picture from before.
        var stale = await owner.PostAsJsonAsync($"/api/albums/{album}/party-game/plan",
            new { action = "exclude", challengeId = ballo, expectedVersion = version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("version_conflict", body.GetProperty("code").GetString());
        // The refusal IS the recovery: the loser ends the request correct.
        Assert.Equal(version + 1, body.GetProperty("snapshot").GetProperty("version").GetInt32());
        Assert.Equal("Brindisi",
            body.GetProperty("snapshot").GetProperty("nextChallenge").GetProperty("title").GetString());

        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyGameExclusions.ToListAsync());
    }

    [Fact]
    public async Task Result_to_intermission_hands_the_television_back_to_the_slideshow()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"]);
        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting", "close_voting", "reveal_result" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();

        // The television is on the game while the result is up.
        Assert.Equal(TvPartyPresentations.Game, await PresentationAsync(album));

        var paused = await CommandAsync(owner, album, "return_to_party", version);
        version = paused.GetProperty("version").GetInt32();
        Assert.Equal("intermission", paused.GetProperty("phase").GetString());
        // The game is ALIVE — this is a pause, not an ending.
        Assert.Equal("live", paused.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, paused.GetProperty("finishedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, paused.GetProperty("currentChallenge").ValueKind);

        // And the screen is the party's again, immediately — no dwell, because a
        // host who just said "back to the party" is standing in front of a room.
        Assert.Equal(TvPartyPresentations.Slideshow, await PresentationAsync(album));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The round the room saw the result of was RESOLVED on the way in.
            var round = await db.PartyGameRounds.SingleAsync();
            Assert.Equal(PartyGameRoundStatuses.Completed, round.Status);
        }

        // Resuming is the same word it always was, and the takeover returns.
        var resumed = await CommandAsync(owner, album, "next_challenge", version);
        Assert.Equal("challenge_reveal", resumed.GetProperty("phase").GetString());
        Assert.Equal("Ballo", resumed.GetProperty("currentChallenge").GetProperty("title").GetString());
        Assert.Equal(2, resumed.GetProperty("roundNumber").GetInt32());
        Assert.Equal(TvPartyPresentations.Game, await PresentationAsync(album));
    }

    [Fact]
    public async Task A_guest_in_an_intermission_is_told_the_party_continues_and_is_shown_no_activity()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"]);
        var token = await ViewTokenAsync(owner, album);
        var guest = _factory.CreateClient();
        await JoinAsync(guest, token);

        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting", "close_voting", "reveal_result", "return_to_party" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();

        var snapshot = await SnapshotAsync(guest, token);
        Assert.Equal("intermission", snapshot.GetProperty("phase").GetString());
        Assert.Equal("live", snapshot.GetProperty("status").GetString());
        // Nothing is on screen, so nothing is described and nothing is votable.
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("challenge").ValueKind);
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("roundId").ValueKind);
    }

    [Fact]
    public async Task The_host_can_still_plan_during_an_intermission()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo", "Brindisi"]);
        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting", "close_voting", "reveal_result", "return_to_party" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();

        var brindisi = Plan(await OwnerAsync(owner, album))
            .Single(x => x.GetProperty("title").GetString() == "Brindisi").GetProperty("id").GetGuid();
        var moved = await PlanAsync(owner, album, "move", brindisi, version, position: 0);
        Assert.Equal("Brindisi", moved.GetProperty("nextChallenge").GetProperty("title").GetString());

        var resumed = await CommandAsync(
            owner, album, "next_challenge", moved.GetProperty("version").GetInt32());
        Assert.Equal("Brindisi", resumed.GetProperty("currentChallenge").GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_restart_discards_the_exclusions_with_the_match()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"]);
        var version = (await CommandAsync(owner, album, "start", 0)).GetProperty("version").GetInt32();
        var ballo = Plan(await OwnerAsync(owner, album))[1].GetProperty("id").GetGuid();
        version = (await PlanAsync(owner, album, "exclude", ballo, version))
            .GetProperty("version").GetInt32();

        foreach (var command in new[] { "start_challenge", "open_voting", "close_voting", "reveal_result", "next_challenge" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();
        var restarted = await CommandAsync(owner, album, "restart_game", version);

        // "Not tonight" described a match that no longer exists, so the replay
        // starts from a clean deck.
        Assert.All(Plan(restarted), x => Assert.False(x.GetProperty("excluded").GetBoolean()));
        Assert.Equal(2, Plan(restarted).Count(x => x.GetProperty("state").GetString() == "remaining"));

        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyGameExclusions.ToListAsync());
    }

    [Fact]
    public async Task A_plan_edit_that_changes_nothing_writes_nothing()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"]);

        // Before the first `start` there is no session row, and a plan action
        // that decides nothing must not create one: a plan edit is a command and
        // may write, but an edit that changes nothing is a read wearing a POST.
        var lobby = await OwnerAsync(owner, album);
        Assert.Equal(0, lobby.GetProperty("version").GetInt32());
        var first = Plan(lobby)[0].GetProperty("id").GetGuid();

        // Already included, and already first.
        var included = await PlanAsync(owner, album, "include", first, 0);
        Assert.Equal(0, included.GetProperty("version").GetInt32());
        var moved = await PlanAsync(owner, album, "move", first, 0, position: 0);
        Assert.Equal(0, moved.GetProperty("version").GetInt32());

        using (var scope = _factory.Services.CreateScope())
        {
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .PartyGameSessions.ToListAsync());
        }

        // A real decision still spends exactly one version — and `start` then
        // quotes what the plan edit left behind, not the 0 it started from.
        var excluded = await PlanAsync(owner, album, "exclude", first, 0);
        Assert.Equal(1, excluded.GetProperty("version").GetInt32());
        var started = await CommandAsync(owner, album, "start", 1);
        Assert.Equal("Ballo", started.GetProperty("currentChallenge").GetProperty("title").GetString());
    }

    // --- helpers -----------------------------------------------------------

    private static JsonElement[] Plan(JsonElement snapshot) =>
        snapshot.GetProperty("plan").EnumerateArray().ToArray();

    /// The presentation a paired television assigned to this party would be
    /// told to mount — the control plane's own projection, not a second copy.
    private async Task<string> PresentationAsync(Guid album)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var linkId = await db.PartyAlbumLinks.AsNoTracking()
            .Where(x => x.AlbumId == album && x.Enabled && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt).Select(x => x.Id).FirstAsync();
        var projection = scope.ServiceProvider.GetRequiredService<ITvPartyPresentationService>();
        return (await projection.ProjectAsync(linkId)).Presentation;
    }

    private async Task<Guid> SetUpAsync(HttpClient owner, string[] titles)
    {
        var album = (await (await owner.PostAsJsonAsync("/api/albums",
                new { name = $"Festa {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var settings = await (await owner.PatchAsJsonAsync(
            $"/api/albums/{album}/party-settings", new { enabled = true }))
            .Content.ReadFromJsonAsync<JsonElement>();
        await PartyTestHost.StartAsync(owner, settings);
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 30, maxChallengeIntervalSeconds = 60,
            votesPerGuest = 3, maxChallengesPerSession = (int?)null, priorityVotingEnabled = true,
        })).EnsureSuccessStatusCode();
        foreach (var title in titles)
        {
            (await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
            {
                title, body = "Descrizione", mediaFileItemId = (Guid?)null, isEnabled = true,
            })).EnsureSuccessStatusCode();
        }
        return album;
    }

    private static async Task<string> ViewTokenAsync(HttpClient owner, Guid album) =>
        (await (await owner.GetAsync($"/api/albums/{album}/party-settings"))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("partyUrl").GetString()!["/party/".Length..];

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

    private static async Task<JsonElement> PlanAsync(
        HttpClient owner, Guid album, string action, Guid challengeId, int expectedVersion,
        int? position = null)
    {
        var response = await owner.PostAsJsonAsync($"/api/albums/{album}/party-game/plan",
            new { action, challengeId, expectedVersion, position });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> SnapshotAsync(HttpClient client, string token)
    {
        var response = await client.GetAsync($"/api/party/{token}/game");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> JoinAsync(HttpClient guest, string token)
    {
        var response = await guest.PostAsync($"/api/party/{token}/game/join", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ChooseAsync(
        HttpClient guest, string token, Guid challengeId, bool selected)
    {
        var response = await guest.PostAsJsonAsync($"/api/party/{token}/game/preferences",
            new { challengeId, selected });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
