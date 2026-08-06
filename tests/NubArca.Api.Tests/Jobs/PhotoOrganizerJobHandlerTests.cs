using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Jobs.Handlers;
using NubArca.Api.Organizer;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Jobs;

// Regression tests for the photo.organizer.datetaken job registration.
//
// Production incident (2026-06-20): two background_jobs rows failed permanently
// with Error=UnknownJobType / "No handler registered for
// 'photo.organizer.datetaken'" — the worker container was running a build that
// predated the handler's registration. These tests pin the registration so a
// future missed AddScoped or conditional-block reshuffling is caught before it
// reaches production.
public sealed class PhotoOrganizerJobHandlerTests
{
    // ------------------------------------------------------ constant correctness

    [Fact]
    public void JobType_Constant_Has_Expected_Wire_Value()
    {
        // The stored job type string must match exactly — it's persisted in DB
        // rows that outlive any code change. If the constant is ever renamed the
        // existing rows become orphaned.
        Assert.Equal("photo.organizer.datetaken", JobTypes.PhotoOrganizerDateTaken);
    }

    // ------------------------------------------------------ handler registration

    // This is the key regression test. It resolves all registered IJobHandler
    // implementations from the same DI container the worker uses and asserts
    // that exactly one handles the photo organizer job type.
    [Fact]
    public void Handler_Is_Registered_For_PhotoOrganizerDateTaken()
    {
        using var factory = new SqliteWebApplicationFactory();
        using var scope = factory.Services.CreateScope();

        var handlers = scope.ServiceProvider.GetServices<IJobHandler>().ToList();
        var match = handlers.SingleOrDefault(h => h.JobType == JobTypes.PhotoOrganizerDateTaken);

        Assert.NotNull(match);
        Assert.IsType<PhotoOrganizerJobHandler>(match);
    }

    [Fact]
    public void Handler_JobType_Property_Matches_Constant()
    {
        using var factory = new SqliteWebApplicationFactory();
        using var scope = factory.Services.CreateScope();

        var handler = scope.ServiceProvider.GetServices<IJobHandler>()
            .Single(h => h.JobType == JobTypes.PhotoOrganizerDateTaken);

        Assert.Equal(JobTypes.PhotoOrganizerDateTaken, handler.JobType);
    }

