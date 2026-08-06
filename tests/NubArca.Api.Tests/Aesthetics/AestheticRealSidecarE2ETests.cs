using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Aesthetics;
using NubArca.Api.Data;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Integration;
using NubArca.Api.Tests.Metadata;
using NubArca.Api.Users;
using Xunit;

namespace NubArca.Api.Tests.Aesthetics;

// REAL end-to-end durable-job test against a RUNNING HumanAesExpert sidecar and a
// disposable PostgreSQL. Gated: it is SKIPPED unless HUMANAES_E2E=1, so it never
// runs in normal CI (it needs the operator-installed model + a live sidecar).
//
// Env:
//   HUMANAES_E2E=1
//   HUMANAES_E2E_PG=Host=127.0.0.1;Port=55432;Database=nubarca_e2e;Username=postgres;Password=e2e
//   HUMANAES_E2E_SIDECAR=http://127.0.0.1:18091
//
// Drives the full path: create lab item -> request analysis -> immutable run ->
// durable BackgroundJob -> jobs worker (JobProcessor) -> REAL sidecar ->
// strict validation -> PostgreSQL run + 12 metrics -> detail service.
[Trait("Category", "External")]
public class AestheticRealSidecarE2ETests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("HUMANAES_E2E") == "1";

    [SkippableFact]
    public async Task Real_durable_job_persists_twelve_metrics_and_detail_exposes_them()
    {
        Skip.IfNot(Enabled, "HUMANAES_E2E != 1 (needs a live sidecar + disposable Postgres).");
        var pg = Environment.GetEnvironmentVariable("HUMANAES_E2E_PG")!;
        var sidecar = Environment.GetEnvironmentVariable("HUMANAES_E2E_SIDECAR")!;

        var settings = new Dictionary<string, string?>
        {
            ["Database:MigrateOnStartup"] = "true",
            ["HumanAesExpert:Enabled"] = "true",
            ["HumanAesExpert:SidecarBaseUrl"] = sidecar,
            ["HumanAesExpert:RequestTimeoutSeconds"] = "600",
            ["HumanAesExpert:MaximumBatchItems"] = "5",
        };
        await using var factory = new PostgresWebApplicationFactory(pg, settings);
        // Force host build (applies migrations on startup).
        _ = factory.Services;

        Guid ownerId, itemId, runId;
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var users = sp.GetRequiredService<IUserService>();
            ownerId = (await users.CreateAsync($"e2e-{Guid.NewGuid():N}@example.com", "E2E")).Id;

            var lab = sp.GetRequiredService<IAestheticLabService>();
            var item = await lab.AddFromUploadAsync(ownerId, "synthetic.png", "image/png",
                new MemoryStream(ImageFixtures.PlainPng(512, 512)));
            itemId = item.Id;

            var analysis = sp.GetRequiredService<IAestheticAnalysisService>();
            var req = await analysis.RequestAnalysisAsync(ownerId, new[] { itemId }, null);
            Assert.Single(req.Enqueued);
            runId = req.Enqueued[0].RunId;

            var db = sp.GetRequiredService<AppDbContext>();
            // One item -> one run -> one job.
            Assert.Equal(1, await db.AestheticAnalysisRuns.CountAsync(r => r.AestheticLabItemId == itemId));
            Assert.Equal(1, await db.BackgroundJobs.CountAsync(j => j.Type == JobTypes.AestheticsAnalyze));
            // Payload carries ONLY the run id (no bytes/blob/path/etc.).
            var payload = await db.BackgroundJobs.AsNoTracking()
                .Where(j => j.Type == JobTypes.AestheticsAnalyze).Select(j => j.PayloadJson).FirstAsync();
            Assert.Contains(runId.ToString("N")[..8], payload.Replace("-", ""), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sha", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("blob", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("storage", payload, StringComparison.OrdinalIgnoreCase);
        }

        // Drive the durable job through the real worker path (real sidecar call).
        using (var scope = factory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            // The single job; the handler calls the real sidecar (~seconds).
            await processor.ProcessAvailableAsync(4);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = await db.AestheticAnalysisRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
            Assert.Equal(AestheticRunStatuses.Succeeded, run.Status);
            Assert.Equal("b8f7ee3f3a1217ecd331fd6d57b6959f5c0da183", run.ModelRevision);
            Assert.Equal("KlingTeam/HumanAesExpert-1B", run.ModelName);
            Assert.Equal(AestheticPreprocessingProfiles.OfficialV1, run.PreprocessingProfileKey);
            Assert.NotNull(run.StartedAt);
            Assert.NotNull(run.CompletedAt);
            Assert.True(run.DurationMs > 0);
            Assert.NotNull(run.RawOutputJson);
            Assert.True(run.RawOutputJson!.Length <= 64 * 1024);
            Assert.Equal("expert_scores", run.CompletedCapabilities);

            // Exactly the 12 catalog metrics, all in [0,1].
            var metrics = await db.AestheticMetrics.AsNoTracking().Where(m => m.RunId == runId).ToListAsync();
            Assert.Equal(12, metrics.Count);
            Assert.Equal(
                AestheticMetricCatalog.ExpertScoreKeys.OrderBy(k => k),
                metrics.Select(m => m.MetricKey).OrderBy(k => k));
            Assert.All(metrics, m =>
            {
                Assert.InRange(m.NumericValue, 0.0, 1.0);
                Assert.Equal(0.0, m.ScaleMin);
                Assert.Equal(1.0, m.ScaleMax);
            });
            // No text / score-head / meta-voter output.
            Assert.False(await db.AestheticTextResults.AnyAsync(t => t.RunId == runId));

            // Detail service exposes the 12 metrics + model identity, no internals.
            var lab = scope.ServiceProvider.GetRequiredService<IAestheticLabService>();
            var detail = await lab.GetDetailAsync(await OwnerOfAsync(db, itemId), itemId);
            Assert.NotNull(detail);
            Assert.NotNull(detail!.LatestRun);
            Assert.Equal(12, detail.LatestRun!.Metrics.Count);
            Assert.Empty(detail.LatestRun.Texts);
            Assert.DoesNotContain("expert_scores", detail.LatestRun.CompletedCapabilities.Where(c => c != "expert_scores"));
        }
    }

    [SkippableFact]
    public async Task Real_micro_batch_of_three_runs_one_independent_job_each()
    {
        Skip.IfNot(Enabled, "HUMANAES_E2E != 1 (needs a live sidecar + disposable Postgres).");
        var pg = Environment.GetEnvironmentVariable("HUMANAES_E2E_PG")!;
        var sidecar = Environment.GetEnvironmentVariable("HUMANAES_E2E_SIDECAR")!;

        var settings = new Dictionary<string, string?>
        {
            ["Database:MigrateOnStartup"] = "true",
            ["HumanAesExpert:Enabled"] = "true",
            ["HumanAesExpert:SidecarBaseUrl"] = sidecar,
            ["HumanAesExpert:RequestTimeoutSeconds"] = "600",
            ["HumanAesExpert:MaximumBatchItems"] = "5",
        };
        await using var factory = new PostgresWebApplicationFactory(pg, settings);
        _ = factory.Services;

        Guid ownerId;
        var itemIds = new List<Guid>();
        var runIds = new List<Guid>();
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            ownerId = (await sp.GetRequiredService<IUserService>().CreateAsync($"mb-{Guid.NewGuid():N}@example.com", "MB")).Id;
            var lab = sp.GetRequiredService<IAestheticLabService>();
            // 3 DISTINCT synthetic images (different dims => different blobs).
            for (var i = 0; i < 3; i++)
            {
                var item = await lab.AddFromUploadAsync(ownerId, $"s{i}.png", "image/png",
                    new MemoryStream(ImageFixtures.PlainPng(480 + i, 480 + i)));
                itemIds.Add(item.Id);
            }
            var req = await sp.GetRequiredService<IAestheticAnalysisService>()
                .RequestAnalysisAsync(ownerId, itemIds, null);
            Assert.Equal(3, req.Enqueued.Count);
            runIds.AddRange(req.Enqueued.Select(e => e.RunId));

            var db = sp.GetRequiredService<AppDbContext>();
            // One independent job per image (never one monolith): the 3 runs map
            // to 3 DISTINCT background jobs (scoped to THIS test's runs).
            var jobIds = await db.AestheticAnalysisRuns.AsNoTracking()
                .Where(r => runIds.Contains(r.Id)).Select(r => r.BackgroundJobId).ToListAsync();
            Assert.Equal(3, jobIds.Count);
            Assert.All(jobIds, j => Assert.NotNull(j));
            Assert.Equal(3, jobIds.Distinct().Count());
            Assert.Equal(3, await db.AestheticAnalysisRuns.CountAsync(r => itemIds.Contains(r.AestheticLabItemId)));
        }

        // Drive all three durable jobs (the sidecar serializes at concurrency 1).
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(8);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // All three succeeded, each with 12 metrics; history is immutable.
            foreach (var runId in runIds)
            {
                var run = await db.AestheticAnalysisRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
                Assert.Equal(AestheticRunStatuses.Succeeded, run.Status);
                Assert.Equal(12, await db.AestheticMetrics.CountAsync(m => m.RunId == runId));
            }
            // No duplicate live runs were created for any item.
            foreach (var itemId in itemIds)
            {
                Assert.Equal(1, await db.AestheticAnalysisRuns.CountAsync(r => r.AestheticLabItemId == itemId));
            }
        }
    }

    private static async Task<Guid> OwnerOfAsync(AppDbContext db, Guid itemId) =>
        await db.AestheticLabItems.AsNoTracking().Where(i => i.Id == itemId).Select(i => i.OwnerUserId).FirstAsync();
}
