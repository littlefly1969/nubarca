using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tv;

namespace NubArca.Api.Tests.Tv;

public sealed class TvPairingTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public TvPairingTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Start_Creates_Pending_ShortLived_Request_With_Hashed_Secret()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/tv/pairing/start", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<TvPairingStartedDto>())!;
        Assert.Equal(8, dto.PublicCode.Length);
        Assert.True(dto.ExpiresAt > DateTime.UtcNow);
        Assert.True(dto.ExpiresAt < DateTime.UtcNow.AddMinutes(11));
        Assert.Contains("/tv/pair?", dto.ApprovalUrl);
        Assert.Contains("#secret=", dto.ApprovalUrl);
        Assert.DoesNotContain("&secret=", dto.ApprovalUrl, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(dto.PairingSecret), dto.ApprovalUrl);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.TvPairingRequests.SingleAsync();
        Assert.Equal(TvPairingStatuses.Pending, stored.Status);
        Assert.Equal(HashToken(dto.PairingSecret), stored.SecretHash);
        Assert.DoesNotContain(dto.PairingSecret, stored.SecretHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approval_Requires_Normal_Authenticated_User()
    {
        var (started, _) = await StartAsync();
        var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new { pairingSecret = started.PairingSecret });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_Secret_And_Expired_Request_Cannot_Be_Approved()
    {
        await _factory.SeedUserAsync();
        var userClient = await _factory.LoginAsync();
        var (started, _) = await StartAsync();

        var invalid = await userClient.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new { pairingSecret = "not-the-secret" });
        Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
        var invalidCode = await userClient.PostAsJsonAsync(
            "/api/tv/pairing/ZZZZZZZZ/approve",
            new { pairingSecret = started.PairingSecret });
        Assert.Equal(HttpStatusCode.NotFound, invalidCode.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pairing = await db.TvPairingRequests.SingleAsync();
            pairing.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        var expired = await userClient.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new { pairingSecret = started.PairingSecret });
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
    }

    [Fact]
    public async Task Pending_Status_Does_Not_Leak_Owner_Or_Internal_Data()
    {
        var (started, tvClient) = await StartAsync();
        var response = await PollAsync(tvClient, started);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("pending", raw);
        AssertNoInternals(raw);
        Assert.DoesNotContain("owner", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Approved_Poll_Sets_Distinct_Secure_PathScoped_Tv_Cookie_And_Hashes_Token()
    {
        var (started, tvClient) = await StartAsync();
        await _factory.SeedUserAsync();
        var userClient = await _factory.LoginAsync();
        var approved = await userClient.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                personalPin = "123456",
                personalPinConfirmation = "123456",
            });
        approved.EnsureSuccessStatusCode();

        var poll = await PollAsync(tvClient, started);
        poll.EnsureSuccessStatusCode();
        var setCookie = poll.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith($"{TvPairingService.CookieName}=", setCookie, StringComparison.Ordinal);
        Assert.DoesNotContain("NubArca.Auth=", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/tv", setCookie, StringComparison.OrdinalIgnoreCase);

        var rawToken = CookieValue(setCookie);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.TvSessions.SingleAsync();
        Assert.Equal(HashToken(rawToken), session.SessionTokenHash);
        Assert.DoesNotContain(rawToken, session.SessionTokenHash, StringComparison.Ordinal);
    }

    // ── atomic first pairing (approval + PIN commit together) ───────────────

    [Fact]
    public async Task Owner_Without_Pin_Cannot_Approve_Without_Creating_One()
    {
        var (started, tvClient) = await StartAsync();
        await _factory.SeedUserAsync();
        var userClient = await _factory.LoginAsync();

        var response = await userClient.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new { pairingSecret = started.PairingSecret });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("pin_required", await response.Content.ReadAsStringAsync());

        // Nothing committed: the pairing stays PENDING, the TV keeps polling
        // without ever receiving a session cookie, and no session row exists —
        // an abandoned flow cannot produce a usable (Party or Personal) TV.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(TvPairingStatuses.Pending, (await db.TvPairingRequests.SingleAsync()).Status);
            Assert.False(await db.TvSessions.AnyAsync());
            Assert.False(await db.TvPersonalPins.AnyAsync());
        }
        var poll = await PollAsync(tvClient, started);
        poll.EnsureSuccessStatusCode();
        Assert.False(poll.Headers.Contains("Set-Cookie"));
        Assert.Contains("pending", await poll.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("12345", "12345", "invalid_pin")]     // too short
    [InlineData("12345a", "12345a", "invalid_pin")]   // non-numeric
    [InlineData("123456", "654321", "pin_mismatch")]  // mismatch
    public async Task Malformed_Or_Mismatched_Pin_Leaves_The_Pairing_Pending(
        string pin, string confirmation, string expectedError)
    {
        var (started, _) = await StartAsync();
        await _factory.SeedUserAsync();
        var userClient = await _factory.LoginAsync();

        var response = await userClient.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                personalPin = pin,
                personalPinConfirmation = confirmation,
            });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedError, await response.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(TvPairingStatuses.Pending, (await db.TvPairingRequests.SingleAsync()).Status);
        Assert.False(await db.TvPersonalPins.AnyAsync());
    }

    [Fact]
    public async Task First_Approval_Commits_Pin_And_Approval_Atomically()
    {
        var (started, tvClient) = await StartAsync();
        var userId = await _factory.SeedUserAsync();
        var userClient = await _factory.LoginAsync();

        var response = await userClient.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                personalPin = "123456",
                personalPinConfirmation = "123456",
            });
        response.EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(TvPairingStatuses.Approved, (await db.TvPairingRequests.SingleAsync()).Status);
            var pin = await db.TvPersonalPins.SingleAsync(p => p.OwnerUserId == userId);
            Assert.Equal(1, pin.Generation);
            Assert.DoesNotContain("123456", pin.PinHash, StringComparison.Ordinal);
        }

        // The TV completes the pairing and gets its session.
        var poll = await PollAsync(tvClient, started);
        poll.EnsureSuccessStatusCode();
        Assert.True(poll.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Approval_Never_Replaces_An_Existing_Pin()
    {
        await _factory.SeedUserAsync();
        var userClient = await _factory.LoginAsync();
        (await userClient.PostAsJsonAsync(
            "/api/tv-personal/pin", new { pin = "654321", confirmPin = "654321" }))
            .EnsureSuccessStatusCode();

        string hashBefore;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            hashBefore = (await db.TvPersonalPins.SingleAsync()).PinHash;
        }

        // Approving a second TV — even with DIFFERENT pin fields supplied —
        // must not touch the owner-level PIN (no per-TV PINs, no silent reset).
        var (started, _) = await StartAsync();
        var response = await userClient.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                personalPin = "111111",
                personalPinConfirmation = "111111",
            });
        response.EnsureSuccessStatusCode();

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await verifyDb.TvPersonalPins.SingleAsync();
        Assert.Equal(hashBefore, row.PinHash);
        Assert.Equal(1, row.Generation);
    }

    [Fact]
    public async Task Poll_Refuses_A_Session_When_The_Owner_Pin_Vanished()
    {
        // Defensive invariant guard: approval committed a PIN, but the row was
        // removed before the TV claimed the pairing (manual intervention /
        // corrupted data). No session may be minted for a PIN-less owner —
        // the pairing expires and the TV starts over.
        var (started, tvClient) = await StartAsync();
        await _factory.SeedUserAsync();
        var userClient = await _factory.LoginAsync();
        (await userClient.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                personalPin = "123456",
                personalPinConfirmation = "123456",
            })).EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.TvPersonalPins.ExecuteDeleteAsync();
        }

        var poll = await PollAsync(tvClient, started);
        poll.EnsureSuccessStatusCode();
        Assert.False(poll.Headers.Contains("Set-Cookie"));
        Assert.Contains("expired", await poll.Content.ReadAsStringAsync());

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verifyDb.TvSessions.AnyAsync());
    }

    [Fact]
    public async Task Tv_Cookie_Can_Access_Only_Tv_Session_Endpoint_Not_Owner_Apis()
    {
        var cookie = await PairAndGetCookieAsync();
        var tvClient = _factory.CreateClient();

        var session = await SendWithCookieAsync(tvClient, HttpMethod.Get, "/api/tv/session", cookie);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        AssertNoInternals(await session.Content.ReadAsStringAsync());

        var me = await SendWithCookieAsync(tvClient, HttpMethod.Get, "/api/auth/me", cookie);
        var files = await SendWithCookieAsync(tvClient, HttpMethod.Get, "/api/folders/children", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, files.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_Updates_LastSeen()
    {
        var cookie = await PairAndGetCookieAsync();
        DateTime before;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.TvSessions.SingleAsync();
            before = session.LastSeenAt;
            session.LastSeenAt = before.AddMinutes(-2);
            await db.SaveChangesAsync();
        }

        var response = await SendWithCookieAsync(
            _factory.CreateClient(), HttpMethod.Post, "/api/tv/session/heartbeat", cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True((await verifyDb.TvSessions.SingleAsync()).LastSeenAt > before.AddMinutes(-2));
    }

    [Fact]
    public async Task Revoked_And_Expired_Tv_Sessions_Fail()
    {
        var cookie = await PairAndGetCookieAsync();
        var client = _factory.CreateClient();
        var revoke = await SendWithCookieAsync(client, HttpMethod.Delete, "/api/tv/session", cookie);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        var revoked = await SendWithCookieAsync(client, HttpMethod.Get, "/api/tv/session", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

        var secondCookie = await PairAndGetCookieAsync("second@example.com");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hash = HashToken(CookieValue(secondCookie));
            var session = await db.TvSessions.SingleAsync(x => x.SessionTokenHash == hash);
            session.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        var expired = await SendWithCookieAsync(
            client, HttpMethod.Get, "/api/tv/session", secondCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    private async Task<(TvPairingStartedDto Started, HttpClient TvClient)> StartAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/tv/pairing/start", null);
        response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<TvPairingStartedDto>())!, client);
    }

    private static Task<HttpResponseMessage> PollAsync(HttpClient client, TvPairingStartedDto started)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        request.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        return client.SendAsync(request);
    }

    private async Task<string> PairAndGetCookieAsync(string email = "owner@example.com")
    {
        var (started, tvClient) = await StartAsync();
        await _factory.SeedUserAsync(email);
        var userClient = await _factory.LoginAsync(email);
        (await userClient.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                personalPin = "123456",
                personalPinConfirmation = "123456",
            })).EnsureSuccessStatusCode();
        var poll = await PollAsync(tvClient, started);
        poll.EnsureSuccessStatusCode();
        return poll.Headers.GetValues("Set-Cookie").Single();
    }

    private static Task<HttpResponseMessage> SendWithCookieAsync(
        HttpClient client, HttpMethod method, string url, string setCookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(setCookie)}");
        return client.SendAsync(request);
    }

    private static string CookieValue(string setCookie)
    {
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static void AssertNoInternals(string raw)
    {
        string[] forbidden =
        [
            "StorageKey", "BlobObjectId", "TokenHash", "SecretHash", "SessionTokenHash",
            "PayloadJson", "PasswordHash", "metadataJson", "OwnerUserId", "ApprovedByUserId",
        ];
        foreach (var needle in forbidden)
        {
            Assert.DoesNotContain(needle, raw, StringComparison.OrdinalIgnoreCase);
        }
    }
}
