using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-04: the `ai video semantic ...` operator CLI, wired through the real
// dispatcher with a SQLite-backed service provider (same harness AiCliTests
// already uses for the generic `ai` surface).
public sealed class VideoSemanticCliTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public VideoSemanticCliTests()
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

    private static readonly string[] ForbiddenNeedles =
    {
        "StorageKey", "storageKey", "storage_key",
        "BlobObjectId", "blobObjectId",
        "Sha256", "sha256",
        "/storage/objects/", "PayloadJson", "TokenHash",
        "EmbeddingBytes", "Vector", "stack trace", "Exception:",
    };

    private static void AssertNoForbidden(string text)
    {
        foreach (var needle in ForbiddenNeedles)
        {
            Assert.DoesNotContain(needle, text, StringComparison.Ordinal);
        }
    }

    private async Task<Guid> SeedEligibleVideoBlobAsync(int seed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();

        var user = new User
        {
            Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@example.com",
            DisplayName = "O", CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var bytes = new byte[64];
        bytes[0] = (byte)seed;
        bytes[1] = (byte)(seed >> 8);
        var file = await files.CreateAsync(user.Id, null, $"v{seed}.mp4", "video/mp4", new MemoryStream(bytes));

        var meta = await db.BlobMetadata.SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
        meta.MediaCategory = MediaCategories.Video;
        meta.VideoExtractionStatus = MetadataStatuses.Completed;
        meta.VideoExtractionVersion = 1;
        meta.DurationSeconds = 60;
        await db.SaveChangesAsync();
        return file.BlobObjectId;
    }

    // ---- status --------------------------------------------------------------

    [Fact]
    public async Task Status_Works_On_Empty_Database_And_Is_Safe()
    {
        var (exit, stdout, stderr) = await RunCli("ai", "video", "semantic", "status");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("eligible_video_blobs=0", stdout);
        Assert.Contains("video_segmentation_enabled=False", stdout);
        Assert.Contains("video_visual_embeddings_enabled=False", stdout);
        Assert.Contains("max_ranked_photo_candidates=300", stdout);
        Assert.Contains("max_ranked_video_candidates=300", stdout);
        AssertNoForbidden(stdout);
    }

    [Fact]
    public async Task Status_Reports_Eligible_Blob_Count()
    {
        await SeedEligibleVideoBlobAsync(1);
        await SeedEligibleVideoBlobAsync(2);

        var (exit, stdout, _) = await RunCli("ai", "video", "semantic", "status");

        Assert.Equal(0, exit);
        Assert.Contains("eligible_video_blobs=2", stdout);
        Assert.Contains("not_processed=2", stdout);
    }

    // ---- segments backfill -----------------------------------------------

    [Fact]
    public async Task Segments_Backfill_DryRun_Selects_Without_Enqueuing()
    {
        await SeedEligibleVideoBlobAsync(1);

        var (exit, stdout, stderr) = await RunCli(
            "ai", "video", "semantic", "segments", "backfill", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("dry-run", stdout);
        Assert.Contains("1 video blob(s) would be selected", stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.BackgroundJobs.CountAsync());
        AssertNoForbidden(stdout);
    }

    [Fact]
    public async Task Segments_Backfill_Enqueues_Job_With_Selected_Count()
    {
        await SeedEligibleVideoBlobAsync(1);

        var (exit, stdout, stderr) = await RunCli("ai", "video", "semantic", "segments", "backfill");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(JobTypes.AiVideosSegmentsBackfill, stdout);
        Assert.Contains("selected_targets=1", stdout);
        Assert.Contains("queued", stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobTypes.AiVideosSegmentsBackfill, job.Type);
        Assert.Equal(JobStatuses.Queued, job.Status);
    }

    [Fact]
    public async Task Segments_Backfill_Duplicate_Request_Matches_Existing_Job()
    {
        await SeedEligibleVideoBlobAsync(1);

        var first = await RunCli("ai", "video", "semantic", "segments", "backfill");
        var second = await RunCli("ai", "video", "semantic", "segments", "backfill");

        Assert.Equal(0, first.exit);
        Assert.Equal(0, second.exit);
        Assert.Contains("queued", first.stdout);
        Assert.Contains("matched existing", second.stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.BackgroundJobs.CountAsync());
    }

    [Fact]
    public async Task Segments_Backfill_No_Eligible_Work_Enqueues_Nothing()
    {
        var (exit, stdout, stderr) = await RunCli("ai", "video", "semantic", "segments", "backfill");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("no eligible work selected", stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.BackgroundJobs.CountAsync());
    }

    [Fact]
    public async Task Segments_Backfill_Rejects_Nonpositive_Limit()
    {
        var (exit, _, stderr) = await RunCli(
            "ai", "video", "semantic", "segments", "backfill", "--limit", "0");

        Assert.Equal(64, exit);
        Assert.Contains("--limit must be a positive integer", stderr);
    }

    [Fact]
    public async Task Segments_Backfill_Rejects_Nonpositive_Segmentation_Version()
    {
        var (exit, _, stderr) = await RunCli(
            "ai", "video", "semantic", "segments", "backfill", "--segmentation-version", "-1");

        Assert.Equal(64, exit);
        Assert.Contains("--segmentation-version must be a positive integer", stderr);
    }

    [Fact]
    public async Task Segments_Backfill_Rejects_Invalid_Blob_Id()
    {
        var (exit, _, stderr) = await RunCli(
            "ai", "video", "semantic", "segments", "backfill", "--blob-id", "not-a-guid");

        Assert.Equal(64, exit);
        Assert.Contains("--blob-id must be a GUID", stderr);
    }

    [Fact]
    public async Task Segments_Backfill_Single_Blob_Scope_Selects_Only_That_Blob()
    {
        var target = await SeedEligibleVideoBlobAsync(1);
        await SeedEligibleVideoBlobAsync(2);

        var (exit, stdout, _) = await RunCli(
            "ai", "video", "semantic", "segments", "backfill", "--dry-run",
            "--blob-id", target.ToString("N"));

        Assert.Equal(0, exit);
        Assert.Contains("1 video blob(s) would be selected", stdout);
    }

    // ---- embeddings backfill ----------------------------------------------

    [Fact]
    public async Task Embeddings_Backfill_Rejects_Unknown_Profile()
    {
        var (exit, _, stderr) = await RunCli(
            "ai", "video", "semantic", "embeddings", "backfill", "--profile", "does-not-exist");

        Assert.Equal(64, exit);
        Assert.Contains("not a known, enabled image-embedding profile", stderr);
    }

    [Fact]
    public async Task Embeddings_Backfill_Reports_No_Usable_Profile_When_None_Configured()
    {
        var (exit, stdout, stderr) = await RunCli("ai", "video", "semantic", "embeddings", "backfill");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("no usable active image-embedding profile", stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.BackgroundJobs.CountAsync());
    }

    [Fact]
    public async Task Embeddings_Backfill_With_Explicit_Profile_Reports_No_Eligible_Work()
    {
        await RunCli("ai", "seed"); // deterministic dev/test profiles

        var (exit, stdout, stderr) = await RunCli(
            "ai", "video", "semantic", "embeddings", "backfill", "--profile", "det-image-embedding-v1");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("profile=det-image-embedding-v1", stdout);
        Assert.Contains("no eligible work selected", stdout); // no completed manifest yet

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.BackgroundJobs.CountAsync());
    }

    [Fact]
    public async Task Embeddings_Backfill_Enqueues_When_A_Completed_Manifest_Exists()
    {
        await RunCli("ai", "seed");
        var blob = await SeedEligibleVideoBlobAsync(1);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.VideoSemanticIndexes.Add(new VideoSemanticIndex
            {
                Id = Guid.NewGuid(), BlobObjectId = blob, SegmentationVersion = 1,
                Status = AiArtifactStatuses.Completed, AttemptCount = 1,
                SegmentCount = 1, SampleCount = 1, CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var (exit, stdout, stderr) = await RunCli(
            "ai", "video", "semantic", "embeddings", "backfill", "--profile", "det-image-embedding-v1");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(JobTypes.AiVideosEmbeddingsBackfill, stdout);
        Assert.Contains("selected_targets=1", stdout);

        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await verifyDb.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobTypes.AiVideosEmbeddingsBackfill, job.Type);
    }

    // ---- retry-failed -------------------------------------------------------

    [Fact]
    public async Task RetryFailed_Segments_Forces_Failed_Only_Scope()
    {
        var blob = await SeedEligibleVideoBlobAsync(1);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.VideoSemanticIndexes.Add(new VideoSemanticIndex
            {
                Id = Guid.NewGuid(), BlobObjectId = blob, SegmentationVersion = 1,
                Status = AiArtifactStatuses.Failed, AttemptCount = 1, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var (exit, stdout, stderr) = await RunCli(
            "ai", "video", "semantic", "retry-failed", "segments", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("1 video blob(s) would be selected", stdout);
    }

    [Fact]
    public async Task RetryFailed_Segments_Never_Reprocesses_Capacity_Exceeded()
    {
        var blob = await SeedEligibleVideoBlobAsync(1);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.VideoSemanticIndexes.Add(new VideoSemanticIndex
            {
                Id = Guid.NewGuid(), BlobObjectId = blob, SegmentationVersion = 1,
                Status = AiArtifactStatuses.Skipped,
                ErrorCode = VideoSemanticErrorCodes.SegmentationCapacityExceeded,
                IsPermanentFailure = true, AttemptCount = 1, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var (exit, stdout, _) = await RunCli(
            "ai", "video", "semantic", "retry-failed", "segments", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Contains("0 video blob(s) would be selected", stdout);
    }

    // ---- usage / help --------------------------------------------------------

    [Fact]
    public async Task Unknown_Video_Semantic_Subcommand_Is_Rejected()
    {
        var (exit, _, stderr) = await RunCli("ai", "video", "semantic", "bogus");

        Assert.Equal(64, exit);
        Assert.Contains("unknown subcommand", stderr);
    }

    [Fact]
    public async Task Missing_Video_Semantic_Subcommand_Is_Rejected()
    {
        var (exit, _, stderr) = await RunCli("ai", "video", "semantic");

        Assert.Equal(64, exit);
        Assert.Contains("missing subcommand", stderr);
    }

    [Fact]
    public async Task Help_Lists_Video_Semantic_Commands()
    {
        var (exit, stdout, _) = await RunCli("--help");

        Assert.Equal(0, exit);
        Assert.Contains("ai video semantic", stdout);
        Assert.Contains("segments backfill", stdout);
        Assert.Contains("embeddings backfill", stdout);
        Assert.Contains("retry-failed", stdout);
    }
}
