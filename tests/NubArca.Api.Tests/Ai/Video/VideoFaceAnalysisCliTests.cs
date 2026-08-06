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

// VFACE-01: the `ai video semantic faces …` operator CLI, wired through the real
// dispatcher with a SQLite-backed service provider.
//
// API/CLI/worker PARITY is the point of this file: the CLI must express exactly
// the same bounded scope the worker payload and the post-segmentation scheduler
// do (blob, segmentation version, analysis version, profile, limit, failed-only),
// and it must enqueue rather than analyse in-process.
public sealed class VideoFaceAnalysisCliTests : IDisposable
{
    private const string FaceProfileKey = "det-face-embedding-v1";

    private readonly SqliteWebApplicationFactory _factory;

    public VideoFaceAnalysisCliTests()
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
        "Sha256", "sha256", "/storage/objects/",
        "PayloadJson", "TokenHash", "EmbeddingBytes", "Vector",
        "PersonId", "personId", "stack trace", "Exception:",
    };

    private static void AssertNoForbidden(string text)
    {
        foreach (var needle in ForbiddenNeedles)
        {
            Assert.DoesNotContain(needle, text, StringComparison.Ordinal);
        }
    }

    private async Task<Guid> SeedVideoWithManifestAsync(int seed, bool completed = true)
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
        var file = await files.CreateAsync(
            user.Id, null, $"v{seed}.mp4", "video/mp4", new MemoryStream(bytes));

        var meta = await db.BlobMetadata.SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
        meta.MediaCategory = MediaCategories.Video;
        meta.VideoExtractionStatus = MetadataStatuses.Completed;
        meta.VideoExtractionVersion = 1;
        meta.DurationSeconds = 60;

        db.VideoSemanticIndexes.Add(new VideoSemanticIndex
        {
            Id = Guid.NewGuid(), BlobObjectId = file.BlobObjectId, SegmentationVersion = 1,
            Status = completed ? AiArtifactStatuses.Completed : AiArtifactStatuses.Failed,
            AttemptCount = 1, SegmentCount = 1, SampleCount = 1,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return file.BlobObjectId;
    }

    [Fact]
    public async Task Faces_Backfill_Enqueues_When_A_Completed_Manifest_Exists()
    {
        await RunCli("ai", "seed");
        await SeedVideoWithManifestAsync(1);

        var (exit, stdout, stderr) = await RunCli(
            "ai", "video", "semantic", "faces", "backfill", "--profile", FaceProfileKey);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(JobTypes.AiVideosFacesBackfill, stdout);
        Assert.Contains("selected_targets=1", stdout);
        AssertNoForbidden(stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobTypes.AiVideosFacesBackfill, job.Type);
        // The CLI ENQUEUES: no analysis and no track exists yet.
        Assert.Equal(0, await db.VideoFaceAnalysisStatuses.CountAsync());
        Assert.Equal(0, await db.VideoFaceTracks.CountAsync());
    }

    [Fact]
    public async Task Faces_Backfill_Enqueues_Nothing_Without_A_Completed_Manifest()
    {
        await RunCli("ai", "seed");
        await SeedVideoWithManifestAsync(1, completed: false);

        var (exit, stdout, stderr) = await RunCli(
            "ai", "video", "semantic", "faces", "backfill", "--profile", FaceProfileKey);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("no eligible work selected", stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.BackgroundJobs.CountAsync());
    }

    [Fact]
    public async Task Dry_Run_Writes_Nothing_And_Enqueues_Nothing()
    {
        await RunCli("ai", "seed");
        await SeedVideoWithManifestAsync(1);

        var (exit, stdout, stderr) = await RunCli(
            "ai", "video", "semantic", "faces", "backfill",
            "--profile", FaceProfileKey, "--dry-run");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("(dry-run)", stdout);
        Assert.Contains("no job enqueued", stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.BackgroundJobs.CountAsync());
        Assert.Equal(0, await db.VideoFaceAnalysisStatuses.CountAsync());
    }

    [Fact]
    public async Task RetryFailed_Faces_Forces_Failed_Only_Scope()
    {
        await RunCli("ai", "seed");
        var blob = await SeedVideoWithManifestAsync(1);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.AiProfiles.SingleAsync(p => p.Key == FaceProfileKey);
            var index = await db.VideoSemanticIndexes.SingleAsync(i => i.BlobObjectId == blob);
            db.VideoFaceAnalysisStatuses.Add(new VideoFaceAnalysisStatus
            {
                Id = Guid.NewGuid(), VideoSemanticIndexId = index.Id, AnalysisVersion = 1,
                DetectionProfileId = profile.Id, EmbeddingProfileId = profile.Id,
                Status = VideoFaceAnalysisStatuses.Failed, PlannedFrameCount = 10,
                FailedFrameCount = 10, ErrorCode = VideoFaceErrorCodes.FrameExtractFailed,
                AttemptCount = 1, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var (exit, stdout, stderr) = await RunCli(
            "ai", "video", "semantic", "retry-failed", "faces", "--profile", FaceProfileKey);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("failed_only=True", stdout);
        Assert.Contains("selected_targets=1", stdout);
        AssertNoForbidden(stdout);
    }

    [Fact]
    public async Task An_Analysis_Version_Narrows_The_Idempotency_Key()
    {
        await RunCli("ai", "seed");
        await SeedVideoWithManifestAsync(1);

        var (exit, stdout, _) = await RunCli(
            "ai", "video", "semantic", "faces", "backfill",
            "--profile", FaceProfileKey, "--analysis-version", "3");

        Assert.Equal(0, exit);
        Assert.Contains("analysis_version=3", stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Contains(":3:", job.IdempotencyKey);
    }

    [Theory]
    [InlineData("--limit", "0")]
    [InlineData("--analysis-version", "0")]
    [InlineData("--segmentation-version", "-1")]
    [InlineData("--blob-id", "not-a-guid")]
    public async Task Invalid_Options_Are_Rejected_Without_Enqueuing(string option, string value)
    {
        await RunCli("ai", "seed");

        var (exit, _, stderr) = await RunCli(
            "ai", "video", "semantic", "faces", "backfill", option, value);

        Assert.Equal(64, exit);
        Assert.NotEqual(string.Empty, stderr);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.BackgroundJobs.CountAsync());
    }

    [Fact]
    public async Task An_Unknown_Profile_Is_Rejected()
    {
        await RunCli("ai", "seed");

        var (exit, _, stderr) = await RunCli(
            "ai", "video", "semantic", "faces", "backfill", "--profile", "no-such-profile");

        Assert.Equal(64, exit);
        Assert.Contains("not a known, enabled face profile", stderr);
    }

    [Fact]
    public async Task An_Image_Profile_Is_Rejected_For_Face_Analysis()
    {
        await RunCli("ai", "seed");

        var (exit, _, stderr) = await RunCli(
            "ai", "video", "semantic", "faces", "backfill",
            "--profile", "det-image-embedding-v1");

        Assert.Equal(64, exit);
        Assert.Contains("not a known, enabled face profile", stderr);
    }

    [Fact]
    public async Task The_Subcommand_Is_Advertised_In_The_Usage_Error()
    {
        var (exit, _, stderr) = await RunCli("ai", "video", "semantic", "nonsense");

        Assert.Equal(64, exit);
        Assert.Contains("faces backfill", stderr);
        Assert.Contains("retry-failed faces", stderr);
    }
}
