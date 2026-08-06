using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Metadata;

// MetadataService owner-facing projection of probed video metadata.
public sealed class MetadataServiceVideoTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly MetadataService _service;

    public MetadataServiceVideoTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _service = new MetadataService(_db, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<(Guid owner, Guid fileId)> SeedVideoAsync(
        string videoStatus, Action<BlobMetadata>? mutate = null)
    {
        var owner = Guid.NewGuid();
        _db.Users.Add(new User { Id = owner, Email = "o@x.com", DisplayName = "O", CreatedAt = DateTime.UtcNow });

        var blobId = Guid.NewGuid();
        _db.BlobObjects.Add(new BlobObject
        {
            Id = blobId, Sha256 = new string('a', 64), SizeBytes = 100,
            StorageKey = "objects/aa/aa/x", CreatedAt = DateTime.UtcNow,
        });

        var meta = new BlobMetadata
        {
            Id = Guid.NewGuid(),
            BlobObjectId = blobId,
            SizeBytes = 100,
            MediaCategory = MediaCategories.Video,
            DetectedContentType = "video/mp4",
            DetectedFormat = "MP4",
            Width = 1920,
            Height = 1080,
            PixelCount = 1920L * 1080,
            ThumbnailStatus = MetadataStatuses.Skipped,
            ExtractionStatus = MetadataStatuses.Skipped,
            VideoExtractionStatus = videoStatus,
            DurationSeconds = 12.5,
            VideoCodec = "h264",
            AudioCodec = "aac",
            FrameRate = 29.97,
            VideoBitrate = 5_000_000,
            HasAudio = true,
            AudioChannels = 2,
            AudioSampleRate = 48_000,
            Rotation = 90,
            CreatedAt = DateTime.UtcNow,
        };
        mutate?.Invoke(meta);
        _db.BlobMetadata.Add(meta);

        var fileId = Guid.NewGuid();
        _db.FileItems.Add(new FileItem
        {
            Id = fileId, OwnerUserId = owner, BlobObjectId = blobId,
            Name = "clip.mp4", MimeType = "video/mp4", SizeBytes = 100,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return (owner, fileId);
    }

    [Fact]
    public async Task Exposes_Video_Block_When_Probe_Completed()
    {
        var (owner, fileId) = await SeedVideoAsync(MetadataStatuses.Completed);

        var response = await _service.GetFileMetadataAsync(owner, fileId);

        Assert.NotNull(response);
        var video = response!.Blob.Video;
        Assert.NotNull(video);
        Assert.Equal(12.5, video!.DurationSeconds);
        Assert.Equal("h264", video.VideoCodec);
        Assert.Equal("aac", video.AudioCodec);
        Assert.Equal(29.97, video.FrameRate);
        Assert.Equal(5_000_000L, video.VideoBitrate);
        Assert.True(video.HasAudio);
        Assert.Equal(2, video.AudioChannels);
        Assert.Equal(48_000, video.AudioSampleRate);
        Assert.Equal(90, video.Rotation);
        // Dimensions live on the shared Blob block.
        Assert.Equal(1920, response.Blob.Width);
        Assert.Equal(1080, response.Blob.Height);
    }

    [Fact]
    public async Task No_Video_Block_Before_Probe_Completes()
    {
        var (owner, fileId) = await SeedVideoAsync(MetadataStatuses.Pending);

        var response = await _service.GetFileMetadataAsync(owner, fileId);

        Assert.NotNull(response);
        Assert.Null(response!.Blob.Video);
    }
}
