using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// RemoveSystemRagIndex drops the system RAG tables (product help and the
// repository) and their raw-SQL pgvector mirror, and must leave the
// owner-private document tables alone; it also closes leftover AI tags jobs.
// Real PostgreSQL, because the mirror is PostgreSQL SQL outside the EF model.
// In the CI merge gate (scripts/assert-concurrency-gate.py), so a skip fails.
[Trait("Category", "External")]
[Collection("RemoveSystemRagIndexMigration")]
public sealed class RemoveSystemRagIndexMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261022200650_RemoveAestheticsLab";
    private const string MigrationUnderTest = "20261022223556_RemoveSystemRagIndex";

    private static readonly string[] SystemTables =
    {
        "rag_sources", "rag_domain_sources", "rag_chunks", "rag_chunk_embeddings",
    };

    private static readonly string[] DocumentTables =
    {
        "document_texts", "document_chunks", "document_chunk_embeddings",
    };

    private PostgreSqlContainer? _container;
    private string? _connectionString;

    private bool Available => _connectionString is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("nubarca_remove_system_rag")
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

    [SkippableFact]
    public async Task Drops_The_System_Index_And_Keeps_The_Document_Tables()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await using var ctx = CreateContext();
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        foreach (var table in SystemTables) Assert.True(await TableExistsAsync(table), table);
        // Stands in for the pgvector mirror on an image without the extension:
        // the migration must drop it whether or not it is there.
        await ExecuteAsync("CREATE TABLE IF NOT EXISTS rag_chunk_embedding_vectors_384 (\"Id\" uuid PRIMARY KEY)");

        await migrator.MigrateAsync(MigrationUnderTest);

        foreach (var table in SystemTables) Assert.False(await TableExistsAsync(table), table);
        Assert.False(await TableExistsAsync("rag_chunk_embedding_vectors_384"));
        foreach (var table in DocumentTables) Assert.True(await TableExistsAsync(table), table);
    }

    [SkippableFact]
    public async Task Rolls_Back_To_Empty_Tables_And_Forward_Again()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await using var ctx = CreateContext();
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(MigrationUnderTest);

        await migrator.MigrateAsync(PreviousMigration);
        foreach (var table in SystemTables) Assert.True(await TableExistsAsync(table), table);

        await migrator.MigrateAsync(MigrationUnderTest);
        foreach (var table in SystemTables) Assert.False(await TableExistsAsync(table), table);
    }

    [SkippableFact]
    public async Task Closes_Leftover_Tag_Jobs_And_Leaves_Every_Other_Job_Alone()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await using var ctx = CreateContext();
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        // background_jobs is the same shape on both sides of the migration.
        var queuedTag = NewJob("ai.tags.generate.backfill", "queued");
        var runningTag = NewJob("ai.tags.generate.backfill", "running");
        runningTag.LockOwner = "worker-1";
        var failedTag = NewJob("ai.tags.generate.backfill", "failed");
        var otherQueued = NewJob("ai.faces.cluster.backfill", "queued");
        var otherRunning = NewJob("ai.documents.index", "running");
        otherRunning.LockOwner = "worker-1";
        ctx.BackgroundJobs.AddRange(queuedTag, runningTag, failedTag, otherQueued, otherRunning);
        await ctx.SaveChangesAsync();

        await migrator.MigrateAsync(MigrationUnderTest);

        Assert.Equal(("cancelled", (string?)null), await JobStateAsync(queuedTag.Id));
        Assert.Equal(("cancelled", (string?)null), await JobStateAsync(runningTag.Id));
        Assert.Equal(("failed", (string?)null), await JobStateAsync(failedTag.Id));
        Assert.Equal(("queued", (string?)null), await JobStateAsync(otherQueued.Id));
        Assert.Equal(("running", "worker-1"), await JobStateAsync(otherRunning.Id));
    }

    private static BackgroundJob NewJob(string type, string status) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Status = status,
        CreatedAt = DateTime.UtcNow,
        AvailableAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private async Task<(string Status, string? LockOwner)> JobStateAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Status\", \"LockOwner\" FROM background_jobs WHERE \"Id\" = @id";
        command.Parameters.Add(new NpgsqlParameter("id", id));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private AppDbContext CreateContext()
        => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@t) IS NOT NULL";
        command.Parameters.Add(new NpgsqlParameter("t", "public." + table));
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("RemoveSystemRagIndexMigration")]
public sealed class RemoveSystemRagIndexMigrationCollection;
