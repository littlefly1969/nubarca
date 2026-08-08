using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Access;
using NubArca.Api.Auth;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Users;

namespace NubArca.Api.Tests.Users;

// SQLite in-memory unit tests for the admin-only user management service.
// Endpoint-level authorization (401/403) and full login round-trips are
// covered by AdminUsersEndpointTests; this file covers service behavior that
// is awkward or impossible to reach through the HTTP+cookie stack — in
// particular the LastAdmin guard branch for a caller distinct from the
// target, which cannot occur over HTTP because only an existing admin can
// call these endpoints, and when exactly one admin remains that admin IS
// the only possible caller (making it a self-demotion/self-disable, which
// is blocked by a separate, always-on guard).
public sealed class AdminUserServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly AdminUserService _service;
    private readonly UserService _users;
    private readonly AuthService _auth;
    private readonly UserPermissionService _permissions;
    private readonly RoleService _roles;
    private Guid? _caller;

    public AdminUserServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(dbOptions);
        _db.Database.EnsureCreated();
        _db.SeedBuiltInRoles();

        var hasher = new PasswordHasher<User>();
        _users = new UserService(_db, TimeProvider.System);
        _auth = new AuthService(_db, _users, hasher, TimeProvider.System);
        _roles = new RoleService(_db, TimeProvider.System);
        _permissions = new UserPermissionService(_db, _roles);
        _service = new AdminUserService(_db, _users, _auth, _permissions, _roles, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // Creation is an escalation-checked operation now, so every call needs a
    // caller. This one is an Administrator, created outside the service so the
    // bootstrap is not itself subject to the check.
    private async Task<Guid> CallerAsync()
    {
        if (_caller is Guid existing)
        {
            return existing;
        }
        var user = await _users.CreateAsync("caller@example.com", "Caller");
        await _users.SetRoleAsync(user.Id, RoleKeys.Administrator);
        _caller = user.Id;
        return user.Id;
    }

    private async Task<AdminUserDto> CreateAsync(CreateAdminUserRequest request)
    {
        var (result, dto) = await _service.CreateAsync(await CallerAsync(), request);
        Assert.Equal(AdminSetRoleResult.Ok, result);
        return dto!;
    }

    // Steps the bootstrap caller out of the administrator count, so a test can
    // set up an installation whose LAST administrator is the one under test.
    private async Task RetireCallerAsync() =>
        await _users.SetRoleAsync(await CallerAsync(), RoleKeys.Member);

    [Fact]
    public async Task CreateAsync_With_Password_Sets_HasPassword_And_Hashes_It()
    {
        var dto = await CreateAsync(new CreateAdminUserRequest(
            "alice@example.com", "Alice", "correct-horse-battery"));

        Assert.True(dto.HasPassword);
        var row = await _db.Users.AsNoTracking().SingleAsync(u => u.Id == dto.Id);
        Assert.NotNull(row.PasswordHash);
        Assert.DoesNotContain("correct-horse-battery", row.PasswordHash);
    }

    [Fact]
    public async Task CreateAsync_Without_Password_Leaves_HasPassword_False()
    {
        var dto = await CreateAsync(new CreateAdminUserRequest(
            "nopass@example.com", "No Pass", null));

        Assert.False(dto.HasPassword);
    }

    [Fact]
    public async Task CreateAsync_Can_Set_Role_Disabled_And_Language()
    {
        var dto = await CreateAsync(new CreateAdminUserRequest(
            "full@example.com", "Full", "correct-horse-battery",
            Role: RoleKeys.Administrator, Disabled: true, Language: "en"));

        Assert.Equal(RoleKeys.Administrator, dto.Role);
        Assert.NotNull(dto.DisabledAt);
        Assert.Equal("en", dto.Language);
    }

    [Fact]
    public async Task ListAsync_Filters_By_Query_Against_Email_And_DisplayName()
    {
        await CreateAsync(new CreateAdminUserRequest("match@example.com", "Nobody", "correct-horse-battery"));
        await CreateAsync(new CreateAdminUserRequest("other@example.com", "Match Name", "correct-horse-battery"));
        await CreateAsync(new CreateAdminUserRequest("nothing@example.com", "Nothing", "correct-horse-battery"));

        var result = await _service.ListAsync("match", includeDisabled: true, limit: 0, offset: 0);

        Assert.Equal(2, result.Total);
        Assert.All(result.Items, u => Assert.True(
            u.Email.Contains("match", StringComparison.OrdinalIgnoreCase)
            || u.DisplayName.Contains("match", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ListAsync_Excludes_Disabled_By_Default()
    {
        var disabledId = (await CreateAsync(new CreateAdminUserRequest(
            "disabled@example.com", "Disabled", "correct-horse-battery", Disabled: true))).Id;
        await CreateAsync(new CreateAdminUserRequest("active@example.com", "Active", "correct-horse-battery"));

        var withoutDisabled = await _service.ListAsync(null, includeDisabled: false, limit: 0, offset: 0);
        Assert.DoesNotContain(withoutDisabled.Items, u => u.Id == disabledId);

        var withDisabled = await _service.ListAsync(null, includeDisabled: true, limit: 0, offset: 0);
        Assert.Contains(withDisabled.Items, u => u.Id == disabledId);
    }

    [Fact]
    public async Task ListAsync_Clamps_Limit_To_Max()
    {
        var result = await _service.ListAsync(null, includeDisabled: true, limit: 10_000, offset: 0);
        Assert.Equal(200, result.Limit);
    }

    [Fact]
    public async Task SetRoleAsync_Blocks_LastAdmin_When_Caller_Differs_From_Target()
    {
        var sole = await CreateAsync(new CreateAdminUserRequest(
            "sole-admin@example.com", "Sole Admin", "correct-horse-battery", Role: RoleKeys.Administrator));
        await RetireCallerAsync();
        // A hypothetical different caller (e.g. a second admin that has since
        // been removed out-of-band) tries to demote the last remaining admin.
        var otherCallerId = Guid.NewGuid();

        var (result, user) = await _service.SetRoleAsync(otherCallerId, sole.Id, RoleKeys.Member);

        Assert.Equal(AdminSetRoleResult.LastAdmin, result);
        Assert.Null(user);
        var row = await _db.Users.AsNoTracking().SingleAsync(u => u.Id == sole.Id);
        Assert.Equal(RoleKeys.Administrator, row.RoleKey);
    }

    [Fact]
    public async Task SetRoleAsync_Blocks_Self_Demotion_Even_With_Other_Admins_Present()
    {
        var a = await CreateAsync(new CreateAdminUserRequest(
            "a@example.com", "A", "correct-horse-battery", Role: RoleKeys.Administrator));
        await CreateAsync(new CreateAdminUserRequest(
            "b@example.com", "B", "correct-horse-battery", Role: RoleKeys.Administrator));

        var (result, user) = await _service.SetRoleAsync(a.Id, a.Id, RoleKeys.Member);

        Assert.Equal(AdminSetRoleResult.SelfDemotion, result);
        Assert.Null(user);
    }

    [Fact]
    public async Task SetRoleAsync_Allows_Demoting_One_Of_Two_Admins()
    {
        var a = await CreateAsync(new CreateAdminUserRequest(
            "a2@example.com", "A2", "correct-horse-battery", Role: RoleKeys.Administrator));
        var b = await CreateAsync(new CreateAdminUserRequest(
            "b2@example.com", "B2", "correct-horse-battery", Role: RoleKeys.Administrator));

        var (result, user) = await _service.SetRoleAsync(a.Id, b.Id, RoleKeys.Member);

        Assert.Equal(AdminSetRoleResult.Ok, result);
        Assert.Equal(RoleKeys.Member, user!.Role);
    }

    [Fact]
    public async Task SetRoleAsync_NotFound_For_Missing_User()
    {
        var (result, user) = await _service.SetRoleAsync(Guid.NewGuid(), Guid.NewGuid(), RoleKeys.Administrator);
        Assert.Equal(AdminSetRoleResult.NotFound, result);
        Assert.Null(user);
    }

    [Fact]
    public async Task SetDisabledAsync_Blocks_LastAdmin_When_Caller_Differs_From_Target()
    {
        var sole = await CreateAsync(new CreateAdminUserRequest(
            "sole-admin2@example.com", "Sole Admin", "correct-horse-battery", Role: RoleKeys.Administrator));
        await RetireCallerAsync();
        var otherCallerId = Guid.NewGuid();

        var (result, user) = await _service.SetDisabledAsync(otherCallerId, sole.Id, disabled: true);

        Assert.Equal(AdminSetDisabledResult.LastAdmin, result);
        Assert.Null(user);
        var row = await _db.Users.AsNoTracking().SingleAsync(u => u.Id == sole.Id);
        Assert.Null(row.DisabledAt);
    }

    [Fact]
    public async Task SetDisabledAsync_Blocks_Self_Disable()
    {
        var admin = await CreateAsync(new CreateAdminUserRequest(
            "self-disable@example.com", "Self Disable", "correct-horse-battery", Role: RoleKeys.Administrator));

        var (result, user) = await _service.SetDisabledAsync(admin.Id, admin.Id, disabled: true);

        Assert.Equal(AdminSetDisabledResult.SelfDisable, result);
        Assert.Null(user);
    }

    [Fact]
    public async Task SetDisabledAsync_Allows_Disabling_A_Non_Admin()
    {
        var admin = await CreateAsync(new CreateAdminUserRequest(
            "admin3@example.com", "Admin3", "correct-horse-battery", Role: RoleKeys.Administrator));
        var plain = await CreateAsync(new CreateAdminUserRequest(
            "plain@example.com", "Plain", "correct-horse-battery"));

        var (result, user) = await _service.SetDisabledAsync(admin.Id, plain.Id, disabled: true);

        Assert.Equal(AdminSetDisabledResult.Ok, result);
        Assert.NotNull(user!.DisabledAt);
    }

    [Fact]
    public async Task ResetPasswordAsync_Returns_False_For_Missing_User()
    {
        var updated = await _service.ResetPasswordAsync(Guid.NewGuid(), "brand-new-password-1");
        Assert.False(updated);
    }
}
