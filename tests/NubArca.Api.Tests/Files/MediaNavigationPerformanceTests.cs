using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.MediaLibrary;
using NubArca.Api.Tests.Integration;

namespace NubArca.Api.Tests.Files;

// Deliberately opt-in: real migrated PostgreSQL, synthetic metadata only, and
// no caller-provided connection string. Never runs against an installation.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class MediaNavigationPerformanceTests(PostgresContainerFixture fixture)
{
    [SkippableTheory]
    [InlineData(50_000)]
    [InlineData(100_000)]
    public async Task ExplainAndMeasureTheActualNavigationQueries(int size)
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("NUBARCA_NAVIGATION_BENCHMARK") == "1", "Opt-in navigation benchmark.");
        Assert.True(fixture.Available, "Benchmark requires Docker and PostgreSQL; a skipped run is not evidence.");
        await fixture.ResetDatabaseAsync();
        var capture = new Capture();
        var directory = Environment.GetEnvironmentVariable("NUBARCA_NAVIGATION_BENCHMARK_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "nubarca-navigation-benchmark");
        Directory.CreateDirectory(directory);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.CommandTimeout(30)).AddInterceptors(capture).Options);
        var owner = Guid.NewGuid(); var other = Guid.NewGuid(); var album = Guid.NewGuid();
        var folder = Guid.NewGuid(); var hiddenFolder = Guid.NewGuid(); var vault = Guid.NewGuid();
        db.Users.AddRange(new User { Id = owner, Email = "benchmark@example.com", DisplayName = "Synthetic", CreatedAt = DateTime.UtcNow },
            new User { Id = other, Email = "other@example.com", DisplayName = "Other", CreatedAt = DateTime.UtcNow });
        db.Folders.AddRange(new Folder { Id = folder, OwnerUserId = owner, Name = "Photos", CreatedAt = DateTime.UtcNow },
            new Folder { Id = hiddenFolder, OwnerUserId = owner, Name = "Excluded folder", CreatedAt = DateTime.UtcNow, MediaPhotosExcluded = true });
        db.PrivateVaults.Add(new PrivateVault { Id = vault, OwnerUserId = owner, PasswordHash = "fixture", CreatedAt = DateTime.UtcNow });
        db.Albums.Add(new Album { Id = album, OwnerUserId = owner, Name = "Synthetic album", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        // One distinct blob and metadata row per media; 20% video, titles and
        // missing user metadata, ten years of dates, another owner, tombstones,
        // vault/excluded rows and real folder-eligibility predicates.
        db.Database.SetCommandTimeout(300); // Bulk fixture construction, outside the read budget.
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO blob_objects ("Id", "Sha256", "StorageKey", "SizeBytes", "ReferenceCount", "CreatedAt")
            SELECT md5('blob-' || i)::uuid, md5(i::text) || md5('sha-' || i), 'synthetic-only', 4000000, 1, now()
            FROM generate_series(1, {{size + size / 5}}) i;
            ANALYZE blob_objects;
            INSERT INTO blob_metadata ("Id", "BlobObjectId", "SizeBytes", "MediaCategory", "DetectedContentType", "ThumbnailStatus", "ExtractionStatus", "CreatedAt", "HasIccProfile", "HasAudio")
            SELECT md5('meta-' || i)::uuid, md5('blob-' || i)::uuid, 4000000,
                CASE WHEN i % 5 = 0 THEN 'video' ELSE 'image' END,
                CASE WHEN i % 5 = 0 THEN 'video/mp4' ELSE 'image/jpeg' END, 'pending', 'pending', now(), false, false
            FROM generate_series(1, {{size + size / 5}}) i;
            ANALYZE blob_metadata;
            INSERT INTO file_items ("Id", "OwnerUserId", "BlobObjectId", "ParentFolderId", "PrivateVaultId", "Name", "MimeType", "SizeBytes", "CreatedAt", "EffectiveDateTaken", "DeletedAt", "MediaLibraryState")
            SELECT md5('file-' || i)::uuid, CASE WHEN i <= {{size}} THEN {{owner}} ELSE {{other}} END, md5('blob-' || i)::uuid,
                CASE WHEN i > {{size}} THEN NULL WHEN i % 19 = 0 THEN {{hiddenFolder}} ELSE {{folder}} END,
                CASE WHEN i <= {{size}} AND i % 101 = 0 THEN {{vault}} ELSE NULL END,
                chr(97 + i % 26) || '-foto-' || lpad(i::text, 7, '0') || CASE WHEN i % 5 = 0 THEN '.mp4' ELSE '.jpg' END,
                CASE WHEN i % 5 = 0 THEN 'video/mp4' ELSE 'image/jpeg' END, 4000000,
                timestamptz '2015-01-01' + (i % 3650) * interval '1 day',
                timestamptz '2014-01-01' + (i % 3650) * interval '1 day',
                CASE WHEN i % 199 = 0 THEN now() ELSE NULL END, CASE WHEN i % 47 = 0 THEN 1 ELSE 0 END
            FROM generate_series(1, {{size + size / 5}}) i;
            ANALYZE file_items;
            INSERT INTO file_item_user_metadata ("Id", "FileItemId", "Title", "Description", "TagsJson", "Rating", "IsFavorite", "CreatedAt")
            SELECT md5('user-meta-' || i)::uuid, md5('file-' || i)::uuid,
                CASE WHEN i % 4 = 0 THEN CASE WHEN i % 37 = 0 THEN 'Été' ELSE chr(97 + i % 26) END || ' ricordo ' || i ELSE NULL END,
                repeat('Ricordo sintetico della vacanza. ', 4), '["vacanza", "famiglia"]', i % 6, i % 7 = 0, now()
            FROM generate_series(1, {{size + size / 5}}) i WHERE i % 5 <> 0;
            ANALYZE file_item_user_metadata;
            INSERT INTO album_items ("Id", "AlbumId", "FileItemId", "AddedByUserId", "AddedAt", "SortOrder")
            SELECT md5('album-item-' || i)::uuid, {{album}}, md5('file-' || i)::uuid, {{owner}}, now(), i
            FROM generate_series(1, {{size}}) i WHERE i % 2 = 0;
            """);
        await db.Database.ExecuteSqlRawAsync("ANALYZE;");
        db.Database.SetCommandTimeout(30);
        var files = new NubArca.Api.Files.FileItemService(db, null!, null!, TimeProvider.System,
            mediaLibrary: new MediaLibraryService(db, TimeProvider.System));
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var environment = new NpgsqlCommand("SELECT json_build_object('version', version(), 'collation', (SELECT datcollate FROM pg_database WHERE datname = current_database()), 'shared_buffers', current_setting('shared_buffers'), 'work_mem', current_setting('work_mem'))::text", connection))
            await File.WriteAllTextAsync(Path.Combine(directory, "environment.json"), (string)(await environment.ExecuteScalarAsync())!);
        var scenarios = new[] {
            ("library", new NubArca.Api.Files.ImageFilters()),
            ("filtered", new NubArca.Api.Files.ImageFilters { Favorite = true, MinRating = 3, Query = "ricordo", DateTakenFrom = new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc) }),
            ("album", new NubArca.Api.Files.ImageFilters { AlbumId = album }),
        };
        var output = new List<object>();
        var regressions = new List<string>();
        foreach (var (scenario, filters) in scenarios)
        foreach (var sort in new[] { NubArca.Api.Files.ImageSortField.DateTaken, NubArca.Api.Files.ImageSortField.Name })
        {
            var direction = NubArca.Api.Files.ImageSortDirection.Asc;
            var index = await files.MediaNavigationAsync(owner, filters, NubArca.Api.Files.MediaKindScope.All, sort, direction, default);
            Assert.NotEmpty(index.Buckets);
            var target = index.Buckets[index.Buckets.Count / 2].Key;
            foreach (var operation in new[] { "index", "window" })
            {
                async Task Read()
                {
                    if (operation == "index") await files.MediaNavigationAsync(owner, filters, NubArca.Api.Files.MediaKindScope.All, sort, direction, default);
                    else
                    {
                        var window = await files.MediaWindowAsync(owner, filters, NubArca.Api.Files.MediaKindScope.All, sort, direction, 50, target, null, false, default);
                        Assert.NotNull(window); Assert.InRange(window!.Items.Count, 1, 50);
                    }
                }
                await Read(); // EF compilation/JIT warmup, separately from measured reads.
                var times = new List<double>();
                for (var sample = 0; sample < 5; sample++)
                {
                    capture.Commands.Clear();
                    var stopwatch = Stopwatch.StartNew(); await Read(); times.Add(stopwatch.Elapsed.TotalMilliseconds);
                }
                var plans = new List<object>();
                // Match the production read's transaction-local planner setting.
                await using var explainTransaction = await connection.BeginTransactionAsync();
                await using (var settings = new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL jit = off;", connection, explainTransaction))
                    await settings.ExecuteNonQueryAsync();
                for (var statement = 0; statement < capture.Commands.Count; statement++)
                {
                    var captured = capture.Commands[statement];
                    await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + captured.Sql, connection, explainTransaction);
                    explain.CommandTimeout = 30;
                    foreach (var parameter in captured.Parameters) explain.Parameters.Add(parameter.Clone());
                    var json = (string)(await explain.ExecuteScalarAsync())!;
                    plans.Add(new { statement, sql = captured.Sql, plan = JsonSerializer.Deserialize<JsonElement>(json) });
                }
                await explainTransaction.RollbackAsync();
                var median = times.Order().ElementAt(2);
                // Generous CI regression ceilings, not advertised device/network
                // latency: the report includes every sample and the actual plan.
                var budgetMs = size / 100_000d * (operation == "index" ? 3000 : 6000);
                if (median > budgetMs) regressions.Add($"{size} {scenario} {sort} {operation}: {median:F1}ms exceeds {budgetMs:F0}ms");
                output.Add(new { size, scenario, sort = sort.ToString(), operation, matched = index.Buckets.Sum(b => b.Count), samplesMs = times,
                    medianMs = median, maxMs = times.Max(), budgetMs, jit = "transaction-local off", plans });
                Console.WriteLine($"NAVIGATION {size} {scenario} {sort} {operation}: median={times.Order().ElementAt(2):F1}ms max={times.Max():F1}ms statements={capture.Commands.Count}");
                await File.AppendAllTextAsync(Path.Combine(directory, "progress.txt"), $"{size} {scenario} {sort} {operation} {times.Order().ElementAt(2):F1} {times.Max():F1}\n");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, $"navigation-{size}.json"), JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(regressions.Count == 0, string.Join("\n", regressions));
    }

    private sealed class Capture : DbCommandInterceptor
    {
        public List<(string Sql, NpgsqlParameter[] Parameters)> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToArray()));
            return ValueTask.FromResult(result);
        }
    }
}
