using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Auth;

// The forgot-password flow end to end over HTTP.
//
// Two properties dominate: nothing observable distinguishes a known address
// from an unknown one, and the raw token never exists anywhere but the message
// and the request that spends it.
public sealed class PasswordRecoveryEndpointTests : IDisposable
{
    private const string NewPassword = "brand-new-password-1";

    private readonly SqliteWebApplicationFactory _factory;

    public PasswordRecoveryEndpointTests()
    {
        // A dedicated (non-pooled) host: these tests configure mail and consume
        // the per-email throttle, neither of which may leak into another test.
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Mail:Enabled"] = "true",
            ["Mail:Smtp:Host"] = "smtp.example.com",
            ["Mail:Smtp:Port"] = "587",
            ["Mail:FromAddress"] = "nubarca@example.com",
            ["Mail:PublicOrigin"] = "https://cloud.example.com",
            ["Mail:TokenLifetimeMinutes"] = "30",
            ["Mail:PerEmailPermitLimit"] = "3",
            ["Mail:PerEmailWindowMinutes"] = "15",
            ["RateLimits:PasswordRecovery:PermitLimit"] = "100",
        });
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient Anon() => _factory.CreateClient();

    private async Task<HttpResponseMessage> RequestAsync(HttpClient client, string email) =>
        await client.PostAsJsonAsync("/api/auth/password-recovery/request", new { email });

    private async Task<string> RequestAndReadTokenAsync(string email)
    {
        _factory.EmailSender.Reset();
        var response = await RequestAsync(Anon(), email);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var message = _factory.EmailSender.Last;
        Assert.NotNull(message);
        return RecordingEmailSender.ExtractToken(message!);
    }

    [Fact]
    public async Task Status_Reports_Enabled_And_Discloses_Nothing_Else()
    {
        var response = await Anon().GetAsync("/api/auth/password-recovery/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"enabled\":true", raw);
        // No account information, no configured host, no from-address.
        Assert.DoesNotContain("example.com", raw);
        Assert.DoesNotContain("smtp", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Known_And_Unknown_Addresses_Get_The_Same_Public_Answer()
    {
        await _factory.SeedUserAsync("known@example.com");

        var known = await RequestAsync(Anon(), "known@example.com");
        var unknown = await RequestAsync(Anon(), "nobody@example.com");

        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(
            await known.Content.ReadAsStringAsync(),
            await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_Disabled_Account_Is_Not_Disclosed_And_Gets_No_Email()
    {
        var userId = await _factory.SeedUserAsync("disabled@example.com");
        await _factory.DisableUserAsync(userId);
        _factory.EmailSender.Reset();

        var disabled = await RequestAsync(Anon(), "disabled@example.com");
        var unknown = await RequestAsync(Anon(), "ghost@example.com");

        Assert.Equal(HttpStatusCode.Accepted, disabled.StatusCode);
        Assert.Equal(
            await disabled.Content.ReadAsStringAsync(),
            await unknown.Content.ReadAsStringAsync());
        Assert.Empty(_factory.EmailSender.Messages);
    }

    [Fact]
    public async Task A_Delivery_Failure_Does_Not_Change_The_Public_Answer()
    {
        await _factory.SeedUserAsync("failing@example.com");
        _factory.EmailSender.Reset();
        _factory.EmailSender.FailDelivery = true;

        var response = await RequestAsync(Anon(), "failing@example.com");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        _factory.EmailSender.FailDelivery = false;
    }

    [Fact]
    public async Task Only_A_Hash_Of_The_Token_Is_Stored()
    {
        await _factory.SeedUserAsync("hashed@example.com");
        var rawToken = await RequestAndReadTokenAsync("hashed@example.com");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.PasswordResetTokens.AsNoTracking().ToListAsync();

        var row = Assert.Single(rows);
        Assert.NotEqual(rawToken, row.TokenHash);
        Assert.DoesNotContain(rawToken, row.TokenHash);
        // Lowercase hex SHA-256 and nothing else.
        Assert.Equal(64, row.TokenHash.Length);
        Assert.All(row.TokenHash, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')));
    }

    [Fact]
    public async Task The_Reset_Link_Uses_The_Configured_Origin_And_The_Fragment()
    {
        await _factory.SeedUserAsync("link@example.com");
        _factory.EmailSender.Reset();
        await RequestAsync(Anon(), "link@example.com");

        var body = _factory.EmailSender.Last!.TextBody;

        // The operator's origin, never the request's Host header — an attacker
        // who can set Host must not be able to have the product mail a link to
        // their own server.
        Assert.Contains("https://cloud.example.com/reset-password#token=", body);
        // Fragment, so the token never reaches a reverse-proxy access log.
        Assert.DoesNotContain("/reset-password?token=", body);
        // No existing password, no other secret, no remote image.
        Assert.DoesNotContain("<img", body);
        Assert.DoesNotContain("http://", body);
    }

    [Fact]
    public async Task A_Valid_Token_Changes_The_Password_Old_Fails_New_Works()
    {
        await _factory.SeedUserAsync("reset-me@example.com");
        var token = await RequestAndReadTokenAsync("reset-me@example.com");

        var reset = await Anon().PostAsJsonAsync(
            "/api/auth/password-recovery/reset",
            new { token, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var oldLogin = await Anon().PostAsJsonAsync("/api/auth/login", new
        {
            email = "reset-me@example.com",
            password = SqliteWebApplicationFactory.TestPassword,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await Anon().PostAsJsonAsync("/api/auth/login", new
        {
            email = "reset-me@example.com",
            password = NewPassword,
        });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task A_Reset_Does_Not_Sign_The_Caller_In()
    {
        await _factory.SeedUserAsync("no-autologin@example.com");
        var token = await RequestAndReadTokenAsync("no-autologin@example.com");

        var client = Anon();
        var reset = await client.PostAsJsonAsync(
            "/api/auth/password-recovery/reset",
            new { token, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // The same client — cookie jar and all — is still anonymous.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_Token_Cannot_Be_Used_Twice()
    {
        await _factory.SeedUserAsync("replay@example.com");
        var token = await RequestAndReadTokenAsync("replay@example.com");

        var first = await Anon().PostAsJsonAsync(
            "/api/auth/password-recovery/reset", new { token, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var second = await Anon().PostAsJsonAsync(
            "/api/auth/password-recovery/reset", new { token, newPassword = "another-password-42" });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task An_Invalid_Token_Is_Rejected_The_Same_Way_As_A_Spent_One()
    {
        await _factory.SeedUserAsync("invalid@example.com");
        var token = await RequestAndReadTokenAsync("invalid@example.com");
        await Anon().PostAsJsonAsync(
            "/api/auth/password-recovery/reset", new { token, newPassword = NewPassword });

        var spent = await Anon().PostAsJsonAsync(
            "/api/auth/password-recovery/reset", new { token, newPassword = "yet-another-password-9" });
        var nonsense = await Anon().PostAsJsonAsync(
            "/api/auth/password-recovery/reset",
            new { token = "not-a-token-at-all", newPassword = "yet-another-password-9" });

        Assert.Equal(HttpStatusCode.BadRequest, spent.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
        Assert.Equal(
            await spent.Content.ReadAsStringAsync(),
            await nonsense.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_Expired_Token_Is_Rejected()
    {
        await _factory.SeedUserAsync("expired@example.com");
        var token = await RequestAndReadTokenAsync("expired@example.com");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.PasswordResetTokens.SingleAsync();
            row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var response = await Anon().PostAsJsonAsync(
            "/api/auth/password-recovery/reset", new { token, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Requesting_Again_Invalidates_The_Previous_Link()
    {
        await _factory.SeedUserAsync("superseded@example.com");
        var first = await RequestAndReadTokenAsync("superseded@example.com");
        var second = await RequestAndReadTokenAsync("superseded@example.com");
        Assert.NotEqual(first, second);

        var stale = await Anon().PostAsJsonAsync(
            "/api/auth/password-recovery/reset", new { token = first, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);

        var current = await Anon().PostAsJsonAsync(
            "/api/auth/password-recovery/reset", new { token = second, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, current.StatusCode);
    }

    [Fact]
    public async Task A_Completed_Reset_Invalidates_Every_Outstanding_Token()
    {
        await _factory.SeedUserAsync("sweep@example.com");
        var token = await RequestAndReadTokenAsync("sweep@example.com");

        await Anon().PostAsJsonAsync(
            "/api/auth/password-recovery/reset", new { token, newPassword = NewPassword });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.PasswordResetTokens.AsNoTracking().ToListAsync();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.NotNull(r.UsedAt));
    }

    [Fact]
    public async Task A_Reset_Invalidates_Sessions_Opened_Before_It()
    {
        await _factory.SeedUserAsync("kill-session@example.com");
        var browser = await _factory.LoginAsync("kill-session@example.com");
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/auth/me")).StatusCode);

        var token = await RequestAndReadTokenAsync("kill-session@example.com");
        await Anon().PostAsJsonAsync(
            "/api/auth/password-recovery/reset", new { token, newPassword = NewPassword });

        // The pre-reset cookie carries the old security version and is refused
        // on its very next request, not at the cookie's fourteen-day expiry.
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task The_Password_Policy_Is_Enforced_On_Reset()
    {
        await _factory.SeedUserAsync("weak@example.com");
        var token = await RequestAndReadTokenAsync("weak@example.com");

        var response = await Anon().PostAsJsonAsync(
            "/api/auth/password-recovery/reset", new { token, newPassword = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // …and the token survives, so an honest user who typed a weak password
        // can simply try again with a stronger one.
        var retry = await Anon().PostAsJsonAsync(
            "/api/auth/password-recovery/reset", new { token, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
    }

    [Fact]
    public async Task Changing_The_Password_Normally_Also_Kills_Outstanding_Links()
    {
        await _factory.SeedUserAsync("change@example.com");
        var token = await RequestAndReadTokenAsync("change@example.com");

        var client = await _factory.LoginAsync("change@example.com");
        var change = await client.PostAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = SqliteWebApplicationFactory.TestPassword,
            newPassword = NewPassword,
        });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        var stale = await Anon().PostAsJsonAsync(
            "/api/auth/password-recovery/reset", new { token, newPassword = "third-password-77" });
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
    }
}

// Rate limiting gets its own host, because a tight limiter must not be shared
// with the tests above (which each send several requests).
public sealed class PasswordRecoveryRateLimitTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public PasswordRecoveryRateLimitTests()
    {
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Mail:Enabled"] = "true",
            ["Mail:Smtp:Host"] = "smtp.example.com",
            ["Mail:FromAddress"] = "nubarca@example.com",
            ["Mail:PublicOrigin"] = "https://cloud.example.com",
            ["Mail:PerEmailPermitLimit"] = "2",
            ["Mail:PerEmailWindowMinutes"] = "15",
            ["RateLimits:PasswordRecovery:PermitLimit"] = "3",
            ["RateLimits:PasswordRecovery:WindowSeconds"] = "300",
        });
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_Same_Address_Is_Throttled_Whether_Or_Not_It_Exists()
    {
        var client = _factory.CreateClient();

        // Two permitted, the third refused — for an address that names no
        // account at all, so a 429 tells an enumerator nothing.
        for (var i = 0; i < 2; i++)
        {
            var ok = await client.PostAsJsonAsync(
                "/api/auth/password-recovery/request", new { email = "ghost@example.com" });
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }

        var throttled = await client.PostAsJsonAsync(
            "/api/auth/password-recovery/request", new { email = "ghost@example.com" });
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    [Fact]
    public async Task A_Different_Address_From_The_Same_Host_Hits_The_Ip_Limit()
    {
        var client = _factory.CreateClient();

        // Distinct addresses, so the per-email window never fires; the per-IP
        // policy is what stops the walk at three.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/auth/password-recovery/request", new { email = $"walk-{i}@example.com" });
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }
}

// Recovery must present as unavailable — never as a silent success — when the
// operator has not configured mail. Ordinary authentication is unaffected.
public sealed class PasswordRecoveryDisabledTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public PasswordRecoveryDisabledTests()
    {
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Mail:Enabled"] = "false",
        });
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Status_Reports_Disabled()
    {
        var response = await _factory.CreateClient().GetAsync("/api/auth/password-recovery/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"enabled\":false", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_Request_Still_Answers_Generically_And_Sends_Nothing()
    {
        await _factory.SeedUserAsync("nomail@example.com");
        _factory.EmailSender.Reset();
        _factory.EmailSender.Enabled = false;

        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/api/auth/password-recovery/request", new { email = "nomail@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(_factory.EmailSender.Messages);
    }

    [Fact]
    public async Task Normal_Authentication_Keeps_Working()
    {
        await _factory.SeedUserAsync("still-works@example.com");

        var client = await _factory.LoginAsync("still-works@example.com");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task The_Admin_Manual_Reset_Remains_The_Fallback()
    {
        var (_, adminClient) = await _factory.CreateRoleClientAsync(
            NubArca.Api.Access.RoleKeys.Administrator, "admin-fallback@example.com");
        var targetId = await _factory.SeedUserAsync("needs-reset@example.com");

        var manual = await adminClient.PostAsJsonAsync(
            $"/api/admin/users/{targetId}/password", new { password = "operator-set-password-1" });
        Assert.Equal(HttpStatusCode.NoContent, manual.StatusCode);

        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new
        {
            email = "needs-reset@example.com",
            password = "operator-set-password-1",
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // …and the email route says so plainly to an administrator, who is
        // allowed to know the installation's own configuration.
        var byEmail = await adminClient.PostAsync(
            $"/api/admin/users/{targetId}/password-reset-email", content: null);
        Assert.Equal(HttpStatusCode.Conflict, byEmail.StatusCode);
    }
}
