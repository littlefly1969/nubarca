using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tv;

namespace NubArca.Api.Tests.Tv;

// The DIRECTIONAL unlock code (dpad-v1) and the transition away from the
// retired numeric PIN (pin-v1).
//
// The defect these exist for is a physical one: with a visible numeric keypad,
// the focus ring walked from key to key as the code was entered, so anyone who
// could see the television could read the secret. Masking the digits never
// helped. The remedy is an alphabet the remote enters BLIND, which is only safe
// if the server treats the two schemes as strictly separate credentials rather
// than as two spellings of one.
public sealed class TvPersonalDpadCodeTests : IDisposable
{
    private const string Code = "URDLSUDLR";
    private const string OtherCode = "SSSUUUDDD";
    private const string LegacyPin = "123456";

    private readonly SqliteWebApplicationFactory _factory = new();

    public TvPersonalDpadCodeTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    // ── entropy and format ──────────────────────────────────────────────────

    [Fact]
    public void The_Code_Space_Is_Larger_Than_The_Numeric_Pin_It_Replaces()
    {
        // 5 symbols ^ 9 presses = 1,953,125, against 10^6 for a 6-digit PIN.
        // Moving to an alphabet that can be entered without looking at the
        // screen must not buy that property with entropy.
        var space = Math.Pow(TvPersonalAreaService.DpadAlphabet.Length,
            TvPersonalAreaService.DpadCodeLength);
        Assert.Equal(1_953_125d, space);
        Assert.True(space > Math.Pow(10, TvPersonalAreaService.PinLength));
    }

    [Fact]
    public void The_Alphabet_Is_Only_The_Five_Blind_Findable_Buttons()
    {
        // MENU, BACK, HOME, the microphone and the transport keys carry system
        // or navigation meaning and can never be spent as secret symbols.
        Assert.Equal("UDLRS", TvPersonalAreaService.DpadAlphabet);
        Assert.Equal(5, TvPersonalAreaService.DpadAlphabet.Length);
    }

    [Theory]
    [InlineData("URDLSUDL")]     // eight symbols
    [InlineData("URDLSUDLRU")]   // ten symbols
    [InlineData("URDLSUDL1")]    // a digit is not a direction
    [InlineData("URDLSUDLX")]    // outside the alphabet
    [InlineData("URDLS UDL")]    // whitespace is never a separator
    [InlineData("URDL-SUDL")]    // nor is punctuation
    [InlineData("")]
    [InlineData(null)]
    public void Malformed_Codes_Are_Refused(string? code)
    {
        Assert.Null(TvPersonalAreaService.NormalizeDpadCode(code));
        Assert.False(TvPersonalAreaService.IsValidDpadCodeFormat(code));
    }

    [Fact]
    public void Case_Is_Normalized_But_Nothing_Else_Is()
    {
        // A permissive parser here would let two different keystroke sequences
        // hash to the same secret.
        Assert.Equal(Code, TvPersonalAreaService.NormalizeDpadCode(Code.ToLowerInvariant()));
        Assert.Null(TvPersonalAreaService.NormalizeDpadCode($" {Code} "));
    }

    // ── owner-side configuration ────────────────────────────────────────────

