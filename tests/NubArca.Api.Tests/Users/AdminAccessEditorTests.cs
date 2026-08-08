using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Access;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Users;

namespace NubArca.Api.Tests.Users;

// The admin Access surface over HTTP: role changes, per-user overrides, the
// safety guards that keep an installation from locking itself out, and the
// separation between the four administrative permissions.
public sealed class AdminAccessEditorTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public AdminAccessEditorTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private Task<(Guid UserId, HttpClient Client)> AdminAsync(string email = "root@example.com") =>
        _factory.CreateRoleClientAsync(RoleKeys.Administrator, email);

    [Fact]
    public async Task The_Catalogue_Endpoint_Describes_Roles_Permissions_And_Baselines()
    {
        var (_, admin) = await AdminAsync();

        var catalogue = await admin.GetFromJsonAsync<PermissionCatalogResponse>("/api/admin/permissions");

        Assert.NotNull(catalogue);
        Assert.Equal(RoleKeys.All, catalogue!.Roles);
        Assert.Equal(
            PermissionCatalog.AllKeys,
            catalogue.Permissions.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(PermissionCatalog.AllKeys, catalogue.RoleBaselines[RoleKeys.Administrator]);
        Assert.Empty(catalogue.RoleBaselines[RoleKeys.Restricted]);
    }

    [Fact]
    public async Task The_Detail_View_Separates_Inherited_Granted_And_Denied()
    {
        var (_, admin) = await AdminAsync();
        var targetId = await _factory.SeedUserAsync("breakdown@example.com");
        await _factory.SetRoleAsync(targetId, RoleKeys.Member);
        await _factory.SetPermissionOverrideAsync(
            targetId, Permissions.PeopleAccess, NubArca.Api.Domain.PermissionEffect.Deny);
        await _factory.SetPermissionOverrideAsync(
            targetId, Permissions.AdminJobsManage, NubArca.Api.Domain.PermissionEffect.Grant);

        var detail = await admin.GetFromJsonAsync<AdminUserDetailDto>($"/api/admin/users/{targetId}");

        var people = detail!.Permissions.Single(p => p.Key == Permissions.PeopleAccess);
        Assert.True(people.InheritedFromRole);
        Assert.Equal("deny", people.Override);
        Assert.False(people.Effective);

        var jobs = detail.Permissions.Single(p => p.Key == Permissions.AdminJobsManage);
        Assert.False(jobs.InheritedFromRole);
        Assert.Equal("grant", jobs.Override);
        Assert.True(jobs.Effective);

        var vault = detail.Permissions.Single(p => p.Key == Permissions.PrivateVaultAccess);
        Assert.True(vault.InheritedFromRole);
        Assert.Null(vault.Override);
        Assert.True(vault.Effective);
    }

    [Fact]
    public async Task An_Override_Can_Be_Set_And_Then_Cleared()
    {
        var (_, admin) = await AdminAsync();
        var targetId = await _factory.SeedUserAsync("toggle@example.com");
        await _factory.SetRoleAsync(targetId, RoleKeys.Restricted);

        var granted = await admin.PutAsJsonAsync(
            $"/api/admin/users/{targetId}/permissions/{Permissions.PeopleAccess}",
            new { effect = "grant" });
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        var afterGrant = await granted.Content.ReadFromJsonAsync<AdminUserDetailDto>();
        Assert.True(afterGrant!.Permissions.Single(p => p.Key == Permissions.PeopleAccess).Effective);

        var cleared = await admin.DeleteAsync(
            $"/api/admin/users/{targetId}/permissions/{Permissions.PeopleAccess}");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var afterClear = await cleared.Content.ReadFromJsonAsync<AdminUserDetailDto>();
        var row = afterClear!.Permissions.Single(p => p.Key == Permissions.PeopleAccess);
        Assert.Null(row.Override);
        Assert.False(row.Effective);
    }

    [Fact]
    public async Task An_Unknown_Permission_Key_Is_Rejected_And_Never_Stored()
    {
        var (_, admin) = await AdminAsync();
        var targetId = await _factory.SeedUserAsync("unknown-key@example.com");

        var response = await admin.PutAsJsonAsync(
            $"/api/admin/users/{targetId}/permissions/definitely.not.a.permission",
            new { effect = "grant" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.UserPermissionOverrides.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task An_Unknown_Effect_Is_Rejected()
    {
        var (_, admin) = await AdminAsync();
        var targetId = await _factory.SeedUserAsync("bad-effect@example.com");

        var response = await admin.PutAsJsonAsync(
            $"/api/admin/users/{targetId}/permissions/{Permissions.PeopleAccess}",
            new { effect = "maybe" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_Administrator_Cannot_Be_Denied_An_Administrative_Permission()
    {
        var (_, admin) = await AdminAsync();
        var otherAdminId = await _factory.SeedUserAsync("peer-admin@example.com");
        await _factory.SetRoleAsync(otherAdminId, RoleKeys.Administrator);
        var peer = await _factory.LoginAsync("peer-admin@example.com");

        var response = await admin.PutAsJsonAsync(
            $"/api/admin/users/{otherAdminId}/permissions/{Permissions.AdminUsersManage}",
            new { effect = "deny" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await peer.GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task The_Last_Administrator_Cannot_Be_Demoted()
    {
        var (adminId, admin) = await AdminAsync("sole@example.com");
        var otherAdminId = await _factory.SeedUserAsync("second@example.com");
        await _factory.SetRoleAsync(otherAdminId, RoleKeys.Administrator);

        // Demote the second administrator: allowed, two exist.
        var first = await admin.PutAsJsonAsync(
            $"/api/admin/users/{otherAdminId}/role", new { role = RoleKeys.Member });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Now the caller is the only one left; self-demotion is refused, which
        // also happens to be the only way the last one could be demoted.
        var second = await admin.PutAsJsonAsync(
            $"/api/admin/users/{adminId}/role", new { role = RoleKeys.Member });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task An_Administrator_Cannot_Disable_Their_Own_Account()
    {
        var (adminId, admin) = await AdminAsync("self-disable@example.com");

        var response = await admin.PutAsJsonAsync(
            $"/api/admin/users/{adminId}/disabled", new { disabled = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task An_Unknown_Role_Is_Rejected()
    {
        var (_, admin) = await AdminAsync();
        var targetId = await _factory.SeedUserAsync("bad-role@example.com");

        var response = await admin.PutAsJsonAsync(
            $"/api/admin/users/{targetId}/role", new { role = "Superuser" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_Administrator_Can_Edit_Another_Users_Profile()
    {
        var (_, admin) = await AdminAsync();
        var targetId = await _factory.SeedUserAsync("editable@example.com");

        var response = await admin.PutAsJsonAsync($"/api/admin/users/{targetId}", new
        {
            displayName = "Edited Name",
            firstName = "Grace",
            lastName = "Hopper",
            language = "en",
            timeZone = "Europe/Rome",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminUserDto>();
        Assert.Equal("Edited Name", dto!.DisplayName);
        Assert.Equal("Grace", dto.FirstName);
        Assert.Equal("Hopper", dto.LastName);
        Assert.Equal("Europe/Rome", dto.TimeZone);
        // Email is the login and recovery identity; the admin editor does not
        // change it either, because that needs a verification workflow.
        Assert.Equal("editable@example.com", dto.Email);
    }

    [Fact]
    public async Task Holding_One_Admin_Permission_Does_Not_Open_Another_Admin_Api()
    {
        // The reason there are four administrative permissions rather than one.
        var (userId, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Restricted, "jobs-only@example.com");
        await _factory.SetPermissionOverrideAsync(
            userId, Permissions.AdminJobsManage, NubArca.Api.Domain.PermissionEffect.Grant);

        Assert.NotEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/import/roots")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/admin/storage-stats?physical=false")).StatusCode);
    }

    [Fact]
    public async Task The_Admin_User_Projection_Never_Carries_Credential_Internals()
    {
        var (_, admin) = await AdminAsync();
        var targetId = await _factory.SeedUserAsync("projection@example.com");

        var raw = await admin.GetStringAsync($"/api/admin/users/{targetId}");

        foreach (var forbidden in new[]
        {
            "passwordHash", "PasswordHash", "securityVersion", "SecurityVersion",
            "tokenHash", "TokenHash",
        })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.Ordinal);
        }
        Assert.Contains("hasPassword", raw, StringComparison.Ordinal);
    }
}
