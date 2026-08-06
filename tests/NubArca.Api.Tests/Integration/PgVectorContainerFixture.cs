using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using Testcontainers.PostgreSql;

namespace NubArca.Api.Tests.Integration;

// Boots an ephemeral pgvector-enabled PostgreSQL container (pgvector/pgvector:pg17,
// same MAJOR version as prod) for the photo-similarity vector integration tests.
// Mirrors PostgresContainerFixture but with the pgvector image so the
// AddPhotoVectorIndex768 migration actually creates the `vector` table + HNSW
// index. If Docker / the image is not reachable, the fixture flags itself
// unavailable and the tests Skip rather than fail.
public sealed class PgVectorContainerFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string? ConnectionString { get; private set; }

    public bool Available => ConnectionString is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("pgvector/pgvector:pg17")
                .WithDatabase("nubarca")
                .WithUsername("nubarca")
                .WithPassword("nubarca")
                .Build();

            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();

            // Apply ALL migrations (incl. AddPhotoVectorIndex768) — on this image
            // pgvector is available, so the vector table + HNSW index are created.
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(ConnectionString)
                .Options;
            await using var ctx = new AppDbContext(options);
            await ctx.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PgVectorContainerFixture] Skipping pgvector integration tests: {ex.GetType().Name}: {ex.Message}");
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
            """
            DO $$
            DECLARE table_list text;
            BEGIN
                SELECT string_agg(format('%I.%I', schemaname, tablename), ', ')
                INTO table_list
                FROM pg_tables
                WHERE schemaname = 'public'
                  AND tablename <> '__EFMigrationsHistory';

                IF table_list IS NOT NULL THEN
                    EXECUTE 'TRUNCATE TABLE ' || table_list || ' RESTART IDENTITY CASCADE';
                END IF;
            END $$;
            """);
    }
}
