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

// Proves the IsAdmin → RoleKey cutover on REPRESENTATIVE pre-migration data.
//
// This is the migration whose ordering is its correctness argument: RoleKey is
// added, backfilled from IsAdmin, and only then is IsAdmin dropped. A scaffolded
// migration drops first, which would silently demote every administrator — and
// the failure would not show up until somebody tried to open /admin in
// production. So the check runs against a real PostgreSQL, on rows that carry
// the old flag, before the deploy rather than after it.
//
// It also proves the promise made to every EXISTING user: an old non-admin
// becomes a Member, and Member carries every non-administrative feature
// permission, so nobody loses access.
[Trait("Category", "External")]
[Collection("RoleMigration")]
public sealed class RoleMigrationTests : IAsyncLifetime
{
    // The migration immediately before the one under test.
    private const string PreviousMigration = "20260804201906_RenameLogicalContainerKeyPrefixes";
    private const string MigrationUnderTest = "20260807191448_AddRolesPermissionsAndPasswordRecovery";

    private PostgreSqlContainer? _container;
    private string? _connectionString;

    private bool Available => _connectionString is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("nubarca_rolemigration")
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
    public async Task Existing_Admins_Become_Administrators_And_Everyone_Else_Becomes_A_Member()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await using var ctx = CreateContext();
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();

        // 1. The schema exactly as it stood before roles existed.
        await migrator.MigrateAsync(PreviousMigration);

        // Two administrators and three ordinary users, inserted through raw SQL
        // because the EF model no longer has the column they are defined by.
        var adminA = await SeedLegacyUserAsync("admin-a@example.com", isAdmin: true);
        var adminB = await SeedLegacyUserAsync("admin-b@example.com", isAdmin: true);
        var plainA = await SeedLegacyUserAsync("plain-a@example.com", isAdmin: false);
        var plainB = await SeedLegacyUserAsync("plain-b@example.com", isAdmin: false);
        // A disabled ordinary user still has to map correctly; being disabled is
        // an independent axis from the role.
        var disabled = await SeedLegacyUserAsync("disabled@example.com", isAdmin: false, disabled: true);

        // 2. Apply the migration under test.
        await migrator.MigrateAsync(MigrationUnderTest);

        var roles = await ReadRolesAsync();

        Assert.Equal(RoleKeys.Administrator, roles[adminA]);
        Assert.Equal(RoleKeys.Administrator, roles[adminB]);
        Assert.Equal(RoleKeys.Member, roles[plainA]);
        Assert.Equal(RoleKeys.Member, roles[plainB]);
        Assert.Equal(RoleKeys.Member, roles[disabled]);

        // The mapping is total: no row was left without a role.
        Assert.Equal(5, roles.Count);
        Assert.All(roles.Values, role => Assert.True(RoleKeys.IsKnown(role)));

        // Nobody was accidentally made Restricted — the failure mode that would
        // silently take People, Laboratory, Cloud Functions and the Private
        // Vault away from every existing account.
        Assert.DoesNotContain(RoleKeys.Restricted, roles.Values);

        // The column that used to decide access is gone, so nothing can read it.
        Assert.False(await ColumnExistsAsync("users", "IsAdmin"));

        // Every migrated non-admin keeps every non-administrative feature.
        foreach (var definition in PermissionCatalog.All.Where(p => !p.Administrative))
        {
            Assert.Contains(definition.Key, RolePermissionCatalog.For(RoleKeys.Member));
        }
        // …and gains no administrative one.
        foreach (var definition in PermissionCatalog.All.Where(p => p.Administrative))
        {
            Assert.DoesNotContain(definition.Key, RolePermissionCatalog.For(RoleKeys.Member));
        }

        // Session versioning starts from a value a cookie can compare against.
        Assert.All(await ReadSecurityVersionsAsync(), version => Assert.Equal(1, version));
    }

    [Fact]
    public async Task The_Rollback_Restores_The_Previous_Administrators()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await using var ctx = CreateContext();
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigration);
        var admin = await SeedLegacyUserAsync("rollback-admin@example.com", isAdmin: true);
        var plain = await SeedLegacyUserAsync("rollback-plain@example.com", isAdmin: false);

        await migrator.MigrateAsync(MigrationUnderTest);
        await migrator.MigrateAsync(PreviousMigration);

        var flags = await ReadAdminFlagsAsync();
        Assert.True(flags[admin]);
        Assert.False(flags[plain]);
    }

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);

    private async Task<Guid> SeedLegacyUserAsync(string email, bool isAdmin, bool disabled = false)
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO users ("Id", "Email", "DisplayName", "PasswordHash", "CreatedAt",
                               "DisabledAt", "IsAdmin", "UiLanguage")
            VALUES (@id, @email, @email, 'not-a-real-hash', now(), @disabledAt, @isAdmin, 'it');
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("isAdmin", isAdmin);
        command.Parameters.AddWithValue(
            "disabledAt", disabled ? DateTime.UtcNow : (object)DBNull.Value);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<Dictionary<Guid, string>> ReadRolesAsync()
    {
        var result = new Dictionary<Guid, string>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "Id", "RoleKey" FROM users;""";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetGuid(0)] = reader.GetString(1);
        }
        return result;
    }

    private async Task<Dictionary<Guid, bool>> ReadAdminFlagsAsync()
    {
        var result = new Dictionary<Guid, bool>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "Id", "IsAdmin" FROM users;""";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetGuid(0)] = reader.GetBoolean(1);
        }
        return result;
    }

    private async Task<List<int>> ReadSecurityVersionsAsync()
    {
        var result = new List<int>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "SecurityVersion" FROM users;""";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetInt32(0));
        }
        return result;
    }

    private async Task<bool> ColumnExistsAsync(string table, string column)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_name = @table AND column_name = @column;
            """;
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }
}

// Its own collection: this class owns a container and must not run alongside the
// shared Postgres fixture's classes competing for Docker resources.
[CollectionDefinition("RoleMigration")]
public sealed class RoleMigrationCollection;
