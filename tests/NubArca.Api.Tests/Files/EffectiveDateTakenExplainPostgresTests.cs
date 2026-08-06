using System.Text;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Integration;
using Xunit;
using Xunit.Abstractions;

namespace NubArca.Api.Tests.Files;

// Slice 88 — PostgreSQL EXPLAIN evidence for the gallery "Date taken" sort.
// Confirms the ROOT CAUSE (the old correlated-subquery ORDER BY forces a Sort
// over a Seq Scan) and the FIX (ordering on the denormalized EffectiveDateTaken
// column resolves via the new composite index with no Sort step), and that the
// rewrite preserves the exact ordering. Skipped when Docker is unavailable.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class EffectiveDateTakenExplainPostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private readonly ITestOutputHelper _output;
    private DbContextOptions<AppDbContext>? _dbOptions;
    private readonly Guid _owner = Guid.NewGuid();

    public EffectiveDateTakenExplainPostgresTests(PostgresContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        if (!_fixture.Available)
        {
            return;
        }
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!)
            .Options;
        await _fixture.ResetDatabaseAsync();
        await SeedAsync(3000);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedAsync(int n)
    {
        await using var db = new AppDbContext(_dbOptions!);

        db.Users.Add(new User
        {
            Id = _owner,
            Email = $"owner-{_owner:N}@example.com",
            DisplayName = "Owner",
            PasswordHash = "x",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        // One shared blob, no blob_metadata row → gallery membership resolves
        // via the client-MIME fallback (image/*). Keeps the seed cheap while
        // exercising the same WHERE shape production uses.
        var blobId = Guid.NewGuid();
        db.BlobObjects.Add(new BlobObject
        {
            Id = blobId,
            Sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
            SizeBytes = 1,
            StorageKey = "ab/cd/seed",
            ReferenceCount = n,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var baseTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < n; i++)
        {
            // Distinct, monotonically increasing dates so ordering is
            // unambiguous. No embedded date / override here, so the effective
            // date equals CreatedAt (both set identically).
            var ts = baseTime.AddMinutes(i);
            db.FileItems.Add(new FileItem
            {
                Id = Guid.NewGuid(),
                OwnerUserId = _owner,
                ParentFolderId = null,
                BlobObjectId = blobId,
                Name = $"img-{i:D5}.jpg",
                MimeType = "image/jpeg",
                SizeBytes = 1,
                CreatedAt = ts,
                EffectiveDateTaken = ts,
                EffectiveDateTakenSource = "uploaded",
            });
        }

        await db.SaveChangesAsync();
        // Give the planner real statistics.
        await db.Database.ExecuteSqlRawAsync("ANALYZE file_items;");
    }

    // Production gallery membership predicate (image/*) for one owner's active files.
    private string Where() =>
        $@"f.""OwnerUserId"" = '{_owner}' AND f.""DeletedAt"" IS NULL AND (
            EXISTS (SELECT 1 FROM blob_metadata m WHERE m.""BlobObjectId"" = f.""BlobObjectId""
                        AND m.""DetectedContentType"" IS NOT NULL AND m.""DetectedContentType"" LIKE 'image/%')
            OR (NOT EXISTS (SELECT 1 FROM blob_metadata m2 WHERE m2.""BlobObjectId"" = f.""BlobObjectId"")
                    AND f.""MimeType"" LIKE 'image/%'))";

    // Old approach: order by a COALESCE over correlated scalar subqueries.
    private string LegacyOrderBySql() =>
        $@"SELECT f.""Id"" FROM file_items f WHERE {Where()}
           ORDER BY COALESCE(
               (SELECT u.""DateTakenOverride"" FROM file_item_user_metadata u WHERE u.""FileItemId"" = f.""Id"" LIMIT 1),
               (SELECT m.""DateTaken"" FROM blob_metadata m WHERE m.""BlobObjectId"" = f.""BlobObjectId"" LIMIT 1),
               f.""CreatedAt"") ASC, f.""Id"" ASC
           LIMIT 51";

    // New approach: order by the denormalized, indexed column.
    private string IndexedOrderBySql() =>
        $@"SELECT f.""Id"" FROM file_items f WHERE {Where()}
           ORDER BY f.""EffectiveDateTaken"" ASC, f.""Id"" ASC
           LIMIT 51";

    // The core ordering in isolation (owner + active only), so the index choice
    // is not masked by the non-sargable membership OR-predicate.
    private string IndexedCoreOrderBySql() =>
        $@"SELECT f.""Id"" FROM file_items f
           WHERE f.""OwnerUserId"" = '{_owner}' AND f.""DeletedAt"" IS NULL
           ORDER BY f.""EffectiveDateTaken"" ASC, f.""Id"" ASC
           LIMIT 51";

    private async Task<string> ExplainAsync(AppDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN (ANALYZE, BUFFERS) " + sql;
            var sb = new StringBuilder();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                sb.AppendLine(reader.GetString(0));
            }
            return sb.ToString();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private async Task<List<Guid>> RunIdsAsync(AppDbContext db, string sql)
    {
        var ids = new List<Guid>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetGuid(0));
            }
            return ids;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [SkippableFact]
    public async Task DateTaken_Sort_Uses_Index_And_Avoids_Sort_After_Denormalization()
    {
        Skip.IfNot(_fixture.Available, "Docker/PostgreSQL not available.");
        await using var db = new AppDbContext(_dbOptions!);

        var before = await ExplainAsync(db, LegacyOrderBySql());
        var afterFull = await ExplainAsync(db, IndexedOrderBySql());
        var core = await ExplainAsync(db, IndexedCoreOrderBySql());

        _output.WriteLine("===== BEFORE (correlated-subquery ORDER BY, full gallery predicate) =====");
        _output.WriteLine(before);
        _output.WriteLine("===== AFTER (EffectiveDateTaken, full gallery predicate) =====");
        _output.WriteLine(afterFull);
        _output.WriteLine("===== CORE (EffectiveDateTaken ordering, owner+active only) =====");
        _output.WriteLine(core);

        // ROOT CAUSE: the old approach evaluates per-row correlated subqueries
        // (SubPlan) inside the Sort Key and must Sort the whole result set.
        Assert.Contains("Sort Key: (COALESCE", before);
        Assert.Contains("Seq Scan on file_items", before);

        // FIX: the full gallery query now resolves the ordering via the new
        // partial index — an ordered index scan with NO Sort step and NO per-row
        // date subqueries. The membership predicate is just a filter applied
        // during the ordered scan, so the LIMIT short-circuits after ~51 rows.
        Assert.Contains("ix_file_items_owner_deleted_effdate_id", afterFull);
        Assert.DoesNotContain("Sort", afterFull);
        Assert.DoesNotContain("COALESCE", afterFull);

        // The core ordering is an index(-only) scan — definitively no Sort.
        Assert.Contains("ix_file_items_owner_deleted_effdate_id", core);
        Assert.DoesNotContain("Sort", core);
    }

    [SkippableFact]
    public async Task Rewrite_Preserves_Exact_Ordering()
    {
        Skip.IfNot(_fixture.Available, "Docker/PostgreSQL not available.");
        await using var db = new AppDbContext(_dbOptions!);

        // On this dataset (no overrides / embedded dates) the effective date
        // equals CreatedAt, so the legacy and indexed orderings must agree.
        var legacy = await RunIdsAsync(db, LegacyOrderBySql());
        var indexed = await RunIdsAsync(db, IndexedOrderBySql());

        Assert.Equal(51, indexed.Count);
        Assert.Equal(legacy, indexed);
    }
}
