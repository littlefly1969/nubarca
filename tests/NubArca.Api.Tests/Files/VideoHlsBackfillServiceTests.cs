using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Admin console: bulk HLS pre-warm. Reuses FakeHlsTranscoder / FakeVideoProbe
// from VideoHlsGenerationServiceTests. Verifies eligibility scoping, limit,
// retry-failed, force, dry-run, and the provider-off no-op.
public sealed class VideoHlsBackfillServiceTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;
    private readonly string _hlsRoot;
    private readonly HlsDerivativeStorage _hls;
    private readonly FakeHlsTranscoder _transcoder = new();
    private readonly FakeVideoProbe _probe = new();

    public VideoHlsBackfillServiceTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
        _hlsRoot = Path.Combine(Path.GetTempPath(), $"nc-hls-bf-{Guid.NewGuid():N}");
        _hls = new HlsDerivativeStorage(_hlsRoot);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_hlsRoot, recursive: true); } catch { /* best effort */ }
    }

    private VideoHlsBackfillService Backfill(IServiceScope scope, MediaOptions? options = null)
    {
        var opts = Options.Create(options ?? new MediaOptions { VideoHlsProvider = "ffmpeg" });
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var generation = new VideoHlsGenerationService(
            db,
            _factory.Services.GetRequiredService<IBlobStorage>(),
            _hls, _transcoder, _probe, opts, TimeProvider.System,
            NullLogger<VideoHlsGenerationService>.Instance);
        return new VideoHlsBackfillService(db, generation, opts,
            NullLogger<VideoHlsBackfillService>.Instance);
    }

    private async Task<Guid> SeedVideoAsync(
        string mediaCategory = MediaCategories.Video,
        string? detectedContentType = "video/mp4",
        string? existingHlsStatus = null)
    {
        var storage = _factory.Services.GetRequiredService<IBlobStorage>();
        var write = await storage.WriteAsync(
            new MemoryStream(Encoding.UTF8.GetBytes($"v-{Guid.NewGuid():N}")));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(), Sha256 = write.Sha256, SizeBytes = write.SizeBytes,
            StorageKey = write.StorageKey, ReferenceCount = 1, CreatedAt = DateTime.UtcNow,
        };
        db.BlobObjects.Add(blob);
        db.BlobMetadata.Add(new BlobMetadata
        {
            Id = Guid.NewGuid(), BlobObjectId = blob.Id, SizeBytes = write.SizeBytes,
            MediaCategory = mediaCategory, DetectedContentType = detectedContentType,
            VideoExtractionStatus = MetadataStatuses.Completed,
            VideoCodec = "h264", AudioCodec = "aac", HasAudio = true, Height = 1080,
            CreatedAt = DateTime.UtcNow,
        });
        if (existingHlsStatus is not null)
        {
            db.BlobHlsDerivatives.Add(new BlobHlsDerivative
            {
                Id = Guid.NewGuid(), BlobObjectId = blob.Id, Status = existingHlsStatus,
                Version = FfmpegVideoHlsTranscoder.Version, CreatedAt = DateTime.UtcNow,
                ReadyAt = existingHlsStatus == VideoHlsStatuses.Ready ? DateTime.UtcNow : null,
            });
        }
        await db.SaveChangesAsync();
        return blob.Id;
    }

    private async Task<int> ReadyCountAsync()
        => await InDbAsync(db => db.BlobHlsDerivatives.CountAsync(d => d.Status == VideoHlsStatuses.Ready));

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task Disabled_Provider_Does_Nothing()
    {
        await SeedVideoAsync();
        using var scope = _factory.Services.CreateScope();
        var result = await Backfill(scope, new MediaOptions { VideoHlsProvider = "none" })
            .RunAsync(new VideoHlsBackfillOptions());
        Assert.Equal(0, result.Candidates);
        Assert.Equal(0, _transcoder.Calls);
    }

    [Fact]
    public async Task Generates_Ladders_For_All_Missing_Videos()
    {
        for (var i = 0; i < 3; i++) await SeedVideoAsync();
        // A non-video is never a candidate. An untrusted CONTAINER is, as long
        // as ffprobe confirmed a video stream (legacy AVI/DivX/MJPEG/DV).
        await SeedVideoAsync(mediaCategory: MediaCategories.Image);
        await SeedVideoAsync(detectedContentType: null);

        using var scope = _factory.Services.CreateScope();
        var result = await Backfill(scope).RunAsync(new VideoHlsBackfillOptions());

        Assert.Equal(4, result.Candidates);
        Assert.Equal(4, result.Generated);
        Assert.Equal(4, await ReadyCountAsync());
    }

    [Fact]
    public async Task Dry_Run_Reports_Candidates_Without_Generating()
    {
        for (var i = 0; i < 2; i++) await SeedVideoAsync();
        using var scope = _factory.Services.CreateScope();
        var result = await Backfill(scope).RunAsync(new VideoHlsBackfillOptions { DryRun = true });

        Assert.Equal(2, result.Candidates);
        Assert.Equal(0, result.Generated);
        Assert.Equal(0, _transcoder.Calls);
    }

    [Fact]
    public async Task Limit_Caps_The_Number_Processed()
    {
        for (var i = 0; i < 5; i++) await SeedVideoAsync();
        using var scope = _factory.Services.CreateScope();
        var result = await Backfill(scope).RunAsync(new VideoHlsBackfillOptions { Limit = 2 });

        Assert.Equal(2, result.Generated);
        Assert.Equal(2, _transcoder.Calls);
    }

    [Fact]
    public async Task Missing_Only_Skips_Ready_And_Failed_Rows()
    {
        await SeedVideoAsync(); // missing → generated
        await SeedVideoAsync(existingHlsStatus: VideoHlsStatuses.Ready);
        await SeedVideoAsync(existingHlsStatus: VideoHlsStatuses.Failed);

        using var scope = _factory.Services.CreateScope();
        var result = await Backfill(scope).RunAsync(new VideoHlsBackfillOptions());

        Assert.Equal(1, result.Candidates);
        Assert.Equal(1, result.Generated);
    }

    [Fact]
    public async Task Retry_Failed_Includes_Failed_But_Not_Ready()
    {
        await SeedVideoAsync(existingHlsStatus: VideoHlsStatuses.Ready);
        await SeedVideoAsync(existingHlsStatus: VideoHlsStatuses.Failed);

        using var scope = _factory.Services.CreateScope();
        var result = await Backfill(scope).RunAsync(new VideoHlsBackfillOptions { RetryFailed = true });

        Assert.Equal(1, result.Candidates); // only the failed one
        Assert.Equal(1, result.Generated);
    }

    [Fact]
    public async Task Force_Reprocesses_Ready_Rows_Too()
    {
        await SeedVideoAsync(existingHlsStatus: VideoHlsStatuses.Ready);
        await SeedVideoAsync(existingHlsStatus: VideoHlsStatuses.Failed);
        await SeedVideoAsync(); // missing

        using var scope = _factory.Services.CreateScope();
        var result = await Backfill(scope).RunAsync(new VideoHlsBackfillOptions { Force = true });

        Assert.Equal(3, result.Candidates);
        Assert.Equal(3, result.Generated);
    }

    [Fact]
    public async Task Yields_And_Resumes_Missing_Backfill_From_Checkpoint()
    {
        for (var i = 0; i < 3; i++) await SeedVideoAsync();
        using var scope = _factory.Services.CreateScope();
        var service = Backfill(scope);

        var first = await service.RunAsync(
            new VideoHlsBackfillOptions(),
            shouldYield: processed => processed >= 1);

        Assert.True(first.MoreWorkRemaining);
        Assert.NotNull(first.NextCheckpointJson);
        Assert.Equal(1, first.Generated);

        var resumed = await service.RunAsync(
            new VideoHlsBackfillOptions(),
            checkpointJson: first.NextCheckpointJson);

        Assert.False(resumed.MoreWorkRemaining);
        Assert.Null(resumed.NextCheckpointJson);
        Assert.Equal(3, resumed.Generated);
        Assert.Equal(3, await ReadyCountAsync());
    }

    [Fact]
    public async Task Force_Backfill_Uses_Cumulative_Offset_Across_Yield()
    {
        for (var i = 0; i < 3; i++)
        {
            await SeedVideoAsync(existingHlsStatus: VideoHlsStatuses.Ready);
        }
        using var scope = _factory.Services.CreateScope();
        var service = Backfill(scope);

        var first = await service.RunAsync(
            new VideoHlsBackfillOptions { Force = true },
            shouldYield: processed => processed >= 1);
        var resumed = await service.RunAsync(
            new VideoHlsBackfillOptions { Force = true },
            checkpointJson: first.NextCheckpointJson);

        Assert.False(resumed.MoreWorkRemaining);
        Assert.Equal(3, resumed.Generated);
        Assert.Equal(3, _transcoder.Calls);
    }
}
