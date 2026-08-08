using System.Net;
using System.Net.Http.Json;
using NubArca.Api.Access;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Auth;

// How quickly a change reaches a session that is already open.
//
// The two mechanisms are deliberately different, and this file is where that
// difference is pinned down:
//   * a CREDENTIAL change bumps User.SecurityVersion, and the cookie carries
//     the version it was minted with, so older sessions are rejected;
//   * a ROLE or PERMISSION change needs no version bump at all, because the
//     authorization handler reads the database on every request.
public sealed class SecurityVersionSessionTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public SecurityVersionSessionTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Changing_Your_Own_Password_Signs_Out_Your_Other_Devices_But_Not_This_One()
    {
        await _factory.SeedUserAsync("two-devices@example.com");
        var laptop = await _factory.LoginAsync("two-devices@example.com");
        var phone = await _factory.LoginAsync("two-devices@example.com");

        var change = await laptop.PostAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = SqliteWebApplicationFactory.TestPassword,
            newPassword = "changed-from-the-laptop-1",
        });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        // The browser that performed the change is re-issued a cookie at the
        // new version: you do not sign yourself out by changing your password.
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/auth/me")).StatusCode);
        // Every other session is gone on its next request.
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task An_Admin_Password_Reset_Signs_The_Target_Out_Everywhere()
    {
        var (_, adminClient) = await _factory.CreateRoleClientAsync(
            RoleKeys.Administrator, "resetter@example.com");
        var targetId = await _factory.SeedUserAsync("target-session@example.com");
        var target = await _factory.LoginAsync("target-session@example.com");
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/auth/me")).StatusCode);

        var reset = await adminClient.PostAsJsonAsync(
            $"/api/admin/users/{targetId}/password", new { password = "operator-chosen-password-1" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_Role_Change_Takes_Effect_Without_A_Relogin()
    {
        var (userId, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Restricted, "promote-live@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/people")).StatusCode);

        await _factory.SetRoleAsync(userId, RoleKeys.Member);

        // Same cookie, same session — the next request already sees it.
        Assert.NotEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/people")).StatusCode);
        var me = await client.GetStringAsync("/api/auth/me");
        Assert.Contains(RoleKeys.Member, me, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Permission_Change_Takes_Effect_Without_A_Relogin()
    {
        var (userId, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Member, "revoke-live@example.com");
        Assert.NotEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/private-vault")).StatusCode);

        // Editing the ROLE, not the user: the account this session belongs to
        // loses the capability on its very next request, with no re-login and
        // no session invalidation.
        await _factory.SetRolePermissionsAsync(
            RoleKeys.Member,
            RoleDefaults.MemberPermissions.Where(k => k != Permissions.PrivateVaultAccess).ToArray());
        _ = userId;

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/private-vault")).StatusCode);
    }

    [Fact]
    public async Task Disabling_A_User_Still_Ends_Their_Session_Promptly()
    {
        var (userId, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Member, "to-disable@example.com");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        await _factory.DisableUserAsync(userId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_Recovered_Password_Cannot_Be_Reused_By_The_Old_Session()
    {
        // Belt and braces alongside the recovery suite: the property matters
        // enough to be asserted from the session side as well.
        await _factory.SeedUserAsync("compromised@example.com");
        var attacker = await _factory.LoginAsync("compromised@example.com");

        var owner = await _factory.LoginAsync("compromised@example.com");
        var change = await owner.PostAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = SqliteWebApplicationFactory.TestPassword,
            newPassword = "kicked-you-out-1",
        });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.GetAsync("/api/auth/me")).StatusCode);
    }
}
