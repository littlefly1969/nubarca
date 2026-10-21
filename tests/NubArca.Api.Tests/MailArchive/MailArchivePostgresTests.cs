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
using Xunit;

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

    public MailArchivePostgresTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
}
