using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Access;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Auth;

// `/api/auth/me` and the self-service profile: what a user may see about
// themselves, and — more importantly — what they may not change about
// themselves.
public sealed class SelfServiceProfileTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public SelfServiceProfileTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record MeProbe(
        Guid Id,
        string Email,
        string DisplayName,
        string? FirstName,
        string? LastName,
        bool IsAdmin,
        string Role,
        string[] EffectivePermissions,
        string Language,
        string? TimeZone,
        DateTime? LastLoginAt);

    [Fact]
    public async Task Me_Carries_Role_Permissions_And_Profile()
    {
        var (_, client) = await _factory.CreateRoleClientAsync(RoleKeys.Member, "me@example.com");

        var me = await client.GetFromJsonAsync<MeProbe>("/api/auth/me");

        Assert.NotNull(me);
        Assert.Equal("me@example.com", me!.Email);
        Assert.Equal(RoleKeys.Member, me.Role);
        Assert.False(me.IsAdmin);
        Assert.NotEmpty(me.EffectivePermissions);
        Assert.Contains(Permissions.PeopleAccess, me.EffectivePermissions);
        Assert.DoesNotContain(Permissions.AdminUsersManage, me.EffectivePermissions);
        // Deterministic order, so two responses for the same authority compare.
        Assert.Equal(
            me.EffectivePermissions.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            me.EffectivePermissions);
    }

    [Fact]
    public async Task Me_Never_Leaks_Credential_Or_Session_Internals()
    {
        var (_, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Administrator, "leak-check@example.com");

        var raw = await client.GetStringAsync("/api/auth/me");

        foreach (var forbidden in new[]
        {
            "passwordHash", "PasswordHash", "tokenHash", "TokenHash",
            "securityVersion", "SecurityVersion", "smtp", "Smtp",
            "permissionOverride", "userPermissionOverride",
        })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Administrator_Sees_The_Whole_Catalogue_And_The_Compatibility_Flag()
    {
        var (_, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Administrator, "admin-me@example.com");

        var me = await client.GetFromJsonAsync<MeProbe>("/api/auth/me");

        Assert.True(me!.IsAdmin);
        Assert.Equal(RoleKeys.Administrator, me.Role);
        Assert.Equal(PermissionCatalog.AllKeys, me.EffectivePermissions);
    }

    [Fact]
    public async Task A_User_Can_Edit_Their_Own_Profile()
    {
        var (_, client) = await _factory.CreateRoleClientAsync(RoleKeys.Member, "profile@example.com");

        var response = await client.PutAsJsonAsync("/api/auth/me/profile", new
        {
            displayName = "  Renamed Owner  ",
            firstName = "Ada",
            lastName = "Lovelace",
            language = "en",
            timeZone = "Europe/Rome",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<MeProbe>();
        Assert.Equal("Renamed Owner", me!.DisplayName);
        Assert.Equal("Ada", me.FirstName);
        Assert.Equal("Lovelace", me.LastName);
        Assert.Equal("en", me.Language);
        Assert.Equal("Europe/Rome", me.TimeZone);
    }

    [Fact]
    public async Task An_Empty_Optional_Field_Clears_It_While_Null_Leaves_It_Alone()
    {
        var (_, client) = await _factory.CreateRoleClientAsync(RoleKeys.Member, "clear@example.com");
        await client.PutAsJsonAsync("/api/auth/me/profile", new
        {
            firstName = "Ada",
            lastName = "Lovelace",
            timeZone = "Europe/Rome",
        });

        var cleared = await client.PutAsJsonAsync("/api/auth/me/profile", new
        {
            firstName = "",
            timeZone = "",
        });

        var me = await cleared.Content.ReadFromJsonAsync<MeProbe>();
        Assert.Null(me!.FirstName);
        Assert.Null(me.TimeZone);
        // Untouched, because the request said nothing about it.
        Assert.Equal("Lovelace", me.LastName);
    }

    [Fact]
    public async Task An_Unknown_Time_Zone_Is_Rejected()
    {
        var (_, client) = await _factory.CreateRoleClientAsync(RoleKeys.Member, "zone@example.com");

        var response = await client.PutAsJsonAsync(
            "/api/auth/me/profile", new { timeZone = "Mars/Olympus_Mons" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_Profile_Endpoint_Cannot_Change_Role_Permissions_Disabled_Or_Email()
    {
        // The request record has no such fields, so extra JSON members are
        // simply ignored. This asserts that ignoring them is what happens —
        // silently accepting one would be a privilege-escalation path.
        var (userId, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Restricted, "escalate@example.com");

        var payload = new StringContent(
            """
            {
              "displayName": "Sneaky",
              "role": "Administrator",
              "roleKey": "Administrator",
              "isAdmin": true,
              "effectivePermissions": ["admin.users.manage"],
              "permissions": ["admin.users.manage"],
              "disabledAt": null,
              "email": "attacker@example.com",
              "securityVersion": 99
            }
            """,
            Encoding.UTF8,
            "application/json");

        var response = await client.PutAsync("/api/auth/me/profile", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var me = await response.Content.ReadFromJsonAsync<MeProbe>();
        Assert.Equal("Sneaky", me!.DisplayName);
        Assert.Equal(RoleKeys.Restricted, me.Role);
        Assert.False(me.IsAdmin);
        Assert.Empty(me.EffectivePermissions);
        Assert.Equal("escalate@example.com", me.Email);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal(RoleKeys.Restricted, row.RoleKey);
        Assert.Equal("escalate@example.com", row.Email);
        Assert.Null(row.DisabledAt);
        Assert.Empty(await db.UserPermissionOverrides.AsNoTracking().ToListAsync());

        // …and the admin API is still shut.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Last_Login_Is_Stamped_By_An_Interactive_Login_Only()
    {
        var userId = await _factory.SeedUserAsync("lastlogin@example.com");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Null((await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).LastLoginAt);
        }

        var client = await _factory.LoginAsync("lastlogin@example.com");
        DateTime afterLogin;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
            Assert.NotNull(row.LastLoginAt);
            afterLogin = row.LastLoginAt!.Value;
        }

        // Ordinary authenticated traffic must not move it.
        for (var i = 0; i < 3; i++)
        {
            await client.GetAsync("/api/auth/me");
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
            Assert.Equal(afterLogin, row.LastLoginAt);
        }
    }

    [Fact]
    public async Task Changing_A_Password_Stamps_PasswordChangedAt()
    {
        var userId = await _factory.SeedUserAsync("stamped@example.com");
        var client = await _factory.LoginAsync("stamped@example.com");

        var change = await client.PostAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = SqliteWebApplicationFactory.TestPassword,
            newPassword = "a-brand-new-password-1",
        });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.NotNull(row.PasswordChangedAt);
    }
}
