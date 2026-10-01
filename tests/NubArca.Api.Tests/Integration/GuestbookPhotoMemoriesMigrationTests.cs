using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// Proves `ReshapeGuestbookAsPhotoMemories` on REPRESENTATIVE prototype data.
//
// The migration is destructive by design — the text-only guest book was a
// prototype — so what matters is the ORDER: the old rows and the persisted
// quota go first, and only then do the NOT NULL columns and constraints
// arrive, with no placeholder default left behind for a row without a
// photograph to slip through. Down is checked too: the memories it cannot
// carry back must give their blob references back on the way out.
//
// Own container, migrated to the migration immediately BEFORE the one under
// test, exactly as ContainerKeyPrefixMigrationTests does.
[Trait("Category", "External")]
[Collection("GuestbookPhotoMemoriesMigration")]
public sealed class GuestbookPhotoMemoriesMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260930143101_AddPrinterSharesAndPaperLog";
    private const string MigrationUnderTest = "20261001110429_ReshapeGuestbookAsPhotoMemories";

    private PostgreSqlContainer? _container;
    private string? _connectionString;

    private bool Available => _connectionString is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("nubarca_guestbook")
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
    public async Task The_prototype_book_and_its_quota_go_before_the_photograph_becomes_required()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await using var ctx = CreateContext();
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        // A prototype dedication — unsigned, text only — and the guest who
        // spent three of their allowance on such things.
        var participantId = Guid.NewGuid();
        await SeedAsync(
            """
            INSERT INTO party_participants ("Id", "PartyAlbumLinkId", "TokenHash", "CreatedAt", "LastSeenAt",
                "AcceptedPhotoCount", "AcceptedVideoCount", "SubmittedMessageCount", "ChallengeVoteCount",
                "AcceptedPhotoPrintCount", "AcceptedStripPrintCount", "AcceptedGridPrintCount",
                "SubmittedGuestbookCount")
            VALUES (@participant, @link, @hash, now(), now(), 0, 0, 0, 0, 0, 0, 0, 3);
            INSERT INTO party_guestbook_entries ("Id", "PartyId", "OwnerUserId", "PartyAlbumLinkId",
                "PartyParticipantId", "AuthorDisplayName", "Body", "Status", "CreatedAt", "UpdatedAt")
            VALUES (@entry, @party, @owner, @link, @participant, NULL, 'Una dedica di prova', 'visible', now(), now());
            """,
            ("participant", participantId),
            ("link", Guid.NewGuid()),
            ("hash", new string('a', 64)),
            ("entry", Guid.NewGuid()),
            ("party", Guid.NewGuid()),
            ("owner", Guid.NewGuid()));

        await migrator.MigrateAsync(MigrationUnderTest);

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM party_guestbook_entries"));
        // The guest is still there; only the book's count went back to zero.
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM party_participants"));
        Assert.Equal(0L, await ScalarAsync("SELECT sum(\"SubmittedGuestbookCount\") FROM party_participants"));

        // The new invariants, as the database states them: required, with NO
        // default a photograph-less row could fall back on.
        foreach (var column in new[]
                 {
                     "AuthorDisplayName", "BlobObjectId", "PhotoWidth", "PhotoHeight",
                     "TemplateKey", "TemplateVersion", "CropCenterX", "CropCenterY", "CropZoom",
                 })
        {
            Assert.Equal(
                "NO|",
                await ScalarAsync(
                    "SELECT is_nullable || '|' || coalesce(column_default, '') FROM information_schema.columns "
                    + $"WHERE table_name = 'party_guestbook_entries' AND column_name = '{column}'"));
        }
        Assert.Equal(
            "YES",
            await ScalarAsync(
                "SELECT is_nullable FROM information_schema.columns "
                + "WHERE table_name = 'party_guestbook_entries' AND column_name = 'PreviewBlobObjectId'"));

        var constraints = await ListAsync(
            "SELECT conname FROM pg_constraint WHERE conrelid = 'party_guestbook_entries'::regclass ORDER BY 1");
        Assert.Contains("ck_party_guestbook_entries_crop", constraints);
        Assert.Contains("ck_party_guestbook_entries_photo_size", constraints);
        Assert.Contains("ck_party_guestbook_entries_template_version", constraints);
        Assert.Contains("FK_party_guestbook_entries_blob_objects_BlobObjectId", constraints);
        Assert.Contains("FK_party_guestbook_entries_blob_objects_PreviewBlobObjectId", constraints);
        // No key to the album file a photograph is chosen from: a memory
        // neither cascades from it nor holds it hostage.
        Assert.DoesNotContain(constraints, c => c.Contains("file_items", StringComparison.OrdinalIgnoreCase));

        // And the checks bite: a framing outside the editor's limits is refused
        // by the database itself.
        var blob = await SeedBlobAsync(referenceCount: 1);
        var outOfRange = await Assert.ThrowsAsync<PostgresException>(() => SeedAsync(
            InsertMemorySql(zoom: 9), ("entry", Guid.NewGuid()), ("party", Guid.NewGuid()),
            ("owner", Guid.NewGuid()), ("blob", blob), ("preview", DBNull.Value)));
        Assert.Equal("ck_party_guestbook_entries_crop", outOfRange.ConstraintName);
    }

    [Fact]
    public async Task Down_gives_back_the_references_the_memories_held()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await using var ctx = CreateContext();
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(MigrationUnderTest);

        // A photograph the album still holds (2 = the file + the memory), and a
        // preview only the memory holds.
        var photo = await SeedBlobAsync(referenceCount: 2);
        var preview = await SeedBlobAsync(referenceCount: 1);
        await SeedAsync(
            InsertMemorySql(zoom: 1),
            ("entry", Guid.NewGuid()), ("party", Guid.NewGuid()), ("owner", Guid.NewGuid()),
            ("blob", photo), ("preview", preview));

        await migrator.MigrateAsync(PreviousMigration);

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM party_guestbook_entries"));
        Assert.Equal(1L, await ScalarAsync($"SELECT \"ReferenceCount\" FROM blob_objects WHERE \"Id\" = '{photo}'"));
        Assert.Equal(0L, await ScalarAsync($"SELECT \"ReferenceCount\" FROM blob_objects WHERE \"Id\" = '{preview}'"));
        // Reaching zero starts the janitor's grace window, as a release does.
        Assert.Equal(
            true,
            await ScalarAsync($"SELECT \"PurgeEligibleAt\" IS NOT NULL FROM blob_objects WHERE \"Id\" = '{preview}'"));
        Assert.Equal(
            "YES",
            await ScalarAsync(
                "SELECT is_nullable FROM information_schema.columns "
                + "WHERE table_name = 'party_guestbook_entries' AND column_name = 'AuthorDisplayName'"));
    }

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);

    private static string InsertMemorySql(double zoom) =>
        $"""
        INSERT INTO party_guestbook_entries ("Id", "PartyId", "OwnerUserId", "AuthorDisplayName", "Body",
            "Status", "CreatedAt", "UpdatedAt", "BlobObjectId", "PreviewBlobObjectId", "PhotoWidth", "PhotoHeight",
            "TemplateKey", "TemplateVersion", "CropCenterX", "CropCenterY", "CropZoom")
        VALUES (@entry, @party, @owner, 'Ada', 'Un ricordo', 'visible', now(), now(), @blob, @preview, 1600, 1200,
            'nubarca', 1, 0.5, 0.5, {zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)});
        """;

    private async Task<Guid> SeedBlobAsync(long referenceCount)
    {
        var id = Guid.NewGuid();
        await SeedAsync(
            """
            INSERT INTO blob_objects ("Id", "Sha256", "SizeBytes", "StorageKey", "ReferenceCount", "CreatedAt")
            VALUES (@id, @sha, 1, @key, @refs, now());
            """,
            ("id", id),
            ("sha", Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()).PadRight(64, '0')),
            ("key", Guid.NewGuid().ToString("N")),
            ("refs", referenceCount));
        return id;
    }

    // Raw SQL with the session's foreign-key triggers OFF: the rows are
    // incidental parents (a party, its owner, its link) that this test has no
    // business building at a pinned schema. The checks still apply.
    private async Task SeedAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using (var replica = new NpgsqlCommand("SET session_replication_role = replica", connection))
        {
            await replica.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value is int i ? (long)i : value;
    }

    private async Task<List<string>> ListAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }
        return values;
    }
}

[CollectionDefinition("GuestbookPhotoMemoriesMigration")]
public sealed class GuestbookPhotoMemoriesMigrationCollection;
