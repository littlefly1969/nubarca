using System.Net;
using System.Net.Http.Json;
using NubArca.Api.Access;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Users;

namespace NubArca.Api.Tests.Users;

// The admin Access surface over HTTP: role assignment, the role catalogue, the
// safety guards that keep an installation from locking itself out, and the
// separation between the administrative permissions.
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
    public async Task The_Catalogue_Endpoint_Describes_Grouping_Dependencies_And_Assignability()
    {
        var (_, admin) = await AdminAsync();

        var catalogue = await admin.GetFromJsonAsync<PermissionCatalogResponse>("/api/admin/permissions");

        Assert.NotNull(catalogue);
        Assert.Equal(
            PermissionCatalog.AllKeys,
            catalogue!.Permissions.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());

        var plates = catalogue.Permissions.Single(p => p.Key == Permissions.LaboratoryPlates);
        Assert.Equal(Permissions.LaboratoryAccess, plates.Parent);
        Assert.True(plates.Assignable);

        // Role management is presented but never offered as a checkbox.
        var rolesManage = catalogue.Permissions.Single(p => p.Key == Permissions.AdminRolesManage);
        Assert.False(rolesManage.Assignable);
        Assert.True(rolesManage.Administrative);
    }

    [Fact]
    public async Task The_User_Detail_Carries_No_Permission_List_At_All()
    {
        // The bug this design removes: a user-shaped permission list that went
        // stale the moment the role changed. Permissions come from the ROLE now.
        var (_, admin) = await AdminAsync();
        var targetId = await _factory.SeedUserAsync("no-perm-list@example.com");

        var raw = await admin.GetStringAsync($"/api/admin/users/{targetId}");

        Assert.DoesNotContain("permissions", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inheritedFromRole", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("override", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"role\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_A_Role_Immediately_Changes_What_The_User_May_Do()
    {
        var (_, admin) = await AdminAsync();
        var targetId = await _factory.SeedUserAsync("promoted@example.com");
        await _factory.SetRoleAsync(targetId, RoleKeys.Restricted);
        var target = await _factory.LoginAsync("promoted@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await target.GetAsync("/api/people")).StatusCode);

        var response = await admin.PutAsJsonAsync(
            $"/api/admin/users/{targetId}/role", new { role = RoleKeys.Member });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Same session, no re-login.
        Assert.NotEqual(HttpStatusCode.Forbidden, (await target.GetAsync("/api/people")).StatusCode);
        Assert.Equal(RoleKeys.Member,
            (await response.Content.ReadFromJsonAsync<AdminUserDto>())!.Role);
    }

    [Fact]
    public async Task There_Is_No_Per_User_Permission_Endpoint_Left()
    {
        // The whole model is gone, not merely hidden: a client that remembers
        // the old route finds nothing there.
        var (_, admin) = await AdminAsync();
        var targetId = await _factory.SeedUserAsync("legacy-route@example.com");

        var set = await admin.PutAsJsonAsync(
            $"/api/admin/users/{targetId}/permissions/{Permissions.PeopleAccess}",
            new { effect = "grant" });
        var clear = await admin.DeleteAsync(
            $"/api/admin/users/{targetId}/permissions/{Permissions.PeopleAccess}");

        Assert.Equal(HttpStatusCode.NotFound, set.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, clear.StatusCode);
    }

    [Fact]
    public async Task A_Custom_Role_Can_Be_Assigned_And_Is_Enforced()
    {
        var (_, admin) = await AdminAsync();
        var created = await admin.PostAsJsonAsync("/api/admin/roles", new
        {
            name = "Laboratorio",
            description = "Laboratory-oriented account",
            permissions = new[] { Permissions.LaboratoryAccess, Permissions.LaboratoryPlates },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var role = await created.Content.ReadFromJsonAsync<RoleDto>();

        var targetId = await _factory.SeedUserAsync("lab-user@example.com");
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(
                $"/api/admin/users/{targetId}/role", new { role = role!.Key })).StatusCode);

        var target = await _factory.LoginAsync("lab-user@example.com");
        Assert.NotEqual(HttpStatusCode.Forbidden, (await target.GetAsync("/api/plates/images")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await target.GetAsync("/api/aesthetics-lab/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await target.GetAsync("/api/people")).StatusCode);
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
        // The reason there are five administrative permissions rather than one.
        var (_, client) = await _factory.CreatePermissionClientAsync(
            "jobs-only@example.com", Permissions.AdminJobsManage);

        Assert.NotEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/roles")).StatusCode);
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
