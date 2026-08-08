using System.Net;
using System.Net.Http.Json;
using NubArca.Api.Access;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Users;

namespace NubArca.Api.Tests.Users;

// Roles as an API: what an administrator can create, edit, duplicate and
// delete — and the four things nobody can do, whatever they send.
public sealed class AdminRoleEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public AdminRoleEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private Task<(Guid UserId, HttpClient Client)> AdminAsync(string email = "root@example.com") =>
        _factory.CreateRoleClientAsync(RoleKeys.Administrator, email);

    private static object Role(string name, params string[] permissions) =>
        new { name, description = (string?)null, permissions };

    // ---------------------------------------------------------------- reading

    [Fact]
    public async Task The_Three_Built_In_Roles_Are_Listed_With_Counts_And_Permissions()
    {
        var (_, admin) = await AdminAsync();

        var listed = await admin.GetFromJsonAsync<ListRolesResponse>("/api/admin/roles");

        Assert.NotNull(listed);
        Assert.Equal(RoleKeys.BuiltIn, listed!.Roles.Select(r => r.Key).ToArray());

        var administrator = listed.Roles.Single(r => r.Key == RoleKeys.Administrator);
        Assert.True(administrator.IsSystem);
        Assert.True(administrator.IsAdministrator);
        Assert.Equal(14, administrator.Permissions.Count);
        Assert.Equal(1, administrator.UserCount);

        // Member carries every non-administrative permission, `cast.access`
        // included — that is the migration contract, not an oversight.
        Assert.Equal(9, listed.Roles.Single(r => r.Key == RoleKeys.Member).Permissions.Count);
        Assert.Empty(listed.Roles.Single(r => r.Key == RoleKeys.Restricted).Permissions);
    }

    [Fact]
    public async Task A_User_Manager_Can_READ_Roles_But_Not_Change_Them()
    {
        // The Users page has to explain what a role means before it is
        // assigned. It must never be able to edit one.
        var (_, client) = await _factory.CreatePermissionClientAsync(
            "user-manager@example.com", Permissions.AdminUsersManage);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/permissions")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/admin/roles", Role("Sneaky", Permissions.PeopleAccess))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"/api/admin/roles/{RoleKeys.Member}",
                Role("Renamed", Permissions.PeopleAccess))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.DeleteAsync($"/api/admin/roles/{RoleKeys.Restricted}")).StatusCode);
    }

    // --------------------------------------------------------------- creating

    [Fact]
    public async Task A_Custom_Role_Is_Created_With_A_Server_Generated_Key()
    {
        var (_, admin) = await AdminAsync();

        var response = await admin.PostAsJsonAsync("/api/admin/roles", new
        {
            name = "Famiglia",
            description = "Shared family account",
            permissions = new[] { Permissions.PeopleAccess, Permissions.TvManage },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var role = await response.Content.ReadFromJsonAsync<RoleDto>();
        // The operator names the role; they never choose its identity.
        Assert.StartsWith(RoleKeys.CustomPrefix, role!.Key, StringComparison.Ordinal);
        Assert.NotEqual("Famiglia", role.Key);
        Assert.Equal("Famiglia", role.Name);
        Assert.False(role.IsSystem);
        Assert.False(role.IsAdministrator);
        Assert.Equal(0, role.UserCount);
        Assert.Equal([Permissions.PeopleAccess, Permissions.TvManage], role.Permissions);
    }

    [Fact]
    public async Task Two_Roles_May_Share_A_Name_And_Stay_Distinct()
    {
        var (_, admin) = await AdminAsync();

        var first = await (await admin.PostAsJsonAsync(
            "/api/admin/roles", Role("Ospiti", Permissions.PeopleAccess)))
            .Content.ReadFromJsonAsync<RoleDto>();
        var second = await (await admin.PostAsJsonAsync(
            "/api/admin/roles", Role("Ospiti", Permissions.TvManage)))
            .Content.ReadFromJsonAsync<RoleDto>();

        Assert.NotEqual(first!.Key, second!.Key);
    }

    [Fact]
    public async Task Duplicating_A_Role_Produces_An_Independent_Copy()
    {
        // The workflow that replaced per-user exceptions: copy the closest role,
        // adjust, save. The copy must not share state with its source.
        var (_, admin) = await AdminAsync();
        var member = await admin.GetFromJsonAsync<RoleDto>($"/api/admin/roles/{RoleKeys.Member}");

        var copy = await (await admin.PostAsJsonAsync("/api/admin/roles", new
        {
            name = $"{member!.Name} copy",
            description = member.Description,
            permissions = member.Permissions,
        })).Content.ReadFromJsonAsync<RoleDto>();

        Assert.Equal(member.Permissions, copy!.Permissions);

        await admin.PutAsJsonAsync($"/api/admin/roles/{copy.Key}", new
        {
            name = copy.Name,
            description = copy.Description,
            permissions = new[] { Permissions.PeopleAccess },
            version = copy.Version,
        });

        // Editing the copy left the original alone.
        var after = await admin.GetFromJsonAsync<RoleDto>($"/api/admin/roles/{RoleKeys.Member}");
        Assert.Equal(member.Permissions, after!.Permissions);
    }

    [Fact]
    public async Task An_Unnamed_Role_Is_Rejected()
    {
        var (_, admin) = await AdminAsync();

        var response = await admin.PostAsJsonAsync("/api/admin/roles", new
        {
            name = "   ",
            permissions = Array.Empty<string>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_Unknown_Permission_Key_Is_Rejected_And_Never_Stored()
    {
        var (_, admin) = await AdminAsync();

        var response = await admin.PostAsJsonAsync(
            "/api/admin/roles", Role("Invented", "definitely.not.a.permission"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var listed = await admin.GetFromJsonAsync<ListRolesResponse>("/api/admin/roles");
        Assert.DoesNotContain(listed!.Roles, r => r.Name == "Invented");
    }

    // ------------------------------------------------------------ dependencies

    [Fact]
    public async Task A_Laboratory_Section_Without_The_Shell_Is_Rejected()
    {
        // The browser enables the parent for the operator. The server refuses
        // the broken shape either way, so a crafted request cannot store a role
        // whose setting reads as working and is not.
        var (_, admin) = await AdminAsync();

        var response = await admin.PostAsJsonAsync(
            "/api/admin/roles", Role("Plates only", Permissions.LaboratoryPlates));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_Laboratory_Section_With_The_Shell_Is_Accepted()
    {
        var (_, admin) = await AdminAsync();

        var response = await admin.PostAsJsonAsync("/api/admin/roles", Role(
            "Plates", Permissions.LaboratoryAccess, Permissions.LaboratoryPlates));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ------------------------------------------------------------- system roles

    [Fact]
    public async Task The_Administrator_Role_Cannot_Be_Edited_Or_Deleted()
    {
        var (_, admin) = await AdminAsync();

        var edit = await admin.PutAsJsonAsync($"/api/admin/roles/{RoleKeys.Administrator}", new
        {
            name = "Boss",
            permissions = new[] { Permissions.PeopleAccess },
        });
        var delete = await admin.DeleteAsync($"/api/admin/roles/{RoleKeys.Administrator}");

        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        var role = await admin.GetFromJsonAsync<RoleDto>($"/api/admin/roles/{RoleKeys.Administrator}");
        Assert.Equal(PermissionCatalog.AllKeys, role!.Permissions);
        Assert.Equal("Administrator", role.Name);
    }

    [Fact]
    public async Task Member_And_Restricted_Are_Editable_But_Not_Deletable()
    {
        var (_, admin) = await AdminAsync();
        var member = await admin.GetFromJsonAsync<RoleDto>($"/api/admin/roles/{RoleKeys.Member}");

        var edit = await admin.PutAsJsonAsync($"/api/admin/roles/{RoleKeys.Member}", new
        {
            name = "Membro",
            description = "Adjusted by the operator",
            permissions = new[] { Permissions.PeopleAccess },
            version = member!.Version,
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var updated = await edit.Content.ReadFromJsonAsync<RoleDto>();
        Assert.Equal("Membro", updated!.Name);
        Assert.Equal([Permissions.PeopleAccess], updated.Permissions);
        Assert.True(updated.IsSystem);

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await admin.DeleteAsync($"/api/admin/roles/{RoleKeys.Member}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await admin.DeleteAsync($"/api/admin/roles/{RoleKeys.Restricted}")).StatusCode);
    }

    // ---------------------------------------------------------------- deleting

    [Fact]
    public async Task An_Unused_Custom_Role_Is_Deleted()
    {
        var (_, admin) = await AdminAsync();
        var role = await (await admin.PostAsJsonAsync(
            "/api/admin/roles", Role("Temporary", Permissions.TvManage)))
            .Content.ReadFromJsonAsync<RoleDto>();

        var response = await admin.DeleteAsync($"/api/admin/roles/{role!.Key}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/admin/roles/{role.Key}")).StatusCode);
    }

    [Fact]
    public async Task A_Role_With_Users_Cannot_Be_Deleted()
    {
        // No cascade into accounts, and no silent reassignment: the operator
        // moves the users first.
        var (_, admin) = await AdminAsync();
        var role = await (await admin.PostAsJsonAsync(
            "/api/admin/roles", Role("Occupied", Permissions.TvManage)))
            .Content.ReadFromJsonAsync<RoleDto>();
        var targetId = await _factory.SeedUserAsync("occupant@example.com");
        await _factory.SetRoleAsync(targetId, role!.Key);

        var response = await admin.DeleteAsync($"/api/admin/roles/{role.Key}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            HttpStatusCode.OK, (await admin.GetAsync($"/api/admin/roles/{role.Key}")).StatusCode);

        // …and once they are moved, it goes.
        await _factory.SetRoleAsync(targetId, RoleKeys.Restricted);
        Assert.Equal(
            HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/roles/{role.Key}")).StatusCode);
    }

    // ------------------------------------------------------------- concurrency

    [Fact]
    public async Task A_Stale_Version_Loses_Rather_Than_Overwriting()
    {
        var (_, admin) = await AdminAsync();
        var role = await (await admin.PostAsJsonAsync(
            "/api/admin/roles", Role("Contested", Permissions.TvManage)))
            .Content.ReadFromJsonAsync<RoleDto>();

        var first = await admin.PutAsJsonAsync($"/api/admin/roles/{role!.Key}", new
        {
            name = role.Name,
            permissions = new[] { Permissions.PeopleAccess },
            version = role.Version,
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // The second editor still holds the version they loaded.
        var second = await admin.PutAsJsonAsync($"/api/admin/roles/{role.Key}", new
        {
            name = role.Name,
            permissions = new[] { Permissions.TvManage },
            version = role.Version,
        });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var after = await admin.GetFromJsonAsync<RoleDto>($"/api/admin/roles/{role.Key}");
        Assert.Equal([Permissions.PeopleAccess], after!.Permissions);
    }

    // ------------------------------------------------------ privilege escalation

    [Fact]
    public async Task Role_Management_Cannot_Be_Put_On_Any_Other_Role()
    {
        var (_, admin) = await AdminAsync();

        var created = await admin.PostAsJsonAsync(
            "/api/admin/roles", Role("Almost admin", Permissions.AdminRolesManage));
        var member = await admin.GetFromJsonAsync<RoleDto>($"/api/admin/roles/{RoleKeys.Member}");
        var edited = await admin.PutAsJsonAsync($"/api/admin/roles/{RoleKeys.Member}", new
        {
            name = member!.Name,
            permissions = member.Permissions.Append(Permissions.AdminRolesManage).ToArray(),
            version = member.Version,
        });

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, edited.StatusCode);
    }

    [Fact]
    public async Task A_User_Manager_Cannot_Promote_Anybody_To_Administrator()
    {
        // admin.users.manage alone: they run ordinary accounts and cannot create
        // an administrator — not somebody else, and not themselves.
        var (managerId, manager) = await _factory.CreatePermissionClientAsync(
            "limited-manager@example.com", Permissions.AdminUsersManage);
        var targetId = await _factory.SeedUserAsync("promotion-target@example.com");

        var other = await manager.PutAsJsonAsync(
            $"/api/admin/users/{targetId}/role", new { role = RoleKeys.Administrator });
        var self = await manager.PutAsJsonAsync(
            $"/api/admin/users/{managerId}/role", new { role = RoleKeys.Administrator });

        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden, (await manager.GetAsync("/api/admin/jobs")).StatusCode);
    }

    [Fact]
    public async Task A_User_Manager_Cannot_Assign_A_Role_Wider_Than_Their_Own()
    {
        var (_, manager) = await _factory.CreatePermissionClientAsync(
            "narrow-manager@example.com", Permissions.AdminUsersManage, Permissions.PeopleAccess);
        var targetId = await _factory.SeedUserAsync("wider@example.com");

        // Member carries the Private Vault, the Laboratory and more, which this
        // caller does not hold.
        var wider = await manager.PutAsJsonAsync(
            $"/api/admin/users/{targetId}/role", new { role = RoleKeys.Member });
        Assert.Equal(HttpStatusCode.Forbidden, wider.StatusCode);

        // A role that is a subset of what they hold is fine.
        var narrow = await manager.PutAsJsonAsync(
            $"/api/admin/users/{targetId}/role", new { role = RoleKeys.Restricted });
        Assert.Equal(HttpStatusCode.OK, narrow.StatusCode);
    }

    [Fact]
    public async Task A_User_Manager_Cannot_Create_An_Account_Wider_Than_Their_Own()
    {
        // The same rule at creation time, because otherwise the guard would only
        // cover half the surface.
        var (_, manager) = await _factory.CreatePermissionClientAsync(
            "creating-manager@example.com", Permissions.AdminUsersManage);

        var response = await manager.PostAsJsonAsync("/api/admin/users", new
        {
            email = "would-be-admin@example.com",
            displayName = "Would Be Admin",
            password = "a-perfectly-fine-password-1",
            role = RoleKeys.Administrator,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_Administrator_May_Assign_Anything()
    {
        var (_, admin) = await AdminAsync();
        var targetId = await _factory.SeedUserAsync("new-admin@example.com");

        var response = await admin.PutAsJsonAsync(
            $"/api/admin/users/{targetId}/role", new { role = RoleKeys.Administrator });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
