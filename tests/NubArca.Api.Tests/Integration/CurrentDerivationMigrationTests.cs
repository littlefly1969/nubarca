using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag.Retrieval;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// Giving a file ONE authoritative reading, against a real PostgreSQL that
// already holds somebody's documents.
//
// The scaffolded migration added `IsCurrent` with a default of false and
// stopped. Every existing row would have come out of the upgrade claiming not
// to be the current reading of its file — and since the retrieval boundary
// requires exactly that flag, every private corpus in the installation would
// have gone dark. Silently: nothing throws, no reason code is produced, the
// join simply stops matching and questions start answering "I don't know".
//
// So these tests are not about the DDL, which EF wrote correctly. They are
// about the one moment when "which reading of this file was being served" is
// still answerable, and about what the migration does when it is not.
[Trait("Category", "External")]
[Collection("CurrentDerivationMigration")]
public sealed class CurrentDerivationMigrationTests : IAsyncLifetime
{
    /// The exact schema Slice 3 shipped.
    private const string SliceThreeSchema = "20260827124758_AddOwnerDocumentExtractionLifecycle";
    private const string MigrationUnderTest = "20260827213657_AddCurrentDerivationAndChunkLocators";

    private PostgreSqlContainer? _container;
    private string? _connectionString;

    private bool Available => _connectionString is not null;

    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _fileA = Guid.NewGuid();
    private readonly Guid _fileB = Guid.NewGuid();
    private readonly Guid _fileC = Guid.NewGuid();
    private readonly Guid _fileD = Guid.NewGuid();
    private Guid _profileOne;
    private Guid _profileTwo;
    private Guid _blob = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("nubarca_currentderivation")
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

    // ---- the upgrade --------------------------------------------------------

    [Fact]
    public async Task A_Single_Completed_Reading_Is_Claimed_As_Current()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(SliceThreeSchema);
        await SeedSliceThreeLibraryAsync();

        await MigrateAsync(MigrationUnderTest);

