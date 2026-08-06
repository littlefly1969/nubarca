using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Jobs;

// Slice 70 — the `jobs` operator CLI, wired through the real dispatcher with a
// SQLite-backed service provider (the factory registers the job graph + real
// backfill services, so handlers exercise the genuine services).
public sealed class JobsCliTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public JobsCliTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<(int exit, string stdout, string stderr)> RunCli(params string[] args)
    {
        using var scope = _factory.Services.CreateScope();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliEntryPoint.RunAsync(args, stdout, stderr, () => scope.ServiceProvider);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static byte[] Png(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private async Task<Guid> UploadPendingImageAsync(HttpClient client)
    {
        var part = new ByteArrayContent(Png(12));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", "p.png" } };
        var resp = await client.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.SingleAsync(f => f.Id == summary!.Id);
        var meta = await db.BlobMetadata.SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
        meta.ExtractionStatus = MetadataStatuses.Pending;
        meta.ExtractionVersion = null;
        await db.SaveChangesAsync();
        return file.BlobObjectId;
    }

    private async Task<int> JobCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BackgroundJobs.CountAsync();
    }

    [Fact]
    public async Task Enqueue_Metadata_Backfill_Creates_Queued_Row()
    {
        var (exit, stdout, stderr) = await RunCli(
            "jobs", "enqueue", "metadata-backfill", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(JobTypes.MetadataEmbeddedBackfill, stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobTypes.MetadataEmbeddedBackfill, job.Type);
        Assert.Equal(JobStatuses.Queued, job.Status);
    }

    [Fact]
    public async Task Enqueue_Media_Derivatives_Creates_Row()
    {
        var (exit, _, stderr) = await RunCli(
            "jobs", "enqueue", "media-derivatives-backfill", "--limit", "5");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(1, await JobCountAsync());
    }

    [Fact]
    public async Task Enqueue_Gallery_Derivatives_Uses_Safe_Forced_Preset()
    {
        var (exit, stdout, stderr) = await RunCli(
            "jobs", "enqueue", "media-gallery-derivatives-regenerate",
            "--force", "--limit", "9", "--batch-size", "3");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(JobTypes.MediaGalleryDerivativesRegenerate, stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        var payload = JsonSerializer.Deserialize<GalleryDerivativesRegenerationJobPayload>(
            job.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(
            new[]
            {
                ThumbnailSizes.Small,
                ThumbnailSizes.Poster,
                ThumbnailSizes.VideoPreviewStrip,
            },
            payload!.Sizes);
        Assert.True(payload.Force);
        Assert.Equal(9, payload.Limit);
        Assert.Equal(3, payload.BatchSize);
        Assert.Equal(JobScheduling.Maintenance, job.Priority);
    }

    [Fact]
    public async Task Worker_Executes_Gallery_Derivatives_Without_UnknownJobType()
    {
        await RunCli(
            "jobs", "enqueue", "media-gallery-derivatives-regenerate",
            "--force", "--dry-run");

        var (exit, _, stderr) = await RunCli("jobs", "run-once");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("UnknownJobType", stderr, StringComparison.Ordinal);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.Succeeded, job.Status);
    }

    [Fact]
    public async Task Enqueue_Video_Metadata_Backfill_Creates_Queued_Row()
    {
        var (exit, stdout, stderr) = await RunCli(
            "jobs", "enqueue", "metadata-video-backfill", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(JobTypes.MetadataVideoBackfill, stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobTypes.MetadataVideoBackfill, job.Type);
        Assert.Equal(JobStatuses.Queued, job.Status);
    }

    // ---- Video-hls slice 1: media-video-hls-generate ----------------------

    [Fact]
    public async Task Enqueue_Video_Hls_Generate_Creates_Queued_Row()
    {
        var blobId = Guid.NewGuid();
        var (exit, stdout, stderr) = await RunCli(
            "jobs", "enqueue", "media-video-hls-generate", "--blob", blobId.ToString());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(JobTypes.MediaVideoHlsGenerate, stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobTypes.MediaVideoHlsGenerate, job.Type);
        Assert.Equal(JobStatuses.Queued, job.Status);
    }

    [Fact]
    public async Task Enqueue_Video_Hls_Backfill_Creates_Queued_Row()
    {
        var (exit, stdout, stderr) = await RunCli(
            "jobs", "enqueue", "media-video-hls-backfill", "--limit", "10", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(JobTypes.MediaVideoHlsBackfill, stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobTypes.MediaVideoHlsBackfill, job.Type);
        Assert.Equal(JobStatuses.Queued, job.Status);
    }

    [Fact]
    public async Task Enqueue_Video_Hls_Generate_Requires_Blob_Id()
    {
        var (exit, _, stderr) = await RunCli(
            "jobs", "enqueue", "media-video-hls-generate");

        Assert.Equal(64, exit);
        Assert.Contains("--blob", stderr);
        Assert.Equal(0, await JobCountAsync());
    }

    [Fact]
    public async Task Enqueue_Video_Hls_Generate_Collapses_Duplicate_Pending_Enqueues()
    {
        var blobId = Guid.NewGuid();
        await RunCli("jobs", "enqueue", "media-video-hls-generate", "--blob", blobId.ToString());
        await RunCli("jobs", "enqueue", "media-video-hls-generate", "--blob", blobId.ToString());

        Assert.Equal(1, await JobCountAsync());
    }

    [Fact]
    public async Task RunOnce_Executes_Video_Hls_Generate_Job()
    {
        // Factory default: provider disabled → the handler runs, the service
        // refuses work (NotEligible) and the job still SUCCEEDS — provider
        // unavailability is an environment state, never a job failure.
        var blobId = Guid.NewGuid();
        await RunCli("jobs", "enqueue", "media-video-hls-generate", "--blob", blobId.ToString());

        var (exit, _, stderr) = await RunCli("jobs", "run-once");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.Succeeded, job.Status);
    }

    [Fact]
    public async Task Enqueue_Posters_Regenerate_Creates_Queued_Row()
    {
        var (exit, stdout, stderr) = await RunCli(
            "jobs", "enqueue", "media-posters-regenerate", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(JobTypes.MediaPostersRegenerate, stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobTypes.MediaPostersRegenerate, job.Type);
        Assert.Equal(JobStatuses.Queued, job.Status);
    }

    [Fact]
    public async Task Enqueue_Unknown_Job_Returns_64()
    {
        var (exit, _, stderr) = await RunCli("jobs", "enqueue", "not-a-real-job");
        Assert.Equal(64, exit);
        Assert.Contains("unknown job", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await JobCountAsync());
    }

    [Fact]
    public async Task List_Shows_Counts_And_Never_Payload()
    {
        await RunCli("jobs", "enqueue", "metadata-backfill", "--limit", "3");
        var (exit, stdout, _) = await RunCli("jobs", "list");

        Assert.Equal(0, exit);
        Assert.Contains("queued=1", stdout);
        // Never prints the payload JSON.
        Assert.DoesNotContain("Limit", stdout);
        Assert.DoesNotContain("PayloadJson", stdout);
        Assert.DoesNotContain("\"DryRun\"", stdout);
    }

    [Fact]
    public async Task RunOnce_Processes_Metadata_Backfill_Via_Real_Service()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var blob = await UploadPendingImageAsync(client);

        // Real (non-dry-run) backfill so the handler must invoke the genuine
        // MetadataBackfillService and advance the extraction version.
        await RunCli("jobs", "enqueue", "metadata-backfill");
        var (exit, stdout, stderr) = await RunCli("jobs", "run-once");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        // The upload also scheduled a bounded post-ingest derivatives job, so
        // run-once drains both; assert it ran rather than pinning an exact count.
        Assert.Contains("processed", stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking()
            .SingleAsync(j => j.Type == JobTypes.MetadataEmbeddedBackfill);
        Assert.Equal(JobStatuses.Succeeded, job.Status);
        // The real service ran: the pending blob now carries the current version.
        var meta = await db.BlobMetadata.AsNoTracking().SingleAsync(m => m.BlobObjectId == blob);
        Assert.NotNull(meta.ExtractionVersion);
    }

    [Fact]
    public async Task Help_Lists_Jobs_Commands()
    {
        var (exit, stdout, _) = await RunCli("--help");
        Assert.Equal(0, exit);
        Assert.Contains("jobs enqueue", stdout);
        Assert.Contains("jobs run-once", stdout);
        Assert.Contains("jobs worker", stdout);
    }
}
