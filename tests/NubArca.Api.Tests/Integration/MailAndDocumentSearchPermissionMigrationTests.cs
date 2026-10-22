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

// `mail.access` and `document-search.access` reaching an installation that
// ALREADY EXISTS. Before them, mail and documents answered every signed-in
// user; the migration is what keeps every Member account using them after the
// upgrade, while Restricted and an operator's own role are left exactly as they
// were.
[Trait("Category", "External")]
[Collection("RoleMigration")]
public sealed class MailAndDocumentSearchPermissionMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261022033406_AddMailMessageTexts";
    private const string MigrationUnderTest = "20261022181107_AddMailAndDocumentSearchPermissions";
    private static readonly string[] Keys = ["document-search.access", "mail.access"];
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
                .WithDatabase("nubarca_maildocsperm")
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
    public async Task Member_Gains_Both_Keys_And_Nobody_Else_Is_Widened()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);
        await SeedCustomRoleAsync();
        var memberBefore = await PermissionsOfAsync(RoleKeys.Member);
        var customBefore = await PermissionsOfAsync(CustomRole);
        Assert.DoesNotContain(memberBefore, k => Keys.Contains(k));

        await MigrateAsync(MigrationUnderTest);

        var memberAfter = await PermissionsOfAsync(RoleKeys.Member);
        Assert.Equal(memberBefore.Concat(Keys).OrderBy(k => k, StringComparer.Ordinal), memberAfter);
        Assert.Empty(await PermissionsOfAsync(RoleKeys.Restricted));
        Assert.Equal(customBefore, await PermissionsOfAsync(CustomRole));

        // Administrator holds the whole catalogue, re-synced on boot.
        Assert.Contains(Permissions.MailAccess, RoleDefaults.AdministratorPermissions);
        Assert.Contains(Permissions.DocumentSearchAccess, RoleDefaults.AdministratorPermissions);
        Assert.Equal(Keys, new[] { Permissions.DocumentSearchAccess, Permissions.MailAccess });
    }

    [Fact]
    public async Task Applying_It_Twice_Is_A_No_Op()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);
        await MigrateAsync(MigrationUnderTest);
        await MigrateAsync(PreviousMigration);
        await ExecuteAsync(
            """
            INSERT INTO role_permissions ("RoleKey", "PermissionKey")
            VALUES ('Member', 'mail.access');
            """);
        await MigrateAsync(MigrationUnderTest);

        var member = await PermissionsOfAsync(RoleKeys.Member);
        Assert.All(Keys, key => Assert.Equal(1, member.Count(k => k == key)));
    }

    [Fact]
    public async Task Rollback_Removes_Only_The_Rows_It_Added()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);
        await SeedCustomRoleAsync();
        await MigrateAsync(MigrationUnderTest);
        await ExecuteAsync(
            """
            INSERT INTO role_permissions ("RoleKey", "PermissionKey")
            VALUES (@role, 'mail.access');
            """,
            ("role", CustomRole));

        await MigrateAsync(PreviousMigration);

        Assert.DoesNotContain(await PermissionsOfAsync(RoleKeys.Member), k => Keys.Contains(k));
        Assert.Contains("mail.access", await PermissionsOfAsync(CustomRole));
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
