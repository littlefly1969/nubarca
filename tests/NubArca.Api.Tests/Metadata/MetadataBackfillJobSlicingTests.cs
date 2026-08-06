using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Metadata;

// Scheduler v2 — metadata.embedded.backfill running through the REAL job engine
// across cooperative slices: one logical row, foreground preemption, terminal
// cancellation without continuation, and the admin API hiding the checkpoint.
public sealed class MetadataBackfillJobSlicingTests
{
    // One-blob-per-slice budget so a handful of blobs takes several slices.
    private static SqliteWebApplicationFactory NewFactory()
        => new(new Dictionary<string, string?> { ["Jobs:MaintenanceSliceItemBudget"] = "1" });

    // Upload N images, then mark their metadata legacy-pending so the backfill
    // has real work (a fresh upload is already at the current version).
    private static async Task<List<Guid>> SeedPendingBlobsAsync(SqliteWebApplicationFactory factory, Guid ownerId, int n)
    {
        var blobIds = new List<Guid>();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            for (var i = 0; i < n; i++)
            {
                var f = await files.CreateAsync(
                    ownerId, null, $"m{i}.png", "image/png", new MemoryStream(ImageFixtures.PlainPng(width: 8 + i)));
                blobIds.Add(f.BlobObjectId);
            }
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BlobMetadata
                .Where(m => blobIds.Contains(m.BlobObjectId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.ExtractionStatus, MetadataStatuses.Pending)
                    .SetProperty(m => m.ExtractionVersion, (int?)null));
        }
        return blobIds;
    }

    private static async Task<Guid> EnqueueMetadataBackfillAsync(SqliteWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        var job = await queue.EnqueueAsync(JobTypes.MetadataEmbeddedBackfill, new MetadataBackfillJobPayload());
        return job.Id;
    }

    private static async Task<int> RunOneSliceAsync(SqliteWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
        return await processor.ProcessAvailableAsync(maxJobs: 1);
    }

    [Fact]
    public async Task Backfill_Runs_Across_Slices_As_One_Row_And_Idempotently()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var ownerId = await factory.SeedUserAsync();
        var blobIds = await SeedPendingBlobsAsync(factory, ownerId, 3);
        var jobId = await EnqueueMetadataBackfillAsync(factory);

        var slices = 0;
        for (var i = 0; i < 30; i++)
        {
            if (await RunOneSliceAsync(factory) == 0) break;
            slices++;
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var status = await db.BackgroundJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.Status).SingleAsync();
            if (JobStatuses.IsTerminal(status)) break;
        }

        await using var verify = factory.Services.CreateAsyncScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await vdb.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal(JobStatuses.Succeeded, row.Status);
        Assert.True(row.SliceNumber >= 1, $"expected continuations, got SliceNumber={row.SliceNumber}");
        Assert.True(slices >= 3, $"budget=1 over 3 blobs should take several slices, took {slices}");
        Assert.Null(row.CheckpointJson);                              // cleared on completion
        Assert.Equal(1, await vdb.BackgroundJobs.CountAsync());       // one logical row, never duplicated

        // Every seeded blob is now at the current version, processed once.
        foreach (var id in blobIds)
        {
            var meta = await vdb.BlobMetadata.AsNoTracking().SingleAsync(m => m.BlobObjectId == id);
            Assert.Equal(EmbeddedImageMetadataExtractor.Version, meta.ExtractionVersion);
            Assert.Equal(MetadataStatuses.Completed, meta.ExtractionStatus);
        }
    }

    [Fact]
    public async Task Queued_Import_Starts_After_The_Current_Metadata_Slice_Yields()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var ownerId = await factory.SeedUserAsync();
        await SeedPendingBlobsAsync(factory, ownerId, 5);
        await EnqueueMetadataBackfillAsync(factory);

        // One metadata slice runs and yields (budget=1, work remains).
        Assert.Equal(1, await RunOneSliceAsync(factory));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var meta = await db.BackgroundJobs.AsNoTracking()
                .SingleAsync(j => j.Type == JobTypes.MetadataEmbeddedBackfill);
            Assert.Equal(JobStatuses.Queued, meta.Status); // yielded, not done
            Assert.True(meta.SliceNumber >= 1);

            // A foreground import arrives mid-run.
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            await queue.EnqueueAsync(JobTypes.AdminImport, new AdminImportJobPayload(Guid.NewGuid()));

            // The next claim picks the foreground import, not the next metadata slice.
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            var claimed = await processor.ClaimNextAsync(CancellationToken.None);
            Assert.Equal(JobTypes.AdminImport, claimed!.Type);
        }
    }

    [Fact]
    public async Task Cancellation_Reaches_Terminal_Cancelled_Without_Continuation()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var ownerId = await factory.SeedUserAsync();
        await SeedPendingBlobsAsync(factory, ownerId, 5);
        var jobId = await EnqueueMetadataBackfillAsync(factory);

        Assert.Equal(1, await RunOneSliceAsync(factory)); // one slice runs, yields
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            Assert.True(await queue.RequestCancellationAsync(jobId));
        }
        await RunOneSliceAsync(factory); // next claim observes the flag → cancelled

        await using var verify = factory.Services.CreateAsyncScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await vdb.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal(JobStatuses.Cancelled, row.Status);
        Assert.Equal(1, await vdb.BackgroundJobs.CountAsync()); // no continuation row spawned
    }

    [Fact]
    public async Task Admin_Job_Summary_Hides_Checkpoint_And_Metadata()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var ownerId = await factory.SeedUserAsync();
        await SeedPendingBlobsAsync(factory, ownerId, 3);
        var jobId = await EnqueueMetadataBackfillAsync(factory);
        Assert.Equal(1, await RunOneSliceAsync(factory)); // produces a checkpoint

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Sanity: a checkpoint really exists internally for this job.
        Assert.NotNull(await db.BackgroundJobs.AsNoTracking().Where(j => j.Id == jobId)
            .Select(j => j.CheckpointJson).SingleAsync());

        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        var summary = await queue.GetAdminJobAsync(jobId);
        Assert.NotNull(summary);
        var json = JsonSerializer.Serialize(summary);

        // Safe scheduler fields are present...
        Assert.Contains("sliceNumber", json, StringComparison.OrdinalIgnoreCase);
        // ...but the internal checkpoint and any raw metadata are NOT exposed.
        Assert.DoesNotContain("checkpoint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failedids", json, StringComparison.OrdinalIgnoreCase);
        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
