using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Scheduler v2 — the REAL media.derivatives.backfill running through the job
// engine across multiple cooperative slices (tiny per-slice item budget), with
// idempotency and single-logical-row guarantees verified end-to-end.
public sealed class MediaDerivativesSlicingTests
{
    // Force a one-item-per-slice budget so a handful of images takes several
    // slices, exercising checkpoint resume.
    private static SqliteWebApplicationFactory NewFactory()
        => new(new Dictionary<string, string?> { ["Jobs:MaintenanceSliceItemBudget"] = "1" });

    private static async Task<FileItem> UploadImageAsync(
        SqliteWebApplicationFactory factory, Guid ownerId, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        // generateSmallThumbnail:false → both derivatives missing, so the
        // backfill has real work to do.
        return await files.CreateAsync(
            ownerId, null, name, "image/png", new MemoryStream(ImageFixtures.PlainPng()),
            generateSmallThumbnail: false);
    }

    [Fact]
    public async Task Backfill_Runs_Across_Slices_Idempotently_As_One_Row()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var ownerId = await factory.SeedUserAsync();

        var files = new List<FileItem>();
        for (var i = 0; i < 3; i++)
        {
            files.Add(await UploadImageAsync(factory, ownerId, $"img{i}.png"));
        }

        // Enqueue via the real queue (registry → maintenance priority → sliced).
        Guid jobId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var job = await queue.EnqueueAsync(
                JobTypes.MediaDerivativesBackfill, new MediaDerivativesBackfillJobPayload());
            jobId = job.Id;
        }

        // Drive the worker slice-by-slice (fresh scope per slice, like JobWorker).
        var slices = 0;
        for (var i = 0; i < 30; i++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            var processed = await processor.ProcessAvailableAsync(maxJobs: 1);
            if (processed == 0) break;
            slices++;

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var status = await db.BackgroundJobs.AsNoTracking()
                .Where(j => j.Id == jobId).Select(j => j.Status).SingleAsync();
            if (JobStatuses.IsTerminal(status)) break;
        }

        await using (var verify = factory.Services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

            // One logical job, one row, completed; multiple slices ran.
            var row = await db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
            Assert.Equal(JobStatuses.Succeeded, row.Status);
            Assert.True(row.SliceNumber >= 1, $"expected >=1 continuation, got {row.SliceNumber}");
            Assert.True(slices >= 3, $"expected the budget=1 backfill to take several slices, took {slices}");
            Assert.Equal(1, await db.BackgroundJobs.CountAsync()); // never duplicated

            // Every image got exactly small + medium — no duplicates, no misses.
            foreach (var f in files)
            {
                var sizes = await db.FileThumbnails.AsNoTracking()
                    .Where(t => t.FileItemId == f.Id)
                    .Select(t => t.Size).OrderBy(s => s).ToListAsync();
                Assert.Equal(new[] { "medium", "small" }, sizes);
            }
        }
    }

    [Fact]
    public async Task Reenqueue_With_Fresh_Checkpoint_Is_A_Noop_When_Nothing_Missing()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var ownerId = await factory.SeedUserAsync();
        await UploadImageAsync(factory, ownerId, "only.png");

        async Task RunToCompletionAsync()
        {
            for (var i = 0; i < 30; i++)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
                if (await processor.ProcessAvailableAsync(maxJobs: 1) == 0) break;
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var anyActive = await db.BackgroundJobs.AsNoTracking()
                    .AnyAsync(j => j.Status == JobStatuses.Queued || j.Status == JobStatuses.Running);
                if (!anyActive) break;
            }
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            await queue.EnqueueAsync(JobTypes.MediaDerivativesBackfill, new MediaDerivativesBackfillJobPayload());
        }
        await RunToCompletionAsync();

        // Second run: a brand-new job with no checkpoint finds nothing missing
        // and completes cleanly without creating duplicate thumbnails.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            await queue.EnqueueAsync(JobTypes.MediaDerivativesBackfill, new MediaDerivativesBackfillJobPayload());
        }
        await RunToCompletionAsync();

        await using var verify = factory.Services.CreateAsyncScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await vdb.FileThumbnails.CountAsync()); // small + medium, no dupes
        Assert.True(await vdb.BackgroundJobs.AsNoTracking()
            .Where(j => j.Type == JobTypes.MediaDerivativesBackfill)
            .AllAsync(j => j.Status == JobStatuses.Succeeded));
    }
}
