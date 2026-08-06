using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using Xunit;

namespace NubArca.Api.Tests.Files;

public sealed class GalleryDerivativesRegenerationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly RecordingThumbnailService _thumbnails = new();
    private readonly GalleryDerivativesRegenerationService _service;
    private readonly Guid _owner = Guid.Parse("10000000-0000-0000-0000-000000000001");

    public GalleryDerivativesRegenerationServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _service = new GalleryDerivativesRegenerationService(
            _db,
            _thumbnails,
            Options.Create(new MediaOptions
            {
                VideoPosterProvider = "ffmpeg",
                FfmpegPath = "ffmpeg",
                VideoMetadataProvider = "ffprobe",
                FfprobePath = "ffprobe",
            }));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Resumes_From_Keyset_Checkpoint_And_Preserves_Phase_Order()
    {
        await SeedAsync();
        var options = new GalleryDerivativesRegenerationOptions
        {
            Sizes =
            [
                ThumbnailSizes.Small,
                ThumbnailSizes.Poster,
                ThumbnailSizes.VideoPreviewStrip,
            ],
            Force = true,
            BatchSize = 1,
        };

        var first = await _service.RunAsync(
            options,
            shouldYield: processed => processed >= 1);

        Assert.True(first.MoreWorkRemaining);
        Assert.Equal(ThumbnailSizes.Small, first.Phase);
        Assert.NotNull(first.LastFileItemId);
        Assert.Equal(1, first.Examined);

        var completed = await _service.RunAsync(
            options,
            checkpointJson: first.NextCheckpointJson);

        Assert.False(completed.MoreWorkRemaining);
        Assert.Equal(4, completed.Examined);
        Assert.Equal(4, completed.CreatedMissing);
        Assert.Equal(
            new[]
            {
                ThumbnailSizes.Small,
                ThumbnailSizes.Small,
                ThumbnailSizes.Poster,
                ThumbnailSizes.VideoPreviewStrip,
            },
            _thumbnails.Calls.Select(c => c.Size).ToArray());
        Assert.All(_thumbnails.Calls, call => Assert.True(call.Force));
    }

    [Fact]
    public async Task Rejects_Real_Video_Run_When_Ffmpeg_Provider_Is_Not_Configured()
    {
        var service = new GalleryDerivativesRegenerationService(
            _db,
            _thumbnails,
            Options.Create(new MediaOptions { VideoPosterProvider = "synthetic" }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunAsync(new GalleryDerivativesRegenerationOptions
            {
                Sizes = [ThumbnailSizes.Poster],
                Force = true,
            }));

        Assert.Contains("ffmpeg", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SeedAsync()
    {
        _db.Users.Add(new User
        {
            Id = _owner,
            Email = "gallery@example.com",
            DisplayName = "Gallery",
            CreatedAt = DateTime.UtcNow,
        });

        AddMedia(
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            MediaCategories.Image,
            "image/jpeg");
        AddMedia(
            Guid.Parse("20000000-0000-0000-0000-000000000002"),
            Guid.Parse("30000000-0000-0000-0000-000000000002"),
            MediaCategories.Image,
            "image/jpeg");
        AddMedia(
            Guid.Parse("20000000-0000-0000-0000-000000000003"),
            Guid.Parse("30000000-0000-0000-0000-000000000003"),
            MediaCategories.Video,
            "video/mp4");
        await _db.SaveChangesAsync();
    }

    private void AddMedia(
        Guid fileId,
        Guid blobId,
        string category,
        string detectedContentType)
    {
        _db.BlobObjects.Add(new BlobObject
        {
            Id = blobId,
            Sha256 = blobId.ToString("N").PadRight(64, '0'),
            SizeBytes = 1,
            StorageKey = $"objects/{blobId:N}",
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        });
        _db.FileItems.Add(new FileItem
        {
            Id = fileId,
            OwnerUserId = _owner,
            BlobObjectId = blobId,
            Name = $"{fileId:N}.bin",
            MimeType = detectedContentType,
            SizeBytes = 1,
            CreatedAt = DateTime.UtcNow,
        });
        _db.BlobMetadata.Add(new BlobMetadata
        {
            Id = Guid.NewGuid(),
            BlobObjectId = blobId,
            MediaCategory = category,
            DetectedContentType = detectedContentType,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private sealed class RecordingThumbnailService : IFileThumbnailService
    {
        public List<(Guid FileId, string Size, bool Force)> Calls { get; } = [];

        public Task<GalleryDerivativeReplacementOutcome> RegenerateGalleryDerivativeAsync(
            Guid fileItemId,
            Guid ownerUserId,
            string size,
            bool force,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileItemId, size, force));
            return Task.FromResult(
                GalleryDerivativeReplacementOutcome.CreatedMissing);
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
}
