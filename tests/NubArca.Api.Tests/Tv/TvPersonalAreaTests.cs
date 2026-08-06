using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tv;

namespace NubArca.Api.Tests.Tv;

// TV Personal Area: owner-side PIN creation (authenticated pairing flow) and
// TV-side unlock/lock/status/home. Personal access always requires BOTH the
// limited TV session cookie AND a server-side unlock grant; wrong/malformed/
// missing PIN collapse into one generic 403 and failures are progressively
// throttled per session.
public sealed class TvPersonalAreaTests : IDisposable
{
    private const string Pin = "123456";

    // The standard pooled factory raises the per-IP bucket so these tests can
    // exercise the per-session progressive cooldown deterministically.
    private readonly SqliteWebApplicationFactory _factory = new();

    public TvPersonalAreaTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    // ── owner-side PIN management (set / change) ────────────────────────────

    [Fact]
    public async Task Owner_Can_Create_A_Missing_Pin_And_It_Is_Stored_Hashed()
    {
        var (userId, owner) = await _factory.CreateAuthenticatedClientAsync();

        var before = await owner.GetFromJsonAsync<JsonElement>("/api/tv-personal/pin");
        Assert.False(before.GetProperty("configured").GetBoolean());

        var created = await owner.PostAsJsonAsync(
            "/api/tv-personal/pin", new { pin = Pin, confirmPin = Pin });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var createdRaw = await created.Content.ReadAsStringAsync();
        var createdDto = JsonDocument.Parse(createdRaw).RootElement;
        Assert.True(createdDto.GetProperty("configured").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, createdDto.GetProperty("updatedAt").ValueKind);
        // Never the hash, salt, generation, attempts, or grant details.
        Assert.DoesNotContain("hash", createdRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("generation", createdRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Pin, createdRaw, StringComparison.Ordinal);

        var after = await owner.GetFromJsonAsync<JsonElement>("/api/tv-personal/pin");
        Assert.True(after.GetProperty("configured").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("updatedAt").ValueKind);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.TvPersonalPins.SingleAsync(p => p.OwnerUserId == userId);
        Assert.DoesNotContain(Pin, row.PinHash, StringComparison.Ordinal);
        Assert.Equal(1, row.Generation);
    }

    [Theory]
    [InlineData("12345")]      // too short
    [InlineData("1234567")]    // too long
    [InlineData("12345a")]     // non-numeric
    [InlineData("")]           // empty
    public async Task Invalid_Pin_Formats_Are_Rejected(string pin)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var response = await owner.PostAsJsonAsync(
            "/api/tv-personal/pin", new { pin, confirmPin = pin });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Mismatched_Confirmation_Is_Rejected()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var response = await owner.PostAsJsonAsync(
            "/api/tv-personal/pin", new { pin = Pin, confirmPin = "654321" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Changing_The_Pin_Bumps_Generation_Revokes_Grants_And_Requires_The_New_Pin()
    {
        var (userId, owner) = await _factory.CreateAuthenticatedClientAsync();
        await CreatePinAsync(owner);
        var cookie = await PairTvAsync(owner);
        var staleToken = await UnlockTokenAsync(cookie);

        var changed = await owner.PostAsJsonAsync(
            "/api/tv-personal/pin", new { pin = "999999", confirmPin = "999999" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.TvPersonalPins.SingleAsync(p => p.OwnerUserId == userId);
            Assert.Equal(2, row.Generation);
            Assert.DoesNotContain("999999", row.PinHash, StringComparison.Ordinal);
            Assert.True(await db.TvPersonalUnlockGrants.AllAsync(g => g.RevokedAt != null));
        }

        // The stale grant fails IMMEDIATELY with the distinct pin_changed reason
        // (clients show the "PIN was changed" notice, pairing stays valid).
        var staleHome = await HomeAsync(cookie, staleToken);
        Assert.Equal(HttpStatusCode.Forbidden, staleHome.StatusCode);
        Assert.Contains("pin_changed", await staleHome.Content.ReadAsStringAsync());

        // The old PIN no longer unlocks; the new one does; Party never blinks.
        var oldPin = await UnlockAsync(cookie, Pin);
        Assert.Equal(HttpStatusCode.Forbidden, oldPin.StatusCode);
        var newPin = await UnlockAsync(cookie, "999999");
        Assert.Equal(HttpStatusCode.OK, newPin.StatusCode);
        var albums = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/albums");
        Assert.Equal(HttpStatusCode.OK, albums.StatusCode);
    }

    [Fact]
    public async Task Pin_Change_Does_Not_Affect_Another_Owners_Tv()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var bobCookie = await PairTvAsync(bob);
        var bobToken = await UnlockTokenAsync(bobCookie);

        (await alice.PostAsJsonAsync(
            "/api/tv-personal/pin", new { pin = "222222", confirmPin = "222222" }))
            .EnsureSuccessStatusCode();

        // Bob's grant and PIN are untouched by Alice's change.
        var bobHome = await HomeAsync(bobCookie, bobToken);
        Assert.Equal(HttpStatusCode.OK, bobHome.StatusCode);
    }

    [Fact]
    public async Task Pin_Management_Requires_Owner_Authentication()
    {
        var anonymous = _factory.CreateClient();
        var status = await anonymous.GetAsync("/api/tv-personal/pin");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        var create = await anonymous.PostAsJsonAsync(
            "/api/tv-personal/pin", new { pin = Pin, confirmPin = Pin });
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
    }

    // ── unlock ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Valid_Session_And_Correct_Pin_Return_A_Grant_Stored_Hashed()
    {
        var (userId, owner) = await _factory.CreateAuthenticatedClientAsync();
        await CreatePinAsync(owner);
        var cookie = await PairTvAsync(owner);

        var response = await UnlockAsync(cookie, Pin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        var raw = await response.Content.ReadAsStringAsync();
        var dto = JsonDocument.Parse(raw).RootElement;
        var token = dto.GetProperty("unlockToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(dto.GetProperty("expiresAt").GetDateTime() > DateTime.UtcNow);
        // No PIN, hash, or internals in the response.
        Assert.DoesNotContain("pinHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Pin, raw, StringComparison.Ordinal);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var grant = await db.TvPersonalUnlockGrants.SingleAsync(g => g.RevokedAt == null);
        Assert.Equal(userId, grant.OwnerUserId);
        Assert.DoesNotContain(token, grant.TokenHash, StringComparison.Ordinal);
        Assert.Equal(TvPersonalAreaService.HashToken(token), grant.TokenHash);
    }

    [Fact]
    public async Task Wrong_Pin_And_No_Pin_Configured_Return_The_Same_Generic_Failure()
    {
        // Owner A has a PIN; owner B is a LEGACY inconsistency (paired before
        // the atomic flow / corrupted data — simulated by removing the PIN row).
        // Both failures must be byte-identical: no PIN-state oracle.
        var (_, withPin) = await _factory.CreateAuthenticatedClientAsync("a@example.com");
        var cookieWithPin = await PairTvAsync(withPin);
        var wrongPin = await UnlockAsync(cookieWithPin, "000000");

        var (bobId, withoutPin) = await _factory.CreateAuthenticatedClientAsync("b@example.com");
        var cookieWithoutPin = await PairTvAsync(withoutPin);
        await DeletePinRowAsync(bobId);
        var noPin = await UnlockAsync(cookieWithoutPin, Pin);

        Assert.Equal(HttpStatusCode.Forbidden, wrongPin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noPin.StatusCode);
        Assert.Equal(
            await wrongPin.Content.ReadAsStringAsync(),
            await noPin.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unlock_Without_Tv_Session_Is_Unauthorized()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/api/tv/personal/unlock", new { pin = Pin });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Owner_Web_Authentication_Alone_Cannot_Call_The_Tv_Personal_Api()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await CreatePinAsync(owner);

        // The owner cookie client has NO TV session cookie: every TV personal
        // endpoint must refuse it outright.
        var unlock = await owner.PostAsJsonAsync("/api/tv/personal/unlock", new { pin = Pin });
        Assert.Equal(HttpStatusCode.Unauthorized, unlock.StatusCode);
        var status = await owner.GetAsync("/api/tv/personal/status");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        var home = await owner.GetAsync("/api/tv/personal/home");
        Assert.Equal(HttpStatusCode.Unauthorized, home.StatusCode);
    }

    [Fact]
    public async Task Repeated_Failures_Trigger_A_Progressive_Bounded_Cooldown()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await CreatePinAsync(owner);
        var cookie = await PairTvAsync(owner);

        // The first free attempts fail generically without a cooldown.
        for (var i = 0; i < TvPersonalAreaService.FreeAttempts - 1; i++)
        {
            var failure = await UnlockAsync(cookie, "000000");
            Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
        }

        // The threshold failure arms the cooldown …
        var arming = await UnlockAsync(cookie, "000000");
        Assert.Equal(HttpStatusCode.Forbidden, arming.StatusCode);

        // … so now even the CORRECT PIN is throttled with Retry-After.
        var throttled = await UnlockAsync(cookie, Pin);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        var retryAfter = throttled.Headers.GetValues("Retry-After").Single();
        Assert.InRange(int.Parse(retryAfter), 1, 30);

        // After the cooldown elapses another failure doubles it (progressive).
        await ExpireCooldownAsync();
        var failureAfterCooldown = await UnlockAsync(cookie, "000000");
        Assert.Equal(HttpStatusCode.Forbidden, failureAfterCooldown.StatusCode);
        var throttledAgain = await UnlockAsync(cookie, Pin);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttledAgain.StatusCode);
        var secondRetry = int.Parse(throttledAgain.Headers.GetValues("Retry-After").Single());
        Assert.InRange(secondRetry, 31, 60);

        // The cooldown is bounded — never a permanent lockout.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.TvSessions.SingleAsync();
        Assert.True(session.PersonalPinLockedUntil <= DateTime.UtcNow.AddMinutes(16));
    }

    [Fact]
    public async Task Attempts_During_An_Active_Cooldown_Do_Not_Extend_It()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);

        for (var i = 0; i < TvPersonalAreaService.FreeAttempts; i++)
        {
            await UnlockAsync(cookie, "000000");
        }

        DateTime? lockedUntil;
        int attempts;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.TvSessions.SingleAsync();
            lockedUntil = session.PersonalPinLockedUntil;
            attempts = session.PersonalPinFailedAttempts;
            Assert.NotNull(lockedUntil);
        }

        // Hammering the button during Retry-After answers 429 every time but
        // must not push the lock further out or count as new failures.
        for (var i = 0; i < 4; i++)
        {
            var throttled = await UnlockAsync(cookie, i % 2 == 0 ? "000000" : Pin);
            Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.TvSessions.SingleAsync();
            Assert.Equal(lockedUntil, session.PersonalPinLockedUntil);
            Assert.Equal(attempts, session.PersonalPinFailedAttempts);
        }
    }

    [Fact]
    public async Task Pin_Change_Clears_The_Cooldown_State_Of_The_Owners_Sessions()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);

        for (var i = 0; i < TvPersonalAreaService.FreeAttempts; i++)
        {
            await UnlockAsync(cookie, "000000");
        }
        var throttled = await UnlockAsync(cookie, Pin);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        // Changing the PIN is the owner's recovery path from a lockout.
        (await owner.PostAsJsonAsync(
            "/api/tv-personal/pin", new { pin = "999999", confirmPin = "999999" }))
            .EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.TvSessions.SingleAsync();
            Assert.Equal(0, session.PersonalPinFailedAttempts);
            Assert.Null(session.PersonalPinLockedUntil);
        }

