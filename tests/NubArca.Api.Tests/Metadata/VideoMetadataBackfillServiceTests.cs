using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using Xunit;

namespace NubArca.Api.Tests.Metadata;

// VideoMetadataBackfillService probing over existing video blobs. Uses a fake
// IVideoMetadataExtractor — no real ffprobe binary required.
public sealed class VideoMetadataBackfillServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly LocalFileSystemBlobStorage _storage;
    private readonly BlobService _blobService;
    private readonly FileThumbnailService _thumbnails;
    private readonly FakeVideoMetadataExtractor _extractor;
    private readonly FileItemService _files;
    private readonly VideoMetadataBackfillService _backfill;

    public VideoMetadataBackfillServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-vbackfill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);

        _storage = new LocalFileSystemBlobStorage(Options.Create(new BlobStorageOptions { RootPath = _storageRoot }));
        _blobService = new BlobService(_storage, _db, TimeProvider.System);
        _thumbnails = new FileThumbnailService(
            _db, _blobService, _storage, new SyntheticVideoPosterProvider(),
            TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()));
        _extractor = new FakeVideoMetadataExtractor();
        _files = new FileItemService(
            _db, _blobService, _thumbnails, TimeProvider.System,
            embeddedExtractor: new EmbeddedImageMetadataExtractor(),
            videoMetadataExtractor: _extractor);
        _backfill = new VideoMetadataBackfillService(_db, _files);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private async Task<Guid> SeedUserAsync()
    {
        var u = new User { Id = Guid.NewGuid(), Email = "o@example.com", DisplayName = "O", CreatedAt = DateTime.UtcNow };
        _db.Users.Add(u);
        await _db.SaveChangesAsync();
        return u.Id;
    }

    private async Task<Guid> UploadVideoAsync(Guid owner, string name)
    {
        // Arbitrary bytes with a video MIME → MediaCategory=video (no real
        // signature needed for the backfill, which keys off MediaCategory).
        var file = await _files.CreateAsync(owner, null, name, "video/mp4", new MemoryStream(new byte[64]));
        return file.BlobObjectId;
    }

    private async Task<Guid> UploadImageAsync(Guid owner, string name)
    {
        var file = await _files.CreateAsync(owner, null, name, "image/png", new MemoryStream(PngBytes()));
        return file.BlobObjectId;
    }

    private static byte[] PngBytes()
    {
        using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(4, 4);
        using var ms = new MemoryStream();
        img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        return ms.ToArray();
    }

    [Fact]
    public async Task Probes_Pending_Video_And_Maps_Fields()
    {
        var owner = await SeedUserAsync();
        var blob = await UploadVideoAsync(owner, "v.mp4");
        _extractor.Result = new VideoMetadataExtractionResult
        {
            Status = MetadataStatuses.Completed,
            Version = FfprobeVideoMetadataExtractor.Version,
            Width = 1920,
            Height = 1080,
            DurationSeconds = 12.5,
            VideoCodec = "h264",
            AudioCodec = "aac",
            HasAudio = true,
            FrameRate = 29.97,
            VideoBitrate = 5_000_000,
            AudioChannels = 2,
            AudioSampleRate = 48_000,
            Rotation = 90,
            CreationTime = new DateTime(2023, 5, 6, 7, 8, 9, DateTimeKind.Utc),
        };

        var result = await _backfill.RunAsync(new MetadataBackfillOptions());

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.Completed);

        var m = await _db.BlobMetadata.AsNoTracking().SingleAsync(x => x.BlobObjectId == blob);
        Assert.Equal(MetadataStatuses.Completed, m.VideoExtractionStatus);
        Assert.Equal(FfprobeVideoMetadataExtractor.Version, m.VideoExtractionVersion);
        Assert.Equal(1920, m.Width);
        Assert.Equal(1080, m.Height);
        Assert.Equal(1920L * 1080, m.PixelCount);
        Assert.Equal(12.5, m.DurationSeconds);
        Assert.Equal("h264", m.VideoCodec);
        Assert.Equal("aac", m.AudioCodec);
        Assert.True(m.HasAudio);
        Assert.Equal(29.97, m.FrameRate);
        Assert.Equal(5_000_000L, m.VideoBitrate);
        Assert.Equal(2, m.AudioChannels);
        Assert.Equal(48_000, m.AudioSampleRate);
        Assert.Equal(90, m.Rotation);
        Assert.Equal(new DateTime(2023, 5, 6, 7, 8, 9, DateTimeKind.Utc), m.DateTaken);

        // The container date flows into the denormalized effective capture date.
        var eff = await _db.FileItems.AsNoTracking().Where(f => f.BlobObjectId == blob)
            .Select(f => f.EffectiveDateTaken).FirstAsync();
        Assert.Equal(new DateTime(2023, 5, 6, 7, 8, 9, DateTimeKind.Utc), eff);
    }

    [Fact]
    public async Task Ignores_Image_Blobs()
    {
        var owner = await SeedUserAsync();
        await UploadImageAsync(owner, "a.png");
        _extractor.Result = Completed();

        var result = await _backfill.RunAsync(new MetadataBackfillOptions());

        Assert.Equal(0, result.Examined);
    }

    [Fact]
    public async Task Is_Idempotent_On_Second_Run()
    {
        var owner = await SeedUserAsync();
        await UploadVideoAsync(owner, "v.mp4");
        _extractor.Result = Completed();

        var first = await _backfill.RunAsync(new MetadataBackfillOptions());
        Assert.Equal(1, first.Processed);

        var second = await _backfill.RunAsync(new MetadataBackfillOptions());
        Assert.Equal(0, second.Examined);
    }

    [Fact]
    public async Task DryRun_Counts_Without_Modifying()
    {
        var owner = await SeedUserAsync();
        var blob = await UploadVideoAsync(owner, "v.mp4");
        _extractor.Result = Completed();

        var result = await _backfill.RunAsync(new MetadataBackfillOptions { DryRun = true });

        Assert.True(result.DryRun);
        Assert.Equal(1, result.Examined);
        var m = await _db.BlobMetadata.AsNoTracking().SingleAsync(x => x.BlobObjectId == blob);
        Assert.Equal(MetadataStatuses.Pending, m.VideoExtractionStatus);
        Assert.Null(m.VideoExtractionVersion);
    }

    [Fact]
    public async Task Skipped_Result_Drops_Out_Of_Candidates()
    {
        var owner = await SeedUserAsync();
        await UploadVideoAsync(owner, "v.mp4");
        // e.g. audio-only / no video stream.
        _extractor.Result = VideoMetadataExtractionResult.ForStatus(
            MetadataStatuses.Skipped, MetadataErrorCodes.UnsupportedFormat, FfprobeVideoMetadataExtractor.Version);

        var first = await _backfill.RunAsync(new MetadataBackfillOptions());
        Assert.Equal(1, first.Processed);
        Assert.Equal(1, first.Skipped);

        var second = await _backfill.RunAsync(new MetadataBackfillOptions());
        Assert.Equal(0, second.Examined);
    }

    [Fact]
    public async Task FailedOnly_Targets_Only_Failed_Rows()
    {
        var owner = await SeedUserAsync();
        var failedBlob = await UploadVideoAsync(owner, "fail.mp4");
        await UploadVideoAsync(owner, "pending.mp4");

        var m = await _db.BlobMetadata.SingleAsync(x => x.BlobObjectId == failedBlob);
        m.VideoExtractionStatus = MetadataStatuses.Failed;
        m.VideoExtractionVersion = FfprobeVideoMetadataExtractor.Version;
        await _db.SaveChangesAsync();
        _extractor.Result = Completed();

        var result = await _backfill.RunAsync(new MetadataBackfillOptions { FailedOnly = true });

        Assert.Equal(1, result.Examined);
    }

    [Fact]
    public async Task Log_Lines_Are_Counts_Only()
    {
        var owner = await SeedUserAsync();
        await UploadVideoAsync(owner, "v.mp4");
        _extractor.Result = Completed();

        var lines = new List<string>();
        await _backfill.RunAsync(new MetadataBackfillOptions(), line => lines.Add(line));

        var joined = string.Join("\n", lines);
        foreach (var needle in new[] { "StorageKey", "objects/", "sha256", "BlobObjectId", ".mp4" })
        {
            Assert.DoesNotContain(needle, joined, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("video metadata backfill", joined);
    }

    private static VideoMetadataExtractionResult Completed()
        => new()
        {
            Status = MetadataStatuses.Completed,
            Version = FfprobeVideoMetadataExtractor.Version,
            Width = 640,
            Height = 480,
            DurationSeconds = 3.0,
            VideoCodec = "h264",
        };
}

// Fake extractor returning a fixed result regardless of input.
internal sealed class FakeVideoMetadataExtractor : IVideoMetadataExtractor
{
    public VideoMetadataExtractionResult Result { get; set; } =
        VideoMetadataExtractionResult.ForStatus(MetadataStatuses.Skipped, null, FfprobeVideoMetadataExtractor.Version);

    public Task<VideoMetadataExtractionResult> ExtractAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent, CancellationToken cancellationToken)
        => Task.FromResult(Result);
}
