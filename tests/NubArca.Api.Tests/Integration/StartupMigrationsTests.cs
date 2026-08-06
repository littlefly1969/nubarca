using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// Slice 48 — opt-in startup migrations. Uses a per-class Postgres container
// (NOT the shared `PostgresContainerFixture`) because the shared fixture
// pre-migrates the schema in InitializeAsync, which would defeat a test that
// has to start from an empty database. The image is the same `postgres:17-
// alpine` so the test reuses the already-pulled layer; container startup is
// only paid once for this class, not once per test.
[Trait("Category", "External")]
public sealed class StartupMigrationsTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _connectionString;

    private bool Available => _connectionString is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("nubarca_startup")
                .WithUsername("nubarca")
                .WithPassword("nubarca")
                .Build();

            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupMigrationsTests] Skipping: {ex.GetType().Name}: {ex.Message}");
            _connectionString = null;
            if (_container is not null)
            {
                try { await _container.DisposeAsync(); } catch { /* best-effort */ }
                _container = null;
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    [SkippableFact]
    public async Task Flag_On_Applies_Migrations_On_Startup()
    {
        Skip.IfNot(Available, "Docker / Postgres container unavailable");

        await ResetSchemaAsync();

        using var factory = new MigrateOnStartupFactory(_connectionString!, migrateOnStartup: true);
        using var client = factory.CreateClient();

        // /health doesn't touch the DB, but reaching it proves the host built
        // — which in turn proves startup migration didn't throw.
        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        Assert.True(await HasMigrationsHistoryAsync(), "__EFMigrationsHistory should exist after startup migration.");
        Assert.True(await TableExistsAsync("users"), "users table should exist after startup migration.");
    }

    [SkippableFact]
    public async Task Flag_Off_Does_Not_Apply_Migrations_On_Startup()
    {
        Skip.IfNot(Available, "Docker / Postgres container unavailable");

        await ResetSchemaAsync();

        using var factory = new MigrateOnStartupFactory(_connectionString!, migrateOnStartup: false);
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        // Default (flag absent / false) must leave the database untouched, so
        // an operator running `up -d` against a populated DB never sees a
        // surprise migration. Schema was reset to empty above; if anything
        // ran a MigrateAsync we'd see the history table.
        Assert.False(await HasMigrationsHistoryAsync(),
            "__EFMigrationsHistory should NOT exist when MigrateOnStartup is off.");
        Assert.False(await TableExistsAsync("users"),
            "users table should NOT exist when MigrateOnStartup is off.");
    }

    [SkippableFact]
    public async Task Flag_On_Is_Idempotent_On_Already_Migrated_Db()
    {
        Skip.IfNot(Available, "Docker / Postgres container unavailable");

        await ResetSchemaAsync();

        // First boot — should apply migrations.
        using (var factory = new MigrateOnStartupFactory(_connectionString!, migrateOnStartup: true))
        {
            using var client = factory.CreateClient();
            (await client.GetAsync("/health")).EnsureSuccessStatusCode();
        }

        var appliedAfterFirst = await CountAppliedMigrationsAsync();
        Assert.True(appliedAfterFirst > 0);

        // Second boot — same flag, already-migrated DB. Must be a clean no-op.
        using (var factory = new MigrateOnStartupFactory(_connectionString!, migrateOnStartup: true))
        {
            using var client = factory.CreateClient();
            (await client.GetAsync("/health")).EnsureSuccessStatusCode();
        }

        Assert.Equal(appliedAfterFirst, await CountAppliedMigrationsAsync());
    }

    // Wipe the database back to a brand-new state. Equivalent to dropping the
    // database and re-creating it, without paying for a fresh container.
    private async Task ResetSchemaAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString!);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP SCHEMA public CASCADE; CREATE SCHEMA public;";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> HasMigrationsHistoryAsync()
        => await TableExistsAsync("__EFMigrationsHistory");

    private async Task<bool> TableExistsAsync(string tableName)
    {
        await using var conn = new NpgsqlConnection(_connectionString!);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT EXISTS(SELECT 1 FROM information_schema.tables " +
            "WHERE table_schema = 'public' AND table_name = @t);";
        cmd.Parameters.AddWithValue("@t", tableName);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<int> CountAppliedMigrationsAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString!)
            .Options;
        await using var ctx = new AppDbContext(options);
        return (await ctx.Database.GetAppliedMigrationsAsync()).Count();
    }
}

// Real WebApplicationFactory bound to the per-class Postgres container. Sets
// only the two settings the slice cares about: the connection string and the
// Database:MigrateOnStartup flag. Everything else (auth, services, etc.) goes
// through the production Program.cs code path so this exercises the same
// startup flow a real deploy would.
internal sealed class MigrateOnStartupFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly bool _migrateOnStartup;
    private readonly string _storageRoot;

    public MigrateOnStartupFactory(string connectionString, bool migrateOnStartup)
    {
        _connectionString = connectionString;
        _migrateOnStartup = migrateOnStartup;
        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-startup-mig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", _connectionString);
        builder.UseSetting("Database:MigrateOnStartup", _migrateOnStartup ? "true" : "false");
        builder.UseSetting("Storage:RootPath", _storageRoot);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}