        var unlocked = await UnlockAsync(cookie, "999999");
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
    }

    [Fact]
    public async Task Successful_Unlock_Resets_The_Failure_State()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await CreatePinAsync(owner);
        var cookie = await PairTvAsync(owner);

        for (var i = 0; i < TvPersonalAreaService.FreeAttempts; i++)
        {
            await UnlockAsync(cookie, "000000");
        }
        await ExpireCooldownAsync();

        var unlocked = await UnlockAsync(cookie, Pin);
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.TvSessions.SingleAsync();
        Assert.Equal(0, session.PersonalPinFailedAttempts);
        Assert.Null(session.PersonalPinLockedUntil);
    }

    // ── personal endpoint authorization ─────────────────────────────────────

    [Fact]
    public async Task Personal_Home_Requires_A_Grant_And_Returns_Minimal_Data()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await CreatePinAsync(owner);
        var cookie = await PairTvAsync(owner);

        // Session alone (no grant) is NOT enough.
        var locked = await HomeAsync(cookie, grant: null);
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        // Garbage grant is not enough either.
        var garbage = await HomeAsync(cookie, grant: "not-a-grant");
        Assert.Equal(HttpStatusCode.Forbidden, garbage.StatusCode);

        var token = await UnlockTokenAsync(cookie);
        var home = await HomeAsync(cookie, token);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        AssertNoStore(home);
        var raw = await home.Content.ReadAsStringAsync();
        var dto = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("Owner", dto.GetProperty("displayName").GetString());
        Assert.True(dto.GetProperty("galleryAvailable").GetBoolean());
        // Minimal by design: no email/id/roles, no internals.
        Assert.DoesNotContain("email", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ownerUserId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isAdmin", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_Grant_From_Another_Session_Is_Denied()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await CreatePinAsync(owner);
        var cookieA = await PairTvAsync(owner);
        var cookieB = await PairTvAsync(owner);

        var tokenA = await UnlockTokenAsync(cookieA);

        // Session B presenting session A's grant must be refused.
        var crossed = await HomeAsync(cookieB, tokenA);
        Assert.Equal(HttpStatusCode.Forbidden, crossed.StatusCode);
        // The rightful session still works.
        var own = await HomeAsync(cookieA, tokenA);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task An_Expired_Grant_Is_Denied()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await CreatePinAsync(owner);
        var cookie = await PairTvAsync(owner);
        var token = await UnlockTokenAsync(cookie);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var grant = await db.TvPersonalUnlockGrants.SingleAsync();
            grant.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        var home = await HomeAsync(cookie, token);
        Assert.Equal(HttpStatusCode.Forbidden, home.StatusCode);
    }

    [Fact]
    public async Task Lock_Revokes_The_Grant_And_Is_Idempotent()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await CreatePinAsync(owner);
        var cookie = await PairTvAsync(owner);
        var token = await UnlockTokenAsync(cookie);

        var first = await LockAsync(cookie);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var second = await LockAsync(cookie);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        // The revoked grant is unusable afterwards.
        var home = await HomeAsync(cookie, token);
        Assert.Equal(HttpStatusCode.Forbidden, home.StatusCode);
    }

    [Fact]
    public async Task A_New_Unlock_Revokes_The_Previous_Grant_Of_The_Same_Session()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await CreatePinAsync(owner);
        var cookie = await PairTvAsync(owner);

        var stale = await UnlockTokenAsync(cookie);
        var fresh = await UnlockTokenAsync(cookie);

        var staleHome = await HomeAsync(cookie, stale);
        Assert.Equal(HttpStatusCode.Forbidden, staleHome.StatusCode);
        var freshHome = await HomeAsync(cookie, fresh);
        Assert.Equal(HttpStatusCode.OK, freshHome.StatusCode);
    }

    [Fact]
    public async Task A_Pin_Generation_Bump_Invalidates_Outstanding_Grants()
    {
        var (userId, owner) = await _factory.CreateAuthenticatedClientAsync();
        await CreatePinAsync(owner);
        var cookie = await PairTvAsync(owner);
        var token = await UnlockTokenAsync(cookie);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pin = await db.TvPersonalPins.SingleAsync(p => p.OwnerUserId == userId);
            pin.Generation += 1; // future change-PIN flow
            await db.SaveChangesAsync();
        }

        var home = await HomeAsync(cookie, token);
        Assert.Equal(HttpStatusCode.Forbidden, home.StatusCode);
    }

    // ── pairing revocation ──────────────────────────────────────────────────

    [Fact]
    public async Task Pairing_Revocation_Invalidates_Personal_Access_Immediately()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await CreatePinAsync(owner);
        var cookie = await PairTvAsync(owner);
        var token = await UnlockTokenAsync(cookie);

        var devices = await owner.GetFromJsonAsync<JsonElement>("/api/tv-devices");
        var sessionId = devices[0].GetProperty("id").GetGuid();
        (await owner.DeleteAsync($"/api/tv-devices/{sessionId}")).EnsureSuccessStatusCode();

        // Everything personal answers 401 (session gone), even with the grant.
        var home = await HomeAsync(cookie, token);
        Assert.Equal(HttpStatusCode.Unauthorized, home.StatusCode);
        var status = await StatusAsync(cookie, token);
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        var unlock = await UnlockAsync(cookie, Pin);
        Assert.Equal(HttpStatusCode.Unauthorized, unlock.StatusCode);
    }

    // ── Party stays PIN-free ────────────────────────────────────────────────

    [Fact]
    public async Task Party_Endpoints_Remain_Available_Without_Personal_Unlock()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        await CreatePinAsync(owner);
        var cookie = await PairTvAsync(owner);

        var albums = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/albums");
        Assert.Equal(HttpStatusCode.OK, albums.StatusCode);
    }

    // ── status ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_Reports_Pin_Configuration_And_Unlock_State()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        // Atomic pairing: a freshly paired session ALWAYS has a configured PIN.
        var cookie = await PairTvAsync(owner);

        var locked = await StatusAsync(cookie, grant: null);
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        AssertNoStore(locked);
        var dto1 = JsonDocument.Parse(await locked.Content.ReadAsStringAsync()).RootElement;
        Assert.True(dto1.GetProperty("pinConfigured").GetBoolean());
        Assert.False(dto1.GetProperty("unlocked").GetBoolean());

        var token = await UnlockTokenAsync(cookie);
        var unlocked = await StatusAsync(cookie, token);
        var dto2 = JsonDocument.Parse(await unlocked.Content.ReadAsStringAsync()).RootElement;
        Assert.True(dto2.GetProperty("pinConfigured").GetBoolean());
        Assert.True(dto2.GetProperty("unlocked").GetBoolean());

        // Legacy/corrupted state (PIN row gone): pinConfigured=false is the
        // defensive signal the clients turn into the "pairing is incomplete"
        // recovery — never a normal onboarding branch anymore.
        await DeletePinRowAsync(ownerId);
        var legacy = await StatusAsync(cookie, token);
        var dto3 = JsonDocument.Parse(await legacy.Content.ReadAsStringAsync()).RootElement;
        Assert.False(dto3.GetProperty("pinConfigured").GetBoolean());
        Assert.False(dto3.GetProperty("unlocked").GetBoolean());
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static async Task CreatePinAsync(HttpClient owner)
        => (await owner.PostAsJsonAsync(
            "/api/tv-personal/pin", new { pin = Pin, confirmPin = Pin })).EnsureSuccessStatusCode();

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

        var pollRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        pollRequest.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var poll = await tvClient.SendAsync(pollRequest);
        poll.EnsureSuccessStatusCode();
        return poll.Headers.GetValues("Set-Cookie").Single();
    }

    private Task<HttpResponseMessage> UnlockAsync(string setCookie, string pin)
        => TvSendAsync(setCookie, HttpMethod.Post, "/api/tv/personal/unlock",
            json: new { pin });

    private async Task<string> UnlockTokenAsync(string setCookie)
    {
        var response = await UnlockAsync(setCookie, Pin);
        response.EnsureSuccessStatusCode();
        var dto = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return dto.GetProperty("unlockToken").GetString()!;
    }

    private Task<HttpResponseMessage> LockAsync(string setCookie)
        => TvSendAsync(setCookie, HttpMethod.Post, "/api/tv/personal/lock");

    private Task<HttpResponseMessage> HomeAsync(string setCookie, string? grant)
        => TvSendAsync(setCookie, HttpMethod.Get, "/api/tv/personal/home", grant);

    private Task<HttpResponseMessage> StatusAsync(string setCookie, string? grant)
        => TvSendAsync(setCookie, HttpMethod.Get, "/api/tv/personal/status", grant);

    private Task<HttpResponseMessage> TvSendAsync(
        string setCookie, HttpMethod method, string url, string? grant = null, object? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(setCookie)}");
        if (grant is not null)
        {
            request.Headers.Add(TvPersonalAreaService.UnlockHeader, grant);
        }
        if (json is not null)
        {
            request.Content = JsonContent.Create(json);
        }
        return _factory.CreateClient().SendAsync(request);
    }

    // Simulate the LEGACY/corrupted "paired without PIN" state (pre-atomic
    // pairings): the invariant makes it unreachable through the API now.
    private async Task DeletePinRowAsync(Guid ownerUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.TvPersonalPins
            .Where(p => p.OwnerUserId == ownerUserId)
            .ExecuteDeleteAsync();
    }

    // Rewind an active per-session cooldown so the next attempt is admitted
    // (the throttle STATE — the failure count — is deliberately preserved).
    private async Task ExpireCooldownAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.TvSessions.SingleAsync();
        session.PersonalPinLockedUntil = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
    }

    private static void AssertNoStore(HttpResponseMessage response)
        => Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");

    private static string CookieValue(string setCookie)
    {
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }
}
