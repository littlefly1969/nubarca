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
/// Carrying a party that is ALREADY RUNNING across the identity change.
///
/// <para>Before this, the guest cookie was scoped to the path of the capability
/// token that minted it, so one browser could hold a separate participant — and
/// a separate allowance — per capability. An upgrade in the middle of somebody's
/// evening must not notice: nothing is duplicated, and nothing is zeroed.</para>
///
/// <para>Each capability contributes its old row exactly once, on its first
/// request after the change. The first such request has no derived row yet and
/// ADOPTS the old one; later ones find the derived row and FOLD the old one in.
/// Because a capability only ever incremented its own counters, the non-zero
/// counters of two old rows are disjoint and summing is exact.</para>
/// </summary>
public sealed class PartyGuestLegacyCookieTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyGuestLegacyCookieTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_guest_mid_party_keeps_the_allowance_they_had_already_spent()
    {
        var party = await SetUpAsync(messagesPerGuest: 3);
        // A guest as they existed before the change: a row keyed by a plain hash
        // of a capability-scoped token, with two greetings already sent.
        var legacyToken = FakeToken();
        await SeedLegacyAsync(party.LinkId, legacyToken, messages: 2);

        var response = await WithLegacyAsync(
            HttpMethod.Post, $"/api/party/{party.Upload}/messages", legacyToken,
            new { displayName = "Anna", text = "Il terzo" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The third of three, not the first of a fresh budget.
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("messagesRemaining").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // ONE guest: the old row was taken over, not left beside a new one.
        var participant = await db.PartyParticipants.SingleAsync();
        Assert.Equal(3, participant.SubmittedMessageCount);
        Assert.Null(participant.RetiredAt);
        // And it is now reachable by the derived key, so the browser cookie the
        // response issued is what finds it from here on.
        Assert.NotEqual(Sha256Hex(legacyToken), participant.TokenHash);
    }

    [Fact]
    public async Task A_browser_that_had_two_capability_guests_ends_with_one_and_keeps_both_counters()
    {
        var party = await SetUpAsync(messagesPerGuest: 10);
        // The pre-change split this whole change exists to end: the same browser
        // held one guest for uploading and another for the view surface.
        var uploadLegacy = FakeToken();
        var viewLegacy = FakeToken();
        await SeedLegacyAsync(party.LinkId, uploadLegacy, messages: 2, photos: 4);
        await SeedLegacyAsync(party.LinkId, viewLegacy, votes: 3);

        // First post-change request: the upload row is adopted.
        var browser = _factory.CreateClient();
        var first = await WithLegacyAsync(browser,
            HttpMethod.Post, $"/api/party/{party.Upload}/upload-session", uploadLegacy);
        first.EnsureSuccessStatusCode();

        // Second: the browser now has its own cookie AND still sends the other
        // legacy one, which is the only moment both are visible together.
        var second = await WithLegacyAsync(browser,
            HttpMethod.Get, $"/api/party/{party.View}/challenges", viewLegacy);
        second.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var live = await db.PartyParticipants.Where(p => p.RetiredAt == null).SingleAsync();
        // Exact, not generous: each capability's counters were disjoint, so the
        // sum is what the guest actually spent.
        Assert.Equal(2, live.SubmittedMessageCount);
        Assert.Equal(4, live.AcceptedPhotoCount);
        Assert.Equal(3, live.ChallengeVoteCount);

        // The folded row is retired rather than deleted: uploads and votes point
        // at it, and the evening it describes really happened.
        var retired = await db.PartyParticipants.Where(p => p.RetiredAt != null).SingleAsync();
        Assert.NotEqual(live.Id, retired.Id);
    }

    [Fact]
    public async Task A_legacy_row_is_folded_in_once_and_never_twice()
    {
        var party = await SetUpAsync(messagesPerGuest: 10);
        var legacy = FakeToken();
        await SeedLegacyAsync(party.LinkId, legacy, messages: 1);

        var browser = _factory.CreateClient();
        // Establish the derived identity first, so the legacy row is FOLDED
        // rather than adopted — the path that adds counters.
        (await browser.PostAsync($"/api/party/{party.Upload}/upload-session", null))
            .EnsureSuccessStatusCode();
        for (var i = 0; i < 3; i++)
        {
            (await WithLegacyAsync(browser,
                HttpMethod.Post, $"/api/party/{party.Upload}/upload-session", legacy))
                .EnsureSuccessStatusCode();
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var live = await db.PartyParticipants.Where(p => p.RetiredAt == null).SingleAsync();
        // One, not three: a retired row is invisible to every lookup, so it can
        // never be counted a second time.
        Assert.Equal(1, live.SubmittedMessageCount);
    }

    [Fact]
    public async Task A_legacy_cookie_from_another_party_carries_nothing_here()
    {
        var a = await SetUpAsync(messagesPerGuest: 10);
        var b = await SetUpAsync(messagesPerGuest: 10);
        var legacyAtA = FakeToken();
        await SeedLegacyAsync(a.LinkId, legacyAtA, messages: 7);

        // Replayed at party B. It is looked up on B's link, matches nothing, and
        // the guest starts where a guest at B should: at zero.
        var response = await WithLegacyAsync(
            HttpMethod.Post, $"/api/party/{b.Upload}/messages", legacyAtA,
            new { displayName = "Anna", text = "Auguri" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(9, (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("messagesRemaining").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(7, (await db.PartyParticipants
            .SingleAsync(p => p.PartyAlbumLinkId == a.LinkId)).SubmittedMessageCount);
    }

    [Fact]
    public async Task A_legacy_cookie_never_becomes_a_voter_on_its_own()
    {
        // Adoption belongs to establishing a session. A privileged action must
        // not manufacture the identity that authorises it, whatever cookie it
        // happens to be carrying.
        var party = await SetUpAsync(messagesPerGuest: 0, withGame: true);
        var legacy = FakeToken();
        await SeedLegacyAsync(party.LinkId, legacy);
        var round = await OpenVotingAsync(party);

        var refused = await WithLegacyAsync(
            HttpMethod.Post, $"/api/party/{party.View}/game/vote", legacy,
            new { roundId = round, value = "yes" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("not_joined",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyGameVotes.ToListAsync());
    }

    // --- helpers -----------------------------------------------------------

    private sealed record Party(HttpClient Owner, Guid Album, Guid LinkId, string View, string Upload);

    private static string FakeToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)));

    /// A participant exactly as the pre-change code wrote one: keyed by a plain
    /// hash of a capability-scoped token.
    private async Task SeedLegacyAsync(
        Guid linkId, string token, int messages = 0, int photos = 0, int votes = 0)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PartyParticipants.Add(new PartyParticipant
        {
            Id = Guid.NewGuid(),
            PartyAlbumLinkId = linkId,
            TokenHash = Sha256Hex(token),
            SubmittedMessageCount = messages,
            AcceptedPhotoCount = photos,
            ChallengeVoteCount = votes,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> WithLegacyAsync(
        HttpMethod method, string url, string legacyToken, object? body = null) =>
        WithLegacyAsync(_factory.CreateClient(), method, url, legacyToken, body);

    private static Task<HttpResponseMessage> WithLegacyAsync(
        HttpClient client, HttpMethod method, string url, string legacyToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("Cookie", $"NubArca.PartyGuest={legacyToken}");
        return client.SendAsync(request);
    }

    private static async Task<Guid> OpenVotingAsync(Party party)
    {
        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
            version = (await (await party.Owner.PostAsJsonAsync(
                    $"/api/albums/{party.Album}/party-game/commands",
                    new { command, expectedVersion = version }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32();
        return Guid.Empty;
    }

    private async Task<Party> SetUpAsync(int messagesPerGuest, bool withGame = false)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync($"{Guid.NewGuid():N}@example.com");
        var album = (await (await owner.PostAsJsonAsync("/api/albums",
                new { name = $"Festa {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings",
            new { enabled = true, uploadEnabled = true })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-slideshow-settings",
            new { maxMessagesPerParticipant = messagesPerGuest })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 30, maxChallengeIntervalSeconds = 60,
            votesPerGuest = 3, maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();
        if (withGame)
        {
            (await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
            {
                title = "Canta", body = "Sali sul tavolo.", kind = "dare",
                mediaFileItemId = (Guid?)null, isEnabled = true, votingMode = "binary",
            })).EnsureSuccessStatusCode();
        }

        var status = await (await owner.GetAsync($"/api/albums/{album}/party-settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var view = status.GetProperty("partyUrl").GetString()!["/party/".Length..];
        var uploadUrl = status.GetProperty("uploadUrl").GetString()!["/party/".Length..];

        using var scope = _factory.Services.CreateScope();
        var linkId = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyAlbumLinks.AsNoTracking()
            .Where(x => x.AlbumId == album && x.Enabled && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt).Select(x => x.Id).FirstAsync();

        return new Party(owner, album, linkId, view,
            uploadUrl[..uploadUrl.IndexOf("/upload", StringComparison.Ordinal)]);
    }
}
