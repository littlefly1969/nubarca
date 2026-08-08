using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Access;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Tests.Access;

// Effective-permission resolution: role baseline + explicit overrides, and the
// rules that make an Administrator's authority non-removable.
public sealed class PermissionResolutionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly UserPermissionService _permissions;

    public PermissionResolutionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _permissions = new UserPermissionService(_db, TimeProvider.System);
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

    [Fact]
    public async Task Administrator_Receives_Every_Permission_In_The_Catalogue()
    {
        var user = await SeedAsync(RoleKeys.Administrator, "admin@example.com");

        var effective = await _permissions.GetEffectiveAsync(user.Id);

        Assert.Equal(PermissionCatalog.AllKeys, effective.ToSortedList());
        Assert.True(effective.IsAdministrator);
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
    public async Task Grant_Override_Adds_A_Permission_The_Role_Does_Not_Carry()
    {
        var user = await SeedAsync(RoleKeys.Restricted, "grant@example.com");

        Assert.True(await _permissions.SetOverrideAsync(
            user.Id, Permissions.PeopleAccess, PermissionEffect.Grant));

        var effective = await _permissions.GetEffectiveAsync(user.Id);
        Assert.True(effective.Has(Permissions.PeopleAccess));
        Assert.False(effective.Has(Permissions.LaboratoryAccess));
    }

    [Fact]
    public async Task Deny_Override_Removes_A_Permission_The_Role_Carries()
    {
        var user = await SeedAsync(RoleKeys.Member, "deny@example.com");

        Assert.True(await _permissions.SetOverrideAsync(
            user.Id, Permissions.PrivateVaultAccess, PermissionEffect.Deny));

        var effective = await _permissions.GetEffectiveAsync(user.Id);
        Assert.False(effective.Has(Permissions.PrivateVaultAccess));
        Assert.True(effective.Has(Permissions.PeopleAccess));
    }

    [Fact]
    public async Task Clearing_An_Override_Restores_The_Role_Baseline()
    {
        var user = await SeedAsync(RoleKeys.Member, "clear@example.com");
        await _permissions.SetOverrideAsync(user.Id, Permissions.PeopleAccess, PermissionEffect.Deny);
        Assert.False((await _permissions.GetEffectiveAsync(user.Id)).Has(Permissions.PeopleAccess));

        Assert.True(await _permissions.ClearOverrideAsync(user.Id, Permissions.PeopleAccess));

        Assert.True((await _permissions.GetEffectiveAsync(user.Id)).Has(Permissions.PeopleAccess));
    }

    [Fact]
    public async Task Unknown_Permission_Key_Is_Never_Persisted()
    {
        var user = await SeedAsync(RoleKeys.Member, "unknown@example.com");

        Assert.False(await _permissions.SetOverrideAsync(
            user.Id, "not-a-real.permission", PermissionEffect.Grant));

        Assert.Empty(await _db.UserPermissionOverrides.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_Deny_Cannot_Strip_An_Administrator()
    {
        // Overrides are not consulted for an Administrator at all: a stored Deny
        // — however it got there — must never be able to remove the authority
        // that lets another administrator undo it.
        var user = await SeedAsync(RoleKeys.Administrator, "protected@example.com");
        await _permissions.SetOverrideAsync(
            user.Id, Permissions.AdminUsersManage, PermissionEffect.Deny);

        var effective = await _permissions.GetEffectiveAsync(user.Id);

        Assert.True(effective.Has(Permissions.AdminUsersManage));
        Assert.Equal(PermissionCatalog.AllKeys, effective.ToSortedList());
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
    public async Task An_Override_For_A_Retired_Permission_Key_Is_Inert()
    {
        // A key removed from the catalogue in a future release must not break
        // the login of everybody who happened to have an override for it.
        var user = await SeedAsync(RoleKeys.Member, "retired@example.com");
        _db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PermissionKey = "retired.feature",
            Effect = PermissionEffect.Grant,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

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
    public void Every_Role_Baseline_Names_Only_Catalogue_Keys()
    {
        foreach (var role in RoleKeys.All)
        {
            foreach (var key in RolePermissionCatalog.For(role))
            {
                Assert.True(PermissionCatalog.IsKnown(key), $"{role} references unknown key {key}");
            }
        }
    }
}
