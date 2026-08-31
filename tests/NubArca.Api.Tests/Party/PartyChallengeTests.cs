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
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().PutAsync(
            $"/api/party/{ViewToken(aStatus)}/challenges/{foreign}/vote", null)).StatusCode);
    }

    [Fact]
    public async Task Boundary_holds_reconnects_and_next_completes_once()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await CreateAlbumAsync(owner, "Festa");
        await EnableAsync(owner, album);
        await EnableGameAsync(owner, album, 3);
        var challenge = await CreateChallengeAsync(owner, album, "Canta");
        var cookie = await PairTvAsync(owner);

        var initial = await TvAsync(cookie, HttpMethod.Get, $"/api/tv/albums/{album}/party-playback");
        Assert.Equal("media", initial.GetProperty("mode").GetString());
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.PartyChallengeSessions.SingleAsync();
            session.NextChallengeAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        var hold = await TvAsync(cookie, HttpMethod.Post, $"/api/tv/albums/{album}/party-playback/boundary");
        Assert.Equal("challenge_hold", hold.GetProperty("mode").GetString());
        Assert.Equal(challenge, hold.GetProperty("activeChallenge").GetProperty("id").GetGuid());
        var reconnect = await TvAsync(cookie, HttpMethod.Get, $"/api/tv/albums/{album}/party-playback");
        Assert.Equal(challenge, reconnect.GetProperty("activeChallenge").GetProperty("id").GetGuid());

        var next = await TvAsync(cookie, HttpMethod.Post, $"/api/tv/albums/{album}/party-playback/next");
        Assert.Equal("media", next.GetProperty("mode").GetString());
        var duplicate = await TvAsync(cookie, HttpMethod.Post, $"/api/tv/albums/{album}/party-playback/next");
        Assert.Equal(1, duplicate.GetProperty("completedCount").GetInt32());
        using var verify = _factory.Services.CreateScope();
        Assert.Equal(1, await verify.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyChallengeCompletions.CountAsync());
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
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static async Task EnableGameAsync(HttpClient owner, Guid album, int votes)
    {
        var response = await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 30,
            maxChallengeIntervalSeconds = 60, votesPerGuest = votes,
            maxChallengesPerSession = (int?)null,
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
