using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// Live voting: one current answer per guest per round, held by the database,
/// and a result nobody sees before the host reveals it.
/// </summary>
public sealed class PartyGameVotingTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyGameVotingTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_guest_votes_once_and_may_change_their_mind_while_voting_is_open()
    {
        var party = await OpenVotingAsync();
        var guest = _factory.CreateClient();
        var round = await RoundIdAsync(guest, party.Token);

        var first = await VoteAsync(guest, party.Token, round, "yes");
        Assert.Equal("yes", first.GetProperty("myVote").GetString());
        Assert.Equal(1, first.GetProperty("voting").GetProperty("received").GetInt32());

        var changed = await VoteAsync(guest, party.Token, round, "no");
        Assert.Equal("no", changed.GetProperty("myVote").GetString());
        // Changing a mind is a replacement, not a second ballot.
        Assert.Equal(1, changed.GetProperty("voting").GetProperty("received").GetInt32());

        using var scope = _factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyGameVotes.CountAsync());
    }

    [Fact]
    public async Task The_same_tap_twice_is_not_an_error_and_not_a_second_vote()
    {
        var party = await OpenVotingAsync();
        var guest = _factory.CreateClient();
        var round = await RoundIdAsync(guest, party.Token);

        await VoteAsync(guest, party.Token, round, "yes");
        var again = await VoteAsync(guest, party.Token, round, "yes");
        Assert.Equal("yes", again.GetProperty("myVote").GetString());
        Assert.Equal(1, again.GetProperty("voting").GetProperty("received").GetInt32());
    }

    [Fact]
    public async Task Two_guests_are_two_votes_and_neither_can_see_the_other_s()
    {
        var party = await OpenVotingAsync();
        var a = _factory.CreateClient();
        var b = _factory.CreateClient();
        var round = await RoundIdAsync(a, party.Token);

        await VoteAsync(a, party.Token, round, "yes");
        var second = await VoteAsync(b, party.Token, round, "no");

        Assert.Equal(2, second.GetProperty("voting").GetProperty("received").GetInt32());
        Assert.Equal("no", second.GetProperty("myVote").GetString());
        // While voting is open, nobody is told the split — not even a voter.
        var voting = second.GetProperty("voting");
        Assert.Equal(JsonValueKind.Null, voting.GetProperty("yes").ValueKind);
        Assert.Equal(JsonValueKind.Null, voting.GetProperty("no").ValueKind);
        Assert.Equal(JsonValueKind.Null, voting.GetProperty("passed").ValueKind);
    }

    [Fact]
    public async Task A_vote_arriving_after_the_host_closed_is_refused_and_told_why()
    {
        var party = await OpenVotingAsync();
        var guest = _factory.CreateClient();
        var round = await RoundIdAsync(guest, party.Token);

        // The exact boundary: the guest tapped while voting was open and the
        // request landed after it closed.
        await CommandAsync(party.Owner, party.Album, "close_voting", 3);

        var refused = await guest.PostAsJsonAsync($"/api/party/{party.Token}/game/vote",
            new { roundId = round, value = "yes" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("voting_closed", body.GetProperty("code").GetString());
        // And the refusal hands back the state it was measured against.
        Assert.Equal("voting_closed", body.GetProperty("snapshot").GetProperty("phase").GetString());

        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyGameVotes.ToListAsync());
    }

    [Fact]
    public async Task A_phone_that_fell_behind_cannot_answer_last_round_s_question()
    {
        var party = await OpenVotingAsync(challenges: 2);
        var guest = _factory.CreateClient();
        var staleRound = await RoundIdAsync(guest, party.Token);

        // The host finishes this round and starts the next one.
        var version = 3;
        foreach (var command in new[] { "close_voting", "reveal_result", "next_challenge", "start_challenge", "open_voting" })
            version = (await CommandAsync(party.Owner, party.Album, command, version))
                .GetProperty("version").GetInt32();

        var refused = await guest.PostAsJsonAsync($"/api/party/{party.Token}/game/vote",
            new { roundId = staleRound, value = "yes" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("stale_round",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task There_is_nothing_to_vote_on_before_the_host_opens_it()
    {
        var party = await SetUpAsync();
        var guest = _factory.CreateClient();
        await CommandAsync(party.Owner, party.Album, "start", 0);
        var round = await RoundIdAsync(guest, party.Token);

        var refused = await guest.PostAsJsonAsync($"/api/party/{party.Token}/game/vote",
            new { roundId = round, value = "yes" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("voting_closed",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_activity_nobody_votes_on_accepts_no_votes()
    {
        var party = await SetUpAsync(votingMode: "none");
        var guest = _factory.CreateClient();
        await CommandAsync(party.Owner, party.Album, "start", 0);
        await CommandAsync(party.Owner, party.Album, "start_challenge", 1);
        var round = await RoundIdAsync(guest, party.Token);

        var refused = await guest.PostAsJsonAsync($"/api/party/{party.Token}/game/vote",
            new { roundId = round, value = "yes" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task Turning_the_game_off_closes_voting_on_the_next_tap()
    {
        var party = await OpenVotingAsync();
        var guest = _factory.CreateClient();
        var round = await RoundIdAsync(guest, party.Token);

        (await party.Owner.PatchAsJsonAsync($"/api/albums/{party.Album}/party-game-settings", new
        {
            gameEnabled = false, minChallengeIntervalSeconds = 30,
            maxChallengeIntervalSeconds = 60, votesPerGuest = 3,
            maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();

        // Not a conflict: there is no game here any more, which is the same
        // answer a stranger's token gets.
        var response = await guest.PostAsJsonAsync($"/api/party/{party.Token}/game/vote",
            new { roundId = round, value = "yes" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Only_yes_or_no_is_an_answer()
    {
        var party = await OpenVotingAsync();
        var guest = _factory.CreateClient();
        var round = await RoundIdAsync(guest, party.Token);
        foreach (var value in new[] { "maybe", "", "YES", "1" })
        {
            var response = await guest.PostAsJsonAsync($"/api/party/{party.Token}/game/vote",
                new { roundId = round, value });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_result_reaches_the_owner_when_voting_closes_and_the_room_when_it_is_revealed()
    {
        var party = await OpenVotingAsync();
        var a = _factory.CreateClient();
        var b = _factory.CreateClient();
        var c = _factory.CreateClient();
        var round = await RoundIdAsync(a, party.Token);
        await VoteAsync(a, party.Token, round, "yes");
        await VoteAsync(b, party.Token, round, "yes");
        await VoteAsync(c, party.Token, round, "no");

        // While open, the owner sees participation and not the split.
        var live = (await OwnerSnapshotAsync(party.Owner, party.Album)).GetProperty("voting");
        Assert.Equal(3, live.GetProperty("received").GetInt32());
        Assert.Equal(JsonValueKind.Null, live.GetProperty("yes").ValueKind);

        var closed = (await CommandAsync(party.Owner, party.Album, "close_voting", 3)).GetProperty("voting");
        Assert.Equal(2, closed.GetProperty("yes").GetInt32());
        Assert.Equal(1, closed.GetProperty("no").GetInt32());
        Assert.True(closed.GetProperty("passed").GetBoolean());

        // The room still does not know: the host decides when to reveal.
        var beforeReveal = (await PublicSnapshotAsync(a, party.Token)).GetProperty("voting");
        Assert.Equal(JsonValueKind.Null, beforeReveal.GetProperty("yes").ValueKind);
        Assert.Equal(3, beforeReveal.GetProperty("received").GetInt32());

        await CommandAsync(party.Owner, party.Album, "reveal_result", 4);
        var revealed = (await PublicSnapshotAsync(a, party.Token)).GetProperty("voting");
        Assert.Equal(2, revealed.GetProperty("yes").GetInt32());
        Assert.Equal(1, revealed.GetProperty("no").GetInt32());
        Assert.True(revealed.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task A_tie_does_not_pass()
    {
        var party = await OpenVotingAsync();
        var a = _factory.CreateClient();
        var b = _factory.CreateClient();
        var round = await RoundIdAsync(a, party.Token);
        await VoteAsync(a, party.Token, round, "yes");
        await VoteAsync(b, party.Token, round, "no");

        var closed = (await CommandAsync(party.Owner, party.Album, "close_voting", 3)).GetProperty("voting");
        Assert.Equal(1, closed.GetProperty("yes").GetInt32());
        Assert.Equal(1, closed.GetProperty("no").GetInt32());
        Assert.False(closed.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task A_television_reads_the_game_without_becoming_a_voter()
    {
        var party = await OpenVotingAsync();
        var tv = _factory.CreateClient();

        // Three polls from a display that holds no guest cookie.
        for (var i = 0; i < 3; i++) await PublicSnapshotAsync(tv, party.Token);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.PartyParticipants.ToListAsync());

        // A guest that JOINS is counted, because joining is a deliberate POST.
        var guest = _factory.CreateClient();
        var joined = await (await guest.PostAsync($"/api/party/{party.Token}/game/join", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, joined.GetProperty("voting").GetProperty("eligible").GetInt32());
        Assert.Equal(JsonValueKind.Null, joined.GetProperty("myVote").ValueKind);
        Assert.Single(await db.PartyParticipants.ToListAsync());
    }

    [Fact]
    public async Task The_room_size_is_never_smaller_than_the_votes_already_in_it()
    {
        var party = await OpenVotingAsync();
        var guest = _factory.CreateClient();
        var round = await RoundIdAsync(guest, party.Token);
        var voted = await VoteAsync(guest, party.Token, round, "yes");
        var voting = voted.GetProperty("voting");
        Assert.True(voting.GetProperty("eligible").GetInt32() >= voting.GetProperty("received").GetInt32());
    }

    [Fact]
    public async Task A_guest_at_another_party_cannot_vote_here()
    {
        var mine = await OpenVotingAsync();
        var theirs = await OpenVotingAsync(name: "Altra festa");
        var guest = _factory.CreateClient();

        // The cookie is path-scoped to the token that minted it, so a session
        // from one party never even reaches the other's endpoints.
        await guest.PostAsync($"/api/party/{theirs.Token}/game/join", null);
        var round = await RoundIdAsync(guest, mine.Token);
        var response = await VoteAsync(guest, mine.Token, round, "yes");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Two rows, one per party: no allowance and no identity crosses over.
        Assert.Equal(2, await db.PartyParticipants.CountAsync());
        Assert.Equal("yes", response.GetProperty("myVote").GetString());
    }

    [Fact]
    public async Task Voting_is_scoped_to_its_own_round()
    {
        var party = await OpenVotingAsync(challenges: 2);
        var guest = _factory.CreateClient();
        var first = await RoundIdAsync(guest, party.Token);
        await VoteAsync(guest, party.Token, first, "yes");

        var version = 3;
        foreach (var command in new[] { "close_voting", "reveal_result", "next_challenge", "start_challenge", "open_voting" })
            version = (await CommandAsync(party.Owner, party.Album, command, version))
                .GetProperty("version").GetInt32();

        // A new round starts empty, and the guest's previous answer is not
        // carried forward.
        var snapshot = await PublicSnapshotAsync(guest, party.Token);
        Assert.Equal(0, snapshot.GetProperty("voting").GetProperty("received").GetInt32());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("myVote").ValueKind);
        Assert.NotEqual(first, snapshot.GetProperty("roundId").GetGuid());
    }

    [Fact]
    public async Task Nothing_owner_shaped_reaches_a_guest()
    {
        var party = await OpenVotingAsync();
        var guest = _factory.CreateClient();
        var snapshot = await PublicSnapshotAsync(guest, party.Token);
        foreach (var forbidden in new[] { "sessionId", "availableCommands", "nextChallenge", "playedRounds" })
            Assert.False(snapshot.TryGetProperty(forbidden, out _), forbidden);
    }

    // --- helpers -----------------------------------------------------------

    private sealed record Party(HttpClient Owner, Guid Album, string Token);

    private async Task<Party> SetUpAsync(
        string votingMode = "binary", int challenges = 1, string? name = null)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync($"{Guid.NewGuid():N}@example.com");
        var album = (await (await owner.PostAsJsonAsync("/api/albums",
                new { name = name ?? $"Festa {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings", new { enabled = true }))
            .EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 30, maxChallengeIntervalSeconds = 60,
            votesPerGuest = 3, maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();
        for (var i = 0; i < challenges; i++)
            (await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
            {
                title = $"Sfida {i}", body = "Descrizione", kind = "dare",
                mediaFileItemId = (Guid?)null, isEnabled = true, votingMode,
            })).EnsureSuccessStatusCode();
        var token = (await (await owner.GetAsync($"/api/albums/{album}/party-settings"))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("partyUrl").GetString()!["/party/".Length..];
        return new Party(owner, album, token);
    }

    private async Task<Party> OpenVotingAsync(int challenges = 1, string? name = null)
    {
        var party = await SetUpAsync(challenges: challenges, name: name);
        await CommandAsync(party.Owner, party.Album, "start", 0);
        await CommandAsync(party.Owner, party.Album, "start_challenge", 1);
        await CommandAsync(party.Owner, party.Album, "open_voting", 2);
        return party;
    }

    private static async Task<Guid> RoundIdAsync(HttpClient guest, string token) =>
        (await PublicSnapshotAsync(guest, token)).GetProperty("roundId").GetGuid();

    private static async Task<JsonElement> PublicSnapshotAsync(HttpClient client, string token)
    {
        var response = await client.GetAsync($"/api/party/{token}/game");
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

    private static async Task<JsonElement> OwnerSnapshotAsync(HttpClient owner, Guid album) =>
        await (await owner.GetAsync($"/api/albums/{album}/party-game"))
            .Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> CommandAsync(
        HttpClient owner, Guid album, string command, int expectedVersion)
    {
        var response = await owner.PostAsJsonAsync($"/api/albums/{album}/party-game/commands",
            new { command, expectedVersion });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
