using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Admin;
using NubArca.Api.Data;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Admin;

// Slice 82 — admin import run visibility, cancellation, and diagnostics.
public sealed class AdminImportRunsTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string NewImportTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nc-import82-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "top.txt"), "top-file");
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "a.txt"), "aaa");
        File.WriteAllText(Path.Combine(sub, "b.txt"), "bbbb");
        _tempDirs.Add(root);
        return root;
    }

    private static Dictionary<string, string?> Enabled(string root) => new()
    {
        ["AdminImport:Enabled"] = "true",
        ["AdminImport:Roots:0"] = root,
    };

    private static async Task<(Guid UserId, HttpClient Client)> AdminAsync(
        SqliteWebApplicationFactory factory, string email)
    {
        factory.EnsureDatabaseCreated();
        var id = await factory.SeedUserAsync(email);
        await factory.PromoteToAdminAsync(id);
        return (id, await factory.LoginAsync(email));
    }

    private static async Task<string> FirstRootIdAsync(HttpClient client)
    {
        var roots = await client.GetFromJsonAsync<AdminImportRootsResponse>("/api/admin/import/roots");
        return roots!.Roots[0].RootId;
    }

    private static async Task<AdminImportRunResponse> StartRunAsync(
        HttpClient client, string rootId, Guid targetUserId, string relativePath = "")
    {
        var resp = await client.PostAsJsonAsync("/api/admin/import/run", new
        {
            rootId,
            relativePath,
            targetUserId,
            destinationFolderId = (Guid?)null,
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AdminImportRunResponse>())!;
    }

    private static async Task ProcessJobsAsync(SqliteWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
        await processor.ProcessAvailableAsync(10);
    }

    // Deterministic monotonic clock: GetTimestamp advances `stepSeconds` each
    // call (frequency = 1 tick/sec) so the import's per-file wall-clock budget
    // trips after a fixed number of files. GetUtcNow is fixed (only affects
    // CreatedAt/UpdatedAt). The import only calls _clock.GetTimestamp() in its
    // budget check, so the trip point is exact.
    private sealed class StepTimeProvider : TimeProvider
    {
        private long _n;
        private readonly long _stepSeconds;
        public StepTimeProvider(long stepSeconds) => _stepSeconds = stepSeconds;
        public override long GetTimestamp() => _n++ * _stepSeconds;
        public override long TimestampFrequency => 1;
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- list ------------------------------------------------------------

    [Fact]
    public async Task ListRuns_NonAdmin_Returns403()
    {
        using var factory = new SqliteWebApplicationFactory(Enabled(NewImportTree()));
        factory.EnsureDatabaseCreated();
        var (_, client) = await factory.CreateAuthenticatedClientAsync("plain@example.com");
        var response = await client.GetAsync("/api/admin/import/runs");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListRuns_NewestFirst_AndPaginated()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var rootId = await FirstRootIdAsync(client);

        // Three runs; CreatedAt ordering is by insertion (TimeProvider.System).
        var r1 = await StartRunAsync(client, rootId, targetId);
        var r2 = await StartRunAsync(client, rootId, targetId);
        var r3 = await StartRunAsync(client, rootId, targetId);

        var all = await client.GetFromJsonAsync<AdminImportRunListResponse>("/api/admin/import/runs");
        Assert.NotNull(all);
        Assert.Equal(3, all!.Total);
        Assert.Equal(r3.ImportRunId, all.Runs[0].ImportRunId); // newest first
        Assert.Equal(r1.ImportRunId, all.Runs[2].ImportRunId);

        // Pagination: limit=2 offset=1 → the middle + oldest.
        var page = await client.GetFromJsonAsync<AdminImportRunListResponse>(
            "/api/admin/import/runs?limit=2&offset=1");
        Assert.Equal(2, page!.Runs.Count);
        Assert.Equal(3, page.Total);
        Assert.Equal(r2.ImportRunId, page.Runs[0].ImportRunId);
        Assert.Equal(r1.ImportRunId, page.Runs[1].ImportRunId);
    }

    // ---- cancellation ----------------------------------------------------

    [Fact]
    public async Task Cancel_QueuedRun_MarksCancellationRequested_AndIsIdempotent()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var rootId = await FirstRootIdAsync(client);
        var run = await StartRunAsync(client, rootId, targetId);

        var c1 = await (await client.PostAsync($"/api/admin/import/runs/{run.ImportRunId}/cancel", null))
            .Content.ReadFromJsonAsync<AdminImportCancelResponse>();
        Assert.True(c1!.CancellationRequested);

        // Idempotent: a second cancel still succeeds.
        var c2resp = await client.PostAsync($"/api/admin/import/runs/{run.ImportRunId}/cancel", null);
        c2resp.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.AdminImportRuns.AsNoTracking().FirstAsync(r => r.Id == run.ImportRunId);
        // Slice 91: cancellation is tracked on the linked background job (single
        // source of truth), not on the run row.
        var jobCancel = await db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Id == row.JobId)
            .Select(j => j.CancellationRequested)
            .FirstAsync();
        Assert.True(jobCancel);
    }

    [Fact]
    public async Task Cancel_BeforeProcessing_StopsCooperatively_NoFilesImported()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var rootId = await FirstRootIdAsync(client);
        var run = await StartRunAsync(client, rootId, targetId);

        // Cancel while still queued, then run the job.
        await client.PostAsync($"/api/admin/import/runs/{run.ImportRunId}/cancel", null);
        await ProcessJobsAsync(factory);

        var status = await client.GetFromJsonAsync<AdminImportRunStatusResponse>(
            $"/api/admin/import/runs/{run.ImportRunId}");
        Assert.Equal("cancelled", status!.Status);
        Assert.Equal(0, status.ImportedFiles);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // No visible FileItem was created for the cancelled run's target user.
        var owned = await db.FileItems.CountAsync(f => f.OwnerUserId == targetId && f.DeletedAt == null);
        Assert.Equal(0, owned);
    }

    [Fact]
    public async Task Cancel_CompletedRun_DoesNotChangeStatus()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var rootId = await FirstRootIdAsync(client);
        var run = await StartRunAsync(client, rootId, targetId);

        await ProcessJobsAsync(factory); // run completes (succeeded)

        var cancel = await (await client.PostAsync($"/api/admin/import/runs/{run.ImportRunId}/cancel", null))
            .Content.ReadFromJsonAsync<AdminImportCancelResponse>();
        Assert.False(cancel!.CancellationRequested);
        Assert.Equal("succeeded", cancel.Status);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.AdminImportRuns.AsNoTracking().FirstAsync(r => r.Id == run.ImportRunId);
        Assert.Equal("succeeded", row.Status);
    }

    [Fact]
    public async Task Cancel_UnknownRun_Returns404()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var response = await client.PostAsync($"/api/admin/import/runs/{Guid.NewGuid()}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- diagnostics -----------------------------------------------------

    [Fact]
    public async Task SuccessfulRun_PopulatesTimingFields_AndMetrics()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var rootId = await FirstRootIdAsync(client);
        var run = await StartRunAsync(client, rootId, targetId);
        await ProcessJobsAsync(factory);

        var status = await client.GetFromJsonAsync<AdminImportRunStatusResponse>(
            $"/api/admin/import/runs/{run.ImportRunId}");
        Assert.Equal("succeeded", status!.Status);
        Assert.Equal(3, status.ImportedFiles);

        // L2 timings are populated (non-null) after a successful run.
        Assert.NotNull(status.Timings.ReadMillis);
        Assert.NotNull(status.Timings.HashMillis);
        Assert.NotNull(status.Timings.WriteMillis);
        Assert.NotNull(status.Timings.BlobDbMillis);
        Assert.NotNull(status.Timings.MetadataMillis);
        Assert.NotNull(status.Timings.FileItemMillis);

        // L1 metrics computable once completed.
        Assert.NotNull(status.Metrics.DurationMillis);
        Assert.NotNull(status.Metrics.AverageImportedFileBytes);
    }

    [Fact]
    public async Task MaxRunMinutes_PausesAndRequeues_PreservingImportedFiles()
    {
        // 6-file tree. Budget = 1 minute; the step clock advances 20s per
        // budget check (checked AFTER each file), so the run imports a few files
        // then pauses + re-queues well before finishing — exercising safe,
        // partial, progress-preserving interruption.
        var root = Path.Combine(Path.GetTempPath(), $"nc-import82big-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        for (var i = 0; i < 6; i++)
        {
            File.WriteAllText(Path.Combine(root, $"f{i}.txt"), $"file-{i}");
        }

        using var factory = new SqliteWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["AdminImport:Enabled"] = "true",
                ["AdminImport:Roots:0"] = root,
                ["AdminImport:MaxRunMinutes"] = "1",
            },
            clockOverride: new StepTimeProvider(20));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var rootId = await FirstRootIdAsync(client);
        var run = await StartRunAsync(client, rootId, targetId);

        // Process exactly ONE job (one slice). Draining the whole requeue chain
        // here would re-walk the imported prefix as conflicts under this
        // degenerate clock; a single slice is what we assert on.
        await using (var procScope = factory.Services.CreateAsyncScope())
        {
            var processor = procScope.ServiceProvider.GetRequiredService<JobProcessor>();
            await processor.ProcessAvailableAsync(1);
        }

        var status = await client.GetFromJsonAsync<AdminImportRunStatusResponse>(
            $"/api/admin/import/runs/{run.ImportRunId}");
        Assert.Equal("paused", status!.Status);
        // Forward progress was made but the run did not finish all 6 files.
        Assert.InRange(status.ImportedFiles, 1, 5);
        Assert.Null(status.CompletedAt); // not completed

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Exactly the imported files exist — no partial/extra FileItem.
        var owned = await db.FileItems.CountAsync(f => f.OwnerUserId == targetId && f.DeletedAt == null);
        Assert.Equal(status.ImportedFiles, owned);
        // A fresh admin.import job was re-queued so the run can resume.
        var queued = await db.BackgroundJobs.CountAsync(j =>
            j.Type == JobTypes.AdminImport && j.Status == JobStatuses.Queued);
        Assert.True(queued >= 1, "expected a re-queued admin.import job");
    }

    [Fact]
    public async Task RunResponses_DoNotLeakInternals()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var rootId = await FirstRootIdAsync(client);
        var run = await StartRunAsync(client, rootId, targetId);
        await ProcessJobsAsync(factory);

        var bodies = new[]
        {
            await (await client.GetAsync("/api/admin/import/runs")).Content.ReadAsStringAsync(),
            await (await client.GetAsync($"/api/admin/import/runs/{run.ImportRunId}")).Content.ReadAsStringAsync(),
        };
        foreach (var body in bodies)
        {
            foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
            {
                Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
            }
            Assert.DoesNotContain(root, body, StringComparison.Ordinal); // absolute server path
        }
    }

    // ---- slice 91: Background Jobs v2 integration -------------------------

    [Fact]
    public async Task StartRun_Enqueues_Job_And_Run_Links_To_It()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var rootId = await FirstRootIdAsync(client);

        var run = await StartRunAsync(client, rootId, targetId);
        Assert.NotNull(run.JobId);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().FirstAsync(j => j.Id == run.JobId);
        Assert.Equal(JobTypes.AdminImport, job.Type);
        var row = await db.AdminImportRuns.AsNoTracking().FirstAsync(r => r.Id == run.ImportRunId);
        Assert.Equal(run.JobId, row.JobId);
    }

    [Fact]
    public async Task Import_Job_Appears_In_Admin_Jobs_List_Without_Payload()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var rootId = await FirstRootIdAsync(client);
        var run = await StartRunAsync(client, rootId, targetId);

        var resp = await client.GetAsync("/api/admin/jobs?type=admin.import");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("admin.import", json);
        // No payload (carries the run id) and no run-id-bearing payload field.
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);

        var page = await resp.Content.ReadFromJsonAsync<AdminJobPage>();
        Assert.Contains(page!.Items, j => j.Id == run.JobId && j.Type == JobTypes.AdminImport);
    }

    [Fact]
    public async Task Generic_Job_Cancel_Cancels_A_Queued_Import_And_Imports_Nothing()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var rootId = await FirstRootIdAsync(client);
        var run = await StartRunAsync(client, rootId, targetId);

        // Cancel via the GENERIC admin jobs endpoint (not the import one).
        var cancel = await client.PostAsync($"/api/admin/jobs/{run.JobId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        await ProcessJobsAsync(factory);

        // Run status reconciles to cancelled, and nothing was imported.
        var status = await client.GetFromJsonAsync<AdminImportRunStatusResponse>(
            $"/api/admin/import/runs/{run.ImportRunId}");
        Assert.Equal("cancelled", status!.Status);
        Assert.Equal(0, status.ImportedFiles);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.FileItems.CountAsync(f => f.OwnerUserId == targetId && f.DeletedAt == null));
    }

    [Fact]
    public async Task Import_Cancel_Endpoint_Sets_The_Job_Cancellation_Flag()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var rootId = await FirstRootIdAsync(client);
        var run = await StartRunAsync(client, rootId, targetId);

        await client.PostAsync($"/api/admin/import/runs/{run.ImportRunId}/cancel", null);

        // The import cancel endpoint delegated to the same job cancellation path.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var flag = await db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Id == run.JobId).Select(j => j.CancellationRequested).FirstAsync();
        Assert.True(flag);
    }

    [Fact]
    public async Task Completed_Import_Reports_Generic_Job_Progress()
    {
        var root = NewImportTree();
        using var factory = new SqliteWebApplicationFactory(Enabled(root));
        var (_, client) = await AdminAsync(factory, "admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        var rootId = await FirstRootIdAsync(client);
        var run = await StartRunAsync(client, rootId, targetId);
        await ProcessJobsAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().FirstAsync(j => j.Id == run.JobId);
        // The handler reported final generic progress on the job.
        Assert.NotNull(job.ProgressCurrent);
        Assert.True(job.ProgressCurrent >= 3); // the fixture has 3 files
        Assert.False(string.IsNullOrEmpty(job.ProgressMessage));
    }
}
