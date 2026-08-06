using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Files;

// Service-level tests for image -> thumbnail generation, the thumbnail open
// path (owner-safe + soft-delete-aware), and the no-thumbnail behaviour on
// non-images / corrupt images.
public sealed class FileThumbnailServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly LocalFileSystemBlobStorage _storage;
    private readonly BlobService _blobService;
    private readonly FileThumbnailService _thumbnails;
    private readonly FileItemService _files;

    public FileThumbnailServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(dbOptions);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-thumb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);

        var blobOptions = Options.Create(new BlobStorageOptions { RootPath = _storageRoot });
        _storage = new LocalFileSystemBlobStorage(blobOptions);
        _blobService = new BlobService(_storage, _db, TimeProvider.System);
        _thumbnails = new FileThumbnailService(
            _db, _blobService, _storage, new SyntheticVideoPosterProvider(),
            TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()));
        _files = new FileItemService(_db, _blobService, _thumbnails, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private async Task<User> SeedUserAsync(string email = "owner@example.com")
    {
        var u = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = "Owner",
            CreatedAt = DateTime.UtcNow,
        };
        _db.Users.Add(u);
        await _db.SaveChangesAsync();
        return u;
    }

    private static byte[] CreatePngBytes(int width, int height)
    {
        using var img = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static byte[] CreateJpegBytes(int width, int height)
    {
        using var img = new Image<Rgb24>(width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder());
        return ms.ToArray();
    }

    // Simulate a wiped derived cache (or a not-yet-generated derivative) so the
    // lazy EnsureAsync path would regenerate — this is where the retry gate acts.
    private async Task WipeThumbnailsAsync(Guid fileItemId)
    {
        var rows = await _db.FileThumbnails.Where(t => t.FileItemId == fileItemId).ToListAsync();
        _db.FileThumbnails.RemoveRange(rows);
        await _db.SaveChangesAsync();
    }

    private async Task SeedDiagnosticAsync(
        Guid fileItemId, string size, string status, DateTime? nextRetryAt = null)
    {
        _db.DerivativeDiagnostics.Add(new DerivativeDiagnostic
        {
            Id = Guid.NewGuid(),
            FileItemId = fileItemId,
            Size = ThumbnailSizes.Normalize(size),
            Status = status,
            AttemptCount = 1,
            FirstAttemptedAt = DateTime.UtcNow,
            LastAttemptedAt = DateTime.UtcNow,
            NextRetryAt = nextRetryAt,
            GeneratorVersion = 1,
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public void MediumPreviewMaxEdge_Defaults_To_1920_And_Clamps()
    {
        Assert.Equal(768, ThumbnailSizes.GetEdge(ThumbnailSizes.Small));
        Assert.Equal(1920, ThumbnailSizes.GetEdge(ThumbnailSizes.Medium));
        var defaults = new MediaDerivativesOptions();
        Assert.Equal((1280, 720), defaults.PosterSize);
        Assert.Equal((480, 270, 6, 2880, 270), defaults.VideoPreviewStripSize);
        Assert.Equal(768, defaults.EdgeFor(ThumbnailSizes.Small));

        Assert.Equal(1920, new MediaDerivativesOptions().EdgeFor(ThumbnailSizes.Medium));
        Assert.Equal(256, new MediaDerivativesOptions
        {
            MediumPreviewMaxEdge = 1,
        }.EdgeFor(ThumbnailSizes.Medium));
        Assert.Equal(8192, new MediaDerivativesOptions
        {
            MediumPreviewMaxEdge = 99999,
        }.EdgeFor(ThumbnailSizes.Medium));
    }

    [Fact]
    public async Task Medium_Preview_Uses_Configured_Max_Edge()
    {
        var owner = await SeedUserAsync();
        var jpeg = CreateJpegBytes(width: 1200, height: 600);
        var file = await _files.CreateAsync(
            owner.Id, null, "wide.jpg", "image/jpeg", new MemoryStream(jpeg));

        var thumbnails = new FileThumbnailService(
            _db, _blobService, _storage, new SyntheticVideoPosterProvider(),
            TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()),
            mediaOptions: Options.Create(new MediaDerivativesOptions
            {
                MediumPreviewMaxEdge = 300,
            }));

        var content = await thumbnails.EnsureAsync(file.Id, owner.Id, ThumbnailSizes.Medium);

        Assert.NotNull(content);
        var thumb = await _db.FileThumbnails.AsNoTracking()
            .SingleAsync(t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Medium);
        Assert.Equal(300, thumb.Width);
        Assert.Equal(150, thumb.Height);
    }

    [Fact]
    public async Task Medium_Regeneration_Replaces_Medium_Only()
    {
        var owner = await SeedUserAsync();
        var jpeg = CreateJpegBytes(width: 1200, height: 600);
        var file = await _files.CreateAsync(
            owner.Id, null, "wide.jpg", "image/jpeg", new MemoryStream(jpeg));
        Assert.True(await _db.FileThumbnails.AnyAsync(
            t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Small));

        Assert.NotNull(await _thumbnails.EnsureAsync(file.Id, owner.Id, ThumbnailSizes.Medium));
        var oldMedium = await _db.FileThumbnails.AsNoTracking()
            .SingleAsync(t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Medium);

        var thumbnails = new FileThumbnailService(
            _db, _blobService, _storage, new SyntheticVideoPosterProvider(),
            TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()),
            mediaOptions: Options.Create(new MediaDerivativesOptions
            {
                MediumPreviewMaxEdge = 300,
            }));
        var service = new MediumPreviewRegenerationService(
            _db, _blobService, thumbnails,
            Options.Create(new MediaDerivativesOptions { MediumPreviewMaxEdge = 300 }));

        var result = await service.RunAsync(new MediumPreviewRegenerationOptions());

        Assert.Equal(1, result.Stats.Cleared);
        Assert.Equal(1, result.Stats.Regenerated);
        Assert.True(await _db.FileThumbnails.AnyAsync(
            t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Small));
        var medium = await _db.FileThumbnails.AsNoTracking()
            .SingleAsync(t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Medium);
        Assert.NotEqual(oldMedium.BlobObjectId, medium.BlobObjectId);
        Assert.Equal(300, medium.Width);
        Assert.Equal(150, medium.Height);
        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.Size == ThumbnailSizes.Poster));
    }

    [Fact]
    public async Task Upload_PNG_Creates_Thumbnail_With_Aspect_Preserving_Dimensions()
    {
        var owner = await SeedUserAsync();
        var png = CreatePngBytes(width: 800, height: 400);

        var file = await _files.CreateAsync(
            owner.Id, null, "wide.png", "image/png", new MemoryStream(png));

        Assert.Equal(800, file.Width);
        Assert.Equal(400, file.Height);

        var thumb = await _db.FileThumbnails.AsNoTracking().SingleAsync(t => t.FileItemId == file.Id);
        Assert.Equal(ThumbnailSizes.Small, thumb.Size);
        // 800x400 scaled into the configured small box at aspect 2:1.
        Assert.Equal(ThumbnailSizes.GetEdge(ThumbnailSizes.Small), thumb.Width);
        Assert.Equal(384, thumb.Height);
    }

    [Fact]
    public async Task Upload_JPEG_Creates_Thumbnail_Row()
    {
        var owner = await SeedUserAsync();
        var jpeg = CreateJpegBytes(width: 600, height: 800);

        var file = await _files.CreateAsync(
            owner.Id, null, "tall.jpg", "image/jpeg", new MemoryStream(jpeg));

        var thumb = await _db.FileThumbnails.AsNoTracking().SingleAsync(t => t.FileItemId == file.Id);
        // 600x800 -> 576x768 (aspect 3:4).
        Assert.Equal(576, thumb.Width);
        Assert.Equal(ThumbnailSizes.GetEdge(ThumbnailSizes.Small), thumb.Height);
        Assert.InRange(thumb.Width, 1, ThumbnailSizes.GetEdge(ThumbnailSizes.Small));
        Assert.InRange(thumb.Height, 1, ThumbnailSizes.GetEdge(ThumbnailSizes.Small));
    }

    // ── Retry gate: on-the-fly EnsureAsync must not re-decode a derivative the
    //    diagnostics already mark as blocked (the "retry storm" fix). ───────────

    [Fact]
    public async Task EnsureAsync_Skips_Regeneration_When_Diagnostic_Is_FailedPermanent()
    {
        var owner = await SeedUserAsync();
        var file = await _files.CreateAsync(
            owner.Id, null, "p.jpg", "image/jpeg", new MemoryStream(CreateJpegBytes(600, 800)));
        await WipeThumbnailsAsync(file.Id); // lazy path would otherwise regenerate
        await SeedDiagnosticAsync(file.Id, ThumbnailSizes.Small, DerivativeStatuses.FailedPermanent);

        var content = await _thumbnails.EnsureAsync(file.Id, owner.Id, ThumbnailSizes.Small);

        Assert.Null(content); // gated — no on-the-fly attempt
        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Theory]
    [InlineData("not_eligible")]
    [InlineData("skipped")]
    public async Task EnsureAsync_Skips_Regeneration_For_Blocking_Statuses(string status)
    {
        var owner = await SeedUserAsync();
        var file = await _files.CreateAsync(
            owner.Id, null, "p.jpg", "image/jpeg", new MemoryStream(CreateJpegBytes(600, 800)));
        await WipeThumbnailsAsync(file.Id);
        await SeedDiagnosticAsync(file.Id, ThumbnailSizes.Small, status);

        Assert.Null(await _thumbnails.EnsureAsync(file.Id, owner.Id, ThumbnailSizes.Small));
        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task EnsureAsync_Regenerates_When_No_Blocking_Diagnostic()
    {
        var owner = await SeedUserAsync();
        var file = await _files.CreateAsync(
            owner.Id, null, "p.jpg", "image/jpeg", new MemoryStream(CreateJpegBytes(600, 800)));
        await WipeThumbnailsAsync(file.Id);
        // no diagnostic → the lazy path must still regenerate normally

        var content = await _thumbnails.EnsureAsync(file.Id, owner.Id, ThumbnailSizes.Small);

        Assert.NotNull(content);
        Assert.Equal(1, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task EnsureAsync_Skips_When_Transient_Backoff_Not_Elapsed()
    {
        var owner = await SeedUserAsync();
        var file = await _files.CreateAsync(
            owner.Id, null, "p.jpg", "image/jpeg", new MemoryStream(CreateJpegBytes(600, 800)));
        await WipeThumbnailsAsync(file.Id);
        await SeedDiagnosticAsync(file.Id, ThumbnailSizes.Small,
            DerivativeStatuses.FailedTransient, nextRetryAt: DateTime.UtcNow.AddHours(1));

        Assert.Null(await _thumbnails.EnsureAsync(file.Id, owner.Id, ThumbnailSizes.Small));
        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task EnsureAsync_Retries_When_Transient_Backoff_Elapsed()
    {
        var owner = await SeedUserAsync();
        var file = await _files.CreateAsync(
            owner.Id, null, "p.jpg", "image/jpeg", new MemoryStream(CreateJpegBytes(600, 800)));
        await WipeThumbnailsAsync(file.Id);
        await SeedDiagnosticAsync(file.Id, ThumbnailSizes.Small,
            DerivativeStatuses.FailedTransient, nextRetryAt: DateTime.UtcNow.AddHours(-1));

        Assert.NotNull(await _thumbnails.EnsureAsync(file.Id, owner.Id, ThumbnailSizes.Small));
        Assert.Equal(1, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task Upload_NonImage_Creates_No_Thumbnail()
    {
        var owner = await SeedUserAsync();
        var file = await _files.CreateAsync(
            owner.Id, null, "notes.txt", "text/plain",
            new MemoryStream(Encoding.UTF8.GetBytes("hello world")));

        Assert.Null(file.Width);
        Assert.Null(file.Height);
        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task Upload_Corrupt_Image_Succeeds_With_No_Thumbnail()
    {
        var owner = await SeedUserAsync();
        // Valid PNG magic followed by garbage. ImageSharp's Identify reports a
        // size on this header but full Decode throws — covers the
        // dimensions-set-but-thumbnail-fails path.
        var corrupt = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xff, 0xff, 0xff, 0xff };

        var file = await _files.CreateAsync(
            owner.Id, null, "broken.png", "image/png", new MemoryStream(corrupt));

        Assert.Equal(corrupt.LongLength, file.SizeBytes);
        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task Upload_Small_Image_Does_Not_Upscale()
    {
        var owner = await SeedUserAsync();
        // 100x100 already fits inside the small box: no upscale.
        var png = CreatePngBytes(width: 100, height: 100);

        var file = await _files.CreateAsync(
            owner.Id, null, "tiny.png", "image/png", new MemoryStream(png));

        var thumb = await _db.FileThumbnails.AsNoTracking().SingleAsync(t => t.FileItemId == file.Id);
        Assert.Equal(100, thumb.Width);
        Assert.Equal(100, thumb.Height);
    }

    [Fact]
    public async Task Forced_Small_Regeneration_Swaps_Row_And_Keeps_Refcounts_Correct()
    {
        var owner = await SeedUserAsync();
        var file = await _files.CreateAsync(
            owner.Id,
            null,
            "replace.png",
            "image/png",
            new MemoryStream(CreatePngBytes(1000, 500)));
        var before = await _db.FileThumbnails.AsNoTracking()
            .SingleAsync(t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Small);

        var differentQuality = new FileThumbnailService(
            _db,
            _blobService,
            _storage,
            new SyntheticVideoPosterProvider(),
            TimeProvider.System,
            NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()),
            mediaOptions: Options.Create(new MediaDerivativesOptions { SmallQuality = 65 }));

        var outcome = await differentQuality.RegenerateGalleryDerivativeAsync(
            file.Id, owner.Id, ThumbnailSizes.Small, force: true);

        Assert.Equal(GalleryDerivativeReplacementOutcome.Replaced, outcome);
        var after = await _db.FileThumbnails.AsNoTracking()
            .SingleAsync(t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Small);
        Assert.NotEqual(before.BlobObjectId, after.BlobObjectId);
        Assert.Equal(768, after.Width);
        Assert.Equal(384, after.Height);
        Assert.Equal(0, await _db.BlobObjects
            .Where(b => b.Id == before.BlobObjectId)
            .Select(b => b.ReferenceCount)
            .SingleAsync());
        Assert.Equal(1, await _db.BlobObjects
            .Where(b => b.Id == after.BlobObjectId)
            .Select(b => b.ReferenceCount)
            .SingleAsync());
    }

    [Fact]
    public async Task Failed_Forced_Regeneration_Preserves_Existing_Derivative()
    {
        var owner = await SeedUserAsync();
        var file = await _files.CreateAsync(
            owner.Id,
            null,
            "preserve.png",
            "image/png",
            new MemoryStream(CreatePngBytes(1000, 500)));
        var before = await _db.FileThumbnails.AsNoTracking()
            .SingleAsync(t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Small);

        var disabled = new FileThumbnailService(
            _db,
            _blobService,
            _storage,
            new SyntheticVideoPosterProvider(),
            TimeProvider.System,
            NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions { EnableThumbnails = false }));

        var outcome = await disabled.RegenerateGalleryDerivativeAsync(
            file.Id, owner.Id, ThumbnailSizes.Small, force: true);

        Assert.Equal(GalleryDerivativeReplacementOutcome.Failed, outcome);
        var after = await _db.FileThumbnails.AsNoTracking()
            .SingleAsync(t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Small);
        Assert.Equal(before.BlobObjectId, after.BlobObjectId);
        Assert.Equal(1, await _db.BlobObjects
            .Where(b => b.Id == before.BlobObjectId)
            .Select(b => b.ReferenceCount)
            .SingleAsync());
    }

    [Fact]
    public async Task Forced_Video_Regeneration_Safely_Replaces_Poster_And_Strip()
    {
        var owner = await SeedUserAsync();
        var file = await _files.CreateAsync(
            owner.Id,
            null,
            "clip.mp4",
            "video/mp4",
            new MemoryStream([0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70]));
        var firstService = new FileThumbnailService(
            _db,
            _blobService,
            _storage,
            new FixedVideoDerivativeProvider(0x11),
            TimeProvider.System,
            NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()));
        Assert.Equal(
            DerivativeOutcome.Generated,
            await firstService.EnsurePosterGeneratedAsync(file.Id, owner.Id));
        Assert.Equal(
            DerivativeOutcome.Generated,
            await firstService.EnsureVideoPreviewStripGeneratedAsync(file.Id, owner.Id));
        var before = await _db.FileThumbnails.AsNoTracking()
            .Where(t => t.FileItemId == file.Id)
            .ToDictionaryAsync(t => t.Size, t => t.BlobObjectId);

        var replacementService = new FileThumbnailService(
            _db,
            _blobService,
            _storage,
            new FixedVideoDerivativeProvider(0x22),
            TimeProvider.System,
            NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()));

        Assert.Equal(
            GalleryDerivativeReplacementOutcome.Replaced,
            await replacementService.RegenerateGalleryDerivativeAsync(
                file.Id, owner.Id, ThumbnailSizes.Poster, force: true));
        Assert.Equal(
            GalleryDerivativeReplacementOutcome.Replaced,
            await replacementService.RegenerateGalleryDerivativeAsync(
                file.Id, owner.Id, ThumbnailSizes.VideoPreviewStrip, force: true));

        var after = await _db.FileThumbnails.AsNoTracking()
            .Where(t => t.FileItemId == file.Id)
            .ToDictionaryAsync(t => t.Size);
        Assert.NotEqual(before[ThumbnailSizes.Poster], after[ThumbnailSizes.Poster].BlobObjectId);
        Assert.NotEqual(
            before[ThumbnailSizes.VideoPreviewStrip],
            after[ThumbnailSizes.VideoPreviewStrip].BlobObjectId);
        var defaults = new MediaDerivativesOptions();
        Assert.Equal(defaults.PosterSize.Width, after[ThumbnailSizes.Poster].Width);
        Assert.Equal(defaults.PosterSize.Height, after[ThumbnailSizes.Poster].Height);
        Assert.Equal(defaults.VideoPreviewStripSize.Width, after[ThumbnailSizes.VideoPreviewStrip].Width);
        Assert.Equal(defaults.VideoPreviewStripSize.Height, after[ThumbnailSizes.VideoPreviewStrip].Height);
        Assert.All(before.Values, oldBlobId =>
            Assert.Equal(
                0,
                _db.BlobObjects.Where(b => b.Id == oldBlobId)
                    .Select(b => b.ReferenceCount)
                    .Single()));
    }

    [Fact]
    public async Task Thumbnail_Blob_Is_Encoded_As_JPEG_And_Decodable()
    {
        var owner = await SeedUserAsync();
        var png = CreatePngBytes(width: 1000, height: 1000);
        var file = await _files.CreateAsync(
            owner.Id, null, "square.png", "image/png", new MemoryStream(png));

        var content = await _thumbnails.OpenAsync(file.Id, owner.Id, ThumbnailSizes.Small);

        Assert.NotNull(content);
        Assert.Equal(FileThumbnailService.ThumbnailMimeType, content!.MimeType);
        Assert.Equal(ThumbnailSizes.GetEdge(ThumbnailSizes.Small), content.Width);
        Assert.Equal(ThumbnailSizes.GetEdge(ThumbnailSizes.Small), content.Height);

        using var ms = new MemoryStream();
        await content.Content.CopyToAsync(ms);
        ms.Position = 0;
        var info = await Image.IdentifyAsync(ms);
        Assert.NotNull(info);
        Assert.Equal(ThumbnailSizes.GetEdge(ThumbnailSizes.Small), info!.Width);
    }

    [Fact]
    public async Task OpenAsync_Foreign_Owner_Returns_Null()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var png = CreatePngBytes(200, 200);
        var aliceFile = await _files.CreateAsync(
            alice.Id, null, "alice.png", "image/png", new MemoryStream(png));

        var content = await _thumbnails.OpenAsync(aliceFile.Id, bob.Id, ThumbnailSizes.Small);

        Assert.Null(content);
    }

    private sealed class FixedVideoDerivativeProvider(byte marker) : IVideoPosterProvider
    {
        public Task<VideoPosterResult?> TryGetPosterAsync(
            Func<CancellationToken, Task<Stream>> openBlobContent,
            CancellationToken cancellationToken)
            => Task.FromResult<VideoPosterResult?>(
                new(new MemoryStream([0xFF, 0xD8, marker, 0xFF, 0xD9]), VideoPosterSources.Ffmpeg));

        public Task<VideoPreviewStripResult?> TryGetPreviewStripAsync(
            Func<CancellationToken, Task<Stream>> openBlobContent,
            double? durationSeconds,
            CancellationToken cancellationToken)
            => Task.FromResult<VideoPreviewStripResult?>(
                new(
                    new MemoryStream([0xFF, 0xD8, marker, marker, 0xFF, 0xD9]),
                    new MediaDerivativesOptions().VideoPreviewStripSize.Width,
                    new MediaDerivativesOptions().VideoPreviewStripSize.Height,
                    new MediaDerivativesOptions().VideoPreviewStripSize.FrameCount));
    }

    [Fact]
    public async Task OpenAsync_SoftDeleted_File_Returns_Null()
    {
        var owner = await SeedUserAsync();
        var png = CreatePngBytes(200, 200);
        var file = await _files.CreateAsync(
            owner.Id, null, "x.png", "image/png", new MemoryStream(png));

        await _files.SoftDeleteAsync(owner.Id, file.Id);

        var content = await _thumbnails.OpenAsync(file.Id, owner.Id, ThumbnailSizes.Small);
        Assert.Null(content);
    }

    [Fact]
    public async Task OpenAsync_NonImage_Returns_Null()
    {
        var owner = await SeedUserAsync();
        var file = await _files.CreateAsync(
            owner.Id, null, "notes.txt", "text/plain",
            new MemoryStream(Encoding.UTF8.GetBytes("hello")));

        var content = await _thumbnails.OpenAsync(file.Id, owner.Id, ThumbnailSizes.Small);
        Assert.Null(content);
    }

    [Fact]
    public async Task OpenAsync_Unknown_Size_Returns_Null()
    {
        var owner = await SeedUserAsync();
        var png = CreatePngBytes(200, 200);
        var file = await _files.CreateAsync(
            owner.Id, null, "x.png", "image/png", new MemoryStream(png));

        var content = await _thumbnails.OpenAsync(file.Id, owner.Id, "huge");
        Assert.Null(content);
    }

    [Fact]
    public async Task Thumbnail_Blob_Reference_Count_Is_One_After_Upload()
    {
        var owner = await SeedUserAsync();
        var png = CreatePngBytes(300, 300);
        var file = await _files.CreateAsync(
            owner.Id, null, "x.png", "image/png", new MemoryStream(png));

        var thumb = await _db.FileThumbnails.AsNoTracking().SingleAsync(t => t.FileItemId == file.Id);
        var thumbBlob = await _db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == thumb.BlobObjectId);
        Assert.Equal(1, thumbBlob.ReferenceCount);
        Assert.NotEqual(file.BlobObjectId, thumb.BlobObjectId);
    }
}
