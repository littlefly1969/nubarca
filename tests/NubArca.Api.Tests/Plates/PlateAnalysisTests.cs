using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Jobs;
using NubArca.Api.Jobs.Handlers;
using NubArca.Api.Metadata;
using NubArca.Api.Plates;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Plates;

public sealed class PlateAnalysisTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public PlateAnalysisTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    // --- helpers ---

    private static byte[] Png(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static async Task<Guid> UploadPlateAsync(HttpClient client, string name = "plate.png", int dim = 40)
    {
        var part = new ByteArrayContent(Png(dim));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var response = await client.PostAsync("/api/plates/images", multipart);
        response.EnsureSuccessStatusCode();
        var item = await response.Content.ReadFromJsonAsync<PlateImageListItem>();
        return item!.Id;
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await work(db);
    }

    private async Task<int> RunJobsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
        return await processor.ProcessAvailableAsync(10);
    }

    private static async Task<string> RawAsync(HttpClient client, string url)
        => await (await client.GetAsync(url)).Content.ReadAsStringAsync();

    // --- registration guards (regression: worker must have the handler) ---

    [Fact]
    public void JobType_Constant_Has_Expected_Wire_Value()
        => Assert.Equal("plates.analyze", JobTypes.PlatesAnalyze);

    [Fact]
    public void Handler_Is_Registered_For_PlatesAnalyze()
    {
        using var scope = _factory.Services.CreateScope();
        var match = scope.ServiceProvider.GetServices<IJobHandler>()
            .SingleOrDefault(h => h.JobType == JobTypes.PlatesAnalyze);
        Assert.NotNull(match);
        Assert.IsType<PlateAnalysisJobHandler>(match);
    }

    [Fact]
    public void Handler_Is_Registered_In_CliServices()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=test;Username=test;Password=test",
            })
            .Build();

        CliEntryPoint.ConfigureCliServices(services, config);

        var hasHandler = services.Any(d =>
            d.ServiceType == typeof(IJobHandler) &&
            d.ImplementationType == typeof(PlateAnalysisJobHandler));
        Assert.True(hasHandler, "PlateAnalysisJobHandler must be registered as IJobHandler in ConfigureCliServices.");
    }

    // --- auth + isolation ---

    [Fact]
    public async Task Analysis_Endpoints_Require_Auth()
    {
        var anon = _factory.CreateClient();
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsync($"/api/plates/images/{id}/analysis", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"/api/plates/images/{id}/analysis/latest")).StatusCode);
    }

    [Fact]
    public async Task Request_Analysis_Returns_404_For_Foreign_Owner()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var plate = await UploadPlateAsync(alice, "a.png");

        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.PostAsync($"/api/plates/images/{plate}/analysis", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/plates/images/{plate}/analysis/latest")).StatusCode);
    }

    // --- enqueue only (no ALPR in the request path) ---

    [Fact]
    public async Task Request_Analysis_Queues_Job_Without_Running_Alpr_Inline()
    {
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadPlateAsync(client);

        var response = await client.PostAsync($"/api/plates/images/{plate}/analysis", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<PlateAnalysisJobSummary>();
        Assert.Equal(PlateAnalysisJobStatuses.Queued, summary!.Status);
        Assert.Equal("plate-alpr-v1", summary.ProfileKey);

        // A background job is queued; the domain job is queued; the image is
        // pending; and NO detections exist yet (analysis did not run inline).
        var bgQueued = await InDbAsync(db => db.BackgroundJobs.AsNoTracking()
            .AnyAsync(j => j.Type == JobTypes.PlatesAnalyze && j.Status == JobStatuses.Queued));
        Assert.True(bgQueued);
        var domainStatus = await InDbAsync(db => db.PlateAnalysisJobs.AsNoTracking()
            .Where(j => j.Id == summary.Id).Select(j => j.Status).SingleAsync());
        Assert.Equal(PlateAnalysisJobStatuses.Queued, domainStatus);
        var imageStatus = await InDbAsync(db => db.PlateImages.AsNoTracking()
            .Where(p => p.Id == plate).Select(p => p.Status).SingleAsync());
        Assert.Equal(PlateImageStatuses.AnalysisPending, imageStatus);
        var detections = await InDbAsync(db => db.PlateDetections.CountAsync(d => d.PlateImageId == plate));
        Assert.Equal(0, detections);
    }

    [Fact]
    public async Task Duplicate_Analysis_Request_Returns_Existing_Job()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadPlateAsync(client);

        var first = await (await client.PostAsync($"/api/plates/images/{plate}/analysis", null))
            .Content.ReadFromJsonAsync<PlateAnalysisJobSummary>();
        var second = await (await client.PostAsync($"/api/plates/images/{plate}/analysis", null))
            .Content.ReadFromJsonAsync<PlateAnalysisJobSummary>();

        Assert.Equal(first!.Id, second!.Id);
        var activeCount = await InDbAsync(db => db.PlateAnalysisJobs.CountAsync(j =>
            j.PlateImageId == plate &&
            (j.Status == PlateAnalysisJobStatuses.Queued || j.Status == PlateAnalysisJobStatuses.Running)));
        Assert.Equal(1, activeCount);
    }

    // --- worker happy path ---

    [Fact]
    public async Task Worker_Runs_Analysis_And_Persists_Detections_And_Statuses()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadPlateAsync(client, dim: 64);
        var jobSummary = await (await client.PostAsync($"/api/plates/images/{plate}/analysis", null))
            .Content.ReadFromJsonAsync<PlateAnalysisJobSummary>();

        await RunJobsAsync();

        var bgStatus = await InDbAsync(db => db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Type == JobTypes.PlatesAnalyze).Select(j => j.Status).SingleAsync());
        Assert.Equal(JobStatuses.Succeeded, bgStatus);

        var job = await InDbAsync(db => db.PlateAnalysisJobs.AsNoTracking().SingleAsync(j => j.Id == jobSummary!.Id));
        Assert.Equal(PlateAnalysisJobStatuses.Completed, job.Status);
        Assert.NotNull(job.CompletedAt);
        Assert.Null(job.ErrorCode);

        var imageStatus = await InDbAsync(db => db.PlateImages.AsNoTracking()
            .Where(p => p.Id == plate).Select(p => p.Status).SingleAsync());
        Assert.Equal(PlateImageStatuses.AnalysisCompleted, imageStatus);

        var detections = await InDbAsync(db => db.PlateDetections.AsNoTracking()
            .Where(d => d.PlateImageId == plate).ToListAsync());
        Assert.NotEmpty(detections);
        foreach (var d in detections)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.NormalizedText));
            Assert.Matches("^[A-Z0-9]+$", d.NormalizedText);
            Assert.InRange(d.BoundingBoxX, 0.0, 1.0);
            Assert.InRange(d.BoundingBoxWidth, 0.0, 1.0);
            Assert.True(d.CombinedConfidence > 0);
        }

        var modelRuns = await InDbAsync(db => db.PlateAnalysisModelRuns.CountAsync(r => r.PlateAnalysisJobId == job.Id));
        Assert.Equal(1, modelRuns);
    }

    [Fact]
    public async Task Detail_And_List_Expose_Sanitized_Analysis_And_No_Internals()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadPlateAsync(client, dim: 64);
        await client.PostAsync($"/api/plates/images/{plate}/analysis", null);
        await RunJobsAsync();

        var detailBody = await RawAsync(client, $"/api/plates/images/{plate}");
        var listBody = await RawAsync(client, "/api/plates/images");
        var latestBody = await RawAsync(client, $"/api/plates/images/{plate}/analysis/latest");

        // Detections + completed status surface.
        Assert.Contains("\"detections\"", detailBody);
        Assert.Contains("completed", detailBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"normalizedText\"", detailBody);
        Assert.Contains("\"platesCount\"", latestBody);

        // No blob / model / owner internals in ANY analysis response.
        var (sha, storageKey) = await InDbAsync(async db =>
        {
            var img = await db.PlateImages.AsNoTracking().FirstAsync(p => p.Id == plate);
            var blob = await db.BlobObjects.AsNoTracking().FirstAsync(b => b.Id == img.BlobObjectId);
            return (blob.Sha256, blob.StorageKey);
        });
        foreach (var body in new[] { detailBody, listBody, latestBody })
        {
            foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
            {
                Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
            }
            Assert.DoesNotContain(sha, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(storageKey, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("polygonJson", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("modelPath", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DetectorModelPath", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ownerUserId", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Foreign_Owner_Cannot_Read_Detections_Or_Latest()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var plate = await UploadPlateAsync(alice, "a.png");
        await alice.PostAsync($"/api/plates/images/{plate}/analysis", null);
        await RunJobsAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/plates/images/{plate}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/plates/images/{plate}/analysis/latest")).StatusCode);
    }

    // --- re-analysis replaces detections ---

    [Fact]
    public async Task Re_Analysis_Replaces_Previous_Detections()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadPlateAsync(client, dim: 64);

        var job1 = await (await client.PostAsync($"/api/plates/images/{plate}/analysis", null))
            .Content.ReadFromJsonAsync<PlateAnalysisJobSummary>();
        await RunJobsAsync();
        var firstIds = await InDbAsync(db => db.PlateDetections.AsNoTracking()
            .Where(d => d.PlateImageId == plate).Select(d => d.Id).ToListAsync());
        Assert.NotEmpty(firstIds);

        var job2 = await (await client.PostAsync($"/api/plates/images/{plate}/analysis", null))
            .Content.ReadFromJsonAsync<PlateAnalysisJobSummary>();
        Assert.NotEqual(job1!.Id, job2!.Id);
        await RunJobsAsync();

        var current = await InDbAsync(db => db.PlateDetections.AsNoTracking()
            .Where(d => d.PlateImageId == plate).ToListAsync());
        // Old detection rows are gone (replaced); the new ones belong to job2.
        Assert.DoesNotContain(current, d => firstIds.Contains(d.Id));
        Assert.All(current, d => Assert.Equal(job2.Id, d.PlateAnalysisJobId));
    }

    // --- failure: image too large → safe code, no stack trace, bg job succeeds ---

    [Fact]
    public async Task Worker_Failure_Stores_Safe_Error_Code_Only()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadPlateAsync(client);

        // Force the pixel guard to trip without touching the bytes.
        await InDbAsync(async db =>
        {
            await db.PlateImages.Where(p => p.Id == plate)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Width, 100_000).SetProperty(p => p.Height, 100_000));
            return 0;
        });
        var jobSummary = await (await client.PostAsync($"/api/plates/images/{plate}/analysis", null))
            .Content.ReadFromJsonAsync<PlateAnalysisJobSummary>();

        await RunJobsAsync();

        var job = await InDbAsync(db => db.PlateAnalysisJobs.AsNoTracking().SingleAsync(j => j.Id == jobSummary!.Id));
        Assert.Equal(PlateAnalysisJobStatuses.Failed, job.Status);
        Assert.Equal(PlateAnalysisErrorCodes.ImageTooLarge, job.ErrorCode);
        Assert.NotNull(job.ErrorMessageSafe);
        Assert.DoesNotContain("Exception", job.ErrorMessageSafe!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at ", job.ErrorMessageSafe!, StringComparison.OrdinalIgnoreCase);

        var imageStatus = await InDbAsync(db => db.PlateImages.AsNoTracking()
            .Where(p => p.Id == plate).Select(p => p.Status).SingleAsync());
        Assert.Equal(PlateImageStatuses.AnalysisFailed, imageStatus);

        // The background job still SUCCEEDS (it recorded the domain outcome).
        var bgStatus = await InDbAsync(db => db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Type == JobTypes.PlatesAnalyze).Select(j => j.Status).SingleAsync());
        Assert.Equal(JobStatuses.Succeeded, bgStatus);
    }

    // --- model not configured (ALPR disabled) ---

    [Fact]
    public async Task Disabled_Alpr_Fails_With_Model_Not_Configured()
    {
        using var disabled = new SqliteWebApplicationFactory(
            new Dictionary<string, string?> { ["Plates:Alpr:Enabled"] = "false" });
        disabled.EnsureDatabaseCreated();
        var (_, client) = await disabled.CreateAuthenticatedClientAsync();

        var part = new ByteArrayContent(Png(40));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", "p.png" } };
        var plate = (await (await client.PostAsync("/api/plates/images", multipart))
            .Content.ReadFromJsonAsync<PlateImageListItem>())!.Id;

        var jobSummary = await (await client.PostAsync($"/api/plates/images/{plate}/analysis", null))
            .Content.ReadFromJsonAsync<PlateAnalysisJobSummary>();

        using (var scope = disabled.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(10);
        }

        using var readScope = disabled.Services.CreateScope();
        var db = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.PlateAnalysisJobs.AsNoTracking().SingleAsync(j => j.Id == jobSummary!.Id);
        Assert.Equal(PlateAnalysisJobStatuses.Failed, job.Status);
        Assert.Equal(PlateAnalysisErrorCodes.ModelNotConfigured, job.ErrorCode);
    }

    // --- cancellation is never a permanent failure ---

    [Fact]
    public async Task Cancelled_Analysis_Is_Not_A_Permanent_Failure()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadPlateAsync(client);
        var jobSummary = await (await client.PostAsync($"/api/plates/images/{plate}/analysis", null))
            .Content.ReadFromJsonAsync<PlateAnalysisJobSummary>();

        var bgJobId = await InDbAsync(db => db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Type == JobTypes.PlatesAnalyze).Select(j => j.Id).SingleAsync());
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IJobQueue>()
                .RequestCancellationAsync(bgJobId);
        }

        await RunJobsAsync();

        // The processor cancels a pre-cancelled job WITHOUT invoking the handler,
        // so the background job is Cancelled and the domain job is left un-run
        // (queued) — crucially NOT a permanent failure (the CLAUDE.md job rule).
        var bgStatus = await InDbAsync(db => db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Id == bgJobId).Select(j => j.Status).SingleAsync());
        Assert.Equal(JobStatuses.Cancelled, bgStatus);

        var job = await InDbAsync(db => db.PlateAnalysisJobs.AsNoTracking().SingleAsync(j => j.Id == jobSummary!.Id));
        Assert.NotEqual(PlateAnalysisJobStatuses.Failed, job.Status);
        Assert.Null(job.FailedAt);
        Assert.Null(job.ErrorCode);

        var imageStatus = await InDbAsync(db => db.PlateImages.AsNoTracking()
            .Where(p => p.Id == plate).Select(p => p.Status).SingleAsync());
        Assert.NotEqual(PlateImageStatuses.AnalysisFailed, imageStatus);
    }

    // --- delete semantics: no orphan analysis rows ---

    [Fact]
    public async Task Deleting_PlateImage_Removes_Analysis_Jobs_And_Detections()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadPlateAsync(client, dim: 64);
        await client.PostAsync($"/api/plates/images/{plate}/analysis", null);
        await RunJobsAsync();

        Assert.True(await InDbAsync(db => db.PlateDetections.AnyAsync(d => d.PlateImageId == plate)));

        var delete = await client.DeleteAsync($"/api/plates/images/{plate}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.False(await InDbAsync(db => db.PlateImages.AnyAsync(p => p.Id == plate)));
        Assert.False(await InDbAsync(db => db.PlateAnalysisJobs.AnyAsync(j => j.PlateImageId == plate)));
        Assert.False(await InDbAsync(db => db.PlateDetections.AnyAsync(d => d.PlateImageId == plate)));
        Assert.Equal(0, await InDbAsync(db => db.PlateAnalysisModelRuns.CountAsync()));
    }

    [Fact]
    public async Task Deleted_PlateImage_Queued_Job_Is_Skipped_Safely()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadPlateAsync(client);
        await client.PostAsync($"/api/plates/images/{plate}/analysis", null);

        // Delete BEFORE the worker runs — cascades the analysis job away.
        await client.DeleteAsync($"/api/plates/images/{plate}");

        var processed = await RunJobsAsync();
        Assert.Equal(1, processed);

        // The background job succeeds (handler no-ops on the missing domain job).
        var bgStatus = await InDbAsync(db => db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Type == JobTypes.PlatesAnalyze).Select(j => j.Status).SingleAsync());
        Assert.Equal(JobStatuses.Succeeded, bgStatus);
    }

    // --- still no Files/Gallery/People-Face leakage after analysis ---

    [Fact]
    public async Task Analyzed_Plate_Creates_No_FileItem_Face_Or_Gallery_Entry()
    {
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();
        var plate = await UploadPlateAsync(client, dim: 64);
        await client.PostAsync($"/api/plates/images/{plate}/analysis", null);
        await RunJobsAsync();

        Assert.Equal(0, await InDbAsync(db => db.FileItems.IgnoreQueryFilters().CountAsync(f => f.OwnerUserId == ownerId)));
        // No People/Face identity artifacts of ANY kind were produced.
        Assert.Equal(0, await InDbAsync(db => db.FaceDetections.CountAsync()));
        Assert.Equal(0, await InDbAsync(db => db.FaceEmbeddings.CountAsync()));
        Assert.Equal(0, await InDbAsync(db => db.PersonGroups.CountAsync()));

        var gallery = await RawAsync(client, "/api/images");
        Assert.DoesNotContain(plate.ToString(), gallery, StringComparison.OrdinalIgnoreCase);
        var files = await RawAsync(client, "/api/folders/children");
        Assert.DoesNotContain(plate.ToString(), files, StringComparison.OrdinalIgnoreCase);
    }
}
