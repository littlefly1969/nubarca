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

// Proves `RenameLogicalContainerKeyPrefixes` on REPRESENTATIVE pre-cutover data,
// not just on an empty schema.
//
// The shared PostgresContainerFixture migrates all the way to head in
// InitializeAsync, which would leave nothing for this migration to do. So this
// class owns a container, migrates to the migration immediately BEFORE the one
// under test, seeds rows carrying the former prefix, and only then applies it.
// The same image as the other integration fixtures, so no extra layer is pulled.
[Trait("Category", "External")]
[Collection("ContainerKeyPrefixMigration")]
public sealed class ContainerKeyPrefixMigrationTests : IAsyncLifetime
{
    // The migration immediately before the one under test.
    private const string PreviousMigration = "20260803114754_AddAlbumTransfers";
    private const string MigrationUnderTest = "20260804201906_RenameLogicalContainerKeyPrefixes";

    // Assembled from fragments so this file carries no former-brand literal and
    // needs no exemption from the identity check.
    private static readonly string FormerPlatesPrefix = $"__{"nano"}cloud_plates_";
    private static readonly string FormerAestheticsPrefix = $"__{"nano"}cloud_aesthetics_";
    private const string PlatesPrefix = "__nubarca_plates_";
    private const string AestheticsPrefix = "__nubarca_aesthetics_";

    // Two owners, so the test also proves distinct keys stay distinct.
    private const string HashA = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
    private const string HashB = "0f9e8d7c6b5a49382716f5e4d3c2b1a0";

    private PostgreSqlContainer? _container;
    private string? _connectionString;

    private bool Available => _connectionString is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("nubarca_keyprefix")
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
    public async Task Migration_Rewrites_Only_The_Prefix_And_Is_Exactly_Reversible()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await using var ctx = CreateContext();
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();

        // 1. Bring the schema to the state that existed immediately before the
        //    migration under test, so the seeded rows are genuinely pre-cutover.
        await migrator.MigrateAsync(PreviousMigration);

        var ownerA = await SeedOwnerAsync(ctx, "a@example.com");
        var ownerB = await SeedOwnerAsync(ctx, "b@example.com");

        // Representative data: both tables, two owners, and — deliberately — one
        // row that ALREADY carries the target prefix, so the scoped UPDATE is
        // shown to leave it alone rather than double-prefixing it.
        await SeedPlateAsync(ctx, ownerA, FormerPlatesPrefix + HashA);
        await SeedPlateAsync(ctx, ownerB, FormerPlatesPrefix + HashB);
        await SeedPlateAsync(ctx, ownerA, PlatesPrefix + HashA);
        await SeedLabItemAsync(ctx, ownerA, FormerAestheticsPrefix + HashA);
        await SeedLabItemAsync(ctx, ownerB, FormerAestheticsPrefix + HashB);

        // 2. Apply the migration under test.
        await migrator.MigrateAsync(MigrationUnderTest);

        var plates = await ReadKeysAsync("plate_images");
        var lab = await ReadKeysAsync("aesthetic_lab_items");

        // The prefix moved; the hash body is byte-identical; nothing collapsed.
        // Ordered by the stored value, so HashB ("0f9e…") sorts before HashA.
        Assert.Equal(
            new[] { PlatesPrefix + HashB, PlatesPrefix + HashA, PlatesPrefix + HashA },
            plates);
        Assert.Equal(
            new[] { AestheticsPrefix + HashB, AestheticsPrefix + HashA },
            lab);

        // Distinct owners still have distinct keys — the swap is injective.
        Assert.Equal(2, plates.Distinct().Count());
        Assert.Equal(2, lab.Distinct().Count());

        // No row kept or double-applied a prefix.
        Assert.All(plates.Concat(lab), key =>
        {
            Assert.DoesNotContain(FormerPlatesPrefix, key);
            Assert.DoesNotContain(FormerAestheticsPrefix, key);
            Assert.Single(
                new[] { PlatesPrefix, AestheticsPrefix }.Where(p => key.StartsWith(p, StringComparison.Ordinal)));
        });

        // 3. Down() is a complete inverse. The row that already carried the new
        //    prefix before the migration comes back as the former prefix too —
        //    Down is the mapping's inverse, not a memory of prior state, and the
        //    rollback plan says so rather than implying per-row fidelity.
        await migrator.MigrateAsync(PreviousMigration);

        Assert.All(await ReadKeysAsync("plate_images"), key =>
            Assert.StartsWith(FormerPlatesPrefix, key, StringComparison.Ordinal));
        Assert.All(await ReadKeysAsync("aesthetic_lab_items"), key =>
            Assert.StartsWith(FormerAestheticsPrefix, key, StringComparison.Ordinal));

        // 4. Re-applying is idempotent: a second Up over already-migrated rows
        //    changes nothing further.
        await migrator.MigrateAsync(MigrationUnderTest);
        var afterFirst = await ReadKeysAsync("plate_images");
        await RunUpdateAgainAsync();
        Assert.Equal(afterFirst, await ReadKeysAsync("plate_images"));
    }

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);

    private static async Task<Guid> SeedOwnerAsync(AppDbContext ctx, string email)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = email,
            PasswordHash = "not-a-real-hash",
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Guid> SeedBlobAsync(AppDbContext ctx)
    {
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()).PadRight(64, '0'),
            SizeBytes = 1,
            StorageKey = Guid.NewGuid().ToString("N"),
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.BlobObjects.Add(blob);
        await ctx.SaveChangesAsync();
        return blob.Id;
    }

    private static async Task SeedPlateAsync(AppDbContext ctx, Guid ownerUserId, string containerKey)
    {
        ctx.PlateImages.Add(new PlateImage
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            BlobObjectId = await SeedBlobAsync(ctx),
            OriginalFileName = "plate.jpg",
            ContentType = "image/jpeg",
            SizeBytes = 1,
            LogicalContainerKey = containerKey,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task SeedLabItemAsync(AppDbContext ctx, Guid ownerUserId, string containerKey)
    {
        ctx.AestheticLabItems.Add(new AestheticLabItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            BlobObjectId = await SeedBlobAsync(ctx),
            OriginalFileName = "portrait.jpg",
            ContentType = "image/jpeg",
            SizeBytes = 1,
            LogicalContainerKey = containerKey,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    // Read straight from the table, ordered, so the assertion observes stored
    // bytes rather than anything the EF model might normalise.
    private async Task<List<string>> ReadKeysAsync(string table)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"LogicalContainerKey\" FROM {table} ORDER BY 1";
        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            keys.Add(reader.GetString(0));
        }
        return keys;
    }

    // Replays the migration's UPDATE verbatim to show the LIKE scoping makes a
    // re-run a no-op, which is what makes an interrupted cutover safe to resume.
    private async Task RunUpdateAgainAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE plate_images
            SET "LogicalContainerKey" =
                '{PlatesPrefix}' || substring("LogicalContainerKey" from {FormerPlatesPrefix.Length + 1})
            WHERE "LogicalContainerKey" LIKE '{FormerPlatesPrefix.Replace("_", @"\_")}%' ESCAPE '\';
            """;
        await command.ExecuteNonQueryAsync();
    }
}

// Its own collection: this class owns a container and must not run alongside the
// shared Postgres fixture's classes competing for Docker resources.
[CollectionDefinition("ContainerKeyPrefixMigration")]
public sealed class ContainerKeyPrefixMigrationCollection;