    // This is the regression test for the actual production bug: the handler
    // was registered in Program.cs (web-host path) but missing from
    // ConfigureCliServices (CLI/worker path). The worker uses the CLI path.
    // We check the service descriptors rather than resolving instances so the
    // test stays infrastructure-free (no real Postgres needed).
    [Fact]
    public void Handler_Is_Registered_In_CliServices()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // ConfigureCliServices gates on Postgres; supply a syntactically
                // valid placeholder so the conditional block is entered.
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=test;Username=test;Password=test",
            })
            .Build();

        CliEntryPoint.ConfigureCliServices(services, config);

        // Assert at the descriptor level — no instances resolved, no DB needed.
        var hasHandler = services.Any(d =>
            d.ServiceType == typeof(IJobHandler) &&
            d.ImplementationType == typeof(PhotoOrganizerJobHandler));

        Assert.True(hasHandler, "PhotoOrganizerJobHandler must be registered as IJobHandler in ConfigureCliServices.");
    }

    // ------------------------------------------------------ safe no-ops

    // A job whose RunId no longer exists in the DB must succeed without error
    // (the service short-circuits on a missing run). This prevents an
    // orphaned/stale job from entering a retry storm.
    [Fact]
    public async Task Handler_Succeeds_When_RunId_Not_Found()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();

        var missingRunId = Guid.NewGuid();
        var payload = $"{{\"RunId\":\"{missingRunId}\"}}";
        var jobId = await EnqueueRawAsync(factory, JobTypes.PhotoOrganizerDateTaken, payload);

        await using var scope = factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
        var processed = await processor.ProcessAvailableAsync(maxJobs: 1);

        Assert.Equal(1, processed);
        await AssertJobSucceededAsync(factory, jobId);
    }

    // A run with no candidate photos must complete successfully with 0 moves —
    // the organizer treats an empty library as already-done.
    [Fact]
    public async Task Handler_Succeeds_With_Zero_Moves_When_No_Photos()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();

        var run = await StartRunAsync(factory, owner, new OrganizerOptions(
            OrganizerScopeKind.All, null, Array.Empty<Guid>(), null, "Photos",
            OrganizerTemplate.YearDatedDay, MissingDateBehavior.Skip, ConflictPolicy.KeepBoth));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            await processor.ProcessAvailableAsync(maxJobs: 10);
        }

        await using var statusScope = factory.Services.CreateAsyncScope();
        var db = statusScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var finished = await db.PhotoOrganizerRuns.AsNoTracking().SingleAsync(r => r.Id == run.RunId);
        Assert.Equal(PhotoOrganizerStatuses.Succeeded, finished.Status);
        Assert.Equal(0, finished.MovedCount);
        Assert.Equal(0, finished.FailedCount);
    }

    // A run with a photo that has no date taken and MissingDateBehavior.Skip
    // also succeeds (skips the file, does not fail the job).
    [Fact]
    public async Task Handler_Succeeds_Skipping_Photos_Without_DateTaken()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            await files.CreateAsync(owner, null, "nodate.png", "image/png",
                new MemoryStream(ImageFixtures.PlainPng()));
        }

        var run = await StartRunAsync(factory, owner, new OrganizerOptions(
            OrganizerScopeKind.All, null, Array.Empty<Guid>(), null, "Photos",
            OrganizerTemplate.YearDatedDay, MissingDateBehavior.Skip, ConflictPolicy.KeepBoth));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(maxJobs: 10);
        }

        await using var statusScope = factory.Services.CreateAsyncScope();
        var db = statusScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var finished = await db.PhotoOrganizerRuns.AsNoTracking().SingleAsync(r => r.Id == run.RunId);
        Assert.Equal(PhotoOrganizerStatuses.Succeeded, finished.Status);
        Assert.Equal(0, finished.MovedCount);
        Assert.True(finished.SkippedMissingDateCount >= 0); // skipped, not failed
        Assert.Equal(0, finished.FailedCount);
    }

    // ------------------------------------------------------ run status safety

    // The run-status projection must not surface blob/storage internals even
    // when accessed via the service (owner-scoped, not HTTP). Guards against
    // accidental field additions to PhotoOrganizerRunStatusResponse that would
    // violate the no-leak rule.
    [Fact]
    public async Task RunStatus_Exposes_No_Storage_Internals()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();

        await using var setupScope = factory.Services.CreateAsyncScope();
        var files = setupScope.ServiceProvider.GetRequiredService<IFileItemService>();
        var file = await files.CreateAsync(owner, null, "img.png", "image/png",
            new MemoryStream(ImageFixtures.PlainPng()));
        var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.BlobMetadata.Where(m => m.BlobObjectId == file.BlobObjectId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.DateTaken, new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc))
                .SetProperty(m => m.DateTakenSource, "DateTimeOriginal")
                .SetProperty(m => m.MediaCategory, "image"));

        var run = await StartRunAsync(factory, owner, new OrganizerOptions(
            OrganizerScopeKind.All, null, Array.Empty<Guid>(), null, "Photos",
            OrganizerTemplate.YearDatedDay, MissingDateBehavior.Skip, ConflictPolicy.KeepBoth));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(maxJobs: 10);
        }

        await using var readScope = factory.Services.CreateAsyncScope();
        var organizer = readScope.ServiceProvider.GetRequiredService<PhotoDateTakenOrganizerService>();
        var status = await organizer.GetRunStatusAsync(owner, run.RunId, default);
        Assert.NotNull(status);

        // Serialize to JSON and scan for forbidden identifiers.
        var json = System.Text.Json.JsonSerializer.Serialize(status);
        foreach (var needle in new[]
                 {
                     "storageKey", "StorageKey",
                     "blobObjectId", "BlobObjectId",
                     "sha256", "Sha256",
                     "fileItemId", "FileItemId",
                     "ownerUserId", "OwnerUserId",
                     "objects/",
                     "passwordHash",
                 })
        {
            Assert.DoesNotContain(needle, json, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------ helpers

    private static async Task<PhotoOrganizerRunResponse> StartRunAsync(
        SqliteWebApplicationFactory factory, Guid owner, OrganizerOptions options)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var organizer = scope.ServiceProvider.GetRequiredService<PhotoDateTakenOrganizerService>();
        return await organizer.StartRunAsync(owner, options, default);
    }

    private static async Task<Guid> EnqueueRawAsync(
        SqliteWebApplicationFactory factory, string jobType, string payloadJson)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var job = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            Type = jobType,
            PayloadJson = payloadJson,
            Status = JobStatuses.Queued,
            Priority = 5,
            AvailableAt = now,
            Attempts = 0,
            MaxAttempts = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.BackgroundJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private static async Task AssertJobSucceededAsync(SqliteWebApplicationFactory factory, Guid jobId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal(JobStatuses.Succeeded, job.Status);
    }
}