    [Fact]
    public async Task Configuring_A_Code_Stores_Only_A_Hash_And_Never_Echoes_The_Secret()
    {
        var (userId, owner) = await _factory.CreateAuthenticatedClientAsync();

        var response = await owner.PostAsJsonAsync(
            "/api/tv-personal/tv-code", new { code = Code, confirmCode = Code });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Code, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("hash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("generation", raw, StringComparison.OrdinalIgnoreCase);
        var dto = JsonDocument.Parse(raw).RootElement;
        Assert.True(dto.GetProperty("configured").GetBoolean());
        Assert.Equal(TvPersonalSecretSchemes.Dpad, dto.GetProperty("scheme").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.TvPersonalPins.SingleAsync(p => p.OwnerUserId == userId);
        Assert.Equal(TvPersonalSecretSchemes.Dpad, row.Scheme);
        // PBKDF2 output, not the plaintext and not a bare digest of it.
        Assert.DoesNotContain(Code, row.PinHash, StringComparison.Ordinal);
        Assert.DoesNotContain(Code.ToLowerInvariant(), row.PinHash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_Mismatched_Confirmation_Is_Refused_Even_When_Only_The_Case_Differs()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var mismatch = await owner.PostAsJsonAsync(
            "/api/tv-personal/tv-code", new { code = Code, confirmCode = OtherCode });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Contains("code_mismatch", await mismatch.Content.ReadAsStringAsync());

        // Case-insensitive equality is the CORRECT behaviour: both sides
        // normalize to the same secret, so refusing would be a false negative.
        var sameSecret = await owner.PostAsJsonAsync(
            "/api/tv-personal/tv-code",
            new { code = Code, confirmCode = Code.ToLowerInvariant() });
        Assert.Equal(HttpStatusCode.OK, sameSecret.StatusCode);
    }

    [Fact]
    public async Task Changing_The_Code_Revokes_Every_Live_Grant_And_Requires_The_New_One()
    {
        var (userId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);
        var staleGrant = await UnlockTokenAsync(cookie, Code);

        var changed = await owner.PostAsJsonAsync(
            "/api/tv-personal/tv-code", new { code = OtherCode, confirmCode = OtherCode });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.TvPersonalPins.SingleAsync(p => p.OwnerUserId == userId);
            Assert.Equal(2, row.Generation);
            Assert.True(await db.TvPersonalUnlockGrants.AllAsync(g => g.RevokedAt != null));
        }

        // The stale grant fails IMMEDIATELY with the distinct reason, not merely
        // at expiry, so the television can show "the code was changed".
        var stale = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/home", staleGrant);
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        Assert.Contains("pin_changed", await stale.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Forbidden, (await UnlockAsync(cookie, Code)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UnlockAsync(cookie, OtherCode)).StatusCode);
    }

    // ── TV-side unlock ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_Correct_Code_Unlocks_And_A_Wrong_One_Does_Not()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);

        Assert.Equal(HttpStatusCode.Forbidden, (await UnlockAsync(cookie, OtherCode)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UnlockAsync(cookie, Code)).StatusCode);
        // Lower case is the same secret: a remote cannot produce case, so the
        // client's spelling must not matter.
        Assert.Equal(HttpStatusCode.OK,
            (await UnlockAsync(cookie, Code.ToLowerInvariant())).StatusCode);
    }

    [Fact]
    public async Task Failures_Are_Progressively_Throttled_And_Never_Permanent()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);

