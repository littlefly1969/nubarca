using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using NubArca.Api.Tv;

namespace NubArca.Api.Tests.Party;

public sealed class PartyChallengeTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyChallengeTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Existing_party_defaults_off_and_legacy_urls_stay_valid()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await CreateAlbumAsync(owner, "Festa");
        var status = await EnableAsync(owner, album);
        Assert.False(status.GetProperty("gameEnabled").GetBoolean());
        var view = ViewToken(status);
        var upload = UploadToken(status);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().GetAsync($"/api/party/{view}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().PostAsync(
            $"/api/party/{upload}/upload-session", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync(
            $"/api/party/{view}/challenges")).StatusCode);
    }

    [Fact]
    public async Task Owner_scope_and_media_reference_are_enforced()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var album = await CreateAlbumAsync(alice, "Alice");
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/albums/{album}/party-challenges")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await alice.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
            {
                title = "Sfida", body = "Fai qualcosa", kind = "dare",
                mediaFileItemId = Guid.NewGuid(), isEnabled = true,
            })).StatusCode);
    }

    [Fact]
    public async Task Guest_budget_is_server_enforced_and_votes_are_idempotent()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await CreateAlbumAsync(owner, "Festa");
        var status = await EnableAsync(owner, album);
        await EnableGameAsync(owner, album, votes: 1);
        var first = await CreateChallengeAsync(owner, album, "Uno");
        var second = await CreateChallengeAsync(owner, album, "Due");
        var guest = _factory.CreateClient();
        var token = ViewToken(status);
        (await guest.GetAsync($"/api/party/{token}/challenges")).EnsureSuccessStatusCode();

        var vote1 = await guest.PutAsync($"/api/party/{token}/challenges/{first}/vote", null);
        vote1.EnsureSuccessStatusCode();
        var again = await guest.PutAsync($"/api/party/{token}/challenges/{first}/vote", null);
        again.EnsureSuccessStatusCode();
        Assert.Equal(1, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("votesUsed").GetInt32());

        var refused = await guest.PutAsync($"/api/party/{token}/challenges/{second}/vote", null);
        refused.EnsureSuccessStatusCode();
        var refusal = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(refusal.GetProperty("voted").GetBoolean());
        Assert.Equal(0, refusal.GetProperty("votesRemaining").GetInt32());

        var removed = await guest.DeleteAsync($"/api/party/{token}/challenges/{first}/vote");
        removed.EnsureSuccessStatusCode();
        Assert.Equal(1, (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("votesRemaining").GetInt32());
        var removedAgain = await guest.DeleteAsync($"/api/party/{token}/challenges/{first}/vote");
        removedAgain.EnsureSuccessStatusCode();
        Assert.Equal(1, (await removedAgain.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("votesRemaining").GetInt32());
    }

    [Fact]
    public async Task Disabling_a_challenge_releases_its_guest_votes()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await CreateAlbumAsync(owner, "Festa");
        var status = await EnableAsync(owner, album);
        await EnableGameAsync(owner, album, votes: 1);
        var challenge = await CreateChallengeAsync(owner, album, "Uno");
        var guest = _factory.CreateClient();
        var token = ViewToken(status);
        (await guest.GetAsync($"/api/party/{token}/challenges")).EnsureSuccessStatusCode();
        (await guest.PutAsync($"/api/party/{token}/challenges/{challenge}/vote", null))
            .EnsureSuccessStatusCode();

        var disabled = await owner.PutAsJsonAsync($"/api/albums/{album}/party-challenges/{challenge}", new
        {
            title = "Uno", body = "Descrizione", kind = "dare",
            mediaFileItemId = (Guid?)null, isEnabled = false,
        });
        disabled.EnsureSuccessStatusCode();

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.PartyChallengeVotes.ToListAsync());
        Assert.Equal(0, await db.PartyParticipants.Select(x => x.ChallengeVoteCount).SingleAsync());
    }

    [Fact]
    public async Task View_token_cannot_vote_on_another_album_challenge()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var a = await CreateAlbumAsync(owner, "A");
        var b = await CreateAlbumAsync(owner, "B");
        var aStatus = await EnableAsync(owner, a);
        await EnableAsync(owner, b);
        await EnableGameAsync(owner, a, 3);
        await EnableGameAsync(owner, b, 3);
        var foreign = await CreateChallengeAsync(owner, b, "Solo B");

        // Establish a real guest session on party A first: a vote never mints an
        // identity, so without this the refusal would be `not_joined` and the
        // test would be asserting the wrong thing entirely.
        var guest = _factory.CreateClient();
        (await guest.GetAsync($"/api/party/{ViewToken(aStatus)}/challenges"))
            .EnsureSuccessStatusCode();

        // A guest of A, properly identified, still cannot reach B's challenge.
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PutAsync(
            $"/api/party/{ViewToken(aStatus)}/challenges/{foreign}/vote", null)).StatusCode);
    }

    /// <summary>
    /// THE OLD AUTOMATIC EFFECT IS GONE, and this is the test that says so.
    ///
    /// <para>A guest preference used to select an activity, freeze the party
    /// slideshow on it and wait for the remote's NEXT. A preference is now
    /// ADVISORY: it tells the host what the room wants and chooses nothing. The
    /// television therefore keeps playing photographs through every boundary,
    /// however many preferences the room has cast, and no hold row, no
    /// completion row and no interruption is written.</para>
    ///
    /// <para>The endpoints answer rather than 404 because an installed TV APK
    /// calls them on every photograph — retiring the behaviour is not the same
    /// as breaking the client.</para>
    /// </summary>
    [Fact]
    public async Task A_preference_never_interrupts_the_slideshow()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await CreateAlbumAsync(owner, "Festa");
        var status = await EnableAsync(owner, album);
        await EnableGameAsync(owner, album, 3);
        var challenge = await CreateChallengeAsync(owner, album, "Canta");
        var cookie = await PairTvAsync(owner);

        // The room says it wants this one, as loudly as it can.
        var guest = _factory.CreateClient();
        var token = ViewToken(status);
        (await guest.GetAsync($"/api/party/{token}/challenges")).EnsureSuccessStatusCode();
        (await guest.PutAsync($"/api/party/{token}/challenges/{challenge}/vote", null))
            .EnsureSuccessStatusCode();

        var initial = await TvAsync(cookie, HttpMethod.Get, $"/api/tv/albums/{album}/party-playback");
        Assert.Equal("media", initial.GetProperty("mode").GetString());

        // Every boundary, and then NEXT: the slideshow never leaves `media`.
        foreach (var _ in Enumerable.Range(0, 3))
        {
            var boundary = await TvAsync(
                cookie, HttpMethod.Post, $"/api/tv/albums/{album}/party-playback/boundary");
            Assert.Equal("media", boundary.GetProperty("mode").GetString());
            Assert.Equal(JsonValueKind.Null, boundary.GetProperty("activeChallenge").ValueKind);
        }
        var next = await TvAsync(cookie, HttpMethod.Post, $"/api/tv/albums/{album}/party-playback/next");
        Assert.Equal("media", next.GetProperty("mode").GetString());

        // Nothing was written on the way: no hold session, no completion — and
        // the preference itself is untouched, because it was never spent.
        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.PartyChallengeSessions.ToListAsync());
        Assert.Empty(await db.PartyChallengeCompletions.ToListAsync());
        Assert.Equal(1, await db.PartyChallengeVotes.CountAsync());
    }

    private static async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static async Task<JsonElement> EnableAsync(HttpClient owner, Guid album)
    {
        var response = await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings", new { enabled = true });
        response.EnsureSuccessStatusCode();
        var settings = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Enabling guest access PUBLISHES the party — an invitation, which is
        // deliberately not the party itself. These tests exercise the party.
        await PartyTestHost.StartAsync(owner, settings);
        return settings;
    }
    // The pre-game preference surface is what these guest routes ARE now, so
    // every one of them needs the host to have asked the room.
    private static async Task EnableGameAsync(
        HttpClient owner, Guid album, int votes, bool priorityVoting = true)
    {
        var response = await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 30,
            maxChallengeIntervalSeconds = 60, votesPerGuest = votes,
            maxChallengesPerSession = (int?)null, priorityVotingEnabled = priorityVoting,
        });
        response.EnsureSuccessStatusCode();
    }
    private static async Task<Guid> CreateChallengeAsync(HttpClient owner, Guid album, string title)
    {
        var response = await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
        { title, body = "Descrizione", kind = "dare", mediaFileItemId = (Guid?)null, isEnabled = true });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static string ViewToken(JsonElement status) =>
        status.GetProperty("partyUrl").GetString()!["/party/".Length..];
    private static string UploadToken(JsonElement status)
    {
        var value = status.GetProperty("uploadUrl").GetString()!["/party/".Length..];
        return value[..value.IndexOf("/upload", StringComparison.Ordinal)];
    }
    private async Task<string> PairTvAsync(HttpClient owner)
    {
        var tv = _factory.CreateClient();
        var started = await (await tv.PostAsync("/api/tv/pairing/start", null))
            .Content.ReadFromJsonAsync<TvPairingStartedDto>();
        (await owner.PostAsJsonAsync($"/api/tv/pairing/{started!.PublicCode}/approve", new
        {
            pairingSecret = started.PairingSecret, personalCode = "URDLSUDLR",
            personalCodeConfirmation = "URDLSUDLR",
        })).EnsureSuccessStatusCode();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        request.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var response = await tv.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single();
    }
    private async Task<JsonElement> TvAsync(string cookie, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        var pair = cookie.Split(';', 2)[0];
        request.Headers.Add("Cookie", pair);
        var response = await _factory.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
