using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-04: proves `--dry-run` (and `retry-failed ... --dry-run`) is a pure
// preview through the REAL CLI dispatcher — a full-database row-count snapshot
// taken before and after every call must be byte-for-byte identical. No
// service-level mock stands in for this: the same SqliteWebApplicationFactory
// harness the CLI runs against in production is used here.
public sealed class VideoSemanticDryRunTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public VideoSemanticDryRunTests()
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

    // Full-table snapshot of every row a video-semantic write could touch,
    // plus the job queue. Equality before/after is the dry-run contract.
    private sealed record Snapshot(
        int Indexes, int Segments, int Samples, int EmbeddingStatuses,
        int SampleEmbeddings, int BackgroundJobs, int FileItems, int BlobMetadata);

    private async Task<Snapshot> SnapshotAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return new Snapshot(
            await db.VideoSemanticIndexes.CountAsync(),
            await db.VideoSemanticSegments.CountAsync(),
            await db.VideoSemanticSamples.CountAsync(),
            await db.VideoSemanticEmbeddingStatuses.CountAsync(),
            await db.VideoSemanticSampleEmbeddings.CountAsync(),
            await db.BackgroundJobs.CountAsync(),
            await db.FileItems.IgnoreQueryFilters().CountAsync(),
            await db.BlobMetadata.CountAsync());
    }

    private async Task<Guid> SeedEligibleVideoBlobAsync(int seed, Guid? ownerOverride = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();

        var ownerId = ownerOverride;
        if (ownerId is null)
        {
            var user = new User
            {
                Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@example.com",
                DisplayName = "O", CreatedAt = DateTime.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            ownerId = user.Id;
        }

        var bytes = new byte[64];
        bytes[0] = (byte)seed;
        bytes[1] = (byte)(seed >> 8);
        var file = await files.CreateAsync(ownerId.Value, null, $"v{seed}.mp4", "video/mp4", new MemoryStream(bytes));

        var meta = await db.BlobMetadata.SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
        meta.MediaCategory = MediaCategories.Video;
        meta.VideoExtractionStatus = MetadataStatuses.Completed;
        meta.VideoExtractionVersion = 1;
        meta.DurationSeconds = 60;
        await db.SaveChangesAsync();
        return file.BlobObjectId;
    }

    private async Task SeedSegmentationIndexAsync(Guid blobId, int version, string status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.VideoSemanticIndexes.Add(new VideoSemanticIndex
        {
            Id = Guid.NewGuid(), BlobObjectId = blobId, SegmentationVersion = version,
            Status = status, AttemptCount = 1, SegmentCount = status == AiArtifactStatuses.Completed ? 1 : 0,
            SampleCount = status == AiArtifactStatuses.Completed ? 1 : 0,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // ---- segments backfill dry-run ------------------------------------------

    [Fact]
    public async Task Segments_DryRun_Touches_No_Row_Anywhere()
    {
        await SeedEligibleVideoBlobAsync(1);
        await SeedEligibleVideoBlobAsync(2);
        var before = await SnapshotAsync();

        var (exit, stdout, stderr) = await RunCli(
            "ai", "video", "semantic", "segments", "backfill", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("2 video blob(s) would be selected", stdout);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task Segments_DryRun_Respects_Limit()
    {
        for (var i = 0; i < 5; i++)
        {
            await SeedEligibleVideoBlobAsync(i);
        }
        var before = await SnapshotAsync();

        var (exit, stdout, _) = await RunCli(
            "ai", "video", "semantic", "segments", "backfill", "--dry-run", "--limit", "2");

        Assert.Equal(0, exit);
        Assert.Contains("2 video blob(s) would be selected", stdout);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task Segments_DryRun_FailedOnly_Selects_Only_Failed_Manifests()
    {
        var ok = await SeedEligibleVideoBlobAsync(1);
        var failed = await SeedEligibleVideoBlobAsync(2);
        await SeedSegmentationIndexAsync(ok, 1, AiArtifactStatuses.Completed);
        await SeedSegmentationIndexAsync(failed, 1, AiArtifactStatuses.Failed);
        var before = await SnapshotAsync();

        var (exit, stdout, _) = await RunCli(
            "ai", "video", "semantic", "segments", "backfill", "--dry-run", "--failed-only");

        Assert.Equal(0, exit);
        Assert.Contains("1 video blob(s) would be selected", stdout);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task Segments_DryRun_SegmentationVersion_Filters_To_That_Version()
    {
        var blob = await SeedEligibleVideoBlobAsync(1);
        await SeedSegmentationIndexAsync(blob, 1, AiArtifactStatuses.Completed);
        var before = await SnapshotAsync();

        // Version 1 is already completed → not a candidate at v1, but IS a
        // candidate at an unused explicit version 2 (an operator-directed
        // reindex target).
        var atV1 = await RunCli(
            "ai", "video", "semantic", "segments", "backfill", "--dry-run", "--segmentation-version", "1");
        var atV2 = await RunCli(
            "ai", "video", "semantic", "segments", "backfill", "--dry-run", "--segmentation-version", "2");

        Assert.Contains("0 video blob(s) would be selected", atV1.stdout);
        Assert.Contains("1 video blob(s) would be selected", atV2.stdout);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task Segments_DryRun_Single_Blob_Scope_Ignores_Other_Blobs()
    {
        var target = await SeedEligibleVideoBlobAsync(1);
        await SeedEligibleVideoBlobAsync(2);
        await SeedEligibleVideoBlobAsync(3);
        var before = await SnapshotAsync();

        var (exit, stdout, _) = await RunCli(
            "ai", "video", "semantic", "segments", "backfill", "--dry-run",
            "--blob-id", target.ToString("N"));

        Assert.Equal(0, exit);
        Assert.Contains("1 video blob(s) would be selected", stdout);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task Segments_DryRun_Excludes_Vault_Only_Blob()
    {
        var visible = await SeedEligibleVideoBlobAsync(1);
        var vaulted = await SeedEligibleVideoBlobAsync(2);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var vaultedFile = await db.FileItems.IgnoreQueryFilters()
                .SingleAsync(f => f.BlobObjectId == vaulted);
            var vault = new PrivateVault
            {
                Id = Guid.NewGuid(), OwnerUserId = vaultedFile.OwnerUserId,
                DisplayName = "Private", PasswordHash = "n/a", CreatedAt = DateTime.UtcNow,
            };
            db.PrivateVaults.Add(vault);
            vaultedFile.PrivateVaultId = vault.Id;
            await db.SaveChangesAsync();
        }
        var before = await SnapshotAsync();

        var (exit, stdout, _) = await RunCli(
            "ai", "video", "semantic", "segments", "backfill", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Contains("1 video blob(s) would be selected", stdout); // visible only
        Assert.Equal(before, await SnapshotAsync());
        _ = visible;
    }

    [Fact]
    public async Task Segments_DryRun_Preview_Count_Matches_The_Real_Enqueue_Selection()
    {
        await SeedEligibleVideoBlobAsync(1);
        await SeedEligibleVideoBlobAsync(2);

        var dryRun = await RunCli("ai", "video", "semantic", "segments", "backfill", "--dry-run");
        var real = await RunCli("ai", "video", "semantic", "segments", "backfill");

        Assert.Contains("2 video blob(s) would be selected", dryRun.stdout);
        Assert.Contains("selected_targets=2", real.stdout);
    }

    // ---- embeddings backfill dry-run -----------------------------------------

    [Fact]
    public async Task Embeddings_DryRun_Touches_No_Row_Anywhere()
    {
        await RunCli("ai", "seed");
        var blob = await SeedEligibleVideoBlobAsync(1);
        await SeedSegmentationIndexAsync(blob, 1, AiArtifactStatuses.Completed);
        var before = await SnapshotAsync();

        var (exit, stdout, stderr) = await RunCli(
            "ai", "video", "semantic", "embeddings", "backfill", "--dry-run",
            "--profile", "det-image-embedding-v1");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("1 video blob(s) would be selected", stdout);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task Embeddings_DryRun_Excludes_Blob_With_Incomplete_Manifest()
    {
        await RunCli("ai", "seed");
        var incomplete = await SeedEligibleVideoBlobAsync(1);
        await SeedSegmentationIndexAsync(incomplete, 1, AiArtifactStatuses.Failed);
        var before = await SnapshotAsync();

        var (exit, stdout, _) = await RunCli(
            "ai", "video", "semantic", "embeddings", "backfill", "--dry-run",
            "--profile", "det-image-embedding-v1");

        Assert.Equal(0, exit);
        Assert.Contains("0 video blob(s) would be selected", stdout);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task RetryFailed_Embeddings_DryRun_Touches_No_Row_Anywhere()
    {
        await RunCli("ai", "seed");
        var blob = await SeedEligibleVideoBlobAsync(1);
        var indexId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.VideoSemanticIndexes.Add(new VideoSemanticIndex
            {
                Id = indexId, BlobObjectId = blob, SegmentationVersion = 1,
                Status = AiArtifactStatuses.Completed, AttemptCount = 1,
                SegmentCount = 1, SampleCount = 1, CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
            });
            var profile = await db.AiProfiles.SingleAsync(p => p.Key == "det-image-embedding-v1");
            db.VideoSemanticEmbeddingStatuses.Add(new VideoSemanticEmbeddingStatus
            {
                Id = Guid.NewGuid(), VideoSemanticIndexId = indexId, ProfileId = profile.Id,
                Status = VideoSemanticEmbeddingStatuses.Failed, ExpectedSampleCount = 1,
                FailedSampleCount = 1, ErrorCode = VideoSemanticErrorCodes.FrameExtraction,
                AttemptCount = 1, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var before = await SnapshotAsync();

        var (exit, stdout, stderr) = await RunCli(
            "ai", "video", "semantic", "retry-failed", "embeddings", "--dry-run",
            "--profile", "det-image-embedding-v1");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("1 video blob(s) would be selected", stdout);
        Assert.Equal(before, await SnapshotAsync());
    }
}
