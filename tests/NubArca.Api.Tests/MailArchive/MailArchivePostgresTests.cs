using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Mail;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.MailArchive;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Integration;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Xunit.Abstractions;

namespace NubArca.Api.Tests.MailArchive;

// MailArchive on real PostgreSQL: the AddMailArchive migration applied by the
// fixture, the filtered unique index that admits one active run per archive
// under truly concurrent starts, the import, the delete-with-tombstone
// transaction and the blob reference audit. The SQLite lane cannot prove the
// concurrency part: its pooled connection serialises the two starts.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class MailArchivePostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public MailArchivePostgresTests(PostgresContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        if (!_fixture.Available) return;
        await _fixture.ResetDatabaseAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!).Options);
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE mail_archives, mail_deletion_tombstones, background_jobs RESTART IDENTITY CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Concurrent_starts_admit_one_run_and_the_import_and_delete_hold_on_postgres()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var engine = FakePstEngine.Sample();
        using var baseFactory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, new Dictionary<string, string?>());
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPstEngine>();
            services.AddSingleton<IPstEngine>(engine);
        }));

        var owner = Guid.NewGuid();
        Guid pstId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User { Id = owner, Email = $"mail-{owner:N}@example.com", DisplayName = "Owner", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await using var bytes = new MemoryStream(FakePstEngine.Bytes("!BDNpostgres"));
            pstId = (await scope.ServiceProvider.GetRequiredService<IFileItemService>()
                .CreateAsync(owner, null, "archivio.pst", "application/vnd.ms-outlook", bytes)).Id;
        }

        // Create the archive first, then race two starts against it.
        MailStartResult first;
        using (var scope = factory.Services.CreateScope())
        {
            first = await scope.ServiceProvider.GetRequiredService<MailArchiveService>().StartFromFileAsync(owner, pstId, CancellationToken.None);
        }
        Assert.Equal(MailStartOutcome.Started, first.Outcome);
        using (var scope = factory.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<AppDbContext>().MailImportRuns.SingleAsync();
            // Finish the first run's job so both racers see no active run.
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().MailImportRuns
                .Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, MailImportRunStatuses.Cancelled));
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().BackgroundJobs
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatuses.Cancelled));
        }
        var racers = Enumerable.Range(0, 6).Select(async _ =>
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<MailArchiveService>()
                .ReimportAsync(owner, first.Archive!.Id, CancellationToken.None);
        }).ToList();
        var outcomes = (await Task.WhenAll(racers)).Select(r => r.Outcome).ToList();
        Assert.Equal(1, outcomes.Count(o => o == MailStartOutcome.Started));
        Assert.All(outcomes.Where(o => o != MailStartOutcome.Started), o => Assert.Equal(MailStartOutcome.AlreadyRunning, o));

        for (var i = 0; i < 200; i++)
        {
            using var scope = factory.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(10) == 0) break;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var archive = await scope.ServiceProvider.GetRequiredService<MailArchiveService>()
                .GetArchiveAsync(owner, first.Archive!.Id, CancellationToken.None);
            Assert.Equal("succeeded", archive!.LatestRun!.Status);
            Assert.Equal(4, archive.MessageCount);

            var query = scope.ServiceProvider.GetRequiredService<MailMessageQueryService>();
            var page = await query.SearchAsync(owner, new MailMessageQuery(archive.Id, null, "日本語", null, null, null, null, null, null, null, 50), CancellationToken.None);
            Assert.Single(page!.Items);
            var target = (await query.SearchAsync(owner, new MailMessageQuery(archive.Id, null, null, null, null, "Relazione trimestrale", null, null, true, null, 50), CancellationToken.None))!
                .Items.Single(m => m.Subject == "Relazione trimestrale");

            Assert.Equal(MailDeleteOutcome.Deleted, await scope.ServiceProvider.GetRequiredService<MailMessageDeletionService>()
                .DeleteAsync(owner, target.Id, CancellationToken.None));
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.MailDeletionTombstones.CountAsync());
            Assert.Equal(5, await db.FileItems.CountAsync(f => f.OwnerUserId == owner && f.DeletedAt == null && f.Name != "archivio.pst"));

            var audit = await scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>().AuditAsync();
            Assert.Equal(audit.TotalBlobs, audit.MatchedReferenceCount);
        }
    }

    private (WebApplicationFactory<Program> Factory, IDisposable Base) NewFactory(FakePstEngine engine)
    {
        var baseFactory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, new Dictionary<string, string?>());
        var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPstEngine>();
            services.AddSingleton<IPstEngine>(engine);
        }));
        return (factory, baseFactory);
    }

    private static async Task<(Guid Owner, Guid ArchiveId)> ImportSampleAsync(WebApplicationFactory<Program> factory)
    {
        var owner = Guid.NewGuid();
        Guid pstId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User { Id = owner, Email = $"mail-{owner:N}@example.com", DisplayName = "Owner", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await using var bytes = new MemoryStream(FakePstEngine.Bytes("!BDN" + owner.ToString("N")));
            pstId = (await scope.ServiceProvider.GetRequiredService<IFileItemService>()
                .CreateAsync(owner, null, "archivio.pst", "application/vnd.ms-outlook", bytes)).Id;
        }
        MailStartResult started;
        using (var scope = factory.Services.CreateScope())
        {
            started = await scope.ServiceProvider.GetRequiredService<MailArchiveService>().StartFromFileAsync(owner, pstId, CancellationToken.None);
        }
        for (var i = 0; i < 200; i++)
        {
            using var scope = factory.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(10) == 0) break;
        }
        return (owner, started.Archive!.Id);
    }

    [SkippableFact]
    public async Task Parallel_deletes_of_the_same_message_delete_once_and_release_the_document_blob_once()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var (factory, baseFactory) = NewFactory(FakePstEngine.Sample());
        using var _ = baseFactory;
        var (owner, archiveId) = await ImportSampleAsync(factory);

        List<(Guid Id, Guid Blob)> messages;
        Dictionary<Guid, long> attachmentRefs;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            messages = (await db.MailMessages.AsNoTracking().Where(m => m.ArchiveId == archiveId)
                .Select(m => new { m.Id, Blob = m.BlobObjectId!.Value }).ToListAsync())
                .Select(m => (m.Id, m.Blob)).ToList();
            attachmentRefs = await db.BlobObjects.AsNoTracking()
                .Where(b => db.FileItems.Where(f => f.OwnerUserId == owner && f.Name != "archivio.pst").Select(f => f.BlobObjectId).Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, b => b.ReferenceCount);
        }
        Assert.Equal(4, messages.Count);
        Assert.NotEmpty(attachmentRefs);

        foreach (var (messageId, blobId) in messages)
        {
            // Eight truly concurrent DELETEs of one message, each on its own
            // connection and transaction.
            using var gate = new SemaphoreSlim(0);
            var racers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                using var scope = factory.Services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<MailMessageDeletionService>();
                await gate.WaitAsync();
                return await service.DeleteAsync(owner, messageId, CancellationToken.None);
            })).ToList();
            gate.Release(racers.Count);
            var outcomes = await Task.WhenAll(racers);

            Assert.Equal(1, outcomes.Count(o => o == MailDeleteOutcome.Deleted));
            Assert.All(outcomes.Where(o => o != MailDeleteOutcome.Deleted), o => Assert.Equal(MailDeleteOutcome.AlreadyDeleted, o));

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.MailMessages.AnyAsync(m => m.Id == messageId));
            Assert.Equal(1, await db.MailDeletionTombstones.CountAsync(t => t.DeletedMessageId == messageId));
            Assert.Equal(0L, await db.BlobObjects.Where(b => b.Id == blobId).Select(b => b.ReferenceCount).FirstAsync());
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Attachment files and their blobs are untouched by any delete.
            var after = await db.BlobObjects.AsNoTracking().Where(b => attachmentRefs.Keys.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, b => b.ReferenceCount);
            Assert.Equal(attachmentRefs, after);
            Assert.Equal(4, await db.MailDeletionTombstones.CountAsync());
            Assert.False(await db.MailMessages.AnyAsync(m => m.Status == MailMessageStatuses.Deleting));
            var archive = await scope.ServiceProvider.GetRequiredService<MailArchiveService>().GetArchiveAsync(owner, archiveId, CancellationToken.None);
            Assert.Equal(0, archive!.MessageCount);
            var audit = await scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>().AuditAsync();
            Assert.Equal(audit.TotalBlobs, audit.MatchedReferenceCount);
        }
    }

    [SkippableFact]
    public async Task Search_on_postgres_uses_the_trigram_and_full_text_indexes_and_stays_owner_scoped()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var (factory, baseFactory) = NewFactory(FakePstEngine.Sample());
        using var _ = baseFactory;
        var (owner, archiveId) = await ImportSampleAsync(factory);
        var (other, otherArchiveId) = await ImportSampleAsync(factory);
        const int seeded = 50_000;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Synthetic rows cloned from one imported message (search reads
            // only the row, never the document), for both owners.
            foreach (var (o, a) in new[] { (owner, archiveId), (other, otherArchiveId) })
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO mail_messages ("Id", "OwnerUserId", "ArchiveId", "FolderId", "SourceMessageKey", "Status",
                        "BlobObjectId", "DocumentFormat", "DocumentVersion", "DocumentSizeBytes", "MessageClass", "Subject",
                        "FromName", "FromAddress", "ToSummary", "Preview", "SortDate", "HasAttachments", "AttachmentCount",
                        "SearchSubject", "SearchFrom", "SearchRecipients", "CreatedAt")
                    SELECT gen_random_uuid(), m."OwnerUserId", m."ArchiveId", m."FolderId", 'm:seed' || g, m."Status",
                        NULL, m."DocumentFormat", m."DocumentVersion", 0, m."MessageClass", 'Seed ' || g,
                        m."FromName", 'sender' || (g % 500) || '@example.test', m."ToSummary", NULL,
                        timestamptz '2020-01-01' + (g || ' minutes')::interval, false, 0,
                        'newsletter numero ' || g || ' ' || md5(g::text), 'sender' || (g % 500) || '@example.test',
                        'team' || (g % 50) || '@example.test', now()
                    FROM generate_series(1, {seeded}) g,
                        LATERAL (SELECT * FROM mail_messages WHERE "ArchiveId" = {a} AND "OwnerUserId" = {o} LIMIT 1) m
                    """);
            }
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO mail_message_texts ("MessageId", "OwnerUserId", "SortDate", "Text")
                SELECT "Id", "OwnerUserId", "SortDate", 'testo del messaggio ' || "SourceMessageKey" || ' ' || md5("Id"::text)
                    || ' saluti cordiali'
                FROM mail_messages WHERE "SourceMessageKey" LIKE 'm:seed%';
                """);
            await db.Database.ExecuteSqlRawAsync("ANALYZE mail_messages; ANALYZE mail_message_texts;");
        }

        var cases = new (string Label, MailMessageQuery Query, string Index)[]
        {
            ("subject", new MailMessageQuery(null, null, null, null, null, "trimestrale", null, null, null, null, 50), "ix_mail_messages_search_subject_trgm"),
            ("from", new MailMessageQuery(null, null, null, "carla@example", null, null, null, null, null, null, 50), "ix_mail_messages_search_from_trgm"),
            ("to", new MailMessageQuery(null, null, null, null, "bob@example", null, null, null, null, null, 50), "ix_mail_messages_search_recipients_trgm"),
            ("q", new MailMessageQuery(null, null, "relazione", null, null, null, null, null, null, null, 50), "ix_mail_messages_search_subject_trgm"),
            // A word of the sample's body only ("Testo originale", embedded),
            // by prefix: through the full-text index.
            ("q body", new MailMessageQuery(null, null, "origin", null, null, null, null, null, null, null, 50), "ix_mail_message_texts_fts"),
        };
        foreach (var (label, query, index) in cases)
        {
            using var scope = factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<MailMessageQueryService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var page = await service.SearchAsync(owner, query, CancellationToken.None);
            stopwatch.Stop();
            Assert.NotEmpty(page!.Items);
            // Owner scoping: the other owner's identical messages never appear.
            Assert.All(page.Items, m => Assert.Equal(archiveId, m.ArchiveId));

            var plan = await ExplainAsync(db, service.Page(owner, query, 50)!.ToQueryString());
            _output.WriteLine($"[{label}] {stopwatch.ElapsedMilliseconds} ms over {2 * seeded + 8} rows\n{plan}");
            Assert.Contains(index, plan);
            Assert.DoesNotContain("Seq Scan on mail_messages", plan);
            Assert.DoesNotContain("Seq Scan on mail_message_texts", plan);
        }
    }

    // Opt-in end-to-end measurement on PostgreSQL with the real engine:
    // NUBARCA_MAIL_VOLUME_PST names a generated PST
    // (`cargo run -p nubarca-pst-fixture-gen -- volume <out> <messages>`) and
    // NUBARCA_PST_IMPORTER_BIN (or the release build) the engine.
    [SkippableFact]
    public async Task Volume_import_end_to_end_on_postgres()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var volume = Environment.GetEnvironmentVariable("NUBARCA_MAIL_VOLUME_PST");
        Skip.If(string.IsNullOrWhiteSpace(volume) || !File.Exists(volume), "NUBARCA_MAIL_VOLUME_PST is not set.");
        var binary = Environment.GetEnvironmentVariable("NUBARCA_PST_IMPORTER_BIN") is { Length: > 0 } configured
            ? configured
            : Path.Combine(RepositoryRoot(), "tools", "nubarca-pst-importer", "target", "release", "nubarca-pst-importer");
        Skip.IfNot(File.Exists(binary), "nubarca-pst-importer is not built.");

        using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!,
            new Dictionary<string, string?> { ["MailArchive:ImporterPath"] = binary });
        var owner = Guid.NewGuid();
        Guid pstId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User { Id = owner, Email = $"mail-{owner:N}@example.com", DisplayName = "Owner", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await using var bytes = File.OpenRead(volume!);
            pstId = (await scope.ServiceProvider.GetRequiredService<IFileItemService>()
                .CreateAsync(owner, null, "volume.pst", "application/vnd.ms-outlook", bytes)).Id;
        }
        MailStartResult started;
        using (var scope = factory.Services.CreateScope())
        {
            started = await scope.ServiceProvider.GetRequiredService<MailArchiveService>().StartFromFileAsync(owner, pstId, CancellationToken.None);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100_000; i++)
        {
            using var scope = factory.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(10) == 0) break;
        }
        clock.Stop();
        using (var scope = factory.Services.CreateScope())
        {
            var archive = (await scope.ServiceProvider.GetRequiredService<MailArchiveService>()
                .GetArchiveAsync(owner, started.Archive!.Id, CancellationToken.None))!;
            Assert.Equal("succeeded", archive.LatestRun!.Status);
            _output.WriteLine($"MAILVOLUME-PG messages={archive.MessageCount} folders={archive.FolderCount} "
                + $"attachments={archive.LatestRun.AttachmentsCreated + archive.LatestRun.AttachmentsReused} "
                + $"seconds={clock.Elapsed.TotalSeconds:F1} msgPerSecond={archive.MessageCount / clock.Elapsed.TotalSeconds:F0}");
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var texts = await db.MailMessageTexts.Where(t => t.OwnerUserId == owner && t.Text != "").CountAsync();
            _output.WriteLine($"MAILVOLUME-PG withText={texts} failed={archive.LatestRun.FailedMessages}");
            // Every message has its text row, written with its completion.
            Assert.Equal(archive.MessageCount, await db.MailMessageTexts.CountAsync(t => t.OwnerUserId == owner));
            var audit = await scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>().AuditAsync();
            Assert.Equal(audit.TotalBlobs, audit.MatchedReferenceCount);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NubArca.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("could not locate the repository root");
    }

    // EXPLAIN ANALYZE of the SQL EF generated, with its parameters inlined
    // as untyped literals (PostgreSQL infers their types as it would bind them).
    private static async Task<string> ExplainAsync(AppDbContext db, string queryString)
    {
        var lines = queryString.Split('\n');
        var sql = string.Join('\n', lines.Where(l => !l.StartsWith("-- ")));
        var parameters = lines.Where(l => l.StartsWith("-- @"))
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^-- (@\w+)='(.*)'(?: \(.*\))?\s*$"))
            .Where(m => m.Success)
            .Select(m => (Name: m.Groups[1].Value, Value: m.Groups[2].Value))
            .OrderByDescending(p => p.Name.Length);
        foreach (var (name, value) in parameters)
        {
            sql = System.Text.RegularExpressions.Regex.Replace(sql, System.Text.RegularExpressions.Regex.Escape(name) + @"\b",
                "'" + value.Replace("'", "''") + "'");
        }
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN (ANALYZE, BUFFERS) " + sql;
        var plan = new System.Text.StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.AppendLine(reader.GetString(0));
        }
        return plan.ToString();
    }
}
