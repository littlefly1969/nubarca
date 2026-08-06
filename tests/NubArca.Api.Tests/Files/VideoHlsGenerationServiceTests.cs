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

// Video-hls slice 1 — generation orchestration: eligibility gates, the
// copy-vs-encode plan, the BlobHlsDerivative row lifecycle and the publish.
public sealed class VideoHlsGenerationServiceTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;
    private readonly string _hlsRoot;
    private readonly HlsDerivativeStorage _hls;
    private readonly FakeHlsTranscoder _transcoder = new();
    private readonly FakeVideoProbe _probe = new();

    public VideoHlsGenerationServiceTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
        _hlsRoot = Path.Combine(Path.GetTempPath(), $"nc-hls-gen-{Guid.NewGuid():N}");
        _hls = new HlsDerivativeStorage(_hlsRoot);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_hlsRoot, recursive: true); } catch { /* best effort */ }
    }

    private static MediaOptions Enabled() => new() { VideoHlsProvider = "ffmpeg" };

    private VideoHlsGenerationService Service(IServiceScope scope, MediaOptions? options = null)
        => new(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            _factory.Services.GetRequiredService<IBlobStorage>(),
            _hls,
            _transcoder,
            _probe,
            Options.Create(options ?? Enabled()),
            TimeProvider.System,
            NullLogger<VideoHlsGenerationService>.Instance);

    // Seed a BlobObject (real bytes in the test store) + its BlobMetadata.
    private async Task<(Guid BlobId, string Sha)> SeedVideoBlobAsync(
        string mediaCategory = MediaCategories.Video,
        string? detectedContentType = "video/mp4",
        string videoExtractionStatus = MetadataStatuses.Completed,
        string? videoCodec = "h264",
        string? audioCodec = "aac",
        bool hasAudio = true,
        int? height = 1080)
    {
        var storage = _factory.Services.GetRequiredService<IBlobStorage>();
        var write = await storage.WriteAsync(
            new MemoryStream(Encoding.UTF8.GetBytes($"video-bytes-{Guid.NewGuid():N}")));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = write.Sha256,
            SizeBytes = write.SizeBytes,
            StorageKey = write.StorageKey,
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.BlobObjects.Add(blob);
        db.BlobMetadata.Add(new BlobMetadata
        {
            Id = Guid.NewGuid(),
            BlobObjectId = blob.Id,
            SizeBytes = write.SizeBytes,
            MediaCategory = mediaCategory,
            DetectedContentType = detectedContentType,
            VideoExtractionStatus = videoExtractionStatus,
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            HasAudio = hasAudio,
            Height = height,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (blob.Id, write.Sha256);
    }

    private async Task<BlobHlsDerivative?> RowAsync(Guid blobId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BlobHlsDerivatives.AsNoTracking()
            .FirstOrDefaultAsync(d => d.BlobObjectId == blobId);
    }

    // ---- PlanFor decision matrix -------------------------------------------

    [Theory]
    // h264 within cap + aac → full copy, low included above 480
    [InlineData("h264", "aac", true, 1920, 1080, null, true, true, true)]
    // hevc → encode video, copy aac audio
    [InlineData("hevc", "aac", true, 1920, 1080, null, false, true, true)]
    // 4K h264 → encode (short side 2160 above cap)
    [InlineData("h264", "aac", true, 3840, 2160, null, false, true, true)]
    // h264 unknown size → encode (conservative), keep low
    [InlineData("h264", "aac", true, null, null, null, false, true, true)]
    // non-aac audio → encode audio, copy video
    [InlineData("h264", "ac3", true, 1280, 720, null, true, false, true)]
    // no audio at all → audio "copy" is vacuous true
    [InlineData("h264", null, false, 1280, 720, null, true, true, true)]
    // small source → single-rendition ladder (short side ≤ 480)
    [InlineData("h264", "aac", true, 854, 480, null, true, true, false)]
    [InlineData("h264", "aac", true, 640, 360, null, true, true, false)]
    // PORTRAIT (v2): size gates use the SHORT side — a 1080×1920 phone video
    // is 1080-class (copyable), not 1920-class
    [InlineData("h264", "aac", true, 1080, 1920, null, true, true, true)]
    [InlineData("h264", "aac", true, 480, 854, null, true, true, false)]
    // ROTATED (v2): any display rotation forces re-encode — stream-copy would
    // keep the rotation tag while the encoded low rung bakes it in, and the
    // renditions would disagree on orientation (player glitch on switches)
    [InlineData("h264", "aac", true, 1920, 1080, 90, false, true, true)]
    [InlineData("h264", "aac", true, 1920, 1080, 270, false, true, true)]
    [InlineData("h264", "aac", true, 1920, 1080, 180, false, true, true)]
    // rotation 0 (explicit) keeps the copy
    [InlineData("h264", "aac", true, 1920, 1080, 0, true, true, true)]
    public void PlanFor_Decides_Copy_And_Ladder_Shape(
        string? videoCodec, string? audioCodec, bool hasAudio, int? width, int? height,
        int? rotation, bool expectCopyVideo, bool expectCopyAudio, bool expectIncludeLow)
    {
        var (copyVideo, copyAudio, includeLow) = VideoHlsGenerationService.PlanFor(
            videoCodec, audioCodec, hasAudio, width, height, rotation, Enabled());
        Assert.Equal(expectCopyVideo, copyVideo);
        Assert.Equal(expectCopyAudio, copyAudio);
        Assert.Equal(expectIncludeLow, includeLow);
    }

    // ---- Gates --------------------------------------------------------------

    [Fact]
    public async Task Disabled_Provider_Is_NotEligible_And_Never_Transcodes()
    {
        var (blobId, _) = await SeedVideoBlobAsync();
        using var scope = _factory.Services.CreateScope();

        var outcome = await Service(scope, new MediaOptions { VideoHlsProvider = "none" })
            .EnsureGeneratedAsync(blobId);

        Assert.Equal(DerivativeOutcome.NotEligible, outcome);
        Assert.Equal(0, _transcoder.Calls);
        // Provider-unavailable is an environment state: no row, no failure.
        Assert.Null(await RowAsync(blobId));
    }

    [Fact]
    public async Task Missing_Blob_Is_NotEligible()
    {
        using var scope = _factory.Services.CreateScope();
        var outcome = await Service(scope).EnsureGeneratedAsync(Guid.NewGuid());
        Assert.Equal(DerivativeOutcome.NotEligible, outcome);
    }

    [Fact]
    public async Task NonVideo_Blob_Is_NotEligible()
    {
        var (blobId, _) = await SeedVideoBlobAsync(mediaCategory: MediaCategories.Image);
        using var scope = _factory.Services.CreateScope();
        var outcome = await Service(scope).EnsureGeneratedAsync(blobId);
        Assert.Equal(DerivativeOutcome.NotEligible, outcome);
        Assert.Equal(0, _transcoder.Calls);
    }

    [Fact]
    // A container the header sniffer does not trust (AVI/DivX/MJPEG/DV…) is
    // still a REAL video when ffprobe parsed a video stream from it — and HLS
    // only ever serves ffmpeg output, so it is eligible. This is what makes the
    // legacy part of a library playable at all.
    public async Task Untrusted_Type_But_Ffprobe_Confirmed_Is_Eligible()
    {
        var (blobId, sha) = await SeedVideoBlobAsync(
            detectedContentType: null,
            videoExtractionStatus: MetadataStatuses.Completed,
            videoCodec: "mpeg4");
        using var scope = _factory.Services.CreateScope();

        var outcome = await Service(scope).EnsureGeneratedAsync(blobId);

        Assert.Equal(DerivativeOutcome.Generated, outcome);
        Assert.True(_hls.Exists(sha));
    }

    // SECURITY: the spoof case must stay excluded. A file whose bytes are not a
    // video fails ffprobe, so neither signal is present — exactly as before the
    // "server-confirmed" relaxation.
    [Fact]
    public async Task Untrusted_Type_Without_Ffprobe_Confirmation_Is_NotEligible()
    {
        var (noProbe, _) = await SeedVideoBlobAsync(
            detectedContentType: null,
            videoExtractionStatus: MetadataStatuses.Failed,
            videoCodec: null);
        var (noCodec, _) = await SeedVideoBlobAsync(
            detectedContentType: null,
            videoExtractionStatus: MetadataStatuses.Completed,
            videoCodec: null);
        using var scope = _factory.Services.CreateScope();
        var service = Service(scope);

        Assert.Equal(DerivativeOutcome.NotEligible, await service.EnsureGeneratedAsync(noProbe));
        Assert.Equal(DerivativeOutcome.NotEligible, await service.EnsureGeneratedAsync(noCodec));
        Assert.Equal(0, _transcoder.Calls);
    }

    // ---- Happy path + idempotency ------------------------------------------

    [Fact]
    public async Task Eligible_Video_Generates_Publishes_And_Marks_Ready()
    {
        var (blobId, sha) = await SeedVideoBlobAsync();
        using var scope = _factory.Services.CreateScope();

        var outcome = await Service(scope).EnsureGeneratedAsync(blobId);

        Assert.Equal(DerivativeOutcome.Generated, outcome);
        Assert.True(_hls.Exists(sha));
        var row = await RowAsync(blobId);
        Assert.NotNull(row);
        Assert.Equal(VideoHlsStatuses.Ready, row!.Status);
        Assert.Null(row.ErrorCode);
        Assert.NotNull(row.ReadyAt);
        Assert.Equal(FfmpegVideoHlsTranscoder.Version, row.Version);
        // h264 + aac at 1080 → full stream copy, two renditions.
        var req = Assert.Single(_transcoder.Requests);
        Assert.True(req.CopyVideo);
        Assert.True(req.CopyAudio);
        Assert.True(req.HasAudio);
        Assert.True(req.IncludeLowRendition);
    }

    [Fact]
    public async Task Second_Call_Skips_Existing_Without_Retranscoding()
    {
        var (blobId, _) = await SeedVideoBlobAsync();
        using var scope = _factory.Services.CreateScope();
        var service = Service(scope);

        await service.EnsureGeneratedAsync(blobId);
        var second = await service.EnsureGeneratedAsync(blobId);

        Assert.Equal(DerivativeOutcome.SkippedExisting, second);
        Assert.Equal(1, _transcoder.Calls);
    }

    [Fact]
    public async Task Ready_Row_With_Wiped_Bytes_Regenerates()
    {
        var (blobId, sha) = await SeedVideoBlobAsync();
        using var scope = _factory.Services.CreateScope();
        var service = Service(scope);

        await service.EnsureGeneratedAsync(blobId);
        _hls.Delete(sha); // operator wiped the derived cache

        var outcome = await service.EnsureGeneratedAsync(blobId);

        Assert.Equal(DerivativeOutcome.Generated, outcome);
        Assert.True(_hls.Exists(sha));
        Assert.Equal(2, _transcoder.Calls);
    }

    // ---- Failures -----------------------------------------------------------

    [Fact]
    public async Task Transcode_Failure_Records_Failed_Row_And_Blocks_Retry_Without_Force()
    {
        var (blobId, sha) = await SeedVideoBlobAsync();
        _transcoder.NextResult = VideoHlsTranscodeResult.Fail(VideoHlsErrorCodes.TranscodeFailed);
        using var scope = _factory.Services.CreateScope();
        var service = Service(scope);

        var outcome = await service.EnsureGeneratedAsync(blobId);
        Assert.Equal(DerivativeOutcome.Failed, outcome);
        Assert.False(_hls.Exists(sha));
        var row = await RowAsync(blobId);
        Assert.Equal(VideoHlsStatuses.Failed, row!.Status);
        Assert.Equal(VideoHlsErrorCodes.TranscodeFailed, row.ErrorCode);

        // Recorded failure short-circuits (no retry storm)...
        _transcoder.NextResult = null;
        var retry = await service.EnsureGeneratedAsync(blobId);
        Assert.Equal(DerivativeOutcome.Failed, retry);
        Assert.Equal(1, _transcoder.Calls);

        // ...until an explicit force re-runs and succeeds.
        var forced = await service.EnsureGeneratedAsync(blobId, force: true);
        Assert.Equal(DerivativeOutcome.Generated, forced);
        Assert.Equal(2, _transcoder.Calls);
        Assert.True(_hls.Exists(sha));
        Assert.Equal(VideoHlsStatuses.Ready, (await RowAsync(blobId))!.Status);
    }

    // ---- Probe fallback -----------------------------------------------------

    [Fact]
    public async Task Unprobed_Blob_Uses_OnTheFly_Probe_For_The_Plan()
    {
        var (blobId, _) = await SeedVideoBlobAsync(
            videoExtractionStatus: MetadataStatuses.Pending,
            videoCodec: null, audioCodec: null, hasAudio: false, height: null);
        _probe.Result = new VideoMetadataExtractionResult
        {
            Status = MetadataStatuses.Completed,
            VideoCodec = "hevc",
            AudioCodec = "aac",
            HasAudio = true,
            Height = 2160,
        };
        using var scope = _factory.Services.CreateScope();

        var outcome = await Service(scope).EnsureGeneratedAsync(blobId);

        Assert.Equal(DerivativeOutcome.Generated, outcome);
        Assert.Equal(1, _probe.Calls);
        var req = Assert.Single(_transcoder.Requests);
        Assert.False(req.CopyVideo);  // hevc → encode
        Assert.True(req.CopyAudio);   // aac → copy
        Assert.True(req.HasAudio);
    }

    [Fact]
    public async Task Failed_OnTheFly_Probe_Records_Probe_Failed()
    {
        var (blobId, _) = await SeedVideoBlobAsync(
            videoExtractionStatus: MetadataStatuses.Pending);
        _probe.Result = VideoMetadataExtractionResult.ForStatus(
            MetadataStatuses.Failed, MetadataErrorCodes.ProbeFailed, 1);
        using var scope = _factory.Services.CreateScope();

        var outcome = await Service(scope).EnsureGeneratedAsync(blobId);

        Assert.Equal(DerivativeOutcome.Failed, outcome);
        Assert.Equal(0, _transcoder.Calls);
        var row = await RowAsync(blobId);
        Assert.Equal(VideoHlsStatuses.Failed, row!.Status);
        Assert.Equal(VideoHlsErrorCodes.ProbeFailed, row.ErrorCode);
    }
}

// ---------------------------------------------------------------------------
// Test fakes

// Fake transcoder: by default succeeds and materializes a complete ladder in
// the staging directory (like real ffmpeg); NextResult overrides ONE run.
public sealed class FakeHlsTranscoder : IVideoHlsTranscoder
{
    public List<VideoHlsTranscodeRequest> Requests { get; } = [];
    public int Calls => Requests.Count;
    public VideoHlsTranscodeResult? NextResult { get; set; }

    public Task<VideoHlsTranscodeResult> TranscodeAsync(
        VideoHlsTranscodeRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (NextResult is { } fixedResult)
        {
            NextResult = null;
            return Task.FromResult(fixedResult);
        }

        File.WriteAllText(Path.Combine(request.OutputDirectory, "master.m3u8"), "#EXTM3U");
        foreach (var name in request.IncludeLowRendition ? new[] { "high", "low" } : ["high"])
        {
            var dir = Path.Combine(request.OutputDirectory, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "stream.m3u8"), "#EXTM3U");
            File.WriteAllBytes(Path.Combine(dir, "init_0.mp4"), [0x01]);
            File.WriteAllBytes(Path.Combine(dir, "seg-0.m4s"), [0x02]);
        }
        return Task.FromResult(VideoHlsTranscodeResult.Ok);
    }
}

public sealed class FakeVideoProbe : IVideoMetadataExtractor
{
    public VideoMetadataExtractionResult Result { get; set; } =
        VideoMetadataExtractionResult.ForStatus(
            MetadataStatuses.Failed, MetadataErrorCodes.ProbeFailed, 1);

    public int Calls { get; private set; }

    public Task<VideoMetadataExtractionResult> ExtractAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}
