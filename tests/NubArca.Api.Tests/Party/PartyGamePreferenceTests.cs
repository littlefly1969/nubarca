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
/// THE TWO VOTES ARE NOT THE SAME VOTE.
///
/// <para>A PREFERENCE is cast before the match, on an activity, and says "I
/// would like to see this". A LIVE VOTE is cast during an activity, on a round,
/// and says "they did it". They share the anonymous participant the party
/// already had and nothing else: no row, no budget, no phase, no consequence.
/// Every test here is one half of that sentence.</para>
///
/// <para>The dangerous failure this suite exists to catch is a preference that
/// starts meaning something: choosing the next activity, interrupting the
/// slideshow, or landing in a yes/no result. Each of those is asserted
/// negatively, against persisted rows rather than against a status code.</para>
/// </summary>
public sealed class PartyGamePreferenceTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyGamePreferenceTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Priority_voting_is_off_by_default_and_the_surface_is_absent_until_it_is_on()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo"], priorityVoting: false);
        var token = await ViewTokenAsync(owner, album);

        // Absence IS the answer: no disabled list, no empty section, no flag to
        // respect — the block is simply not in the snapshot.
        var off = await JoinAsync(_factory.CreateClient(), token);
        Assert.Equal(JsonValueKind.Null, off.GetProperty("preferences").ValueKind);
        Assert.False((await OwnerAsync(owner, album)).GetProperty("priorityVotingEnabled").GetBoolean());

        await SetPriorityVotingAsync(owner, album, true, votesPerGuest: 2);

        var on = await JoinAsync(_factory.CreateClient(), token);
        var preferences = on.GetProperty("preferences");
        Assert.True(preferences.GetProperty("open").GetBoolean());
        Assert.Equal(2, preferences.GetProperty("votesPerGuest").GetInt32());
        Assert.Equal(2, preferences.GetProperty("items").GetArrayLength());
        Assert.True((await OwnerAsync(owner, album)).GetProperty("priorityVotingEnabled").GetBoolean());
    }

    [Fact]
    public async Task A_guest_gets_N_distinct_activities_and_at_most_one_preference_each()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta", "Ballo", "Brindisi"], votesPerGuest: 2);
        var token = await ViewTokenAsync(owner, album);
        var guest = _factory.CreateClient();
        var ids = (await JoinAsync(guest, token)).GetProperty("preferences")
            .GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();

        var first = await ChooseAsync(guest, token, ids[0], true);
        Assert.Equal(1, first.GetProperty("votesUsed").GetInt32());
        Assert.Equal(1, first.GetProperty("votesRemaining").GetInt32());

        // The same activity twice is the same preference, not a second one.
        var again = await ChooseAsync(guest, token, ids[0], true);
        Assert.Equal(1, again.GetProperty("votesUsed").GetInt32());

        var second = await ChooseAsync(guest, token, ids[1], true);
        Assert.Equal(0, second.GetProperty("votesRemaining").GetInt32());

        // The budget is the server's. A third is refused, and the refusal hands
        // back the surface it was measured against.
        var refused = await guest.PostAsJsonAsync($"/api/party/{token}/game/preferences",
            new { challengeId = ids[2], selected = true });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("limit_reached", body.GetProperty("code").GetString());
        Assert.Equal(2, body.GetProperty("preferences").GetProperty("votesUsed").GetInt32());

        // Giving one back frees exactly one slot.
        var removed = await ChooseAsync(guest, token, ids[0], false);
        Assert.Equal(1, removed.GetProperty("votesRemaining").GetInt32());
        await ChooseAsync(guest, token, ids[2], true);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.PartyChallengeVotes.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(x => x.PartyChallengeId).Distinct().Count());
    }

    [Fact]
    public async Task One_browser_is_one_participant_across_the_party_the_preferences_and_the_game()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta"]);
        var token = await ViewTokenAsync(owner, album);

        // ONE HttpClient is ONE browser: the cookie jar is the identity, and
        // nothing below ever sends an id of its own.
        var browser = _factory.CreateClient();

        // The party itself, then the preferences, then the live vote.
        (await browser.GetAsync($"/api/party/{token}")).EnsureSuccessStatusCode();
        var joined = await JoinAsync(browser, token);
        var challenge = joined.GetProperty("preferences").GetProperty("items")[0]
            .GetProperty("id").GetGuid();
        await ChooseAsync(browser, token, challenge, true);

        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
            version = (await CommandAsync(owner, album, command, version))
                .GetProperty("version").GetInt32();

        var round = (await SnapshotAsync(browser, token)).GetProperty("roundId").GetGuid();
        (await browser.PostAsJsonAsync($"/api/party/{token}/game/vote",
            new { roundId = round, value = "yes" })).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // ONE participant row — not one per capability, and not one per surface.
        var participant = await db.PartyParticipants.Where(x => x.RetiredAt == null).SingleAsync();
        Assert.Equal(participant.Id,
            (await db.PartyChallengeVotes.SingleAsync()).PartyParticipantId);
        Assert.Equal(participant.Id, (await db.PartyGameVotes.SingleAsync()).PartyParticipantId);
    }

    [Fact]
    public async Task Start_freezes_the_preferences_and_restart_reopens_them_without_deleting_one()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta"]);
        var token = await ViewTokenAsync(owner, album);
        var guest = _factory.CreateClient();
        var challenge = (await JoinAsync(guest, token)).GetProperty("preferences")
            .GetProperty("items")[0].GetProperty("id").GetGuid();
        await ChooseAsync(guest, token, challenge, true);

        // The first `start` is the boundary. Nothing else closes them.
        var version = (await CommandAsync(owner, album, "start", 0)).GetProperty("version").GetInt32();

        var closed = (await SnapshotAsync(guest, token)).GetProperty("preferences");
        Assert.False(closed.GetProperty("open").GetBoolean());
        // The preference itself is still there and still THEIRS — read-only is
        // not the same as erased.
        Assert.True(closed.GetProperty("items")[0].GetProperty("selected").GetBoolean());

        var refused = await guest.PostAsJsonAsync($"/api/party/{token}/game/preferences",
            new { challengeId = challenge, selected = false });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("preferences_closed",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Play it again. The match is discarded; what the room asked for is not.
        foreach (var command in new[] { "start_challenge", "open_voting", "close_voting", "reveal_result", "next_challenge" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();
        Assert.Equal("finished", (await OwnerAsync(owner, album)).GetProperty("status").GetString());
        await CommandAsync(owner, album, "restart_game", version);

        var reopened = (await SnapshotAsync(guest, token)).GetProperty("preferences");
        Assert.True(reopened.GetProperty("open").GetBoolean());
        Assert.True(reopened.GetProperty("items")[0].GetProperty("selected").GetBoolean());
        Assert.Equal(1, reopened.GetProperty("votesUsed").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.PartyChallengeVotes.CountAsync());
        // The MATCH went, as restart always means.
        Assert.Empty(await db.PartyGameRounds.ToListAsync());
    }

    [Fact]
    public async Task A_preference_chooses_nothing_and_enters_no_result()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        // Deck order is the host's: Canta first, Ballo second.
        var album = await SetUpAsync(owner, ["Canta", "Ballo"], votesPerGuest: 3);
        var token = await ViewTokenAsync(owner, album);

        var items = (await JoinAsync(_factory.CreateClient(), token))
            .GetProperty("preferences").GetProperty("items").EnumerateArray().ToArray();
        var second = items[1].GetProperty("id").GetGuid();

        // Three guests all ask for the SECOND activity. If preferences chose
        // anything, this is what would make them choose it.
        for (var i = 0; i < 3; i++)
        {
            var guest = _factory.CreateClient();
            await JoinAsync(guest, token);
            await ChooseAsync(guest, token, second, true);
        }

        // The host's own order survives untouched: Canta is still next.
        var lobby = await OwnerAsync(owner, album);
        Assert.Equal("Canta", lobby.GetProperty("nextChallenge").GetProperty("title").GetString());
        var version = (await CommandAsync(owner, album, "start", 0)).GetProperty("version").GetInt32();
        var live = await OwnerAsync(owner, album);
        Assert.Equal("Canta", live.GetProperty("currentChallenge").GetProperty("title").GetString());

        // And it never reaches the live result: a round nobody voted on has no
        // yes/no whatever the room asked for before the match.
        foreach (var command in new[] { "start_challenge", "open_voting", "close_voting" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();
        var closed = await OwnerAsync(owner, album);
        Assert.Equal(0, closed.GetProperty("voting").GetProperty("received").GetInt32());
        Assert.Equal(0, closed.GetProperty("voting").GetProperty("yes").GetInt32());

        using var scope = _factory.Services.CreateScope();
        // Three preferences, zero live votes. The two tables never touch.
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(3, await db.PartyChallengeVotes.CountAsync());
        Assert.Empty(await db.PartyGameVotes.ToListAsync());
    }

    [Fact]
    public async Task A_live_vote_is_one_per_participant_per_round_and_only_while_voting_is_open()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta"]);
        var token = await ViewTokenAsync(owner, album);
        var guest = _factory.CreateClient();
        await JoinAsync(guest, token);

        var version = (await CommandAsync(owner, album, "start", 0)).GetProperty("version").GetInt32();

        // Before voting opens there is nothing to answer, even with the right
        // round in hand.
        var round = (await SnapshotAsync(guest, token)).GetProperty("roundId").GetGuid();
        var early = await guest.PostAsJsonAsync($"/api/party/{token}/game/vote",
            new { roundId = round, value = "yes" });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("voting_closed",
            (await early.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        foreach (var command in new[] { "start_challenge", "open_voting" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();

        // Changing your mind REPLACES. The unique (round, participant) index is
        // the authority, so three taps are still one row.
        await VoteAsync(guest, token, round, "yes");
        await VoteAsync(guest, token, round, "no");
        var final = await VoteAsync(guest, token, round, "no");
        Assert.Equal("no", final.GetProperty("myVote").GetString());
        Assert.Equal(1, final.GetProperty("voting").GetProperty("received").GetInt32());

        await CommandAsync(owner, album, "close_voting", version);
        var late = await guest.PostAsJsonAsync($"/api/party/{token}/game/vote",
            new { roundId = round, value = "yes" });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vote = await db.PartyGameVotes.SingleAsync();
        Assert.Equal("no", vote.Value);
    }

    [Fact]
    public async Task A_preference_needs_an_identity_the_party_issued()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Canta"]);
        var token = await ViewTokenAsync(owner, album);

        // A client that has never joined holds no participant. It may READ the
        // activities — there is nothing private about the host's deck — and it
        // may not choose, because a cookie the server never issued is a claim.
        var stranger = _factory.CreateClient();
        var read = await SnapshotAsync(stranger, token);
        var challenge = read.GetProperty("preferences").GetProperty("items")[0]
            .GetProperty("id").GetGuid();
        Assert.False(read.GetProperty("preferences").GetProperty("items")[0]
            .GetProperty("selected").GetBoolean());

        var refused = await stranger.PostAsJsonAsync($"/api/party/{token}/game/preferences",
            new { challengeId = challenge, selected = true });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("not_joined",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyChallengeVotes.ToListAsync());
    }

    // --- helpers -----------------------------------------------------------

    private async Task<Guid> SetUpAsync(
        HttpClient owner, string[] titles, int votesPerGuest = 3, bool priorityVoting = true)
    {
        var album = (await (await owner.PostAsJsonAsync("/api/albums",
                new { name = $"Festa {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var settings = await (await owner.PatchAsJsonAsync(
            $"/api/albums/{album}/party-settings", new { enabled = true }))
            .Content.ReadFromJsonAsync<JsonElement>();
        await PartyTestHost.StartAsync(owner, settings);
        await SetPriorityVotingAsync(owner, album, priorityVoting, votesPerGuest);
        foreach (var title in titles)
        {
            // No `kind`: the composer stopped asking for one, so the tests stop
            // sending one, and a new activity becomes `custom`.
            (await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
            {
                title, body = "Descrizione", mediaFileItemId = (Guid?)null, isEnabled = true,
            })).EnsureSuccessStatusCode();
        }
        return album;
    }

    private static async Task SetPriorityVotingAsync(
        HttpClient owner, Guid album, bool enabled, int votesPerGuest) =>
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 30, maxChallengeIntervalSeconds = 60,
            votesPerGuest, maxChallengesPerSession = (int?)null, priorityVotingEnabled = enabled,
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

    private static async Task<JsonElement> VoteAsync(
        HttpClient guest, string token, Guid roundId, string value)
    {
        var response = await guest.PostAsJsonAsync($"/api/party/{token}/game/vote",
            new { roundId, value });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
