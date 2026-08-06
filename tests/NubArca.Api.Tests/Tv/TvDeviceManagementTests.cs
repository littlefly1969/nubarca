using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tv;

namespace NubArca.Api.Tests.Tv;

// Owner-side TV device/session management: list + revoke, owner-scoped, and the
// effect of an owner revoke on the limited TV session.
public sealed class TvDeviceManagementTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public TvDeviceManagementTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task List_Requires_Authentication()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/tv-devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.DeleteAsync($"/api/tv-devices/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Owner_Lists_Only_Own_Sessions_With_Safe_Fields()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("owner@example.com");
        await PairTvAsync(owner);

        // A second owner with their own paired TV.
        var (_, other) = await _factory.CreateAuthenticatedClientAsync("other@example.com");
        await PairTvAsync(other);

        var response = await owner.GetAsync("/api/tv-devices");
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();
        var devices = (await owner.GetFromJsonAsync<List<TvDeviceDto>>("/api/tv-devices"))!;

        Assert.Single(devices);
        Assert.Equal("active", devices[0].Status);
        AssertNoInternals(raw);
    }

    [Fact]
    public async Task Owner_Revoke_Marks_Session_Revoked_And_Is_Idempotent()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        await PairTvAsync(owner);
        var sessionId = await SingleSessionIdAsync(ownerId);

        var revoke = await owner.DeleteAsync($"/api/tv-devices/{sessionId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.NotNull((await db.TvSessions.SingleAsync(x => x.Id == sessionId)).RevokedAt);
        }

        // Idempotent: revoking again still succeeds.
        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.DeleteAsync($"/api/tv-devices/{sessionId}")).StatusCode);

        var devices = (await owner.GetFromJsonAsync<List<TvDeviceDto>>("/api/tv-devices"))!;
        Assert.Equal("revoked", devices.Single(d => d.Id == sessionId).Status);
    }

    [Fact]
    public async Task Owner_Cannot_Revoke_Another_Owners_Session()
    {
        var (aliceId, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        await PairTvAsync(alice);
        var aliceSession = await SingleSessionIdAsync(aliceId);

        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        // Bob cannot see Alice's session, nor revoke it (generic 404).
        var bobDevices = (await bob.GetFromJsonAsync<List<TvDeviceDto>>("/api/tv-devices"))!;
        Assert.Empty(bobDevices);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.DeleteAsync($"/api/tv-devices/{aliceSession}")).StatusCode);

        // Alice's session is untouched.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null((await db.TvSessions.SingleAsync(x => x.Id == aliceSession)).RevokedAt);
    }

    [Fact]
    public async Task Owner_Revoke_Immediately_Blocks_The_Tv_Session()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);

        // Give the owner a ShowOnTv album so /api/tv/albums has something to hide.
        var albumId = await CreateShowOnTvAlbumAsync(owner);

        // Before revoke: the TV session works.
        Assert.Equal(HttpStatusCode.OK, (await TvGet("/api/tv/session", cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TvGet("/api/tv/albums", cookie)).StatusCode);

        // Owner revokes.
        var sessionId = await SingleSessionIdAsync(ownerId);
        (await owner.DeleteAsync($"/api/tv-devices/{sessionId}")).EnsureSuccessStatusCode();

        // After revoke: every TV endpoint fails immediately. The session is
        // invalid, so even the album-items endpoint is Unauthorized (the session
        // check precedes the album/ShowOnTv check).
        Assert.Equal(HttpStatusCode.Unauthorized, (await TvGet("/api/tv/session", cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TvGet("/api/tv/albums", cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await TvGet($"/api/tv/albums/{albumId}/items", cookie)).StatusCode);
    }

    [Fact]
    public async Task Tv_Self_SignOut_Still_Works()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);

        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Delete, "/api/tv/session");
        req.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(cookie)}");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(req)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await TvGet("/api/tv/session", cookie)).StatusCode);
    }

    // --- helpers ---

    private async Task<string> PairTvAsync(HttpClient owner)
    {
        var tvClient = _factory.CreateClient();
        var start = await tvClient.PostAsync("/api/tv/pairing/start", null);
        start.EnsureSuccessStatusCode();
        var started = (await start.Content.ReadFromJsonAsync<TvPairingStartedDto>())!;

        (await owner.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                // Atomic first pairing: approval creates the owner's PIN when
                // missing; ignored for owners who already have one.
                personalPin = "123456",
                personalPinConfirmation = "123456",
            })).EnsureSuccessStatusCode();

        var poll = new HttpRequestMessage(HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        poll.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var pollResp = await tvClient.SendAsync(poll);
        pollResp.EnsureSuccessStatusCode();
        return pollResp.Headers.GetValues("Set-Cookie").Single();
    }

    private async Task<Guid> CreateShowOnTvAlbumAsync(HttpClient owner)
    {
        var create = await owner.PostAsJsonAsync("/api/albums", new { name = "On TV" });
        create.EnsureSuccessStatusCode();
        var detail = await create.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var albumId = detail.GetProperty("id").GetGuid();
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = true }))
            .EnsureSuccessStatusCode();
        return albumId;
    }

    private async Task<Guid> SingleSessionIdAsync(Guid ownerUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.TvSessions.SingleAsync(x => x.OwnerUserId == ownerUserId)).Id;
    }

    private Task<HttpResponseMessage> TvGet(string url, string setCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(setCookie)}");
        return _factory.CreateClient().SendAsync(request);
    }

    private static string CookieValue(string setCookie)
    {
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }

    private static void AssertNoInternals(string raw)
    {
        string[] forbidden =
        [
            "SessionTokenHash", "TokenHash", "pairingSecret", "SecretHash",
            "OwnerUserId", "StorageKey", "BlobObjectId",
        ];
        foreach (var needle in forbidden)
        {
            Assert.DoesNotContain(needle, raw, StringComparison.OrdinalIgnoreCase);
        }
    }
}
