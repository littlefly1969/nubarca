using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// One invariant, from every angle a caller can approach it from.
///
/// <para><b>Join mints identity. Vote never does.</b> The only sequence that
/// creates a Party Game voter is a successful join, a persisted participant, and
/// a later resolve-only vote. Presenting a cookie is a claim; a claim the server
/// never issued is worth nothing, and is refused before a row of any kind is
/// written.</para>
///
/// <para>These tests exist because the opposite was true: the vote endpoint used
/// to resolve-or-CREATE, so a fresh cookie was a fresh voter and the right to
/// change a party's result was available to anyone who could set a header. No
/// amount of rate limiting fixes that — a limiter bounds how many requests an
/// identity may make, not whether it should have been an identity.</para>
/// </summary>
public sealed class PartyGameVoterIdentityTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyGameVoterIdentityTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_cookie_the_server_never_issued_is_not_a_voter()
    {
        var party = await OpenVotingAsync();
        var before = await CountsAsync();

        // Correctly sized, correctly alphabetted, entirely invented: exactly
        // what a caller writing the cookie by hand would produce, and exactly
        // what the rate limiter's shape check cannot tell from the real thing.
        var response = await VoteAsync(FakeToken(), party.Token, party.RoundId, "yes");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_joined", await CodeAsync(response));
        // And it was not handed one on the way out either.
        Assert.False(response.Headers.Contains("Set-Cookie"));

        var after = await CountsAsync();
        Assert.Equal(before.Participants, after.Participants);
        Assert.Equal(before.Votes, after.Votes);
    }

    [Fact]
    public async Task No_cookie_at_all_is_not_a_voter_either()
    {
        var party = await OpenVotingAsync();
        var before = await CountsAsync();

        var response = await _factory.CreateClient().PostAsJsonAsync(
            $"/api/party/{party.Token}/game/vote",
            new { roundId = party.RoundId, value = "yes" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_joined", await CodeAsync(response));
        var after = await CountsAsync();
        Assert.Equal(before.Participants, after.Participants);
        Assert.Equal(before.Votes, after.Votes);
    }

    [Fact]
    public async Task A_hundred_invented_identities_change_nothing_at_all()
    {
        var party = await OpenVotingAsync();

        // One real guest, so there is a result to try to move.
        var honest = _factory.CreateClient();
        (await honest.PostAsync($"/api/party/{party.Token}/game/join", null)).EnsureSuccessStatusCode();
        (await honest.PostAsJsonAsync($"/api/party/{party.Token}/game/vote",
            new { roundId = party.RoundId, value = "yes" })).EnsureSuccessStatusCode();
        var before = await CountsAsync();

        // A different well-formed invention every time — the shape a limiter
        // partitions on, and the shape that used to mint a voter.
        for (var i = 0; i < 100; i++)
        {
            var response = await VoteAsync(FakeToken(), party.Token, party.RoundId, "no");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("not_joined", await CodeAsync(response));
        }

        var after = await CountsAsync();
        Assert.Equal(before.Participants, after.Participants);
        Assert.Equal(before.Votes, after.Votes);

        // The result is exactly what the one real guest said.
        var closed = await CommandAsync(party.Owner, party.Album, "close_voting", 3);
        var voting = closed.GetProperty("voting");
        Assert.Equal(1, voting.GetProperty("received").GetInt32());
        Assert.Equal(1, voting.GetProperty("yes").GetInt32());
        Assert.Equal(0, voting.GetProperty("no").GetInt32());
        Assert.True(voting.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task A_guest_who_joined_votes_and_keeps_one_answer_when_they_change_it()
    {
        var party = await OpenVotingAsync();
        var guest = _factory.CreateClient();

        var joined = await guest.PostAsync($"/api/party/{party.Token}/game/join", null);
        joined.EnsureSuccessStatusCode();
        // Joining is what issues the identity, and it says so on the wire.
        Assert.Contains(joined.Headers.GetValues("Set-Cookie"),
            x => x.StartsWith("NubArca.PartyBrowser=", StringComparison.Ordinal));

        var first = await guest.PostAsJsonAsync($"/api/party/{party.Token}/game/vote",
            new { roundId = party.RoundId, value = "yes" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("yes",
            (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("myVote").GetString());

        var changed = await guest.PostAsJsonAsync($"/api/party/{party.Token}/game/vote",
            new { roundId = party.RoundId, value = "no" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var body = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no", body.GetProperty("myVote").GetString());
        // The SAME identity replaced its answer rather than adding one.
        Assert.Equal(1, body.GetProperty("voting").GetProperty("received").GetInt32());

        var counts = await CountsAsync();
        Assert.Equal(1, counts.Participants);
        Assert.Equal(1, counts.Votes);
    }

    [Fact]
    public async Task Joining_twice_is_the_same_guest_and_not_a_second_one()
    {
        var party = await OpenVotingAsync();
        var guest = _factory.CreateClient();

        (await guest.PostAsync($"/api/party/{party.Token}/game/join", null)).EnsureSuccessStatusCode();
        (await guest.PostAsync($"/api/party/{party.Token}/game/join", null)).EnsureSuccessStatusCode();
        (await guest.PostAsJsonAsync($"/api/party/{party.Token}/game/vote",
            new { roundId = party.RoundId, value = "yes" })).EnsureSuccessStatusCode();

        var counts = await CountsAsync();
        Assert.Equal(1, counts.Participants);
        Assert.Equal(1, counts.Votes);
    }

    [Fact]
    public async Task Only_join_ever_creates_a_participant()
    {
        var party = await OpenVotingAsync();

        // Every other Party Game endpoint, hit hard, by callers with no cookie
        // and with an invented one. None of them may leave an identity behind.
        for (var i = 0; i < 10; i++)
        {
            (await _factory.CreateClient()
                .GetAsync($"/api/party/{party.Token}/game")).EnsureSuccessStatusCode();
            (await _factory.CreateClient()
                .GetAsync($"/api/party/{party.Token}/game?display=1")).EnsureSuccessStatusCode();
            await VoteAsync(FakeToken(), party.Token, party.RoundId, "yes");
        }

        Assert.Equal(0, (await CountsAsync()).Participants);

        // And then one join, which is the one thing that does.
        var guest = _factory.CreateClient();
        (await guest.PostAsync($"/api/party/{party.Token}/game/join", null)).EnsureSuccessStatusCode();
        Assert.Equal(1, (await CountsAsync()).Participants);
    }

    // --- helpers -----------------------------------------------------------

    private sealed record Party(HttpClient Owner, Guid Album, string Token, Guid RoundId);

    /// 32 CSPRNG bytes as unpadded base64url — byte-for-byte the shape
    /// PartyGuestIdentity issues, and no more real for it.
    private static string FakeToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private Task<HttpResponseMessage> VoteAsync(
        string cookie, string token, Guid roundId, string value)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/party/{token}/game/vote")
        {
            Content = JsonContent.Create(new { roundId, value }),
        };
        request.Headers.Add("Cookie", $"NubArca.PartyBrowser={cookie}");
        return _factory.CreateClient().SendAsync(request);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private async Task<(int Participants, int Votes)> CountsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.PartyParticipants.CountAsync(), await db.PartyGameVotes.CountAsync());
    }

    private async Task<Party> OpenVotingAsync()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = (await (await owner.PostAsJsonAsync("/api/albums",
                new { name = $"Festa {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings", new { enabled = true }))
            .EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 30, maxChallengeIntervalSeconds = 60,
            votesPerGuest = 3, maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
        {
            title = "Canta", body = "Sali sul tavolo.", kind = "dare",
            mediaFileItemId = (Guid?)null, isEnabled = true, votingMode = "binary",
        })).EnsureSuccessStatusCode();
        var token = (await (await owner.GetAsync($"/api/albums/{album}/party-settings"))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("partyUrl").GetString()!["/party/".Length..];

        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
            version = (await CommandAsync(owner, album, command, version)).GetProperty("version").GetInt32();

        // Read the round from a television, which never becomes a participant.
        var round = (await (await _factory.CreateClient()
                .GetAsync($"/api/party/{token}/game?display=1"))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roundId").GetGuid();
        return new Party(owner, album, token, round);
    }

    private static async Task<JsonElement> CommandAsync(
        HttpClient owner, Guid album, string command, int expectedVersion)
    {
        var response = await owner.PostAsJsonAsync($"/api/albums/{album}/party-game/commands",
            new { command, expectedVersion });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
