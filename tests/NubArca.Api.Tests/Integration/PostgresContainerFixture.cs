using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using Testcontainers.PostgreSql;

namespace NubArca.Api.Tests.Integration;

// Boots an ephemeral PostgreSQL container for the lifetime of the integration
// test collection (see PostgresIntegrationCollection). If Docker is not reachable,
// the fixture silently flags itself unavailable; tests that use it should call
// Skip.IfNot(fixture.Available, ...) so they are skipped, not failed, on machines
// without Docker.
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string? ConnectionString { get; private set; }

    public bool Available => ConnectionString is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("nubarca")
                .WithUsername("nubarca")
                .WithPassword("nubarca")
                .Build();

            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(ConnectionString)
                .Options;

            await using var ctx = new AppDbContext(options);
            await ctx.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PostgresContainerFixture] Skipping integration tests: {ex.GetType().Name}: {ex.Message}");
            ConnectionString = null;
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

    // Truncates every domain table for a clean slate between tests. Safe to call
    // when the fixture is unavailable (no-op).
    public async Task ResetDatabaseAsync()
    {
        if (!Available)
        {
            return;
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString!)
            .Options;

        await using var ctx = new AppDbContext(options);
        await ctx.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE share_links, file_items, folders, audit_logs, blob_objects, users RESTART IDENTITY CASCADE;");
    }
}
