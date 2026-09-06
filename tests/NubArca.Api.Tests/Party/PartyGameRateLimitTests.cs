using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// A party is one Wi-Fi network. Everything else public in Party is limited per
/// address, and for an occasional act that is right; the live game is
/// continuous, so per-address limiting would make the room's own size the thing
/// that breaks it.
///
/// These tests are about that: a real room — more guests and more screens than a
/// per-IP bucket would ever allow — behaving normally, from one apparent
/// address, without a single legitimate 429.
/// </summary>
public sealed class PartyGameRateLimitTests
{
    // Deliberately larger than the numbers in the requirement, so the proof is
    // not sitting exactly on the boundary it is proving.
    private const int Guests = 25;
    private const int Displays = 16;

    [Fact]
    public async Task A_room_of_guests_behind_one_address_can_all_vote()
    {
        // The generic party bucket is crushed to 1 for the whole run. If the
        // game were on it — as it was — the second request of the evening would
        // already be a 429.
        using var factory = Factory(
            ("RateLimits:Party:PermitLimit", "1"),
            ("RateLimits:PartyMessage:PermitLimit", "1"));
        var party = await OpenVotingAsync(factory);

        var guests = new List<HttpClient>();
        for (var i = 0; i < Guests; i++)
        {
            var guest = factory.CreateClient();
            guests.Add(guest);
            var joined = await guest.PostAsync($"/api/party/{party.Token}/game/join", null);
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        }

        // Every guest votes, and half of them change their mind twice — which is
        // what a room actually does while a challenge is being judged.
        foreach (var (guest, index) in guests.Select((g, i) => (g, i)))
        {
            Assert.Equal(HttpStatusCode.OK,
                (await VoteAsync(guest, party.Token, party.RoundId, index % 2 == 0 ? "yes" : "no")).StatusCode);
            if (index % 2 == 0)
            {
                Assert.Equal(HttpStatusCode.OK,
                    (await VoteAsync(guest, party.Token, party.RoundId, "no")).StatusCode);
                Assert.Equal(HttpStatusCode.OK,
                    (await VoteAsync(guest, party.Token, party.RoundId, "yes")).StatusCode);
            }
        }

        var closed = await CommandAsync(party.Owner, party.Album, "close_voting", 3);
        Assert.Equal(Guests, closed.GetProperty("voting").GetProperty("received").GetInt32());
    }

    [Fact]
    public async Task A_wall_of_screens_behind_one_address_can_poll_at_the_real_cadence()
    {
        using var factory = Factory(("RateLimits:Party:PermitLimit", "1"));
        var party = await OpenVotingAsync(factory);

        // Televisions hold no participant cookie, so they all land in the
        // address fallback together — the case that bucket exists for. Two
        // minutes of the real 2.5s cadence, from sixteen screens at once.
        var polls = 48;
        var displays = new List<HttpClient>();
        for (var i = 0; i < Displays; i++) displays.Add(factory.CreateClient());

        foreach (var poll in Enumerable.Range(0, polls))
        foreach (var display in displays)
        {
            var response = await display.GetAsync($"/api/party/{party.Token}/game?display=1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            _ = poll;
        }
    }

    [Fact]
    public async Task A_guest_polling_and_voting_never_touches_the_generic_party_bucket()
    {
        // Both generic buckets are exhausted after ONE request each. The game
        // must be untouched by that, and the ordinary hub must still be limited.
        using var factory = Factory(
            ("RateLimits:Party:PermitLimit", "1"),
            ("RateLimits:PartyMessage:PermitLimit", "1"));
        var party = await OpenVotingAsync(factory);
        var guest = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK,
            (await guest.PostAsync($"/api/party/{party.Token}/game/join", null)).StatusCode);
        for (var i = 0; i < 40; i++)
            Assert.Equal(HttpStatusCode.OK,
                (await guest.GetAsync($"/api/party/{party.Token}/game")).StatusCode);
        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.OK,
                (await VoteAsync(guest, party.Token, party.RoundId, i % 2 == 0 ? "yes" : "no")).StatusCode);