        // File A is the ordinary production case: one extraction profile, one
        // row, unambiguously the reading that was being served.
        Assert.True(await IsCurrentAsync(_fileA));
    }

    [Fact]
    public async Task A_Single_Skipped_Reading_Is_Also_Claimed_As_Current()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(SliceThreeSchema);
        await SeedSliceThreeLibraryAsync();

        await MigrateAsync(MigrationUnderTest);

        // "These bytes cannot be read" IS the current interpretation of file B.
        // Leaving it uncurrent would make `IsCurrent` mean "successfully read",
        // which is a different question the boundary already asks separately —
        // and would let a later skip for the same file create a second current
        // row without tripping the unique index.
        Assert.True(await IsCurrentAsync(_fileB));
        Assert.Equal(
            "skipped",
            await ScalarAsync<string>(
                @"SELECT ""Status"" FROM document_texts WHERE ""FileItemId"" = @f",
                ("f", _fileB)));
    }

    [Fact]
    public async Task A_File_With_Two_Readings_Gets_No_Current_Row()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(SliceThreeSchema);
        await SeedSliceThreeLibraryAsync();

        await MigrateAsync(MigrationUnderTest);

        // File C has two completed rows under two profiles. They differ by a
        // profile whose meaning SQL cannot read, so there is no honest winner —
        // picking the newest, the first or the longest would be inventing
        // provenance and then serving somebody's document through it. The file
        // is unanswerable until `documents index` establishes a reading, which
        // is recoverable; a confident answer from the wrong one is not.
        Assert.Equal(
            2,
            await ScalarAsync<long>(
                @"SELECT count(*) FROM document_texts WHERE ""FileItemId"" = @f", ("f", _fileC)));
        Assert.Equal(
            0,
            await ScalarAsync<long>(
                @"SELECT count(*) FROM document_texts WHERE ""FileItemId"" = @f AND ""IsCurrent""",
                ("f", _fileC)));
    }

    [Fact]
    public async Task A_Document_With_Chunks_Is_Still_Retrievable_After_The_Upgrade()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(SliceThreeSchema);
        await SeedSliceThreeLibraryAsync();

        await MigrateAsync(MigrationUnderTest);

        // THE ASSERTION THE SCAFFOLDED MIGRATION FAILED, and it failed it for
        // every document in the installation. Read through the real corpus
        // source, not by querying the flag: the flag being set is only
        // interesting because the shared boundary requires it.
        await using var ctx = CreateContext();
        var corpus = await new OwnerDocumentCorpusSource(ctx).LoadAsync(_owner);

        Assert.NotEmpty(corpus.Chunks);
        Assert.Contains(corpus.Chunks, c => c.Text.Contains("filtro", StringComparison.Ordinal));
        Assert.All(corpus.Chunks, c => Assert.Equal(_owner, c.OwnerUserId));
    }

    [Fact]
    public async Task Historical_Readings_Are_Neither_Retrieved_Nor_Embeddable()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(SliceThreeSchema);
        await SeedSliceThreeLibraryAsync();
        await MigrateAsync(MigrationUnderTest);

        // Demote file D's reading and leave every chunk exactly where it is.
        // Cleanup is housekeeping; the flag is the boundary, and it has to hold
        // while the rows are still there.
        await ExecuteAsync(
            @"UPDATE document_texts SET ""IsCurrent"" = false WHERE ""FileItemId"" = @f",
            ("f", _fileD));

        await using var ctx = CreateContext();
        Assert.True(
            await ctx.DocumentChunks.AnyAsync(),
            "the historical chunks must still be present for this test to mean anything");

        var corpus = await new OwnerDocumentCorpusSource(ctx).LoadAsync(_owner);
        Assert.Empty(corpus.Chunks);

        // And the same boundary refuses them to the embedder, so a superseded
        // reading cannot acquire fresh vectors either.
        var embeddable = await OwnerDocumentEligibility
            .EligibleChunks(
                ctx.DocumentChunks.AsNoTracking(),
                ctx.DocumentTexts.AsNoTracking(),
                ctx.FileItems.AsNoTracking(),
                _owner)
            .CountAsync();
        Assert.Equal(0, embeddable);
    }

    [Fact]
    public async Task Two_Current_Readings_Of_One_File_Cannot_Be_Written()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(SliceThreeSchema);
        await SeedSliceThreeLibraryAsync();
        await MigrateAsync(MigrationUnderTest);

        // File A already has a current row. A second one is not a bug that
        // throws somewhere later — it is a corpus that quietly answers one
        // question from two readings of the same document. PostgreSQL refuses
        // the write instead.
        var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            @"INSERT INTO document_texts
                (""Id"", ""FileItemId"", ""OwnerUserId"", ""ProfileId"", ""SourceBlobObjectId"",
                 ""Source"", ""Status"", ""ChunkFormatVersion"", ""IsCurrent"", ""CreatedAt"")
              VALUES (@id, @f, @o, @p, @b, 'native', 'completed', 1, true, now())",
            ("id", Guid.NewGuid()), ("f", _fileA), ("o", _owner),
            ("p", _profileTwo), ("b", _blob)));

        Assert.Equal("23505", failure.SqlState);
        Assert.Contains("ux_document_texts_current_per_file", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Unique_Index_Is_Created_After_The_Backfill()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        // Ordering, proved by the only thing that can prove it: a database whose
        // pre-upgrade state would violate the index if the backfill had run
        // second. File A and file B are both claimed current in the same
        // statement, and file C is left alone — an index built before the
        // backfill would have nothing to complain about, so this asserts the
        // upgrade completes AND lands in the state only the correct order
        // produces.
        await MigrateAsync(SliceThreeSchema);
        await SeedSliceThreeLibraryAsync();

        await MigrateAsync(MigrationUnderTest);

        Assert.Equal(
            1,
            await ScalarAsync<long>(
                @"SELECT count(*) FROM pg_indexes
                  WHERE tablename = 'document_texts'
                    AND indexname = 'ux_document_texts_current_per_file'"));

        // Partial, on the flag. A non-filtered unique index on FileItemId would
        // forbid the historical rows this design keeps as provenance.
        var definition = await ScalarAsync<string>(
            @"SELECT indexdef FROM pg_indexes
              WHERE tablename = 'document_texts'
                AND indexname = 'ux_document_texts_current_per_file'");
        Assert.Contains("WHERE", definition, StringComparison.Ordinal);
        Assert.Contains("IsCurrent", definition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Fresh_Database_Migrates_To_Head()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        // No upgrade path at all: the migration has to build the column, the
        // constraints and the index from nothing, which is the case a
        // data-repair statement is easiest to break.
        await MigrateAsync(null);

        Assert.Equal(
            1,
            await ScalarAsync<long>(
                @"SELECT count(*) FROM information_schema.columns
                  WHERE table_name = 'document_texts' AND column_name = 'IsCurrent'"));
        Assert.Equal(
            3,
            await ScalarAsync<long>(
                @"SELECT count(*) FROM information_schema.columns
                  WHERE table_name = 'document_chunks'
                    AND column_name IN ('LocatorKind', 'LocatorIndex', 'LocatorLabel')"));
        Assert.Equal(
            0,
            await ScalarAsync<long>(@"SELECT count(*) FROM document_texts"));
    }

    // ---- fixture ------------------------------------------------------------

    /// A library exactly as Slice 3 would have left it: no `IsCurrent` column,
    /// one row per (file, profile), chunks written by the native extractor.
    private async Task SeedSliceThreeLibraryAsync()
    {
        _profileOne = Guid.NewGuid();
        _profileTwo = Guid.NewGuid();
        var modelOne = Guid.NewGuid();
        var modelTwo = Guid.NewGuid();

        // The role catalogue is seeded by the application, not by a migration, so
        // a database that has only been migrated has none. The owner needs one
        // to exist at all.
        await ExecuteAsync(
            @"INSERT INTO access_roles
                (""Key"", ""Name"", ""Description"", ""IsSystem"", ""IsAdministrator"",
                 ""Version"", ""CreatedAt"", ""UpdatedAt"")
              VALUES ('member', 'Member', 'Test role', true, false, 1, now(), now())
              ON CONFLICT (""Key"") DO NOTHING");

        await ExecuteAsync(
            @"INSERT INTO users
                (""Id"", ""Email"", ""DisplayName"", ""FirstName"", ""LastName"", ""PasswordHash"",
                 ""RoleKey"", ""SecurityVersion"", ""TimeZone"", ""UiLanguage"", ""CreatedAt"")
              VALUES (@o, @mail, 'Owner', 'Owner', 'Test', '', 'member', 1, 'UTC', 'it', now())",
            ("o", _owner), ("mail", $"owner-{_owner:N}@example.invalid"));

        await ExecuteAsync(
            @"INSERT INTO blob_objects (""Id"", ""Sha256"", ""StorageKey"", ""SizeBytes"", ""ReferenceCount"", ""CreatedAt"")
              VALUES (@b, @sha, @key, 100, 4, now())",
            ("b", _blob), ("sha", new string('a', 64)), ("key", "objects/aa/aa/" + new string('a', 64)));

        foreach (var (model, profile, key) in new[]
                 {
                     (modelOne, _profileOne, "doc-native-text-v1"),
                     (modelTwo, _profileTwo, "doc-native-text-v1-recreated"),
                 })
        {
            await ExecuteAsync(
                @"INSERT INTO ai_models
                    (""Id"", ""Key"", ""Provider"", ""Capability"", ""Modality"", ""DistanceMetric"",
                     ""Enabled"", ""Version"", ""CreatedAt"")
                  VALUES (@id, @key, 'none', 'document-extraction', 'text', '', true, 1, now())",
                ("id", model), ("key", key + "-model"));
            await ExecuteAsync(
                @"INSERT INTO ai_profiles
                    (""Id"", ""Key"", ""AiModelId"", ""Capability"", ""Modality"", ""DistanceMetric"",
                     ""ConfigHash"", ""IsDefault"", ""Enabled"", ""CreatedAt"")
                  VALUES (@id, @key, @model, 'document-extraction', 'text', '', '', false, true, now())",
                ("id", profile), ("key", key), ("model", model));
        }

        await AddFileAsync(_fileA, "manuale-a.md");
        await AddFileAsync(_fileB, "immagine-b.bin");
        await AddFileAsync(_fileC, "ambiguo-c.md");
        await AddFileAsync(_fileD, "manuale-d.md");

        // A: the ordinary case — one completed reading.
        await AddDocumentAsync(Guid.NewGuid(), _fileA, _profileOne, "completed", BodyA);

        // B: one permanent content skip.
        await AddSkippedDocumentAsync(Guid.NewGuid(), _fileB, _profileOne);

        // C: two completed readings under two profiles — the ambiguous state.
        await AddDocumentAsync(Guid.NewGuid(), _fileC, _profileOne, "completed", BodyA);
        await AddDocumentAsync(Guid.NewGuid(), _fileC, _profileTwo, "completed", BodyA);

        // D: a completed reading WITH chunks, so retrieval can be exercised.
        var documentD = Guid.NewGuid();
        await AddDocumentAsync(documentD, _fileD, _profileOne, "completed", BodyD);
        await AddChunkAsync(documentD, _profileOne, 1, "Manutenzione › Filtro", BodyD);
    }

    private const string BodyA =
        "Il manuale descrive la manutenzione ordinaria dell'impianto installato.";

    private const string BodyD =
        "Il filtro dell'acqua va pulito ogni sei mesi chiudendo il rubinetto di ingresso.";

    private Task AddFileAsync(Guid id, string name) => ExecuteAsync(
        @"INSERT INTO file_items
            (""Id"", ""OwnerUserId"", ""BlobObjectId"", ""Name"", ""MimeType"", ""SizeBytes"",
             ""MediaLibraryState"", ""CreatedAt"", ""EffectiveDateTaken"", ""EffectiveDateTakenSource"")
          VALUES (@id, @o, @b, @name, 'text/markdown', 100, 0, now(), now(), 'uploaded')",
        ("id", id), ("o", _owner), ("b", _blob), ("name", name));

    private Task AddDocumentAsync(Guid id, Guid fileItemId, Guid profileId, string status, string text)
        => ExecuteAsync(
            @"INSERT INTO document_texts
                (""Id"", ""FileItemId"", ""OwnerUserId"", ""ProfileId"", ""SourceBlobObjectId"",
                 ""Source"", ""Status"", ""TextHash"", ""Text"", ""CharCount"",
                 ""ChunkFormatVersion"", ""CreatedAt"")
              VALUES (@id, @f, @o, @p, @b, 'native', @status, @hash, @text, @len, 1, now())",
            ("id", id), ("f", fileItemId), ("o", _owner), ("p", profileId), ("b", _blob),
            ("status", status), ("hash", new string('b', 64)), ("text", text), ("len", text.Length));

    private Task AddSkippedDocumentAsync(Guid id, Guid fileItemId, Guid profileId) => ExecuteAsync(
        @"INSERT INTO document_texts
            (""Id"", ""FileItemId"", ""OwnerUserId"", ""ProfileId"", ""SourceBlobObjectId"",
             ""Source"", ""Status"", ""ErrorCode"", ""ChunkFormatVersion"", ""CreatedAt"")
          VALUES (@id, @f, @o, @p, @b, 'native', 'skipped', 'binary-content', 1, now())",
        ("id", id), ("f", fileItemId), ("o", _owner), ("p", profileId), ("b", _blob));

    private Task AddChunkAsync(Guid documentTextId, Guid profileId, int ordinal, string heading, string text)
        => ExecuteAsync(
            @"INSERT INTO document_chunks
                (""Id"", ""DocumentTextId"", ""OwnerUserId"", ""ProfileId"", ""Ordinal"",
                 ""Heading"", ""Text"", ""TextHash"", ""CreatedAt"")
              VALUES (@id, @d, @o, @p, @ord, @heading, @text, @hash, now())",
            ("id", Guid.NewGuid()), ("d", documentTextId), ("o", _owner), ("p", profileId),
            ("ord", ordinal), ("heading", heading), ("text", text), ("hash", new string('c', 64)));

    private async Task<bool> IsCurrentAsync(Guid fileItemId) => await ScalarAsync<bool>(
        @"SELECT ""IsCurrent"" FROM document_texts WHERE ""FileItemId"" = @f", ("f", fileItemId));

    private async Task MigrateAsync(string? target)
    {
        await using var ctx = CreateContext();
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
        if (target is null)
        {
            await migrator.MigrateAsync();
        }
        else
        {
            await migrator.MigrateAsync(target);
        }
    }

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString!)
            .Options;
        return new AppDbContext(options);
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T));
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
