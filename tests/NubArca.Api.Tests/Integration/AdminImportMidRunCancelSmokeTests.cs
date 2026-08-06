using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Admin;
using NubArca.Api.Data;
using NubArca.Api.Jobs;
using NubArca.Api.Users;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// Slice 91 smoke test — MID-RUN cooperative cancellation of an admin import,
// end-to-end through Background Jobs v2 on REAL PostgreSQL (so the lease/
// heartbeat timer + handler run on separate pooled connections, exactly as in
// production). Proves: a running import observes the job's cancellation flag
// (flipped by the heartbeat), stops at a safe checkpoint, finalizes the run +
// job as `cancelled`, and leaves only COMPLETE FileItems for the files it had
// already imported (no partial/corrupt rows). Skipped when Docker is absent.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class AdminImportMidRunCancelSmokeTests
{
    private readonly PostgresContainerFixture _fixture;

    public AdminImportMidRunCancelSmokeTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Running_Import_Cancelled_MidRun_Stops_Cleanly_With_No_Partial_FileItems()
    {
        Skip.IfNot(_fixture.Available, "Docker/PostgreSQL not available.");

        // ~120 small files with a 25ms inter-file delay → the import takes a
        // few seconds, giving the 1s heartbeat time to observe a cancel and the
        // handler time to stop well before the walk finishes.
        const int fileCount = 120;
        var importDir = Path.Combine(Path.GetTempPath(), $"nc-midrun-{Guid.NewGuid():N}");
        Directory.CreateDirectory(importDir);
        for (var i = 0; i < fileCount; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(importDir, $"f{i:D4}.txt"), $"content-{i}");
        }

        var settings = new Dictionary<string, string?>
        {
            ["AdminImport:Enabled"] = "true",
            ["AdminImport:Roots:0"] = importDir,
            ["AdminImport:DelayBetweenFilesMs"] = "25",
            ["AdminImport:MaxRunMinutes"] = "0",   // no budget pause
            ["AdminImport:YieldEveryFiles"] = "1", // flush progress every file
            ["Jobs:HeartbeatSeconds"] = "1",
            ["Jobs:LeaseSeconds"] = "30",
        };

        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, settings);

        // Clean slate (the collection runs serially, so no concurrent writers).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE background_jobs, admin_import_runs, file_items, folders, " +
                "blob_objects, audit_logs, users RESTART IDENTITY CASCADE;");
        }

        Guid targetUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            targetUserId = (await users.CreateAsync("target@example.com", "Target")).Id;
        }

        // Enqueue the import.
        Guid runId, jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var import = scope.ServiceProvider.GetRequiredService<IAdminImportService>();
            var started = await import.StartRunAsync(
                Guid.NewGuid(),
                new AdminImportRunRequest(await FirstRootIdAsync(factory), "", targetUserId, null),
                CancellationToken.None);
            runId = started.ImportRunId;
            jobId = started.JobId!.Value;
        }

        // Run the job on a background task (its own scope, as the worker would).
        var run = Task.Run(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            await processor.ProcessAvailableAsync(1, log: null, CancellationToken.None);
        });

        // Wait until the import is genuinely mid-run (some files in, not done).
        var importedAtCancel = await WaitForMidRunAsync(factory, runId, fileCount, run);

        // Cancel via the job engine (the same path the dashboard + import cancel
        // endpoint use) — the heartbeat flips the running handler's flag.
        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            Assert.True(await jobs.RequestCancellationAsync(jobId));
        }

        // The handler should stop and finalize within a couple of heartbeats.
        Assert.True(await CompletesWithinAsync(run, TimeSpan.FromSeconds(30)),
            "Import job did not finish after cancellation.");

        // Assert coherent terminal state + no partial FileItems.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var import = scope.ServiceProvider.GetRequiredService<IAdminImportService>();

            var job = await db.BackgroundJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
            var status = await import.GetRunStatusAsync(runId, CancellationToken.None);
            var fileItemCount = await db.FileItems.CountAsync(
                f => f.OwnerUserId == targetUserId && f.DeletedAt == null);

            Assert.Equal(JobStatuses.Cancelled, job.Status);          // job is cancelled
            Assert.Equal("cancelled", status!.Status);                // run reconciles to cancelled
            Assert.InRange(status.ImportedFiles, 1, fileCount - 1);   // stopped mid-run
            // Every imported file is a COMPLETE FileItem — no partial/extra rows.
            Assert.Equal(status.ImportedFiles, fileItemCount);

            Assert.True(importedAtCancel <= status.ImportedFiles); // a few more may land before it stops
        }

        try { Directory.Delete(importDir, recursive: true); } catch { /* best effort */ }
    }

    private static async Task<string> FirstRootIdAsync(PostgresWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var import = scope.ServiceProvider.GetRequiredService<IAdminImportService>();
        return import.GetRoots().Roots[0].RootId;
    }

    // Polls the run row until it is running with at least 2 files imported (but
    // not finished). Fails fast if the job completes before we catch it mid-run.
    private static async Task<int> WaitForMidRunAsync(
        PostgresWebApplicationFactory factory, Guid runId, int fileCount, Task run)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var r = await db.AdminImportRuns.AsNoTracking()
                    .Where(x => x.Id == runId)
                    .Select(x => new { x.Status, x.ImportedFiles })
                    .FirstOrDefaultAsync();
                if (r is not null && r.ImportedFiles >= 2 && r.ImportedFiles < fileCount)
                {
                    return r.ImportedFiles;
                }
            }
            Assert.False(run.IsCompleted, "Import finished before it could be cancelled mid-run.");
            await Task.Delay(40);
        }
        throw new Xunit.Sdk.XunitException("Timed out waiting for the import to reach mid-run.");
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
        => await Task.WhenAny(task, Task.Delay(timeout)) == task;
}