        // The generic bucket really is at one: the hub is limited exactly as
        // before, so nothing was loosened on the way past.
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/party/{party.Token}")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await anon.GetAsync($"/api/party/{party.Token}")).StatusCode);
    }

    [Fact]
    public async Task One_guest_cannot_spend_the_whole_room_s_allowance()
    {
        // The point of partitioning per guest is that a guest has an allowance
        // AND that it is their own: exhausting it must stop them and nobody else.
        using var factory = Factory(
            ("RateLimits:PartyGameVote:PermitLimit", "3"),
            ("RateLimits:PartyGameVote:AddressPermitLimit", "50"));
        var party = await OpenVotingAsync(factory);

        var noisy = factory.CreateClient();
        (await noisy.PostAsync($"/api/party/{party.Token}/game/join", null)).EnsureSuccessStatusCode();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK,
                (await VoteAsync(noisy, party.Token, party.RoundId, "yes")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await VoteAsync(noisy, party.Token, party.RoundId, "no")).StatusCode);

        // The guest beside them, on the same address, is unaffected.
        var quiet = factory.CreateClient();
        (await quiet.PostAsync($"/api/party/{party.Token}/game/join", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK,
            (await VoteAsync(quiet, party.Token, party.RoundId, "yes")).StatusCode);
    }

    [Fact]
    public void A_partition_names_the_guest_when_one_identified_itself()
    {
        var token = new string('a', 43);
        var other = new string('b', 43);

        Assert.True(PartyGameRateLimits.IsGuest(PartitionFor(token, "10.0.0.1")));
        // Same guest from a different address is the same bucket; two guests on
        // one address are two buckets. That is the whole point.
        Assert.Equal(PartitionFor(token, "10.0.0.1"), PartitionFor(token, "10.0.0.2"));
        Assert.NotEqual(PartitionFor(token, "10.0.0.1"), PartitionFor(other, "10.0.0.1"));

        // The key is a fingerprint, never the token: a partition key must not be
        // a way to read a guest's session back out of a diagnostic.
        Assert.DoesNotContain(token, PartitionFor(token, "10.0.0.1"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("not-a-token-because-it-has-punctuation!!!!!!")]
    public void Anything_that_is_not_a_server_shaped_token_falls_back_to_the_address(string? cookie)
    {
        var key = PartitionFor(cookie, "10.0.0.7");
        Assert.False(PartyGameRateLimits.IsGuest(key));
        Assert.Equal(key, PartitionFor(cookie, "10.0.0.7"));
        Assert.NotEqual(key, PartitionFor(cookie, "10.0.0.8"));
    }

    private static string PartitionFor(string? cookie, string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(address);
        if (cookie is not null)
            context.Request.Headers.Cookie = $"NubArca.PartyGuest={cookie}";
        return PartyGameRateLimits.Partition(context);
    }

    // --- helpers -----------------------------------------------------------

    private sealed record Party(HttpClient Owner, Guid Album, string Token, Guid RoundId);

    private static SqliteWebApplicationFactory Factory(params (string Key, string Value)[] settings)
    {
        var factory = new SqliteWebApplicationFactory(
            settings.ToDictionary(s => s.Key, s => (string?)s.Value));
        factory.EnsureDatabaseCreated();
        return factory;
    }

    private static async Task<Party> OpenVotingAsync(SqliteWebApplicationFactory factory)
    {
        var (_, owner) = await factory.CreateAuthenticatedClientAsync();
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

        var round = (await (await factory.CreateClient()
                .GetAsync($"/api/party/{token}/game?display=1"))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roundId").GetGuid();
        return new Party(owner, album, token, round);
    }

    private static Task<HttpResponseMessage> VoteAsync(
        HttpClient guest, string token, Guid roundId, string value) =>
        guest.PostAsJsonAsync($"/api/party/{token}/game/vote", new { roundId, value });

    private static async Task<JsonElement> CommandAsync(
        HttpClient owner, Guid album, string command, int expectedVersion)
    {
        var response = await owner.PostAsJsonAsync($"/api/albums/{album}/party-game/commands",
            new { command, expectedVersion });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
