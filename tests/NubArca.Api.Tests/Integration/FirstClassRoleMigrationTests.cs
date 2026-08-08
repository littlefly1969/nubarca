using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Access;
using NubArca.Api.Data;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// Proves the per-user-override cutover on REPRESENTATIVE pre-migration data.
//
// The promise is exact and is the only reason this migration is safe to run on
// a populated installation: for every account, the set of permissions in force
// AFTER the migration equals the set that was in force BEFORE it. The old model
// was `role baseline ∪ grants − denies`; the new one is `the role's set`. So the
// test computes the old answer in the test itself, from the rows as they stood,
// and compares it to what the new schema resolves.
//
// A scaffolded migration would have dropped user_permission_overrides first and
// silently discarded every exception — a failure nobody would notice until a
// user reported losing access days later. So the check runs against a real
// PostgreSQL, on rows that carry the old shape, before the deploy.
[Trait("Category", "External")]
[Collection("RoleMigration")]
public sealed class FirstClassRoleMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260807191448_AddRolesPermissionsAndPasswordRecovery";
    private const string MigrationUnderTest = "20260808122509_MakeRolesFirstClass";

    // The Member baseline as the OLD code defined it: every non-administrative
    // feature permission.
    private static readonly string[] MemberBaseline =
    [
        "cloud-functions.access", "laboratory.access", "laboratory.aesthetics",
        "laboratory.plates", "people.access", "private-vault.access",
        "semantic-search.access", "tv.manage",
    ];

    private PostgreSqlContainer? _container;
    private string? _connectionString;

    private bool Available => _connectionString is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("nubarca_firstclassroles")
                .WithUsername("nubarca")
                .WithPassword("nubarca")
                .Build();

            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception)
        {
            // No reachable Docker: the tests skip rather than fail.
            _connectionString = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------- case A

    [Fact]
    public async Task With_No_Overrides_Every_User_Keeps_Their_Role()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateToPreviousAsync();
        var admin = await SeedUserAsync("a-admin@example.com", RoleKeys.Administrator);
        var member = await SeedUserAsync("a-member@example.com", RoleKeys.Member);
        var restricted = await SeedUserAsync("a-restricted@example.com", RoleKeys.Restricted);

        await MigrateToTestAsync();

        var roles = await ReadUserRolesAsync();
        Assert.Equal(RoleKeys.Administrator, roles[admin]);
        Assert.Equal(RoleKeys.Member, roles[member]);
        Assert.Equal(RoleKeys.Restricted, roles[restricted]);

        // No role was invented for accounts that needed none.
        Assert.Equal(RoleKeys.BuiltIn.OrderBy(k => k), (await ReadRoleKeysAsync()).OrderBy(k => k));
        Assert.Equal(MemberBaseline, await ReadRolePermissionsAsync(RoleKeys.Member));
        Assert.Empty(await ReadRolePermissionsAsync(RoleKeys.Restricted));
        Assert.Equal(13, (await ReadRolePermissionsAsync(RoleKeys.Administrator)).Length);
    }

    // ---------------------------------------------------------------- case B

    [Fact]
    public async Task A_Grant_Becomes_A_Role_That_Carries_It()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateToPreviousAsync();
        var user = await SeedUserAsync("b-grant@example.com", RoleKeys.Restricted);
        await SeedOverrideAsync(user, "people.access", "Grant");
        var before = new[] { "people.access" };

        await MigrateToTestAsync();

        Assert.Equal(before, await ReadEffectivePermissionsAsync(user));
        var roleKey = (await ReadUserRolesAsync())[user];
        Assert.StartsWith(RoleKeys.CustomPrefix, roleKey, StringComparison.Ordinal);
        Assert.Equal("Migrated access 1", await ReadRoleNameAsync(roleKey));
        Assert.Equal(
            "Created automatically from the previous per-user permission model.",
            await ReadRoleDescriptionAsync(roleKey));
    }

    // ---------------------------------------------------------------- case C

    [Fact]
    public async Task A_Deny_Becomes_A_Role_That_Omits_It()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateToPreviousAsync();
        var user = await SeedUserAsync("c-deny@example.com", RoleKeys.Member);
        await SeedOverrideAsync(user, "private-vault.access", "Deny");
        var before = MemberBaseline.Where(k => k != "private-vault.access").ToArray();

        await MigrateToTestAsync();

        Assert.Equal(before, await ReadEffectivePermissionsAsync(user));
        Assert.DoesNotContain("private-vault.access", await ReadEffectivePermissionsAsync(user));
    }

    // ---------------------------------------------------------------- case D

    [Fact]
    public async Task Users_With_Identical_Effective_Sets_Share_One_Migrated_Role()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateToPreviousAsync();
        // Two accounts that arrive at the same answer by different routes: one
        // Restricted plus grants, one Member minus everything else.
        var first = await SeedUserAsync("d-first@example.com", RoleKeys.Restricted);
        await SeedOverrideAsync(first, "people.access", "Grant");
        await SeedOverrideAsync(first, "tv.manage", "Grant");

        var second = await SeedUserAsync("d-second@example.com", RoleKeys.Member);
        foreach (var key in MemberBaseline.Where(k => k is not ("people.access" or "tv.manage")))
        {
            await SeedOverrideAsync(second, key, "Deny");
        }

        // A third with a genuinely different set gets its own role.
        var third = await SeedUserAsync("d-third@example.com", RoleKeys.Restricted);
        await SeedOverrideAsync(third, "admin.jobs.manage", "Grant");

        await MigrateToTestAsync();

        var expected = new[] { "people.access", "tv.manage" };
        Assert.Equal(expected, await ReadEffectivePermissionsAsync(first));
        Assert.Equal(expected, await ReadEffectivePermissionsAsync(second));
        Assert.Equal(["admin.jobs.manage"], await ReadEffectivePermissionsAsync(third));

        var roles = await ReadUserRolesAsync();
        Assert.Equal(roles[first], roles[second]);
        Assert.NotEqual(roles[first], roles[third]);

        // Two distinct sets, two migrated roles — not three, and not one.
        var migrated = (await ReadRoleKeysAsync())
            .Where(k => k.StartsWith(RoleKeys.CustomPrefix, StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, migrated.Length);
    }

    // -------------------------------------------------- reuse of a built-in

    [Fact]
    public async Task An_Override_That_Reproduces_A_Built_In_Set_Reuses_That_Role()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateToPreviousAsync();
        // A Restricted account granted the whole Member baseline is a Member.
        var user = await SeedUserAsync("reuse@example.com", RoleKeys.Restricted);
        foreach (var key in MemberBaseline)
        {
            await SeedOverrideAsync(user, key, "Grant");
        }

        await MigrateToTestAsync();

        Assert.Equal(RoleKeys.Member, (await ReadUserRolesAsync())[user]);
        Assert.Empty((await ReadRoleKeysAsync())
            .Where(k => k.StartsWith(RoleKeys.CustomPrefix, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_Administrators_Overrides_Are_Ignored_And_They_Stay_An_Administrator()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateToPreviousAsync();
        var user = await SeedUserAsync("admin-denied@example.com", RoleKeys.Administrator);
        // The old resolver ignored these entirely, so the effective set was — and
        // remains — the whole catalogue.
        await SeedOverrideAsync(user, "admin.users.manage", "Deny");

        await MigrateToTestAsync();

        Assert.Equal(RoleKeys.Administrator, (await ReadUserRolesAsync())[user]);
        Assert.Equal(13, (await ReadEffectivePermissionsAsync(user)).Length);
    }

    [Fact]
    public async Task An_Orphaned_Laboratory_Section_Is_Not_Carried_Into_A_Role()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateToPreviousAsync();
        // Plates without the shell opened nothing under the composite policy, so
        // dropping it preserves effective access exactly.
        var user = await SeedUserAsync("orphan@example.com", RoleKeys.Member);
        await SeedOverrideAsync(user, "laboratory.access", "Deny");

        await MigrateToTestAsync();

        var effective = await ReadEffectivePermissionsAsync(user);
        Assert.DoesNotContain("laboratory.access", effective);
        Assert.DoesNotContain("laboratory.plates", effective);
        Assert.DoesNotContain("laboratory.aesthetics", effective);
        Assert.Contains("people.access", effective);
    }

    // ------------------------------------------------------------- structure

    [Fact]
    public async Task The_Override_Table_Is_Gone_And_The_Role_Reference_Is_Enforced()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateToPreviousAsync();
        await SeedUserAsync("structure@example.com", RoleKeys.Member);

        await MigrateToTestAsync();

        Assert.False(await TableExistsAsync("user_permission_overrides"));
        Assert.True(await TableExistsAsync("access_roles"));
        Assert.True(await TableExistsAsync("role_permissions"));

        // A user can never reference a role that does not exist…
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecuteAsync("""UPDATE users SET "RoleKey" = 'Invented' WHERE "Email" = 'structure@example.com';"""));

        // …and a role with users cannot be deleted out from under them.
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecuteAsync("""DELETE FROM access_roles WHERE "Key" = 'Member';"""));
    }

    [Fact]
    public async Task An_Unrecognised_Legacy_Role_Value_Becomes_A_Member()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateToPreviousAsync();
        // The old resolver read anything it did not recognise as the Member
        // baseline, so Member is the exact-preservation answer — and the foreign
        // key needs every value to name a real role.
        var user = await SeedUserAsync("legacy-role@example.com", "SomethingElse");

        await MigrateToTestAsync();

        Assert.Equal(RoleKeys.Member, (await ReadUserRolesAsync())[user]);
        Assert.Equal(MemberBaseline, await ReadEffectivePermissionsAsync(user));
    }

    // -------------------------------------------------------------- rollback

    [Fact]
    public async Task The_Rollback_Restores_The_Exact_Effective_Permissions()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateToPreviousAsync();
        var user = await SeedUserAsync("rollback@example.com", RoleKeys.Restricted);
        await SeedOverrideAsync(user, "people.access", "Grant");
        await SeedOverrideAsync(user, "tv.manage", "Grant");

        await MigrateToTestAsync();
        await MigrateToPreviousAsync();

        Assert.True(await TableExistsAsync("user_permission_overrides"));
        Assert.False(await TableExistsAsync("access_roles"));

        // Back on Member, with the grants and denies that reproduce exactly the
        // two permissions this account had.
        var roles = await ReadUserRolesAsync();
        Assert.Equal(RoleKeys.Member, roles[user]);

        var effective = MemberBaseline.ToHashSet(StringComparer.Ordinal);
        foreach (var (key, effect) in await ReadOverridesAsync(user))
        {
            if (effect == "Grant") effective.Add(key); else effective.Remove(key);
        }
        Assert.Equal(
            new[] { "people.access", "tv.manage" },
            effective.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    // ----------------------------------------------------------------- setup

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);

    private async Task MigrateToPreviousAsync()
    {
        await using var ctx = CreateContext();
        await ctx.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(PreviousMigration);
    }

    private async Task MigrateToTestAsync()
    {
        await using var ctx = CreateContext();
        await ctx.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(MigrationUnderTest);
    }

    private async Task<Guid> SeedUserAsync(string email, string roleKey)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO users ("Id", "Email", "DisplayName", "PasswordHash", "CreatedAt",
                               "RoleKey", "UiLanguage", "SecurityVersion")
            VALUES (@id, @email, @email, 'not-a-real-hash', now(), @roleKey, 'it', 1);
            """,
            ("id", id), ("email", email), ("roleKey", roleKey));
        return id;
    }

    private Task SeedOverrideAsync(Guid userId, string permissionKey, string effect) =>
        ExecuteAsync(
            """
            INSERT INTO user_permission_overrides
                ("Id", "UserId", "PermissionKey", "Effect", "CreatedAt", "UpdatedAt")
            VALUES (gen_random_uuid(), @userId, @permissionKey, @effect, now(), now());
            """,
            ("userId", userId), ("permissionKey", permissionKey), ("effect", effect));

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<T>> QueryAsync<T>(
        string sql, Func<NpgsqlDataReader, T> read, params (string Name, object Value)[] parameters)
    {
        var result = new List<T>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(read(reader));
        }
        return result;
    }

    private async Task<Dictionary<Guid, string>> ReadUserRolesAsync() =>
        (await QueryAsync(
            """SELECT "Id", "RoleKey" FROM users;""",
            r => (r.GetGuid(0), r.GetString(1))))
        .ToDictionary(x => x.Item1, x => x.Item2);

    private Task<List<string>> ReadRoleKeysAsync() =>
        QueryAsync("""SELECT "Key" FROM access_roles;""", r => r.GetString(0));

    private async Task<string[]> ReadRolePermissionsAsync(string roleKey) =>
        (await QueryAsync(
            """SELECT "PermissionKey" FROM role_permissions WHERE "RoleKey" = @roleKey ORDER BY "PermissionKey";""",
            r => r.GetString(0),
            ("roleKey", roleKey))).ToArray();

    // What the user may actually do after the migration: their role's set.
    private async Task<string[]> ReadEffectivePermissionsAsync(Guid userId) =>
        (await QueryAsync(
            """
            SELECT rp."PermissionKey"
            FROM users u JOIN role_permissions rp ON rp."RoleKey" = u."RoleKey"
            WHERE u."Id" = @userId
            ORDER BY rp."PermissionKey";
            """,
            r => r.GetString(0),
            ("userId", userId))).ToArray();

    private async Task<string> ReadRoleNameAsync(string roleKey) =>
        (await QueryAsync(
            """SELECT "Name" FROM access_roles WHERE "Key" = @roleKey;""",
            r => r.GetString(0), ("roleKey", roleKey))).Single();

    private async Task<string> ReadRoleDescriptionAsync(string roleKey) =>
        (await QueryAsync(
            """SELECT "Description" FROM access_roles WHERE "Key" = @roleKey;""",
            r => r.GetString(0), ("roleKey", roleKey))).Single();

    private Task<List<(string Key, string Effect)>> ReadOverridesAsync(Guid userId) =>
        QueryAsync(
            """SELECT "PermissionKey", "Effect" FROM user_permission_overrides WHERE "UserId" = @userId;""",
            r => (r.GetString(0), r.GetString(1)),
            ("userId", userId));

    private async Task<bool> TableExistsAsync(string table) =>
        (await QueryAsync(
            """SELECT COUNT(*) FROM information_schema.tables WHERE table_name = @table;""",
            r => r.GetInt64(0),
            ("table", table))).Single() > 0;
}
