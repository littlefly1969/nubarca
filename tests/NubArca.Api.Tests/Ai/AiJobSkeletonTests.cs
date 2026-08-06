using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Data;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Ai;

// Phase 0C: the AI skeleton backfills are real, registered, enqueueable jobs in
// the Compute band that NO-OP safely. These tests drive the genuine queue +
// processor (like JobsCliTests) and assert the no-op contract: no embeddings/
// chunks/detections/annotations, no per-blob status rows, and at most one
// aggregate transient diagnostic on provider-unavailable.
public sealed class AiJobSkeletonTests
{
    private static readonly string[] AllAiJobTypes =
    {
        JobTypes.AiPhotosEmbeddingsBackfill,
        JobTypes.AiDocumentsExtractBackfill,
        JobTypes.AiDocumentsEmbeddingsBackfill,
        JobTypes.AiFacesDetectBackfill,
        JobTypes.AiFacesEmbeddingsBackfill,
        JobTypes.AiFacesClusterBackfill,
        JobTypes.AiTagsGenerateBackfill,
    };

    private static SqliteWebApplicationFactory NewFactory(params (string Key, string Value)[] settings)
    {
        var dict = settings.ToDictionary(s => s.Key, s => (string?)s.Value);
        var factory = new SqliteWebApplicationFactory(dict);
        factory.EnsureDatabaseCreated();
        return factory;
    }

    private static async Task<string> EnqueueAndRunAsync(
        SqliteWebApplicationFactory factory, string jobType, AiBackfillJobPayload? payload = null)
    {
        Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var job = await queue.EnqueueAsync(jobType, payload ?? new AiBackfillJobPayload());
            jobId = job.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            await processor.ProcessAvailableAsync(10);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.BackgroundJobs.Where(j => j.Id == jobId).Select(j => j.Status).SingleAsync();
        }
    }

    private static async Task AssertNoAiDomainRowsAsync(SqliteWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Phase 0C never materializes per-blob status rows nor any derived AI data.
        Assert.Equal(0, await db.BlobAiArtifactStatuses.CountAsync());
        Assert.Equal(0, await db.BlobEmbeddings.CountAsync());
        Assert.Equal(0, await db.DocumentTexts.CountAsync());
        Assert.Equal(0, await db.DocumentChunks.CountAsync());
        Assert.Equal(0, await db.DocumentChunkEmbeddings.CountAsync());
        Assert.Equal(0, await db.FaceDetections.CountAsync());
        Assert.Equal(0, await db.FaceEmbeddings.CountAsync());
        Assert.Equal(0, await db.AiAnnotations.CountAsync());
    }

    private static async Task<int> DiagnosticCountAsync(SqliteWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AiIndexDiagnostics.CountAsync();
    }

    [Fact]
    public void All_Ai_Job_Types_Are_Registered_Handlers()
    {
        using var factory = NewFactory();
        using var scope = factory.Services.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IJobHandler>().ToList();

        foreach (var jobType in AllAiJobTypes)
        {
            Assert.Contains(handlers, h => h.JobType == jobType);
        }
    }

    [Theory]
    [MemberData(nameof(AiJobTypeCases))]
    public async Task Ai_Job_NoOps_When_Ai_Disabled(string jobType)
    {
        // Default factory: Ai:Enabled is false.
        using var factory = NewFactory();

        var status = await EnqueueAndRunAsync(factory, jobType);

        Assert.Equal(JobStatuses.Succeeded, status);
        Assert.Equal(0, await DiagnosticCountAsync(factory)); // disabled is normal: no diagnostic
        await AssertNoAiDomainRowsAsync(factory);
    }

    [Fact]
    public async Task Ai_Job_NoOps_When_Capability_Flag_Disabled()
    {
        // AI enabled globally, but the photos-embeddings capability flag is off.
        using var factory = NewFactory(("Ai:Enabled", "true"));

        var status = await EnqueueAndRunAsync(factory, JobTypes.AiPhotosEmbeddingsBackfill);

        Assert.Equal(JobStatuses.Succeeded, status);
        Assert.Equal(0, await DiagnosticCountAsync(factory)); // flag-off is normal: no diagnostic
        await AssertNoAiDomainRowsAsync(factory);
    }

    [Fact]
    public async Task Provider_Unavailable_Writes_No_Status_Rows_And_At_Most_One_Transient_Diagnostic()
    {
        // AI enabled + capability flag on, but NO profile seeded → the default
        // profile is missing, so the provider is unavailable.
        using var factory = NewFactory(
            ("Ai:Enabled", "true"),
            ("Ai:ImageEmbeddingsEnabled", "true"));

        var status = await EnqueueAndRunAsync(factory, JobTypes.AiPhotosEmbeddingsBackfill);

        Assert.Equal(JobStatuses.Succeeded, status);
        await AssertNoAiDomainRowsAsync(factory); // never per-blob skipped/failed rows

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // At most one aggregate transient provider diagnostic.
        var diagnostics = await db.AiIndexDiagnostics.AsNoTracking().ToListAsync();
        Assert.True(diagnostics.Count <= 1);
        Assert.All(diagnostics, d =>
        {
            Assert.Equal("provider", d.TargetKind);
            Assert.False(d.IsPermanent);
            Assert.Null(d.BlobObjectId);
        });
    }

    [Fact]
    public async Task Available_Provider_Still_NoOps_In_Phase0C()
    {
        // AI enabled + flag on + deterministic profiles seeded → the capability
        // resolves available, but Phase 0C still performs no inference.
        using var factory = NewFactory(
            ("Ai:Enabled", "true"),
            ("Ai:ImageEmbeddingsEnabled", "true"));

        using (var scope = factory.Services.CreateScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
            await registry.SeedDeterministicProfilesAsync();
        }

        var status = await EnqueueAndRunAsync(factory, JobTypes.AiPhotosEmbeddingsBackfill);

        Assert.Equal(JobStatuses.Succeeded, status);
        Assert.Equal(0, await DiagnosticCountAsync(factory)); // available: no diagnostic
        await AssertNoAiDomainRowsAsync(factory);
    }

    [Fact]
    public async Task Cancelled_Ai_Job_Writes_No_Permanent_Diagnostic()
    {
        using var factory = NewFactory(
            ("Ai:Enabled", "true"),
            ("Ai:ImageEmbeddingsEnabled", "true"));

        Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var job = await queue.EnqueueAsync(
                JobTypes.AiPhotosEmbeddingsBackfill, new AiBackfillJobPayload());
            jobId = job.Id;
            await queue.RequestCancellationAsync(jobId);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            await processor.ProcessAvailableAsync(10);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobStatus = await db.BackgroundJobs.Where(j => j.Id == jobId).Select(j => j.Status).SingleAsync();
            Assert.Equal(JobStatuses.Cancelled, jobStatus);
            // No permanent diagnostic from a cancellation.
            Assert.Equal(0, await db.AiIndexDiagnostics.CountAsync(d => d.IsPermanent));
        }

        await AssertNoAiDomainRowsAsync(factory);
    }

    public static IEnumerable<object[]> AiJobTypeCases() => AllAiJobTypes.Select(t => new object[] { t });
}
