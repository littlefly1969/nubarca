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
/// VOTE NEVER MINTS IDENTITY — including the older challenge vote, which is a
/// different voting system on the same principle.
///
/// <para>The hosted game's vote was made resolve-only; these two endpoints were
/// still resolve-or-create, so a fresh cookie was still a fresh voter on the
/// challenge deck. The rule does not belong to one game: the right to spend a
/// budget and change a ranking comes from an identity the party ISSUED, never
/// from a header a caller can set.</para>
///
/// <para>The ordinary flow is unchanged: a guest opens the party surface, which
/// establishes their session, and votes from there.</para>
/// </summary>
public sealed class PartyChallengeVoterIdentityTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyChallengeVoterIdentityTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_guest_who_opened_the_party_can_vote_and_take_it_back()
    {
        var party = await SetUpAsync(votesPerGuest: 3);
        var guest = _factory.CreateClient();

        // GET establishes the session — this IS how a guest arrives.
        (await guest.GetAsync($"/api/party/{party.View}/challenges")).EnsureSuccessStatusCode();

        var voted = await guest.PutAsync(VoteUrl(party, party.ChallengeId), null);
        Assert.Equal(HttpStatusCode.OK, voted.StatusCode);
        var after = await voted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(after.GetProperty("voted").GetBoolean());
        Assert.Equal(1, after.GetProperty("votesUsed").GetInt32());
        Assert.Equal(2, after.GetProperty("votesRemaining").GetInt32());

        var undone = await guest.DeleteAsync(VoteUrl(party, party.ChallengeId));
        Assert.Equal(HttpStatusCode.OK, undone.StatusCode);
        Assert.Equal(3, (await undone.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("votesRemaining").GetInt32());

        var counts = await CountsAsync();
        Assert.Equal(1, counts.Participants);
        Assert.Equal(0, counts.Votes);
    }

    [Fact]
    public async Task The_per_guest_vote_budget_is_still_enforced()
    {
        var party = await SetUpAsync(votesPerGuest: 2, challenges: 3);
        var guest = _factory.CreateClient();
        var listed = await (await guest.GetAsync($"/api/party/{party.View}/challenges"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var ids = listed.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("id").GetGuid()).ToArray();

        Assert.Equal(HttpStatusCode.OK, (await guest.PutAsync(VoteUrl(party, ids[0]), null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.PutAsync(VoteUrl(party, ids[1]), null)).StatusCode);

        // The third is refused by the budget, not by the identity rule.
        var third = await guest.PutAsync(VoteUrl(party, ids[2]), null);
        third.EnsureSuccessStatusCode();
        var refusal = await third.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(refusal.GetProperty("voted").GetBoolean());
        Assert.Equal(0, refusal.GetProperty("votesRemaining").GetInt32());
        Assert.Equal(2, (await CountsAsync()).Votes);
    }

    [Fact]
    public async Task A_direct_vote_with_no_cookie_at_all_is_refused_and_writes_nothing()
    {
        var party = await SetUpAsync();
        var before = await CountsAsync();

        var response = await _factory.CreateClient().PutAsync(VoteUrl(party, party.ChallengeId), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_joined", await ErrorAsync(response));
        // Not handed an identity on the way out, either.
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task A_direct_unvote_with_no_cookie_is_refused_the_same_way()
    {
        var party = await SetUpAsync();
        var before = await CountsAsync();

        var response = await _factory.CreateClient().DeleteAsync(VoteUrl(party, party.ChallengeId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_joined", await ErrorAsync(response));
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task An_invented_cookie_is_not_a_voter()
    {
        var party = await SetUpAsync();
        var before = await CountsAsync();

        // Correctly sized, correctly alphabetted, entirely invented.
        var response = await WithCookieAsync(
            HttpMethod.Put, VoteUrl(party, party.ChallengeId), $"NubArca.PartyBrowser={FakeToken()}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_joined", await ErrorAsync(response));
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task A_hundred_invented_identities_add_no_voters_and_no_votes()
    {
        var party = await SetUpAsync(votesPerGuest: 1);
        // One real guest, so there is a ranking to try to move.
        var honest = _factory.CreateClient();
        (await honest.GetAsync($"/api/party/{party.View}/challenges")).EnsureSuccessStatusCode();
        (await honest.PutAsync(VoteUrl(party, party.ChallengeId), null)).EnsureSuccessStatusCode();
        var before = await CountsAsync();

        for (var i = 0; i < 100; i++)
        {
            var response = await WithCookieAsync(
                HttpMethod.Put, VoteUrl(party, party.ChallengeId),
                $"NubArca.PartyBrowser={FakeToken()}");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        await AssertUnchangedAsync(before);
        Assert.Equal(1, before.Votes);
    }

    [Fact]
    public async Task A_legacy_cookie_alone_does_not_authorise_a_vote()
    {
        // Adoption belongs to establishing a session. A privileged action must
        // not manufacture the identity that authorises it, whatever cookie it
        // is carrying.
        var party = await SetUpAsync();
        var legacy = FakeToken();
        await SeedLegacyAsync(party.LinkId, legacy);
        var before = await CountsAsync();

        var response = await WithCookieAsync(
            HttpMethod.Put, VoteUrl(party, party.ChallengeId), $"NubArca.PartyGuest={legacy}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_joined", await ErrorAsync(response));
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task An_identity_issued_by_one_party_cannot_vote_at_another()
    {
        var theirs = await SetUpAsync();
        var mine = await SetUpAsync();

        // A real session at THEIR party, and the cookie it issued.
        var guest = _factory.CreateClient();
        var opened = await guest.GetAsync($"/api/party/{theirs.View}/challenges");
        opened.EnsureSuccessStatusCode();
        var cookie = opened.Headers.GetValues("Set-Cookie")
            .Single(x => x.StartsWith("NubArca.PartyBrowser=", StringComparison.Ordinal))
            .Split(';', 2)[0];
        var before = await CountsAsync();

        // Replayed here on purpose. It derives a different key on this link, so
        // it resolves to nothing.
        var response = await WithCookieAsync(
            HttpMethod.Put, VoteUrl(mine, mine.ChallengeId), cookie);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_joined", await ErrorAsync(response));
        // Their guest still exists; no second one was created at my party.
        Assert.Equal(before.Participants, (await CountsAsync()).Participants);
        Assert.Equal(0, (await CountsAsync()).Votes);
    }

    // --- helpers -----------------------------------------------------------

    private sealed record Party(HttpClient Owner, Guid Album, Guid LinkId, string View, Guid ChallengeId);

    private static string VoteUrl(Party party, Guid challengeId) =>
        $"/api/party/{party.View}/challenges/{challengeId}/vote";

    private static string FakeToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private Task<HttpResponseMessage> WithCookieAsync(HttpMethod method, string url, string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", cookie);
        return _factory.CreateClient().SendAsync(request);
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private async Task<(int Participants, int Votes, int Claimed)> CountsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (
            await db.PartyParticipants.CountAsync(),
            await db.PartyChallengeVotes.CountAsync(),
            await db.PartyParticipants.SumAsync(p => p.ChallengeVoteCount));
    }

    private async Task AssertUnchangedAsync((int Participants, int Votes, int Claimed) before)
    {
        var after = await CountsAsync();
        Assert.Equal(before.Participants, after.Participants);
        Assert.Equal(before.Votes, after.Votes);
        // The budget counter is the third thing a refused vote must not move.
        Assert.Equal(before.Claimed, after.Claimed);
    }

    private async Task SeedLegacyAsync(Guid linkId, string token)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PartyParticipants.Add(new NubArca.Api.Domain.PartyParticipant
        {
            Id = Guid.NewGuid(),
            PartyAlbumLinkId = linkId,
            TokenHash = Convert.ToHexStringLower(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))),
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Party> SetUpAsync(int votesPerGuest = 3, int challenges = 1)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync($"{Guid.NewGuid():N}@example.com");
        var album = (await (await owner.PostAsJsonAsync("/api/albums",
                new { name = $"Festa {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await EnableAndStartPartyAsync(owner, album);
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 30, maxChallengeIntervalSeconds = 60,
            votesPerGuest, maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();

        var names = new[] { "Uno", "Due", "Tre" };
        var first = Guid.Empty;
        for (var i = 0; i < challenges; i++)
        {
            var created = await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
            {
                title = names[i], body = "Descrizione", kind = "dare",
                mediaFileItemId = (Guid?)null, isEnabled = true,
            });
            created.EnsureSuccessStatusCode();
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            if (i == 0) first = id;
        }

        var view = (await (await owner.GetAsync($"/api/albums/{album}/party-settings"))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("partyUrl").GetString()!["/party/".Length..];

        using var scope = _factory.Services.CreateScope();
        var linkId = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyAlbumLinks.AsNoTracking()
            .Where(x => x.AlbumId == album && x.Enabled && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt).Select(x => x.Id).FirstAsync();

        return new Party(owner, album, linkId, view, first);
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
