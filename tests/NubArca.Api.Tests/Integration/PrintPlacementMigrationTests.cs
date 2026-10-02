using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// Proves `AddPrintPlacementOwnerPrintAndMediaRemaining` on real PostgreSQL:
// every framing already stored keeps its exact value, the relaxed zoom
// constraints admit a photograph zoomed OUT but still refuse nonsense, and the
// new columns and table hold their own invariants. Own container, migrated to
// the migration immediately BEFORE the one under test.
[Trait("Category", "External")]
[Collection("PrintPlacementMigration")]
public sealed class PrintPlacementMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261001223659_AddGuestbookLiveControls";
    private const string MigrationUnderTest = "20261002162917_AddPrintPlacementOwnerPrintAndMediaRemaining";

    private PostgreSqlContainer? _container;
    private string? _connectionString;

    private bool Available => _connectionString is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("nubarca_placement")
                .WithUsername("nubarca")
                .WithPassword("nubarca")
                .Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception)
        {
            _connectionString = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    [SkippableFact]
    public async Task Stored_Framings_Survive_Unchanged_And_The_New_Rules_Hold()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");
        await using var ctx = CreateContext();
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        var memory = Guid.NewGuid();
        var source = Guid.NewGuid();
        var device = Guid.NewGuid();
        await SeedAsync(Memory(memory, 1.4));
        await SeedAsync($"""
            INSERT INTO print_job_sources ("Id", "PrintJobId", "SlotIndex", "FileItemId", "CropX", "CropY", "CropWidth", "CropHeight")
            VALUES ('{source}', '{Guid.NewGuid()}', 0, '{Guid.NewGuid()}', 0.1, 0.2, 0.5, 0.6);
            INSERT INTO printer_devices ("Id", "PrintStationId", "DeviceKey", "DisplayName", "AdapterKind",
                "CapabilitiesJson", "LastObservedState", "LastSeenAt")
            VALUES ('{device}', '{Guid.NewGuid()}', 'dnp', 'DNP', 'cups', '{"{"}{"}"}', 'ready', now());
            """);

        await migrator.MigrateAsync(MigrationUnderTest);

        // What was stored is exactly what it was.
        Assert.Equal(1.4, await ScalarAsync($"SELECT \"CropZoom\" FROM party_guestbook_entries WHERE \"Id\" = '{memory}'"));
        Assert.Equal("0.1|0.2|0.5|0.6|||", await ScalarAsync(
            $"SELECT concat_ws('|', \"CropX\", \"CropY\", \"CropWidth\", \"CropHeight\", coalesce(\"PlacementCenterX\"::text, ''), " +
            $"coalesce(\"PlacementCenterY\"::text, ''), coalesce(\"PlacementZoom\"::text, '')) FROM print_job_sources WHERE \"Id\" = '{source}'"));
        Assert.Null(await ScalarAsync($"SELECT \"MediaRemainingPrints\" FROM printer_devices WHERE \"Id\" = '{device}'"));

        // A photograph zoomed out is a framing now; no zoom at all, or beyond 4, still is not.
        await SeedAsync(Memory(Guid.NewGuid(), 0.5));
        await Assert.ThrowsAsync<PostgresException>(() => SeedAsync(Memory(Guid.NewGuid(), 0)));
        await Assert.ThrowsAsync<PostgresException>(() => SeedAsync(Memory(Guid.NewGuid(), 4.5)));

        // A placement is all three numbers or none.
        await Assert.ThrowsAsync<PostgresException>(() => SeedAsync(
            $"UPDATE print_job_sources SET \"PlacementZoom\" = 0.8 WHERE \"Id\" = '{source}'"));
        await SeedAsync(
            $"UPDATE print_job_sources SET \"PlacementCenterX\" = 0.5, \"PlacementCenterY\" = 0.5, \"PlacementZoom\" = 0.8 WHERE \"Id\" = '{source}'");

        // The printer's count: none, or a plausible one.
        await SeedAsync($"UPDATE printer_devices SET \"MediaRemainingPrints\" = 187 WHERE \"Id\" = '{device}'");
        await Assert.ThrowsAsync<PostgresException>(() => SeedAsync(
            $"UPDATE printer_devices SET \"MediaRemainingPrints\" = -1 WHERE \"Id\" = '{device}'"));

        // One accepted direct print per owner and key.
        var owner = Guid.NewGuid();
        var key = new string('k', 64);
        var insert = $"""
            INSERT INTO owner_photo_print_requests ("Id", "OwnerUserId", "IdempotencyKeyHash", "RequestFingerprint", "PrintJobId", "CreatedAt")
            VALUES ('{Guid.NewGuid()}', '{owner}', '{key}', '{new string('f', 64)}', '{Guid.NewGuid()}', now());
            """;
        await SeedAsync(insert);
        await Assert.ThrowsAsync<PostgresException>(() => SeedAsync(insert));
    }

    private static string Memory(Guid id, double zoom) => $"""
        INSERT INTO party_guestbook_entries ("Id", "PartyId", "OwnerUserId", "AuthorDisplayName", "Body",
            "Status", "CreatedAt", "UpdatedAt", "BlobObjectId", "PhotoWidth", "PhotoHeight",
            "TemplateKey", "TemplateVersion", "CropCenterX", "CropCenterY", "CropZoom")
        VALUES ('{id}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', 'Ada', 'Un ricordo', 'visible', now(), now(),
            '{Guid.NewGuid()}', 1600, 1200, 'nubarca', 1, 0.5, 0.5, {zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)});
        """;

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);

    /// <summary>
    /// Runs SQL as a replica: foreign keys (triggers) are not checked, since the
    /// seed is not a whole party — CHECK constraints, which are under test, are.
    /// </summary>
    private async Task SeedAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET session_replication_role = replica", connection))
            await role.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }
}
