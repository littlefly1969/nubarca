using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// A CHOICE ROUND, played. The room is handed the host's own answers and picks
/// one, and the result names a winner and what it costs.
///
/// What these defend:
///   * the ballot reaches the phone and the television WITH the question — a
///     guest cannot answer a question whose answers arrived separately;
///   * an answer is checked against THIS activity's options: an id from another
///     activity is a well-formed guid and must not be recorded as a vote;
///   * the result is per option, and a percentage is never computed here —
///     counts only, so two surfaces cannot round a party's verdict differently;
///   * a tie goes to the FIRST answer the host wrote, because the ballot's
///     order is an order somebody chose;
///   * with nobody voting there is no winner: "everyone abstained" is not a
///     verdict;
///   * the room learns the split only when the host closes voting, exactly as a
///     binary round does.
/// </summary>
public sealed class PartyGameChoiceRoundTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyGameChoiceRoundTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_room_is_offered_the_answers_and_the_result_names_one()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Ballare", "Cantare", "Niente"]);
        var token = await ViewTokenAsync(owner, album);
        var tv = _factory.CreateClient();
        var guestA = _factory.CreateClient();
        var guestB = _factory.CreateClient();
        await JoinAsync(guestA, token);
        await JoinAsync(guestB, token);

        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();

        // The ballot travels with the question, to the television and the phone.
        var stage = await DisplayAsync(tv, token);
        var options = stage.GetProperty("challenge").GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(["Ballare", "Cantare", "Niente"],
            options.Select(o => o.GetProperty("label").GetString()!).ToArray());
        Assert.Equal("Balla da solo", options[0].GetProperty("outcome").GetString());
        var round = stage.GetProperty("roundId").GetGuid();

        var guestBallot = (await SnapshotAsync(guestA, token))
            .GetProperty("challenge").GetProperty("options");
        Assert.Equal(3, guestBallot.GetArrayLength());

        // Both pick the second answer.
        var second = options[1].GetProperty("id").GetGuid();
        await VoteAsync(guestA, token, round, second.ToString());
        var afterB = await VoteAsync(guestB, token, round, second.ToString());
        Assert.Equal(2, afterB.GetProperty("voting").GetProperty("received").GetInt32());

        // Nobody sees the split while voting is open — not the television, and
        // not a guest who has already answered.
        Assert.Equal(JsonValueKind.Null,
            (await DisplayAsync(tv, token)).GetProperty("voting").GetProperty("options").ValueKind);

        var closed = await CommandAsync(owner, album, "close_voting", version);
        var result = closed.GetProperty("voting").GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(3, result.Count);
        Assert.Equal(0, result[0].GetProperty("votes").GetInt32());
        Assert.Equal(2, result[1].GetProperty("votes").GetInt32());
        Assert.True(result[1].GetProperty("winning").GetBoolean());
        Assert.False(result[0].GetProperty("winning").GetBoolean());
        // The consequence rides with the winner, so the television needs no
        // second lookup on the frame the room is watching.
        Assert.Equal("Canta il ritornello", result[1].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_tie_goes_to_the_first_answer_the_host_wrote()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Ballare", "Cantare"]);
        var token = await ViewTokenAsync(owner, album);
        var guestA = _factory.CreateClient();
        var guestB = _factory.CreateClient();
        await JoinAsync(guestA, token);
        await JoinAsync(guestB, token);

        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();
        var stage = await SnapshotAsync(guestA, token);
        var round = stage.GetProperty("roundId").GetGuid();
        var ballot = stage.GetProperty("challenge").GetProperty("options").EnumerateArray().ToList();

        await VoteAsync(guestA, token, round, ballot[0].GetProperty("id").GetGuid().ToString());
        await VoteAsync(guestB, token, round, ballot[1].GetProperty("id").GetGuid().ToString());

        var closed = await CommandAsync(owner, album, "close_voting", version);
        var result = closed.GetProperty("voting").GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(1, result[0].GetProperty("votes").GetInt32());
        Assert.Equal(1, result[1].GetProperty("votes").GetInt32());
        Assert.True(result[0].GetProperty("winning").GetBoolean());
        Assert.False(result[1].GetProperty("winning").GetBoolean());
    }

    [Fact]
    public async Task Nobody_voting_is_not_a_verdict()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Ballare", "Cantare"]);
        var token = await ViewTokenAsync(owner, album);

        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();

        var closed = await CommandAsync(owner, album, "close_voting", version);
        var result = closed.GetProperty("voting").GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(2, result.Count);
        Assert.All(result, o => Assert.False(o.GetProperty("winning").GetBoolean()));
        Assert.Equal(0, closed.GetProperty("voting").GetProperty("received").GetInt32());
    }

    [Fact]
    public async Task An_answer_from_another_activity_is_not_an_answer_to_this_one()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, ["Ballare", "Cantare"]);
        // A SECOND choice activity, whose option ids are perfectly well formed
        // and belong to a question the room is not being asked.
        await AddChoiceAsync(owner, album, "Altra", ["Rossa", "Blu"]);
        var token = await ViewTokenAsync(owner, album);
        var guest = _factory.CreateClient();
        await JoinAsync(guest, token);

        var deck = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{album}/party-challenges");
        var other = deck.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("title").GetString() == "Altra")
            .GetProperty("options")[0].GetProperty("id").GetGuid();

        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();
        var round = (await SnapshotAsync(guest, token)).GetProperty("roundId").GetGuid();

        // Refused as a BAD ANSWER (400), not as a closed vote: the round is open
        // and the guest is welcome — what they sent is not one of the answers.
        var refused = await guest.PostAsJsonAsync($"/api/party/{token}/game/vote",
            new { roundId = round, value = other.ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // And a plain yes is no answer to a choice round either.
        var verdict = await guest.PostAsJsonAsync($"/api/party/{token}/game/vote",
            new { roundId = round, value = "yes" });
        Assert.Equal(HttpStatusCode.BadRequest, verdict.StatusCode);

        var after = await SnapshotAsync(guest, token);
        Assert.Equal(0, after.GetProperty("voting").GetProperty("received").GetInt32());
    }

    // --- helpers -----------------------------------------------------------

    private async Task<Guid> SetUpAsync(HttpClient owner, string[] labels)
    {
        var album = (await (await owner.PostAsJsonAsync("/api/albums",
                new { name = $"Festa {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await EnableAndStartPartyAsync(owner, album);
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 30, maxChallengeIntervalSeconds = 60,
            votesPerGuest = 3, maxChallengesPerSession = (int?)null, priorityVotingEnabled = true,
        })).EnsureSuccessStatusCode();
        await AddChoiceAsync(owner, album, "La penitenza", labels);
        return album;
    }

    private static readonly Dictionary<string, string> Outcomes = new()
    {
        ["Ballare"] = "Balla da solo",
        ["Cantare"] = "Canta il ritornello",
    };

    private static Task AddChoiceAsync(
        HttpClient owner, Guid album, string title, IReadOnlyList<string> labels) =>
        owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
        {
            title,
            body = "La sala decide",
            mediaFileItemId = (Guid?)null,
            isEnabled = true,
            votingMode = "choice",
            voteQuestion = "Cosa deve fare?",
            options = labels
                .Select(l => new { label = l, outcome = Outcomes.GetValueOrDefault(l) })
                .ToArray(),
        }).ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    private async Task EnableAndStartPartyAsync(HttpClient owner, Guid album)
    {
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/tv-settings", new { showOnTv = true }))
            .EnsureSuccessStatusCode();
        var settings = await owner.PatchAsJsonAsync(
            $"/api/albums/{album}/party-settings", new { enabled = true });
        settings.EnsureSuccessStatusCode();
        await PartyTestHost.StartAsync(owner, await settings.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static async Task<string> ViewTokenAsync(HttpClient owner, Guid album) =>
        (await (await owner.GetAsync($"/api/albums/{album}/party-settings"))
            .Content.ReadFromJsonAsync<JsonElement>())
        .GetProperty("partyUrl").GetString()!["/party/".Length..];

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
}
