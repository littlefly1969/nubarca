using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Jobs;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 100: end-to-end backend behaviour through the real store/row/diagnostics
// path. Builds the backfill with a chosen renderer so vips, fallback, and
// fallback-failure are all exercised against a real (SQLite) database +
// filesystem blob store.
public sealed class MediaDerivativeBackendDbTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly LocalFileSystemBlobStorage _storage;
    private readonly BlobService _blobService;
    private readonly MutableTimeProvider _clock;
    private readonly DerivativeDiagnosticsService _diagnostics;
    private readonly FileItemService _files;

    public MediaDerivativeBackendDbTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-backend-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);
        _storage = new LocalFileSystemBlobStorage(Options.Create(new BlobStorageOptions { RootPath = _storageRoot }));
        _clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero));
        _blobService = new BlobService(_storage, _db, _clock);
        _diagnostics = new DerivativeDiagnosticsService(_db, _clock);

        // Seeding uses an ImageSharp-only thumbnail service (generateSmallThumbnail:false
        // means it never renders during seeding anyway).
        var seedThumbs = new FileThumbnailService(
            _db, _blobService, _storage, new SyntheticVideoPosterProvider(),
            _clock, NullLogger<FileThumbnailService>.Instance, Options.Create(new ImageProcessingOptions()));
        _files = new FileItemService(_db, _blobService, seedThumbs, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch { /* best effort */ }
    }

    // Build a backfill whose FileThumbnailService renders through `renderer`.
    private MediaDerivativesBackfillService Backfill(ImageDerivativeRenderer renderer)
    {
        var thumbs = new FileThumbnailService(
            _db, _blobService, _storage, new SyntheticVideoPosterProvider(),
            _clock, NullLogger<FileThumbnailService>.Instance, Options.Create(new ImageProcessingOptions()),
            renderer, Options.Create(new MediaDerivativesOptions()));
        return new MediaDerivativesBackfillService(_db, thumbs, mediaLibrary: null, _diagnostics, _clock);
    }

    private static ImageSharpDerivativeBackend ImageSharp() => new(NullLogger<ImageSharpDerivativeBackend>.Instance);

    private static ImageDerivativeRenderer VipsRenderer(out VipsDerivativeBackend vips)
    {
        var runtime = new VipsRuntime(Options.Create(new MediaDerivativesOptions()), NullLogger<VipsRuntime>.Instance);
        vips = new VipsDerivativeBackend(runtime, NullLogger<VipsDerivativeBackend>.Instance);
        return new ImageDerivativeRenderer(
            ImageSharp(), vips,
            Options.Create(new MediaDerivativesOptions { ImageBackend = "vips" }),
            NullLogger<ImageDerivativeRenderer>.Instance);
    }

    private static ImageDerivativeRenderer FallbackRenderer()
        => new(
            ImageSharp(), new AlwaysFailingBackend(),
            Options.Create(new MediaDerivativesOptions { ImageBackend = "vips", FallbackToImageSharp = true }),
            NullLogger<ImageDerivativeRenderer>.Instance);

    private async Task<Guid> SeedUserAsync()
    {
        var u = new User { Id = Guid.NewGuid(), Email = "o@e.com", DisplayName = "O", CreatedAt = _clock.GetUtcNow().UtcDateTime };
        _db.Users.Add(u);
        await _db.SaveChangesAsync();
        return u.Id;
    }

    private async Task<FileItem> SeedImageAsync(Guid owner, string name, byte[] bytes)
    {
        var file = await _files.CreateAsync(owner, null, name, "image/png", new MemoryStream(bytes), generateSmallThumbnail: false);
        var meta = await _db.BlobMetadata.FirstAsync(m => m.BlobObjectId == file.BlobObjectId);
        meta.MediaCategory = MediaCategories.Image;
        meta.DetectedContentType = "image/png";
        meta.DetectedFormat = "PNG";
        await _db.SaveChangesAsync();
        return file;
    }

    private async Task<List<(string Size, int W, int H)>> ThumbsAsync(Guid fileItemId) =>
        await _db.FileThumbnails.AsNoTracking()
            .Where(t => t.FileItemId == fileItemId)
            .OrderBy(t => t.Size)
            .Select(t => new ValueTuple<string, int, int>(t.Size, t.Width, t.Height))
            .ToListAsync();

    [Fact]
    public async Task Vips_Backfill_Generates_Correct_Derivatives_And_Attributes_Backend()
    {
        var renderer = VipsRenderer(out var vips);
        if (!vips.IsAvailable) return; // native libvips unavailable on this RID

        var owner = await SeedUserAsync();
        var file = await SeedImageAsync(owner, "v.png", ImageFixtures.PlainPng(800, 400));

        var result = await Backfill(renderer).RunAsync(new MediaDerivativesBackfillOptions());

        var thumbs = await ThumbsAsync(file.Id);
        Assert.Equal(new[]
        {
            ("medium", 800, 400), // ≤1920 → native (no upscale)
            ("small", 768, 384),  // fit in the central small box
        }, thumbs);
        Assert.Equal(0, await _db.DerivativeDiagnostics.CountAsync());
        Assert.Equal(1, result.Stats.VipsImages);
        Assert.Equal(0, result.Stats.FallbackImages);
    }

    [Fact]
    public async Task Fallback_Generates_Via_ImageSharp_When_Preferred_Backend_Fails()
    {
        var owner = await SeedUserAsync();
        var file = await SeedImageAsync(owner, "f.png", ImageFixtures.PlainPng(300, 300));

        var result = await Backfill(FallbackRenderer()).RunAsync(new MediaDerivativesBackfillOptions());

        // ImageSharp fallback produced both derivatives — no diagnostic, fallback counted.
        Assert.Equal(2, (await ThumbsAsync(file.Id)).Count);
        Assert.Equal(0, await _db.DerivativeDiagnostics.CountAsync());
        Assert.Equal(1, result.Stats.ImageSharpImages);
        Assert.Equal(1, result.Stats.FallbackImages);
        Assert.Equal(0, result.Stats.VipsImages);
    }

    [Fact]
    public async Task Fallback_Failure_Records_Backend_And_FellBack_In_Diagnostic()
    {
        var owner = await SeedUserAsync();
        // Identify succeeds (valid header) but BOTH the failing preferred AND the
        // ImageSharp fallback cannot decode the body → a recorded decode failure.
        var file = await SeedImageAsync(owner, "bad.png", ImageFixtures.UndecodablePng(3));

        await Backfill(FallbackRenderer()).RunAsync(new MediaDerivativesBackfillOptions());

        var d = await _db.DerivativeDiagnostics.AsNoTracking()
            .FirstOrDefaultAsync(x => x.FileItemId == file.Id && x.Size == ThumbnailSizes.Small);
        Assert.NotNull(d);
        Assert.Equal(DerivativeStatuses.FailedPermanent, d!.Status);
        Assert.Equal(DerivativeErrorCodes.DecodeFailed, d.ErrorCode);
        // The diagnostic attributes the final backend + records that a fallback ran.
        Assert.Equal(DerivativeBackends.ImageSharp, d.Backend);
        Assert.Equal("fell_back_to_imagesharp", d.Message);
        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task Refcount_Stays_Clean_After_Backend_Failure()
    {
        var owner = await SeedUserAsync();
        var bad = await SeedImageAsync(owner, "bad.png", ImageFixtures.UndecodablePng(9));

        await Backfill(FallbackRenderer()).RunAsync(new MediaDerivativesBackfillOptions());

        var blob = await _db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == bad.BlobObjectId);
        Assert.Equal(1, blob.ReferenceCount); // only the FileItem reference
        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == bad.Id));
        Assert.False(await _db.BlobObjects.AnyAsync(b => b.ReferenceCount < 1)); // no orphan derived blob
    }

    private sealed class AlwaysFailingBackend : IImageDerivativeBackend
    {
        public string Name => DerivativeBackends.Vips;
        public bool IsAvailable => true;
        public Task<IReadOnlyList<RenderedDerivative?>> RenderAsync(
            ReadOnlyMemory<byte> source, IReadOnlyList<DerivativeRequest> requests, CancellationToken cancellationToken)
            => throw new ImageBackendException(DerivativeErrorCodes.DecodeFailed, "always fails");
    }
}
