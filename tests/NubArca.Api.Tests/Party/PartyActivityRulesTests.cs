using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// An activity's rules are COLUMNS. Body is what the host wrote for the room to
/// read; how long it lasts and whether anybody votes are things the runtime acts
/// on, and a runtime that had to parse prose would be one edit away from wrong.
/// </summary>
public sealed class PartyActivityRulesTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyActivityRulesTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Rules_survive_a_round_trip_and_stay_out_of_the_body()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner);

        var created = await CreateAsync(owner, album, new
        {
            title = "Canta", body = "Sali sul tavolo.", kind = "dare",
            mediaFileItemId = (Guid?)null, isEnabled = true,
            durationSeconds = 120, votingMode = "binary", voteQuestion = "Ce l'ha fatta?",
        });
        Assert.Equal(120, created.GetProperty("durationSeconds").GetInt32());
        Assert.Equal("binary", created.GetProperty("votingMode").GetString());
        Assert.Equal("Ce l'ha fatta?", created.GetProperty("voteQuestion").GetString());
        Assert.Equal("Sali sul tavolo.", created.GetProperty("body").GetString());

        var listed = (await ListAsync(owner, album)).EnumerateArray().Single();
        Assert.Equal(120, listed.GetProperty("durationSeconds").GetInt32());
        Assert.Equal("binary", listed.GetProperty("votingMode").GetString());
    }

    [Fact]
    public async Task An_activity_written_before_the_composer_becomes_an_ordinary_voted_one()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner);

        // Exactly the payload the settings panel sent before the rule columns
        // existed. It must still be a valid activity.
        var created = await CreateAsync(owner, album, new
        {
            title = "Vecchia", body = "Descrizione", kind = "dare",
            mediaFileItemId = (Guid?)null, isEnabled = true,
        });
        Assert.Equal(JsonValueKind.Null, created.GetProperty("durationSeconds").ValueKind);
        Assert.Equal("binary", created.GetProperty("votingMode").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("voteQuestion").ValueKind);
    }

    [Fact]
    public async Task An_edit_that_omits_the_mode_keeps_the_one_the_activity_had()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner);
        var id = (await CreateAsync(owner, album, new
        {
            title = "Brindisi", body = "Alza il calice.", kind = "custom",
            mediaFileItemId = (Guid?)null, isEnabled = true, votingMode = "none",
        })).GetProperty("id").GetGuid();

        // A caller that predates the composer edits title and body only. It must
        // not silently reset the activity to a voted one.
        var updated = await owner.PutAsJsonAsync($"/api/albums/{album}/party-challenges/{id}", new
        {
            title = "Brindisi", body = "Alza il calice, poi bevi.", kind = "custom",
            mediaFileItemId = (Guid?)null, isEnabled = true,
        });
        updated.EnsureSuccessStatusCode();
        Assert.Equal("none",
            (await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("votingMode").GetString());
    }

    [Fact]
    public async Task A_blank_question_is_no_question()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner);
        var created = await CreateAsync(owner, album, new
        {
            title = "Canta", body = "Sali sul tavolo.", kind = "dare",
            mediaFileItemId = (Guid?)null, isEnabled = true, voteQuestion = "   ",
        });
        Assert.Equal(JsonValueKind.Null, created.GetProperty("voteQuestion").ValueKind);
    }

    [Theory]
    [InlineData(4, "binary", null)]          // shorter than anybody can do anything
    [InlineData(3601, "binary", null)]       // no longer a party activity
    [InlineData(null, "rating", null)]       // a mode the runtime cannot run
    [InlineData(null, "quiz", null)]
    public async Task Rules_the_runtime_cannot_honour_are_refused(
        int? durationSeconds, string votingMode, string? voteQuestion)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner);
        var response = await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
        {
            title = "Canta", body = "Sali sul tavolo.", kind = "dare",
            mediaFileItemId = (Guid?)null, isEnabled = true,
            durationSeconds, votingMode, voteQuestion,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_question_longer_than_the_limit_is_refused()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner);
        var response = await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
        {
            title = "Canta", body = "Sali sul tavolo.", kind = "dare",
            mediaFileItemId = (Guid?)null, isEnabled = true,
            voteQuestion = new string('x', 121),
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unvoted_activity_never_opens_a_vote_and_goes_straight_to_its_result()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, game: true);
        await CreateAsync(owner, album, new
        {
            title = "Brindisi", body = "Alza il calice.", kind = "custom",
            mediaFileItemId = (Guid?)null, isEnabled = true, votingMode = "none",
        });

        await CommandAsync(owner, album, "start", 0);
        var active = await CommandAsync(owner, album, "start_challenge", 1);

        // The vote is ABSENT from what the control room may do, not refused
        // after the fact — and the primary action is the result.
        var commands = active.GetProperty("availableCommands")
            .EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal("reveal_result", commands[0]);
        Assert.DoesNotContain("open_voting", commands);

        var refused = await owner.PostAsJsonAsync($"/api/albums/{album}/party-game/commands",
            new { command = "open_voting", expectedVersion = 2 });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var result = await CommandAsync(owner, album, "reveal_result", 2);
        Assert.Equal("result", result.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task A_timed_activity_gets_a_deadline_when_it_starts_and_not_before()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, game: true);
        await CreateAsync(owner, album, new
        {
            title = "Canta", body = "Sali sul tavolo.", kind = "dare",
            mediaFileItemId = (Guid?)null, isEnabled = true, durationSeconds = 120,
        });

        // Revealing puts it on screen; the clock has not started.
        var reveal = await CommandAsync(owner, album, "start", 0);
        Assert.Equal(JsonValueKind.Null, reveal.GetProperty("phaseEndsAt").ValueKind);
        Assert.Equal(120, reveal.GetProperty("currentChallenge").GetProperty("durationSeconds").GetInt32());

        var active = await CommandAsync(owner, album, "start_challenge", 1);
        var started = active.GetProperty("phaseStartedAt").GetDateTime();
        var ends = active.GetProperty("phaseEndsAt").GetDateTime();
        Assert.Equal(120, (int)Math.Round((ends - started).TotalSeconds));

        // Opening the vote is a new phase, and the activity's clock is done.
        var voting = await CommandAsync(owner, album, "open_voting", 2);
        Assert.Equal(JsonValueKind.Null, voting.GetProperty("phaseEndsAt").ValueKind);
    }

    [Fact]
    public async Task An_untimed_activity_never_acquires_a_deadline()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, game: true);
        await CreateAsync(owner, album, new
        {
            title = "Canta", body = "Sali sul tavolo.", kind = "dare",
            mediaFileItemId = (Guid?)null, isEnabled = true,
        });
        await CommandAsync(owner, album, "start", 0);
        var active = await CommandAsync(owner, album, "start_challenge", 1);
        Assert.Equal(JsonValueKind.Null, active.GetProperty("phaseEndsAt").ValueKind);
    }

    [Fact]
    public async Task A_guest_is_told_how_the_activity_is_played_but_never_who_is_playing_it()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await SetUpAsync(owner, game: true);
        await CreateAsync(owner, album, new
        {
            title = "Canta", body = "Sali sul tavolo.", kind = "dare",
            mediaFileItemId = (Guid?)null, isEnabled = true,
            durationSeconds = 60, votingMode = "binary", voteQuestion = "Ce l'ha fatta?",
        });
        var token = await ViewTokenAsync(owner, album);
        await CommandAsync(owner, album, "start", 0);

        var snapshot = await (await _factory.CreateClient().GetAsync($"/api/party/{token}/game"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var challenge = snapshot.GetProperty("challenge");
        Assert.Equal(60, challenge.GetProperty("durationSeconds").GetInt32());
        Assert.Equal("binary", challenge.GetProperty("votingMode").GetString());
        Assert.Equal("Ce l'ha fatta?", challenge.GetProperty("voteQuestion").GetString());
        Assert.False(snapshot.TryGetProperty("availableCommands", out _));
    }

    // --- helpers -----------------------------------------------------------

    private async Task<Guid> SetUpAsync(HttpClient owner, bool game = false)
    {
        var album = (await (await owner.PostAsJsonAsync("/api/albums", new { name = $"Festa {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await EnableAndStartPartyAsync(owner, album);
        if (game)
            (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
            {
                gameEnabled = true, minChallengeIntervalSeconds = 30,
                maxChallengeIntervalSeconds = 60, votesPerGuest = 3,
                maxChallengesPerSession = (int?)null,
            })).EnsureSuccessStatusCode();
        return album;
    }

    private static async Task<JsonElement> CreateAsync(HttpClient owner, Guid album, object body)
    {
        var response = await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ListAsync(HttpClient owner, Guid album) =>
        (await (await owner.GetAsync($"/api/albums/{album}/party-challenges"))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");

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
