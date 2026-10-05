using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Admin;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Storage;
using Xunit;

namespace NubArca.Api.Tests.Files;

public sealed class VideoColorRegenerationServiceTests : IDisposable
{
    private static readonly Guid Owner = Guid.Parse("10000000-0000-0000-0000-000000000001");

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly MarkedBlobService _blobs = new();
    private readonly RecordingThumbnailService _thumbnails = new();
    private readonly RecordingJobQueue _jobs = new();

    public VideoColorRegenerationServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.SeedBuiltInRoles();
        _db.Users.Add(new User { Id = Owner, Email = "video@example.com", DisplayName = "Video", CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private VideoColorRegenerationService Service() =>
        new(_db, _blobs, new MarkerColorProbe(), _thumbnails, _jobs);

    [Fact]
    public async Task An_Hdr_Video_Gets_New_Pictures_For_Every_Live_File_And_A_New_Ladder()
    {
        var (blob, files) = Seed("HDR", "hevc", liveFiles: 2, trashedFiles: 1, ladder: true);

        var result = await Service().RunAsync(new VideoColorRegenerationOptions(), default);

        Assert.Equal(new VideoColorRegenerationResult(1, 1, 4, 0, 1), result);
        Assert.Equal(
            files.Take(2).SelectMany(f => new[] { (f, ThumbnailSizes.Poster), (f, ThumbnailSizes.VideoPreviewStrip) }).ToHashSet(),
            _thumbnails.Calls.Select(c => (c.FileId, c.Size)).ToHashSet());
        Assert.All(_thumbnails.Calls, c => Assert.True(c.Force));
        var job = Assert.Single(_jobs.Enqueued);
        Assert.Equal(JobTypes.MediaVideoHlsGenerate, job.Type);
        Assert.Equal($"{JobTypes.MediaVideoHlsGenerate}:{blob:N}", job.IdempotencyKey);
        var payload = JsonSerializer.Deserialize<VideoHlsGenerateJobPayload>(job.PayloadJson)!;
        Assert.Equal(new VideoHlsGenerateJobPayload(blob, Force: true), payload);
    }

    [Fact]
    public async Task A_Video_Without_A_Ladder_Gets_None_Queued()
    {
        // The ladder is made on first playback, converted already.
        Seed("P3", "h264", liveFiles: 1, ladder: false);

        var result = await Service().RunAsync(new VideoColorRegenerationOptions(), default);

        Assert.Equal(new VideoColorRegenerationResult(1, 1, 2, 0, 0), result);
        Assert.Empty(_jobs.Enqueued);
    }

    [Fact]
    public async Task An_Ordinary_Video_Is_Left_Alone()
    {
        Seed("SDR", "h264", liveFiles: 1, ladder: true);

        var result = await Service().RunAsync(new VideoColorRegenerationOptions(), default);

        Assert.Equal(new VideoColorRegenerationResult(1, 0, 0, 0, 0), result);
        Assert.Empty(_thumbnails.Calls);
        Assert.Empty(_jobs.Enqueued);
    }

    [Fact]
    public async Task Only_A_Copied_H264_Ladder_Is_Redone_For_A_Format_Players_Cannot_Decode()
    {
        // Its colours are fine, so its pictures are; an H.264 ladder copied it
        // as it was. Another codec was encoded, so its ladder is fine too.
        var (h264, _) = Seed("444", "h264", liveFiles: 1, ladder: true);
        Seed("444", "mjpeg", liveFiles: 1, ladder: true);

        var result = await Service().RunAsync(new VideoColorRegenerationOptions(), default);

        Assert.Equal(new VideoColorRegenerationResult(2, 0, 0, 0, 1), result);
        Assert.Empty(_thumbnails.Calls);
        Assert.Equal($"{JobTypes.MediaVideoHlsGenerate}:{h264:N}", Assert.Single(_jobs.Enqueued).IdempotencyKey);
    }

    [Fact]
    public async Task A_Dry_Run_Counts_And_Changes_Nothing()
    {
        Seed("HDR", "hevc", liveFiles: 1, ladder: true);
        Seed("SDR", "h264", liveFiles: 1, ladder: true);

        var result = await Service().RunAsync(new VideoColorRegenerationOptions { DryRun = true }, default);

        Assert.Equal(new VideoColorRegenerationResult(2, 1, 0, 0, 0), result);
        Assert.Empty(_thumbnails.Calls);
        Assert.Empty(_jobs.Enqueued);
    }

    [Fact]
    public async Task Pages_Through_Every_Video_And_Stops_At_The_Limit()
    {
        for (var i = 0; i < 5; i++) Seed("HDR", "hevc", liveFiles: 1, ladder: false);
        Seed("HDR", "hevc", liveFiles: 1, ladder: false, category: MediaCategories.Image);

        var all = await Service().RunAsync(new VideoColorRegenerationOptions { PageSize = 2, DryRun = true }, default);
        var limited = await Service().RunAsync(new VideoColorRegenerationOptions { PageSize = 2, Limit = 3, DryRun = true }, default);

        Assert.Equal(5, all.Examined);
        Assert.Equal(3, limited.Examined);
    }

    private (Guid Blob, List<Guid> Files) Seed(
        string marker, string codec, int liveFiles, bool ladder, int trashedFiles = 0, string category = MediaCategories.Video)
    {
        var blob = Guid.NewGuid();
        _blobs.Content[blob] = Encoding.ASCII.GetBytes(marker);
        _db.BlobObjects.Add(new BlobObject
        {
            Id = blob, Sha256 = blob.ToString("N").PadRight(64, '0'), SizeBytes = 3,
            StorageKey = $"objects/{blob:N}", ReferenceCount = liveFiles + trashedFiles, CreatedAt = DateTime.UtcNow,
        });
        _db.BlobMetadata.Add(new BlobMetadata
        {
            Id = Guid.NewGuid(), BlobObjectId = blob, MediaCategory = category, DetectedContentType = "video/mp4",
            VideoCodec = codec, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var files = new List<Guid>();
        for (var i = 0; i < liveFiles + trashedFiles; i++)
        {
            var file = Guid.NewGuid();
            files.Add(file);
            _db.FileItems.Add(new FileItem
            {
                Id = file, OwnerUserId = Owner, BlobObjectId = blob, Name = $"{file:N}.mov", MimeType = "video/quicktime",
                SizeBytes = 3, CreatedAt = DateTime.UtcNow, DeletedAt = i < liveFiles ? null : DateTime.UtcNow,
            });
        }
        if (ladder)
        {
            _db.BlobHlsDerivatives.Add(new BlobHlsDerivative
            {
                Id = Guid.NewGuid(), BlobObjectId = blob, Status = VideoHlsStatuses.Ready, Version = 2,
                CreatedAt = DateTime.UtcNow, ReadyAt = DateTime.UtcNow,
            });
        }
        _db.SaveChanges();
        return (blob, files);
    }

    /// <summary>Reads the format from the marker each test video holds.</summary>
    private sealed class MarkerColorProbe : IVideoColorProbe
    {
        public async Task<VideoColorFormat> ProbeAsync(string inputPath, CancellationToken cancellationToken)
            => await File.ReadAllTextAsync(inputPath, cancellationToken) switch
            {
                "HDR" => new VideoColorFormat("yuv420p10le", "tv", "bt2020nc", "arib-std-b67", "bt2020"),
                "P3" => new VideoColorFormat("yuvj420p", "pc", "bt709", "bt709", "smpte432"),
                "444" => new VideoColorFormat("yuv444p", "tv", "bt709", "bt709", "bt709"),
                _ => new VideoColorFormat("yuv420p", "tv", "bt709", "bt709", "bt709"),
            };
    }

    private sealed class MarkedBlobService : IBlobService
    {
        public Dictionary<Guid, byte[]> Content { get; } = [];

        public Task<Stream> OpenContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(Content[blobObjectId]));

        public Task<BlobObject> StoreAsync(Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BlobStoreResult> StoreMeasuredAsync(Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BlobObject> StoreDerivedAsync(Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream?> OpenDerivedContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReleaseAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkPurgeEligibleIfUnreferencedAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BlobObject> AcquireExistingAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryRestoreDerivedFromOriginalAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingThumbnailService : IFileThumbnailService
    {
        public List<(Guid FileId, string Size, bool Force)> Calls { get; } = [];

        public Task<GalleryDerivativeReplacementOutcome> RegenerateGalleryDerivativeAsync(
            Guid fileItemId, Guid ownerUserId, string size, bool force, CancellationToken cancellationToken = default)
        {
            Calls.Add((fileItemId, size, force));
            return Task.FromResult(GalleryDerivativeReplacementOutcome.Replaced);
        }

        public Task<bool> TryGenerateSmallAsync(Guid fileItemId, Guid sourceBlobId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ThumbnailContent?> OpenAsync(Guid fileItemId, Guid ownerUserId, string size, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ThumbnailContent?> OpenVaultAsync(Guid fileItemId, Guid ownerUserId, Guid vaultId, string size, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ThumbnailContent?> EnsureAsync(Guid fileItemId, Guid ownerUserId, string size, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ImageDerivativesResult> EnsureImageDerivativesAsync(Guid fileItemId, Guid ownerUserId, IReadOnlyCollection<string> sizes, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<DerivativeOutcome> EnsurePosterGeneratedAsync(Guid fileItemId, Guid ownerUserId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<DerivativeOutcome> EnsureVideoPreviewStripGeneratedAsync(Guid fileItemId, Guid ownerUserId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed record EnqueuedJob(string Type, string PayloadJson, string? IdempotencyKey);

    private sealed class RecordingJobQueue : IJobQueue
    {
        public List<EnqueuedJob> Enqueued { get; } = [];

        public Task<BackgroundJob> EnqueueAsync<TPayload>(
            string type, TPayload payload, int? maxAttempts = null, int? priority = null,
            string? idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(new EnqueuedJob(type, JsonSerializer.Serialize(payload), idempotencyKey));
            return Task.FromResult(new BackgroundJob { Id = Guid.NewGuid(), Type = type });
        }

        public Task<JobQueueSnapshot> GetSnapshotAsync(int recentLimit = 20, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> RequestCancellationAsync(Guid jobId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<AdminJobPage> ListAdminJobsAsync(AdminJobFilter filter, int page, int pageSize, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<AdminJobSummary?> GetAdminJobAsync(Guid jobId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
