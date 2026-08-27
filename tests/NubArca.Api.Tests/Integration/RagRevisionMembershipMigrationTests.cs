using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// Moving the snapshot revision from the SOURCE row to the MEMBERSHIP row,
// against a real PostgreSQL that already holds an index.
//
// The scaffolded version of this migration dropped `rag_sources."Revision"` and
// added `rag_domain_sources."Revision"` with a default of '', in that order and
// with nothing in between. Every existing membership would have come out of the
// upgrade claiming no revision at all — which retrieval reads as an incoherent
// index and refuses — so a perfectly good corpus would have gone dark on deploy
// and the only fix would have been a full reindex of every domain.
//
// There is exactly ONE moment when a membership's revision is recoverable: while
// the source row it points at still carries it. That is what these tests are
// about. They are not about the DDL, which EF wrote correctly.
[Trait("Category", "External")]
[Collection("RagRevisionMigration")]
public sealed class RagRevisionMembershipMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260826222640_AddRagIndexFormatVersion";
    private const string MigrationUnderTest = "20260827082647_MoveRagRevisionToDomainMembership";

    private const string SharedKey = "docs/help/faces.md";
    private const string RepositoryOnlyKey = "src/NubArca.Api/Rag/Indexing/RagIndexer.cs";
    private const string Repository = "nubarca-repository";
    private const string Help = "product-help";

    private PostgreSqlContainer? _container;
    private string? _connectionString;

    private bool Available => _connectionString is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("nubarca_ragrevision")
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
    public async Task Every_Existing_Membership_Inherits_Its_Sources_Revision()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);
        await SeedPreUpgradeIndexAsync();

        await MigrateAsync(MigrationUnderTest);

        // The shared source was indexed by both domains at the same commit, so
        // both memberships describe that commit — which is what they meant when
        // the revision lived on the row they shared.
        Assert.Equal("aaaaaaaa", await RevisionOfAsync(Repository, SharedKey));
        Assert.Equal("aaaaaaaa", await RevisionOfAsync(Help, SharedKey));
        Assert.Equal("aaaaaaaa", await RevisionOfAsync(Repository, RepositoryOnlyKey));

        // Nothing claims no revision. This is the assertion the scaffolded
        // migration failed, and it failed it for every row.
        Assert.Equal(0, await ScalarAsync<long>(
            """SELECT count(*) FROM rag_domain_sources WHERE "Revision" = '';"""));
    }

    [Fact]
    public async Task The_Upgrade_Preserves_Memberships_Chunks_And_Embeddings()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);
        await SeedPreUpgradeIndexAsync();

        var sources = await ScalarAsync<long>("SELECT count(*) FROM rag_sources;");
        var memberships = await ScalarAsync<long>("SELECT count(*) FROM rag_domain_sources;");
        var chunks = await ScalarAsync<long>("SELECT count(*) FROM rag_chunks;");

        await MigrateAsync(MigrationUnderTest);

        // A revision moving between tables is not a reindex. Nothing derived is
        // recomputed and nothing is dropped.
        Assert.Equal(sources, await ScalarAsync<long>("SELECT count(*) FROM rag_sources;"));
        Assert.Equal(memberships, await ScalarAsync<long>("SELECT count(*) FROM rag_domain_sources;"));
        Assert.Equal(chunks, await ScalarAsync<long>("SELECT count(*) FROM rag_chunks;"));
    }

    [Fact]
    public async Task The_Upgrade_Leaves_One_Revision_Authority()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);
        await SeedPreUpgradeIndexAsync();

        await MigrateAsync(MigrationUnderTest);

        // Two competing revision columns would be worse than the wrong one: the
        // stale copy would keep answering wherever a query forgot to change.
        Assert.False(await ColumnExistsAsync("rag_sources", "Revision"));
        Assert.True(await ColumnExistsAsync("rag_domain_sources", "Revision"));

        // Source identity is content-shaped now, and SourceKey alone no longer
        // constrains anything — that is what lets two interpretations coexist
        // while domains upgrade one at a time.
        Assert.True(await IndexExistsAsync("ux_rag_sources_key_content_format"));
        Assert.True(await IndexExistsAsync("ix_rag_sources_key"));
        Assert.True(await IndexExistsAsync("ix_rag_domain_sources_domain_revision"));
        Assert.False(await IndexExistsAsync("ux_rag_sources_key"));
        Assert.False(await IndexExistsAsync("ix_rag_sources_revision"));
    }

    [Fact]
    public async Task A_Shared_Source_Can_Then_Advance_One_Domain_At_A_Time()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);
        await SeedPreUpgradeIndexAsync();
        await MigrateAsync(MigrationUnderTest);

        // The move the previous schema could not represent, performed directly
        // against the upgraded tables: the repository advances and Help does not.
        await ExecuteAsync(
            """
            UPDATE rag_domain_sources
            SET "Revision" = 'bbbbbbbb'
            WHERE "DomainKey" = @domain;
            """,
            ("domain", Repository));

        Assert.Equal("bbbbbbbb", await RevisionOfAsync(Repository, SharedKey));
        Assert.Equal("aaaaaaaa", await RevisionOfAsync(Help, SharedKey));
        // One content row still, because the bytes never changed.
        Assert.Equal(1, await ScalarAsync<long>(
            """SELECT count(*) FROM rag_sources WHERE "SourceKey" = @key;""",
            ("key", SharedKey)));
    }

    [Fact]
    public async Task Rollback_Restores_The_Revision_Onto_The_Source()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);
        await SeedPreUpgradeIndexAsync();
        await MigrateAsync(MigrationUnderTest);

        await MigrateAsync(PreviousMigration);

        // Down is a real inverse when the domains agree, which is the state an
        // operator rolling back a release is in.
        Assert.True(await ColumnExistsAsync("rag_sources", "Revision"));
        Assert.Equal("aaaaaaaa", await ScalarAsync<string>(
            """SELECT "Revision" FROM rag_sources WHERE "SourceKey" = @key;""",
            ("key", SharedKey)));
        Assert.Equal(0, await ScalarAsync<long>(
            """SELECT count(*) FROM rag_sources WHERE "Revision" = '';"""));
    }

    [Fact]
    public async Task Rollback_Refuses_While_Two_Domains_Hold_Different_Content()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);
        await SeedPreUpgradeIndexAsync();
        await MigrateAsync(MigrationUnderTest);

        // Mid-upgrade: the repository has moved to new bytes for the shared
        // document and Help has not. The old schema has one row per key and
        // cannot say this at all.
        await ExecuteAsync(
            """
            INSERT INTO rag_sources
                ("Id", "SourceKey", "Path", "Title", "SourceKind", "ContentHash",
                 "IndexFormatVersion", "Language", "CodeLanguage", "CreatedAt")
            VALUES (@id, @key, @key, 'faces.md', 'documentation', @hash, 1, 'it', 'markdown', now());

            UPDATE rag_domain_sources
            SET "SourceId" = @id, "Revision" = 'bbbbbbbb'
            WHERE "DomainKey" = @domain
              AND "SourceId" IN (SELECT "Id" FROM rag_sources WHERE "SourceKey" = @key AND "Id" <> @id);
            """,
            ("id", Guid.NewGuid()), ("key", SharedKey),
            ("hash", new string('b', 64)), ("domain", Repository));

        // Refused, and named. Silently picking a winner would rewrite whichever
        // domain lost, and letting PostgreSQL report it as a unique violation
        // halfway through would leave the operator guessing.
        var error = await Assert.ThrowsAsync<PostgresException>(
            () => MigrateAsync(PreviousMigration));
        Assert.Contains("cannot downgrade", error.MessageText, StringComparison.Ordinal);
        Assert.Contains(SharedKey, error.MessageText, StringComparison.Ordinal);
    }

    // ---- plumbing ---------------------------------------------------------

    /// A realistic pre-upgrade index: one document both domains claim, one only
    /// the repository does, chunks under each, all at one commit — because the
    /// old schema could not hold anything else.
    private Task SeedPreUpgradeIndexAsync()
    {
        var shared = Guid.NewGuid();
        var repositoryOnly = Guid.NewGuid();
        return ExecuteAsync(
            """
            INSERT INTO rag_sources
                ("Id", "SourceKey", "Path", "Title", "SourceKind", "Revision", "ContentHash",
                 "IndexFormatVersion", "Language", "CodeLanguage", "CreatedAt")
            VALUES
                (@shared, @sharedKey, @sharedKey, 'faces.md', 'documentation', 'aaaaaaaa',
                 @sharedHash, 1, 'it', 'markdown', now()),
                (@repoOnly, @repoOnlyKey, @repoOnlyKey, 'RagIndexer.cs', 'source-code', 'aaaaaaaa',
                 @repoOnlyHash, 1, 'it', 'csharp', now());

            INSERT INTO rag_domain_sources
                ("Id", "DomainKey", "SourceId", "Priority", "CreatedAt")
            VALUES
                (@m1, @repository, @shared, 65, now()),
                (@m2, @help, @shared, 100, now()),
                (@m3, @repository, @repoOnly, 65, now());

            INSERT INTO rag_chunks
                ("Id", "SourceId", "Ordinal", "Heading", "Text", "TextHash", "Language", "CreatedAt")
            VALUES
                (@c1, @shared, 1, 'Volti', 'Apri Volti e scegli Assegna nome.', @c1h, 'it', now()),
                (@c2, @shared, 2, 'Gruppi', 'I gruppi suggeriti raccolgono volti simili.', @c2h, 'it', now()),
                (@c3, @repoOnly, 1, 'RagIndexer', 'Turns a snapshot into an index.', @c3h, 'en', now());
            """,
            ("shared", shared), ("repoOnly", repositoryOnly),
            ("sharedKey", SharedKey), ("repoOnlyKey", RepositoryOnlyKey),
            ("sharedHash", new string('a', 64)), ("repoOnlyHash", new string('c', 64)),
            ("m1", Guid.NewGuid()), ("m2", Guid.NewGuid()), ("m3", Guid.NewGuid()),
            ("repository", Repository), ("help", Help),
            ("c1", Guid.NewGuid()), ("c2", Guid.NewGuid()), ("c3", Guid.NewGuid()),
            ("c1h", new string('1', 64)), ("c2h", new string('2', 64)), ("c3h", new string('3', 64)));
    }

    private async Task<string> RevisionOfAsync(string domainKey, string sourceKey)
        => await ScalarAsync<string>(
            """
            SELECT m."Revision"
            FROM rag_domain_sources m
            JOIN rag_sources s ON s."Id" = m."SourceId"
            WHERE m."DomainKey" = @domain AND s."SourceKey" = @key;
            """,
            ("domain", domainKey), ("key", sourceKey));

    private Task<bool> ColumnExistsAsync(string table, string column)
        => ScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_name = @table AND column_name = @column);
            """,
            ("table", table), ("column", column));

    private Task<bool> IndexExistsAsync(string name)
        => ScalarAsync<bool>(
            """SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = @name);""",
            ("name", name));

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
