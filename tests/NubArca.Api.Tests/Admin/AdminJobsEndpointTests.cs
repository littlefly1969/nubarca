using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Admin;

// Slice 90 — admin jobs dashboard endpoints. Admin-gated visibility + safe
// cooperative cancellation. Asserts the no-leak contract (no PayloadJson, no
// LockOwner, no forbidden needles) on every response.
public sealed class AdminJobsEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public AdminJobsEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> AdminClientAsync(string email = "admin@example.com")
    {
        var userId = await _factory.SeedUserAsync(email);
        await _factory.PromoteToAdminAsync(userId);
        return await _factory.LoginAsync(email);
    }

    private async Task<HttpClient> NonAdminClientAsync(string email = "user@example.com")
    {
        await _factory.SeedUserAsync(email);
        return await _factory.LoginAsync(email);
    }

    // A payload string crafted to look sensitive, so we can prove the admin API
    // never echoes PayloadJson back.
    private const string SecretNeedle = "S3cretStorageKey/ab/cd/deadbeef";

    private async Task<Guid> InsertJobAsync(
        string status,
        string type = JobTypes.StorageReconcile,
        int attempts = 0,
        int? progressCurrent = null,
        int? progressTotal = null,
        string? progressMessage = null,
        string? errorCode = null,
        string? errorMessage = null,
        bool running = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        db.BackgroundJobs.Add(new BackgroundJob
        {
            Id = id,
            Type = type,
            Status = status,
            // Deliberately stuff a sensitive-looking value into the private payload.
            PayloadJson = $"{{\"secret\":\"{SecretNeedle}\"}}",
            Attempts = attempts,
            MaxAttempts = 3,
            Priority = 100,
            CreatedAt = now,
            AvailableAt = now,
            StartedAt = running ? now : null,
            LockOwner = running ? "worker-host-01:abcdef" : null,
            LeaseUntil = running ? now.AddSeconds(120) : null,
            HeartbeatAt = running ? now : null,
            ProgressCurrent = progressCurrent,
            ProgressTotal = progressTotal,
            ProgressMessage = progressMessage,
            LastErrorCode = errorCode,
            LastErrorMessage = errorMessage,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static void AssertNoLeak(string json)
    {
        Assert.DoesNotContain(SecretNeedle, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lockowner", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("worker-host-01", json, StringComparison.OrdinalIgnoreCase);
        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- auth -------------------------------------------------------------

    [Fact]
    public async Task List_Without_Auth_Returns_401()
    {
        var resp = await _factory.CreateClient().GetAsync("/api/admin/jobs");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task List_As_NonAdmin_Returns_403()
    {
        var client = await NonAdminClientAsync();
        var resp = await client.GetAsync("/api/admin/jobs");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task List_As_Admin_Returns_Items_And_Counts()
    {
        await InsertJobAsync(JobStatuses.Queued);
        await InsertJobAsync(JobStatuses.Running, running: true);
        await InsertJobAsync(JobStatuses.Failed, errorCode: "IOException", errorMessage: "disk full");
        var client = await AdminClientAsync();

        var resp = await client.GetAsync("/api/admin/jobs");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        AssertNoLeak(json);

        var page = await resp.Content.ReadFromJsonAsync<AdminJobPage>();
        Assert.Equal(3, page!.Total);
        Assert.Equal(1, page.Counts.Queued);
        Assert.Equal(1, page.Counts.Running);
        Assert.Equal(1, page.Counts.Failed);
    }

    [Fact]
    public async Task List_Is_Paginated()
    {
        for (var i = 0; i < 5; i++) await InsertJobAsync(JobStatuses.Queued);
        var client = await AdminClientAsync();

        var resp = await client.GetAsync("/api/admin/jobs?page=1&pageSize=2");
        var page = await resp.Content.ReadFromJsonAsync<AdminJobPage>();
        Assert.Equal(2, page!.Items.Count);
        Assert.Equal(5, page.Total);
        Assert.Equal(2, page.PageSize);
    }

    [Fact]
    public async Task List_Status_Filter_Works()
    {
        await InsertJobAsync(JobStatuses.Queued);
        await InsertJobAsync(JobStatuses.Failed, errorCode: "X");
        var client = await AdminClientAsync();

        var resp = await client.GetAsync("/api/admin/jobs?status=failed");
        var page = await resp.Content.ReadFromJsonAsync<AdminJobPage>();
        Assert.Single(page!.Items);
        Assert.Equal(JobStatuses.Failed, page.Items[0].Status);
        // Counts remain whole-table (not filtered).
        Assert.Equal(1, page.Counts.Queued);
    }

    [Fact]
    public async Task List_Rejects_Unknown_Status_Filter()
    {
        var client = await AdminClientAsync();
        var resp = await client.GetAsync("/api/admin/jobs?status=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- detail -----------------------------------------------------------

    [Fact]
    public async Task Detail_Returns_Safe_Fields_With_Progress_And_Error()
    {
        var id = await InsertJobAsync(
            JobStatuses.Failed, attempts: 2,
            progressCurrent: 3, progressTotal: 10, progressMessage: "phase 2",
            errorCode: "InvalidOperationException", errorMessage: "deliberate failure");
        var client = await AdminClientAsync();

        var resp = await client.GetAsync($"/api/admin/jobs/{id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        AssertNoLeak(json);
        Assert.DoesNotContain(" at ", json); // no stack-trace frames

        var job = await resp.Content.ReadFromJsonAsync<AdminJobSummary>();
        Assert.Equal(3, job!.ProgressCurrent);
        Assert.Equal(10, job.ProgressTotal);
        Assert.Equal("phase 2", job.ProgressMessage);
        Assert.Equal("InvalidOperationException", job.LastErrorCode);
        Assert.Equal("deliberate failure", job.LastErrorMessage);
        Assert.Equal(2, job.Attempts);
    }

    [Fact]
    public async Task Detail_Missing_Returns_404()
    {
        var client = await AdminClientAsync();
        var resp = await client.GetAsync($"/api/admin/jobs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- cancel -----------------------------------------------------------

    [Fact]
    public async Task Cancel_Queued_Sets_Flag_And_Returns_Safe_Summary()
    {
        var id = await InsertJobAsync(JobStatuses.Queued);
        var client = await AdminClientAsync();

        var resp = await client.PostAsync($"/api/admin/jobs/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        AssertNoLeak(json);

        var job = await resp.Content.ReadFromJsonAsync<AdminJobSummary>();
        Assert.True(job!.CancellationRequested);
        Assert.Equal(JobStatuses.Queued, job.Status); // still queued; engine finishes it as cancelled
    }

    [Fact]
    public async Task Cancel_Running_Sets_Flag_And_Returns_Safe_Summary()
    {
        var id = await InsertJobAsync(JobStatuses.Running, running: true);
        var client = await AdminClientAsync();

        var resp = await client.PostAsync($"/api/admin/jobs/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var job = await resp.Content.ReadFromJsonAsync<AdminJobSummary>();
        Assert.True(job!.CancellationRequested);
    }

    [Fact]
    public async Task Cancel_Terminal_Returns_409()
    {
        var id = await InsertJobAsync(JobStatuses.Succeeded);
        var client = await AdminClientAsync();

        var resp = await client.PostAsync($"/api/admin/jobs/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Cancel_Missing_Returns_404()
    {
        var client = await AdminClientAsync();
        var resp = await client.PostAsync($"/api/admin/jobs/{Guid.NewGuid()}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Cancel_As_NonAdmin_Returns_403()
    {
        var id = await InsertJobAsync(JobStatuses.Queued);
        var client = await NonAdminClientAsync();
        var resp = await client.PostAsync($"/api/admin/jobs/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
