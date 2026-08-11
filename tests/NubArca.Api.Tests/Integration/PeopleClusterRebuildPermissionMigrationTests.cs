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

// `people.cluster.rebuild` reaching an installation that ALREADY EXISTS.
//
// The catalogue gives the key to a fresh installation for free — Member is
// derived from the non-administrative keys, and Administrator is re-synced to
// the whole catalogue on every boot. Neither of those helps a database that is
// already running: the role seeder never rewrites a built-in role that is
// present, precisely so an operator's edits survive a deploy. So the one row
// this migration writes is the difference between "every existing account can
// rebuild their own face groups" and "only accounts created after the upgrade
// can", and it is worth proving on a real PostgreSQL rather than assuming.
//
// The other half is what it must NOT do. Restricted is empty by design and a
// custom role belongs to the operator; a release that quietly widened either
// would be a worse defect than the missing capability.
[Trait("Category", "External")]
[Collection("RoleMigration")]
public sealed class PeopleClusterRebuildPermissionMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260810161235_AddPersonFaceReferences";
    private const string MigrationUnderTest = "20260811140834_AddPeopleClusterRebuildPermission";
    private const string Key = "people.cluster.rebuild";
    private const string CustomRole = "custom:operator-built";

    private PostgreSqlContainer? _container;
    private string? _connectionString;

    private bool Available => _connectionString is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("nubarca_clusterrebuildperm")
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

    [Fact]
    public async Task Member_Gains_The_Key_And_Nobody_Else_Is_Widened()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);

        // The state a real installation is in before the upgrade: the three
        // built-in roles from the roles migration, plus a role the operator
        // built for themselves.
        await SeedCustomRoleAsync();
        var memberBefore = await PermissionsOfAsync(RoleKeys.Member);
        var customBefore = await PermissionsOfAsync(CustomRole);
        Assert.DoesNotContain(Key, memberBefore);
        Assert.DoesNotContain(Key, await PermissionsOfAsync(RoleKeys.Restricted));
        Assert.DoesNotContain(Key, customBefore);

        await MigrateAsync(MigrationUnderTest);

        // Member gains it — once, and without losing anything it had.
        var memberAfter = await PermissionsOfAsync(RoleKeys.Member);
        Assert.Contains(Key, memberAfter);
        Assert.Equal(1, memberAfter.Count(k => k == Key));
        Assert.Equal(memberBefore.Concat([Key]).OrderBy(k => k, StringComparer.Ordinal), memberAfter);

        // Restricted is empty by design and stays that way.
        Assert.DoesNotContain(Key, await PermissionsOfAsync(RoleKeys.Restricted));
        // The operator's own role is exactly as they left it.
        Assert.Equal(customBefore, await PermissionsOfAsync(CustomRole));

        // Administrator's authority is CATALOGUE-driven and re-synced on boot,
        // so reading role_permissions here would test the wrong thing: what
        // matters is that the key is part of what an Administrator always holds.
        Assert.Contains(Key, RoleDefaults.AdministratorPermissions);
        Assert.Equal(Permissions.PeopleClusterRebuild, Key);
    }

    [Fact]
    public async Task Applying_It_Twice_Is_A_No_Op()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);
        await MigrateAsync(MigrationUnderTest);

        // A fresh installation can already have the row from the catalogue-based
        // seeding before this ever runs, so the insert has to be guarded.
        await ExecuteAsync(
            """
            INSERT INTO role_permissions ("RoleKey", "PermissionKey")
            SELECT 'Member', 'people.cluster.rebuild'
            WHERE EXISTS (SELECT 1 FROM access_roles WHERE "Key" = 'Member')
              AND NOT EXISTS (
                SELECT 1 FROM role_permissions
                WHERE "RoleKey" = 'Member' AND "PermissionKey" = 'people.cluster.rebuild');
            """);

        Assert.Equal(1, (await PermissionsOfAsync(RoleKeys.Member)).Count(k => k == Key));
    }

    [Fact]
    public async Task Rollback_Removes_Only_The_Row_It_Added()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);
        await SeedCustomRoleAsync();
        await MigrateAsync(MigrationUnderTest);
        Assert.Contains(Key, await PermissionsOfAsync(RoleKeys.Member));

        // An operator who granted the same key to their own role afterwards must
        // keep it: the Down names Member explicitly and cannot reach anything else.
        await ExecuteAsync(
            """
            INSERT INTO role_permissions ("RoleKey", "PermissionKey")
            VALUES (@role, 'people.cluster.rebuild');
            """,
            ("role", CustomRole));

        await MigrateAsync(PreviousMigration);

        Assert.DoesNotContain(Key, await PermissionsOfAsync(RoleKeys.Member));
        Assert.Contains(Key, await PermissionsOfAsync(CustomRole));
    }

    // ---- plumbing ---------------------------------------------------------

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString!)
            .Options;
        return new AppDbContext(options);
    }

    private async Task MigrateAsync(string target)
    {
        await using var ctx = CreateContext();
        await ctx.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(target);
    }

    private Task SeedCustomRoleAsync() => ExecuteAsync(
        """
        INSERT INTO access_roles
            ("Key", "Name", "Description", "IsSystem", "IsAdministrator",
             "CreatedAt", "UpdatedAt", "Version")
        VALUES (@role, 'Operator built', null, false, false, now(), now(), 1)
        ON CONFLICT ("Key") DO NOTHING;

        INSERT INTO role_permissions ("RoleKey", "PermissionKey")
        SELECT @role, 'people.access'
        WHERE NOT EXISTS (
            SELECT 1 FROM role_permissions
            WHERE "RoleKey" = @role AND "PermissionKey" = 'people.access');
        """,
        ("role", CustomRole));

    private async Task<string[]> PermissionsOfAsync(string roleKey)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """SELECT "PermissionKey" FROM role_permissions WHERE "RoleKey" = @role ORDER BY "PermissionKey";""";
        command.Parameters.AddWithValue("role", roleKey);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }
        return result.ToArray();
    }

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
}
