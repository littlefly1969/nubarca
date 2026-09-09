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
/// A whole party, through the real HTTP surface, with four independent clients:
/// one owner, one television, two guests.
///
/// Every other Party Game test proves one rule. This one proves the evening —
/// that the rules compose, that nothing needs a technician between the first
/// command and the last, and that the things which actually go wrong at a party
/// (a phone that fell behind, a tap that arrives late, a television somebody
/// unplugged, a host who pressed twice) leave the game in exactly one state.
///
/// The clients are separate <see cref="HttpClient"/>s precisely so their cookies
/// are separate. A guest here is as anonymous to the server as a guest at a real
/// party is.
/// </summary>
public sealed class PartyGameEndToEndTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyGameEndToEndTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task An_entire_party_runs_from_the_first_command_to_the_last()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"]);
        var token = await ViewTokenAsync(owner, album);
        var tv = _factory.CreateClient();
        var guestA = _factory.CreateClient();
        var guestB = _factory.CreateClient();

        // 1. The owner opens the control room. Nothing has started, and reading
        //    it has not started anything either.
        var lobby = await OwnerAsync(owner, album);
        Assert.Equal("lobby", lobby.GetProperty("status").GetString());
        Assert.Equal(0, lobby.GetProperty("version").GetInt32());

        // 2. Two guests arrive. 3. The television is switched on.
        await JoinAsync(guestA, token);
        await JoinAsync(guestB, token);
        await DisplayAsync(tv, token);

        var ready = await OwnerAsync(owner, album);
        Assert.Equal(2, ready.GetProperty("guestsPresent").GetInt32());
        Assert.Equal(JsonValueKind.Number, ready.GetProperty("displaySeenSecondsAgo").ValueKind);

        // 4-7. The host walks the first activity to a vote.
        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();

        var stage = await DisplayAsync(tv, token);
        Assert.Equal("voting_open", stage.GetProperty("phase").GetString());
        Assert.Equal("Canta", stage.GetProperty("challenge").GetProperty("title").GetString());
        var round = stage.GetProperty("roundId").GetGuid();

        // 8-9. One guest each way.
        await VoteAsync(guestA, token, round, "yes");
        var afterB = await VoteAsync(guestB, token, round, "no");
        Assert.Equal(2, afterB.GetProperty("voting").GetProperty("received").GetInt32());

        // The television sees participation and NOT the split, and neither does
        // a guest who has already voted.
        var during = await DisplayAsync(tv, token);
        Assert.Equal(2, during.GetProperty("voting").GetProperty("received").GetInt32());
        Assert.Equal(JsonValueKind.Null, during.GetProperty("voting").GetProperty("yes").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            (await SnapshotAsync(guestA, token)).GetProperty("voting").GetProperty("yes").ValueKind);

        // 10. A guest changes their mind. It replaces, never appends.
        var changed = await VoteAsync(guestA, token, round, "no");
        Assert.Equal("no", changed.GetProperty("myVote").GetString());
        Assert.Equal(2, changed.GetProperty("voting").GetProperty("received").GetInt32());

        // 11. The same tap again, twice. Still one answer, still no error.
        await VoteAsync(guestA, token, round, "no");
        await VoteAsync(guestA, token, round, "no");
        Assert.Equal(2, (await SnapshotAsync(guestA, token))
            .GetProperty("voting").GetProperty("received").GetInt32());

        // 12. The host closes voting, and only the host is told the result.
        var closed = await CommandAsync(owner, album, "close_voting", version);
        version = closed.GetProperty("version").GetInt32();
        Assert.Equal(0, closed.GetProperty("voting").GetProperty("yes").GetInt32());
        Assert.Equal(2, closed.GetProperty("voting").GetProperty("no").GetInt32());
        Assert.False(closed.GetProperty("voting").GetProperty("passed").GetBoolean());
        Assert.Equal(JsonValueKind.Null,
            (await DisplayAsync(tv, token)).GetProperty("voting").GetProperty("yes").ValueKind);

        // 13. The reveal reaches the room.
        version = (await CommandAsync(owner, album, "reveal_result", version)).GetProperty("version").GetInt32();
        var revealed = await DisplayAsync(tv, token);
        Assert.Equal("result", revealed.GetProperty("phase").GetString());
        Assert.Equal(2, revealed.GetProperty("voting").GetProperty("no").GetInt32());
        Assert.False(revealed.GetProperty("voting").GetProperty("passed").GetBoolean());

        // 14. On to the second activity.
        var second = await CommandAsync(owner, album, "next_challenge", version);
        version = second.GetProperty("version").GetInt32();
        Assert.Equal("Ballo", second.GetProperty("currentChallenge").GetProperty("title").GetString());
        Assert.Equal(2, second.GetProperty("roundNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextChallenge").ValueKind);

        // 15-17. Everything reconnects by reading again. No client holds state
        //        the server cannot reproduce, so there is nothing to replay.
        var reloadedTv = await DisplayAsync(_factory.CreateClient(), token);
        Assert.Equal("challenge_reveal", reloadedTv.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, reloadedTv.GetProperty("myVote").ValueKind);

        var reconnectedGuest = await SnapshotAsync(guestA, token);
        Assert.Equal("challenge_reveal", reconnectedGuest.GetProperty("phase").GetString());
        // A new round is a new question: last round's answer is not carried over.
        Assert.Equal(JsonValueKind.Null, reconnectedGuest.GetProperty("myVote").ValueKind);

        var reloadedOwner = await OwnerAsync(owner, album);
        Assert.Equal(version, reloadedOwner.GetProperty("version").GetInt32());
        Assert.Equal(1, reloadedOwner.GetProperty("playedRounds").GetInt32());

        // 18. The host ends the evening.
        var finished = await CommandAsync(owner, album, "finish", version);
        Assert.Equal("finished", finished.GetProperty("status").GetString());
        Assert.Equal(["restart_game"], finished.GetProperty("availableCommands")
            .EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("finished", (await DisplayAsync(tv, token)).GetProperty("status").GetString());
        Assert.Equal("finished", (await SnapshotAsync(guestA, token)).GetProperty("status").GetString());

        // Two rounds, one abandoned by the finish, two votes and no strays.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rounds = await db.PartyGameRounds.OrderBy(x => x.Sequence).ToListAsync();
        Assert.Equal(["completed", "abandoned"], rounds.Select(x => x.Status));
        Assert.Equal(2, await db.PartyGameVotes.CountAsync());
        Assert.All(await db.PartyGameVotes.ToListAsync(),
            v => Assert.Equal(rounds[0].Id, v.PartyGameRoundId));
    }

    [Fact]
    public async Task A_host_who_presses_twice_advances_the_party_once()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo", "Brindisi"]);

        // The same command, from the same version, four times over — a slow
        // network and an impatient thumb.
        //
        // Sent one after another rather than in parallel: this test host is
        // backed by a single shared SQLite connection, so simultaneity here
        // would be testing the harness. The genuine two-connection race is
        // PartyGameConcurrencyTests; what matters here is that four requests
        // quoting a version the server has already spent produce ONE advance.
        await CommandAsync(owner, album, "start", 0);
        var duplicates = new List<HttpResponseMessage>();
        for (var i = 0; i < 4; i++)
            duplicates.Add(await owner.PostAsJsonAsync($"/api/albums/{album}/party-game/commands",
                new { command = "start_challenge", expectedVersion = 1 }));

        Assert.Single(duplicates, r => r.IsSuccessStatusCode);
        Assert.All(duplicates.Where(r => !r.IsSuccessStatusCode),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));

        var snapshot = await OwnerAsync(owner, album);
        Assert.Equal("challenge_active", snapshot.GetProperty("phase").GetString());
        Assert.Equal(2, snapshot.GetProperty("version").GetInt32());
        Assert.Equal(1, snapshot.GetProperty("roundNumber").GetInt32());
    }

    [Fact]
    public async Task A_command_built_from_a_snapshot_that_arrived_late_is_refused_with_the_truth()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"]);

        // Two surfaces read the same state. One acts; the other is still
        // holding the picture from before.
        var shared = await OwnerAsync(owner, album);
        var staleVersion = shared.GetProperty("version").GetInt32();
        await CommandAsync(owner, album, "start", staleVersion);

        var late = await owner.PostAsJsonAsync($"/api/albums/{album}/party-game/commands",
            new { command = "start", expectedVersion = staleVersion });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        var body = await late.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("version_conflict", body.GetProperty("code").GetString());
        // The refusal is the recovery: the caller ends the request correct.
        Assert.Equal("challenge_reveal", body.GetProperty("snapshot").GetProperty("phase").GetString());
        Assert.Equal(1, body.GetProperty("snapshot").GetProperty("version").GetInt32());
        Assert.Equal(1, await RoundCountAsync());
    }

    [Fact]
    public async Task A_tap_that_leaves_a_phone_before_the_host_closes_and_lands_after_is_not_a_vote()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta"]);
        var token = await ViewTokenAsync(owner, album);
        var guest = _factory.CreateClient();

        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();

        // The guest reads the round while voting is open — the exact boundary
        // case: the phone is right about the round and wrong about the phase.
        var round = (await JoinAsync(guest, token)).GetProperty("roundId").GetGuid();
        await CommandAsync(owner, album, "close_voting", version);

        var late = await guest.PostAsJsonAsync($"/api/party/{token}/game/vote",
            new { roundId = round, value = "yes" });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        var body = await late.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("voting_closed", body.GetProperty("code").GetString());
        Assert.Equal("voting_closed", body.GetProperty("snapshot").GetProperty("phase").GetString());

        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyGameVotes.ToListAsync());
    }

    [Fact]
    public async Task A_phone_two_activities_behind_cannot_answer_the_wrong_question()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"]);
        var token = await ViewTokenAsync(owner, album);
        var guest = _factory.CreateClient();

        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();
        var oldRound = (await JoinAsync(guest, token)).GetProperty("roundId").GetGuid();

        // The party moves on while the phone is in a pocket.
        foreach (var command in new[] { "close_voting", "reveal_result", "next_challenge", "start_challenge", "open_voting" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();

        var wrong = await guest.PostAsJsonAsync($"/api/party/{token}/game/vote",
            new { roundId = oldRound, value = "yes" });
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal("stale_round",
            (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Reading again is the whole recovery, and then the vote lands.
        var now = await SnapshotAsync(guest, token);
        var right = await VoteAsync(guest, token, now.GetProperty("roundId").GetGuid(), "yes");
        Assert.Equal("yes", right.GetProperty("myVote").GetString());
        Assert.Equal(1, right.GetProperty("voting").GetProperty("received").GetInt32());
    }

    [Fact]
    public async Task A_television_that_was_unplugged_and_switched_back_on_needs_no_help()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"]);
        var token = await ViewTokenAsync(owner, album);

        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting", "close_voting", "reveal_result" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();

        // A completely fresh client — no cookies, no history, nothing carried
        // over — lands on the current scene.
        var newTv = await DisplayAsync(_factory.CreateClient(), token);
        Assert.Equal("result", newTv.GetProperty("phase").GetString());
        Assert.Equal("Canta", newTv.GetProperty("challenge").GetProperty("title").GetString());

        // And reading changed nothing: the version the owner holds still works.
        var next = await CommandAsync(owner, album, "next_challenge", version);
        Assert.Equal(2, next.GetProperty("roundNumber").GetInt32());
    }

    [Fact]
    public async Task Simultaneous_guests_never_produce_more_answers_than_there_are_guests()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta"]);
        var token = await ViewTokenAsync(owner, album);

        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();

        // Six phones, each with its own cookie. They arrive in a rush and two
        // of them tap twice. (Requests are sequential for the harness reason
        // above; the real simultaneous race is PartyGameConcurrencyTests.)
        var guests = new List<HttpClient>();
        var rounds = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var guest = _factory.CreateClient();
            guests.Add(guest);
            rounds.Add((await JoinAsync(guest, token)).GetProperty("roundId").GetGuid());
        }
        for (var i = 0; i < guests.Count; i++)
            await VoteAsync(guests[i], token, rounds[i], i % 2 == 0 ? "yes" : "no");
        for (var i = 0; i < 2; i++)
            await VoteAsync(guests[i], token, rounds[i], "yes");

        var closed = await CommandAsync(owner, album, "close_voting", version);
        var voting = closed.GetProperty("voting");
        Assert.Equal(6, voting.GetProperty("received").GetInt32());
        Assert.Equal(6, voting.GetProperty("yes").GetInt32() + voting.GetProperty("no").GetInt32());
        // The two who tapped twice changed their answer; they did not add one.
        Assert.Equal(4, voting.GetProperty("yes").GetInt32());
        Assert.True(voting.GetProperty("eligible").GetInt32() >= 6);

        using var scope = _factory.Services.CreateScope();
        Assert.Equal(6, await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyGameVotes.CountAsync());
    }

    [Fact]
    public async Task An_evening_of_mixed_activities_needs_no_technician()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta"], votingMode: "binary");
        // A second activity nobody votes on, and a third that is switched off.
        await AddAsync(owner, album, "Brindisi", votingMode: "none");
        await AddAsync(owner, album, "Esclusa", enabled: false);
        var token = await ViewTokenAsync(owner, album);
        var guest = _factory.CreateClient();

        var version = 0;
        var seen = new List<string>();
        var guard = 0;
        while (guard++ < 30)
        {
            var snapshot = await OwnerAsync(owner, album);
            version = snapshot.GetProperty("version").GetInt32();
            if (snapshot.GetProperty("status").GetString() == "finished") break;

            var commands = snapshot.GetProperty("availableCommands")
                .EnumerateArray().Select(x => x.GetString()!).ToArray();
            Assert.NotEmpty(commands);
            var primary = commands[0];
            seen.Add($"{snapshot.GetProperty("phase").GetString()}:{primary}");

            if (primary == "close_voting")
            {
                var round = (await JoinAsync(guest, token)).GetProperty("roundId").GetGuid();
                await VoteAsync(guest, token, round, "yes");
            }
            await CommandAsync(owner, album, primary, version);
        }

        // The host only ever pressed the one command the server offered, and the
        // evening reached its end: the voted activity through a vote, the
        // unvoted one straight to its result, and the excluded one never played.
        Assert.Contains("voting_open:close_voting", seen);
        Assert.Contains("challenge_active:reveal_result", seen);
        Assert.Equal("finished", (await OwnerAsync(owner, album)).GetProperty("status").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var played = await db.PartyGameRounds
            .Join(db.PartyChallenges, r => r.PartyChallengeId, c => c.Id, (r, c) => c.Title)
            .ToListAsync();
        Assert.Equal(2, played.Count);
        Assert.DoesNotContain("Esclusa", played);
        Assert.All(await db.PartyGameRounds.ToListAsync(),
            r => Assert.Equal(PartyGameRoundStatuses.Completed, r.Status));
    }

    // --- helpers -----------------------------------------------------------

    private async Task<Guid> SetUpAsync(HttpClient owner, string[] titles, string votingMode = "binary")
    {
        var album = (await (await owner.PostAsJsonAsync("/api/albums",
                new { name = $"Festa {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await EnableAndStartPartyAsync(owner, album);
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 30, maxChallengeIntervalSeconds = 60,
            votesPerGuest = 3, maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();
        foreach (var title in titles) await AddAsync(owner, album, title, votingMode);
        return album;
    }

    private static async Task AddAsync(
        HttpClient owner, Guid album, string title, string votingMode = "binary", bool enabled = true) =>
        (await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
        {
            title, body = "Descrizione", kind = "dare",
            mediaFileItemId = (Guid?)null, isEnabled = enabled, votingMode,
        })).EnsureSuccessStatusCode();

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

    private static async Task<JsonElement> SnapshotAsync(HttpClient client, string token)
    {
        var response = await client.GetAsync($"/api/party/{token}/game");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// A television, which says so and therefore never becomes a participant.
    private static async Task<JsonElement> DisplayAsync(HttpClient tv, string token)
    {
        var response = await tv.GetAsync($"/api/party/{token}/game?display=1");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> JoinAsync(HttpClient guest, string token)
    {
        var response = await guest.PostAsync($"/api/party/{token}/game/join", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> VoteAsync(
        HttpClient guest, string token, Guid roundId, string value)
    {
        var response = await guest.PostAsJsonAsync($"/api/party/{token}/game/vote",
            new { roundId, value });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> RoundCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyGameRounds.CountAsync();
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
