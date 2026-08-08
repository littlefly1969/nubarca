using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Access;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Tests.Access;

// Effective-permission resolution: USER → ROLE → PERMISSIONS, with nothing in
// between, and the rules that make an Administrator's authority non-removable.
public sealed class PermissionResolutionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly RoleService _roles;
    private readonly UserPermissionService _permissions;

    public PermissionResolutionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.SeedBuiltInRoles();
        _roles = new RoleService(_db, TimeProvider.System);
        _permissions = new UserPermissionService(_db, _roles);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<User> SeedAsync(string roleKey, string email)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = email,
            RoleKey = roleKey,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    private async Task<string> CustomRoleAsync(string name, params string[] permissions)
    {
        var (result, role) = await _roles.CreateAsync(new CreateRoleRequest(name, null, permissions));
        Assert.Equal(RoleMutationResult.Ok, result);
        return role!.Key;
    }

    [Fact]
    public async Task The_Built_In_Roles_Are_Seeded_With_Their_Documented_Sets()
    {
        var roles = await _roles.ListAsync();

        Assert.Equal(RoleKeys.BuiltIn, roles.Select(r => r.Key).ToArray());
        Assert.All(roles, role => Assert.True(role.IsSystem));

        var administrator = roles.Single(r => r.Key == RoleKeys.Administrator);
        Assert.True(administrator.IsAdministrator);
        Assert.Equal(PermissionCatalog.AllKeys, administrator.Permissions);
        // Nine feature permissions plus five administrative ones.
        Assert.Equal(14, administrator.Permissions.Count);

        Assert.Equal(RoleDefaults.MemberPermissions, roles.Single(r => r.Key == RoleKeys.Member).Permissions);
        Assert.Empty(roles.Single(r => r.Key == RoleKeys.Restricted).Permissions);
    }

    [Fact]
    public async Task Seeding_Twice_Changes_Nothing()
    {
        var before = await _roles.ListAsync();

        await _roles.EnsureBuiltInRolesAsync();

        var after = await _roles.ListAsync();
        Assert.Equal(
            before.Select(r => (r.Key, string.Join(",", r.Permissions))),
            after.Select(r => (r.Key, string.Join(",", r.Permissions))));
    }

    [Fact]
    public async Task Seeding_Does_Not_Rewrite_An_Edited_Member_Set()
    {
        // The operator owns Member after migration; a deploy must not silently
        // put back what they took away.
        var member = (await _roles.GetAsync(RoleKeys.Member))!;
        await _roles.UpdateAsync(RoleKeys.Member, new UpdateRoleRequest(
            member.Name, member.Description, [Permissions.PeopleAccess], member.Version));

        await _roles.EnsureBuiltInRolesAsync();

        Assert.Equal([Permissions.PeopleAccess], (await _roles.GetAsync(RoleKeys.Member))!.Permissions);
    }

    [Fact]
    public async Task Seeding_Restores_A_Permission_Missing_From_The_Administrator()
    {
        // The one set that IS a contract: a release adding a catalogue key must
        // not leave administrators without it.
        await _db.RolePermissions
            .Where(p => p.RoleKey == RoleKeys.Administrator && p.PermissionKey == Permissions.AdminRolesManage)
            .ExecuteDeleteAsync();

        await _roles.EnsureBuiltInRolesAsync();

        Assert.Contains(
            Permissions.AdminRolesManage,
            (await _roles.GetAsync(RoleKeys.Administrator))!.Permissions);
    }

    [Fact]
    public async Task Administrator_Receives_Every_Permission_In_The_Catalogue()
    {
        var user = await SeedAsync(RoleKeys.Administrator, "admin@example.com");

        var effective = await _permissions.GetEffectiveAsync(user.Id);

        Assert.Equal(PermissionCatalog.AllKeys, effective.ToSortedList());
        Assert.True(effective.IsAdministrator);
    }

    [Fact]
    public async Task An_Administrator_Keeps_Everything_Even_With_Rows_Deleted()
    {
        // Resolution never queries for an administrator. A missing row — however
        // it got that way — must not be able to strip the authority that lets
        // another administrator put it back.
        var user = await SeedAsync(RoleKeys.Administrator, "protected@example.com");
        await _db.RolePermissions.Where(p => p.RoleKey == RoleKeys.Administrator).ExecuteDeleteAsync();

        var effective = await _permissions.GetEffectiveAsync(user.Id);

        Assert.Equal(PermissionCatalog.AllKeys, effective.ToSortedList());
        Assert.True(effective.Has(Permissions.AdminUsersManage));
    }

    [Fact]
    public async Task Member_Receives_Every_Non_Administrative_Feature_Permission()
    {
        // This is the MIGRATION contract, not a preference: an account that was
        // a non-admin before roles existed became a Member, and must still be
        // able to do everything it could do then. A feature permission missing
        // here is an access regression for every existing user.
        var user = await SeedAsync(RoleKeys.Member, "member@example.com");

        var effective = await _permissions.GetEffectiveAsync(user.Id);

        foreach (var definition in PermissionCatalog.All.Where(p => !p.Administrative))
        {
            Assert.True(effective.Has(definition.Key), $"Member is missing {definition.Key}");
        }
        foreach (var definition in PermissionCatalog.All.Where(p => p.Administrative))
        {
            Assert.False(effective.Has(definition.Key), $"Member unexpectedly has {definition.Key}");
        }
        Assert.False(effective.IsAdministrator);
    }

    [Fact]
    public async Task Restricted_Holds_No_Advanced_Permission()
    {
        var user = await SeedAsync(RoleKeys.Restricted, "restricted@example.com");

        var effective = await _permissions.GetEffectiveAsync(user.Id);

        Assert.Empty(effective.ToSortedList());
    }

    [Fact]
    public async Task A_Custom_Role_Resolves_To_Exactly_Its_Own_Permissions()
    {
        var roleKey = await CustomRoleAsync(
            "Laboratorio", Permissions.LaboratoryAccess, Permissions.LaboratoryPlates);
        var user = await SeedAsync(roleKey, "custom@example.com");

        var effective = await _permissions.GetEffectiveAsync(user.Id);

        Assert.Equal(
            new[] { Permissions.LaboratoryAccess, Permissions.LaboratoryPlates }
                .OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            effective.ToSortedList());
        Assert.False(effective.Has(Permissions.PeopleAccess));
    }

    [Fact]
    public async Task Editing_A_Role_Changes_Every_Assigned_User()
    {
        // The whole reason there are no per-user exceptions: one edit, and
        // everybody in the role is affected on their next resolution.
        var roleKey = await CustomRoleAsync("Famiglia", Permissions.PeopleAccess);
        var first = await SeedAsync(roleKey, "family-a@example.com");
        var second = await SeedAsync(roleKey, "family-b@example.com");
        Assert.False((await _permissions.GetEffectiveAsync(first.Id)).Has(Permissions.TvManage));

        var role = (await _roles.GetAsync(roleKey))!;
        await _roles.UpdateAsync(roleKey, new UpdateRoleRequest(
            role.Name, role.Description, [Permissions.PeopleAccess, Permissions.TvManage], role.Version));

        // A fresh resolver, as the next request would have.
        var fresh = new UserPermissionService(_db, new RoleService(_db, TimeProvider.System));
        Assert.True((await fresh.GetEffectiveAsync(first.Id)).Has(Permissions.TvManage));
        Assert.True((await fresh.GetEffectiveAsync(second.Id)).Has(Permissions.TvManage));
    }

    [Fact]
    public async Task A_Disabled_User_Holds_No_Permission_Whatever_Their_Role()
    {
        var user = await SeedAsync(RoleKeys.Administrator, "disabled@example.com");
        user.DisabledAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var effective = await _permissions.GetEffectiveAsync(user.Id);

        Assert.Empty(effective.ToSortedList());
    }

    [Fact]
    public async Task A_Role_Row_For_A_Retired_Permission_Key_Is_Inert()
    {
        // A key removed from the catalogue in a future release must not break
        // the login of everybody whose role happened to mention it.
        var roleKey = await CustomRoleAsync("Legacy", Permissions.PeopleAccess);
        _db.RolePermissions.Add(new RolePermission { RoleKey = roleKey, PermissionKey = "retired.feature" });
        await _db.SaveChangesAsync();
        var user = await SeedAsync(roleKey, "retired@example.com");

        var effective = await _permissions.GetEffectiveAsync(user.Id);

        Assert.False(effective.Has("retired.feature"));
        Assert.True(effective.Has(Permissions.PeopleAccess));
    }

    [Fact]
    public async Task Effective_Permissions_Are_Ordinal_Sorted()
    {
        var user = await SeedAsync(RoleKeys.Member, "sorted@example.com");

        var list = (await _permissions.GetEffectiveAsync(user.Id)).ToSortedList();

        Assert.Equal(list.OrderBy(k => k, StringComparer.Ordinal).ToArray(), list);
    }

    [Fact]
    public async Task Every_Seeded_Role_Names_Only_Catalogue_Keys()
    {
        foreach (var role in await _roles.ListAsync())
        {
            foreach (var key in role.Permissions)
            {
                Assert.True(PermissionCatalog.IsKnown(key), $"{role.Key} references unknown key {key}");
            }
        }
    }

    [Fact]
    public void The_Catalogue_Declares_The_Laboratory_Dependency_Once()
    {
        // The role editor and the composite endpoint policies both read this,
        // so the two cannot drift apart.
        Assert.Equal(Permissions.LaboratoryAccess, PermissionCatalog.ParentOf(Permissions.LaboratoryPlates));
        Assert.Equal(Permissions.LaboratoryAccess, PermissionCatalog.ParentOf(Permissions.LaboratoryAesthetics));
        Assert.Null(PermissionCatalog.ParentOf(Permissions.LaboratoryAccess));
    }

    [Fact]
    public void Only_Role_Management_Is_Administrator_Only()
    {
        Assert.True(PermissionCatalog.IsAdministratorOnly(Permissions.AdminRolesManage));
        Assert.DoesNotContain(Permissions.AdminRolesManage, PermissionCatalog.AssignableKeys);
        Assert.Equal(13, PermissionCatalog.AssignableKeys.Count);
        Assert.Equal(14, PermissionCatalog.AllKeys.Count);
    }
}
