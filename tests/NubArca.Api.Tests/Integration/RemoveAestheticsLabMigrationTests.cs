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

// RemoveAestheticsLab drops six tables, so what it must get right is everything
// that pointed INTO them: the blob references the lab owned, the analysis jobs
// queued for it and the retired permission. Real PostgreSQL, because the
// migration's release statement is PostgreSQL SQL.
[Trait("Category", "External")]
[Collection("RemoveAestheticsLabMigration")]
public sealed class RemoveAestheticsLabMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261022181107_AddMailAndDocumentSearchPermissions";
    private const string MigrationUnderTest = "20261022200650_RemoveAestheticsLab";
    private const string AnalyzeJobType = "ai.aesthetics.human-aesexpert.analyze";

    private PostgreSqlContainer? _container;
    private string? _connectionString;

    private bool Available => _connectionString is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("nubarca_remove_aesthetics")
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
    public async Task Releases_Lab_References_Then_Drops_The_Lab()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await using var ctx = CreateContext();
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        var owner = await SeedOwnerAsync(ctx, "lab-owner@example.com");

        // A blob only the lab used: one lab item.
        var labOnly = await SeedBlobAsync(ctx, referenceCount: 1);
        var labOnlyItem = await SeedLabItemAsync(owner, labOnly);
        // A gallery photo added to the lab: one reference for its FileItem
        // (represented by the count alone) and one for the lab item.
        var shared = await SeedBlobAsync(ctx, referenceCount: 2);
        await SeedLabItemAsync(owner, shared);
        // A derived rendition of the lab-only item: its own blob, one reference.
        var derivative = await SeedBlobAsync(ctx, referenceCount: 1);
        await SeedLabDerivativeAsync(owner, labOnlyItem, derivative);
        // A blob the lab never touched.
        var unrelated = await SeedBlobAsync(ctx, referenceCount: 3);

        ctx.BackgroundJobs.Add(NewJob(AnalyzeJobType));
        ctx.BackgroundJobs.Add(NewJob("plates.analyze"));
        await ctx.SaveChangesAsync();
        // The built-in roles were seeded with the permission when roles became
        // first-class.
        Assert.True(await CountAsync(
            "SELECT count(*) FROM role_permissions WHERE \"PermissionKey\" = 'laboratory.aesthetics'") > 0);

        await migrator.MigrateAsync(MigrationUnderTest);

        var (labOnlyCount, labOnlyPurge) = await ReadBlobAsync(labOnly);
        Assert.Equal(0, labOnlyCount);
        Assert.NotNull(labOnlyPurge);

        var (sharedCount, sharedPurge) = await ReadBlobAsync(shared);
        Assert.Equal(1, sharedCount);
        Assert.Null(sharedPurge);

        var (derivativeCount, derivativePurge) = await ReadBlobAsync(derivative);
        Assert.Equal(0, derivativeCount);
        Assert.NotNull(derivativePurge);

        var (unrelatedCount, unrelatedPurge) = await ReadBlobAsync(unrelated);
        Assert.Equal(3, unrelatedCount);
        Assert.Null(unrelatedPurge);

        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM background_jobs WHERE \"Type\" = '{AnalyzeJobType}'"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM background_jobs WHERE \"Type\" = 'plates.analyze'"));
        Assert.Equal(0, await CountAsync(
            "SELECT count(*) FROM role_permissions WHERE \"PermissionKey\" = 'laboratory.aesthetics'"));
        Assert.Equal(0, await CountAsync(
            "SELECT count(*) FROM information_schema.tables WHERE table_name LIKE 'aesthetic\\_%'"));
    }

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);

    private static BackgroundJob NewJob(string type) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Status = "queued",
        CreatedAt = DateTime.UtcNow,
        AvailableAt = DateTime.UtcNow,
    };

    // The schema is one migration behind the model, which adds only the lab's
    // removal, so owners, blobs and jobs go through the model as usual.
    private static async Task<Guid> SeedOwnerAsync(AppDbContext ctx, string email)
    {
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            Email = email,
            DisplayName = email,
            PasswordHash = "not-a-real-hash",
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedBlobAsync(AppDbContext ctx, long referenceCount)
    {
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()).PadRight(64, '0'),
            SizeBytes = 1,
            StorageKey = Guid.NewGuid().ToString("N"),
            ReferenceCount = referenceCount,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.BlobObjects.Add(blob);
        await ctx.SaveChangesAsync();
        return blob.Id;
    }

    // The lab is no longer in the EF model, so its rows are written with SQL.
    private async Task<Guid> SeedLabItemAsync(Guid owner, Guid blobObjectId)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO aesthetic_lab_items
                ("Id", "OwnerUserId", "BlobObjectId", "OriginalFileName", "ContentType",
                 "SizeBytes", "LogicalContainerKey", "CreatedAt", "UpdatedAt")
            VALUES (@p0, @p1, @p2, 'portrait.jpg', 'image/jpeg', 1, '__nubarca_aesthetics_x', now(), now())
            """,
            new NpgsqlParameter("p0", id),
            new NpgsqlParameter("p1", owner),
            new NpgsqlParameter("p2", blobObjectId));
        return id;
    }

    private async Task SeedLabDerivativeAsync(Guid owner, Guid labItemId, Guid blobObjectId)
    {
        await ExecuteAsync(
            """
            INSERT INTO aesthetic_lab_derivatives
                ("Id", "OwnerUserId", "AestheticLabItemId", "Size", "BlobObjectId", "ContentType",
                 "Width", "Height", "SizeBytes", "CreatedAt")
            VALUES (@p0, @p1, @p2, 'small', @p3, 'image/webp', 1, 1, 1, now())
            """,
            new NpgsqlParameter("p0", Guid.NewGuid()),
            new NpgsqlParameter("p1", owner),
            new NpgsqlParameter("p2", labItemId),
            new NpgsqlParameter("p3", blobObjectId));
    }

    private async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(long Count, DateTime? PurgeEligibleAt)> ReadBlobAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"ReferenceCount\", \"PurgeEligibleAt\" FROM blob_objects WHERE \"Id\" = @id";
        command.Parameters.Add(new NpgsqlParameter("id", id));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetDateTime(1));
    }
}

[CollectionDefinition("RemoveAestheticsLabMigration")]
public sealed class RemoveAestheticsLabMigrationCollection;