        for (var i = 0; i < TvPersonalAreaService.FreeAttempts; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await UnlockAsync(cookie, OtherCode)).StatusCode);
        }
        // Now in cooldown: even the CORRECT code is refused with 429 + a
        // Retry-After, which is what actually bounds online guessing against a
        // 1.95M space.
        var throttled = await UnlockAsync(cookie, Code);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.TvSessions.SingleAsync();
        Assert.NotNull(session.PersonalPinLockedUntil);
        // Bounded: never a permanent lockout of the owner's own television.
        Assert.True(session.PersonalPinLockedUntil <= DateTime.UtcNow.Add(TvPersonalAreaService.MaxCooldown));
    }

    [Fact]
    public async Task The_Raw_Code_Is_Never_Persisted_Anywhere()
    {
        var (userId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);
        await UnlockTokenAsync(cookie, Code);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.TvPersonalPins.SingleAsync(p => p.OwnerUserId == userId);
        Assert.DoesNotContain(Code, row.PinHash, StringComparison.OrdinalIgnoreCase);

        // Not in the audit trail either: an audited payload naming the code (or
        // even its length) would move the leak from the screen to the log.
        var audits = await db.AuditLogs.AsNoTracking()
            .Select(a => a.MetadataJson).ToListAsync();
        foreach (var payload in audits)
        {
            Assert.DoesNotContain(Code, payload ?? "", StringComparison.OrdinalIgnoreCase);
        }

        // And no grant row carries it.
        var grants = await db.TvPersonalUnlockGrants.AsNoTracking()
            .Select(g => g.TokenHash).ToListAsync();
        foreach (var hash in grants)
        {
            Assert.DoesNotContain(Code, hash, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── the legacy transition ───────────────────────────────────────────────

    [Fact]
    public async Task A_Legacy_Numeric_Row_Still_Unlocks_And_Reports_Its_Scheme()
    {
        // An already-paired television must not stop working the moment this
        // ships. The row keeps verifying; the STATUS is what tells the TV it can
        // no longer offer an entry surface for it.
        var (userId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);
        await DowngradeToLegacyPinAsync(userId, LegacyPin);

        var status = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/status");
        var dto = JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement;
        Assert.True(dto.GetProperty("pinConfigured").GetBoolean());
        Assert.Equal(TvPersonalSecretSchemes.LegacyPin, dto.GetProperty("scheme").GetString());

        // The numeric secret still verifies (the `pin` field of the request body
        // exists only for the previous native contract).
        var unlocked = await TvSendAsync(
            cookie, HttpMethod.Post, "/api/tv/personal/unlock", json: new { pin = LegacyPin });
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
    }

    [Fact]
    public async Task A_Directional_Code_Cannot_Unlock_A_Legacy_Row_And_Vice_Versa()
    {
        // The schemes are separate credentials, not two spellings of one. Which
        // format is even eligible is decided by the STORED scheme, never by the
        // shape of the input — otherwise a client could probe which one an
        // account holds by watching which errors it gets.
        var (userId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);
        await DowngradeToLegacyPinAsync(userId, LegacyPin);

        var wrongScheme = await UnlockAsync(cookie, Code);
        Assert.Equal(HttpStatusCode.Forbidden, wrongScheme.StatusCode);
        // Indistinguishable from a plain wrong secret.
        var plainWrong = await TvSendAsync(
            cookie, HttpMethod.Post, "/api/tv/personal/unlock", json: new { pin = "654321" });
        Assert.Equal(HttpStatusCode.Forbidden, plainWrong.StatusCode);
        Assert.Equal(
            await wrongScheme.Content.ReadAsStringAsync(),
            await plainWrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Configuring_A_Directional_Code_Replaces_A_Legacy_Row_In_One_Transaction()
    {
        var (userId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);
        await DowngradeToLegacyPinAsync(userId, LegacyPin);
        var legacyGrant = await UnlockTokenAsync(cookie, LegacyPin, legacy: true);

        var upgraded = await owner.PostAsJsonAsync(
            "/api/tv-personal/tv-code", new { code = Code, confirmCode = Code });
        Assert.Equal(HttpStatusCode.OK, upgraded.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.TvPersonalPins.SingleAsync(p => p.OwnerUserId == userId);
            // ONE row, now describing the new scheme — never two live schemes.
            Assert.Equal(TvPersonalSecretSchemes.Dpad, row.Scheme);
            Assert.True(await db.TvPersonalUnlockGrants.AllAsync(g => g.RevokedAt != null));
        }

        // The crossover inherits the ordinary change semantics: the old secret
        // stops working and every outstanding grant is dead.
        var stale = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/home", legacyGrant);
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        var oldPin = await TvSendAsync(
            cookie, HttpMethod.Post, "/api/tv/personal/unlock", json: new { pin = LegacyPin });
        Assert.Equal(HttpStatusCode.Forbidden, oldPin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UnlockAsync(cookie, Code)).StatusCode);
    }

    [Fact]
    public async Task No_Endpoint_Can_Create_A_Numeric_Pin_Any_More()
    {
        // The retired owner-side WRITE route is gone (the GET on that path is
        // still the status read, so posting to it is a 405 rather than a 404).
        // The only creation paths left — pairing approval and the account page —
        // both write a directional code.
        var (userId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var legacyRoute = await owner.PostAsJsonAsync(
            "/api/tv-personal/pin", new { pin = LegacyPin, confirmPin = LegacyPin });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, legacyRoute.StatusCode);

        await PairAsync(owner);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.TvPersonalPins.SingleAsync(p => p.OwnerUserId == userId);
        Assert.Equal(TvPersonalSecretSchemes.Dpad, row.Scheme);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task<string> PairAsync(HttpClient owner)
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
                personalCode = Code,
                personalCodeConfirmation = Code,
            })).EnsureSuccessStatusCode();

        var poll = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        poll.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var response = await tvClient.SendAsync(poll);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single();
    }

    private Task<HttpResponseMessage> UnlockAsync(string cookie, string code)
        => TvSendAsync(cookie, HttpMethod.Post, "/api/tv/personal/unlock", json: new { code });

    private async Task<string> UnlockTokenAsync(string cookie, string secret, bool legacy = false)
    {
        var response = legacy
            ? await TvSendAsync(cookie, HttpMethod.Post, "/api/tv/personal/unlock",
                json: new { pin = secret })
            : await UnlockAsync(cookie, secret);
        response.EnsureSuccessStatusCode();
        var dto = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return dto.GetProperty("unlockToken").GetString()!;
    }

    private Task<HttpResponseMessage> TvSendAsync(
        string cookie, HttpMethod method, string url, string? grant = null, object? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(cookie)}");
        if (grant is not null) request.Headers.Add(TvPersonalAreaService.UnlockHeader, grant);
        if (json is not null) request.Content = JsonContent.Create(json);
        return _factory.CreateClient().SendAsync(request);
    }

    // Rewrite the owner's credential row into the retired numeric scheme, which
    // is what a database upgraded from before this change actually looks like:
    // the migration backfills every existing row to "pin-v1". No API can
    // produce this state any more, so it has to be constructed directly.
    private async Task DowngradeToLegacyPinAsync(Guid ownerUserId, string pin)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<TvPersonalPin>>();
        var row = await db.TvPersonalPins.SingleAsync(p => p.OwnerUserId == ownerUserId);
        row.Scheme = TvPersonalSecretSchemes.LegacyPin;
        row.PinHash = hasher.HashPassword(row, pin);
        await db.SaveChangesAsync();
    }

    private static string CookieValue(string setCookie)
    {
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }
}
