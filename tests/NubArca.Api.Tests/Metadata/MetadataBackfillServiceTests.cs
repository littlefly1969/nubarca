using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Metadata;

// Slice 55 — MetadataBackfillService re-extraction over existing blobs.
public sealed class MetadataBackfillServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly LocalFileSystemBlobStorage _storage;
    private readonly BlobService _blobService;
    private readonly FileThumbnailService _thumbnails;
    private readonly EmbeddedImageMetadataExtractor _extractor;
    private readonly FileItemService _files;
    private readonly MetadataBackfillService _backfill;

    public MetadataBackfillServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-backfill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);

        _storage = new LocalFileSystemBlobStorage(Options.Create(new BlobStorageOptions { RootPath = _storageRoot }));
        _blobService = new BlobService(_storage, _db, TimeProvider.System);
        _thumbnails = new FileThumbnailService(
            _db, _blobService, _storage, new SyntheticVideoPosterProvider(),
            TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()));
        _extractor = new EmbeddedImageMetadataExtractor();
        _files = new FileItemService(_db, _blobService, _thumbnails, TimeProvider.System, _extractor);
        _backfill = new MetadataBackfillService(_db, _files);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private async Task<User> SeedUserAsync()
    {
        var u = new User { Id = Guid.NewGuid(), Email = "o@example.com", DisplayName = "O", CreatedAt = DateTime.UtcNow };
        _db.Users.Add(u);
        await _db.SaveChangesAsync();
        return u;
    }

    private static byte[] Png(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private async Task<Guid> UploadImageAsync(Guid owner, string name, int dim)
    {
        var file = await _files.CreateAsync(owner, null, name, "image/png", new MemoryStream(Png(dim)));
        return file.BlobObjectId;
    }

    // Simulates a pre-slice-54 blob: extraction pending, no version, fields empty.
    private async Task MarkPendingLegacyAsync(Guid blobObjectId)
    {
        var meta = await _db.BlobMetadata.SingleAsync(m => m.BlobObjectId == blobObjectId);
        meta.ExtractionStatus = MetadataStatuses.Pending;
        meta.ExtractionVersion = null;
        meta.ExtractedAt = null;
        meta.CameraMake = null;
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task DryRun_Counts_Candidates_Without_Modifying_Rows()
    {
        var owner = (await SeedUserAsync()).Id;
        var blob = await UploadImageAsync(owner, "a.png", 10);
        await MarkPendingLegacyAsync(blob);

        var before = await _db.BlobMetadata.AsNoTracking().SingleAsync(m => m.BlobObjectId == blob);

        var result = await _backfill.RunAsync(new MetadataBackfillOptions { DryRun = true });

        Assert.True(result.DryRun);
        Assert.Equal(1, result.Examined);
        Assert.Equal(0, result.Processed);

        var after = await _db.BlobMetadata.AsNoTracking().SingleAsync(m => m.BlobObjectId == blob);
        Assert.Equal(MetadataStatuses.Pending, after.ExtractionStatus);
        Assert.Null(after.ExtractionVersion);
        Assert.Equal(before.ExtractedAt, after.ExtractedAt);
    }

    [Fact]
    public async Task Processes_Pending_Rows_And_Sets_Current_Version()
    {
        var owner = (await SeedUserAsync()).Id;
        var blob = await UploadImageAsync(owner, "a.png", 10);
        await MarkPendingLegacyAsync(blob);

        var result = await _backfill.RunAsync(new MetadataBackfillOptions());

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.Succeeded);

        var after = await _db.BlobMetadata.AsNoTracking().SingleAsync(m => m.BlobObjectId == blob);
        Assert.Equal(MetadataStatuses.Completed, after.ExtractionStatus);
        Assert.Equal(EmbeddedImageMetadataExtractor.Version, after.ExtractionVersion);
    }

    [Fact]
    public async Task Is_Idempotent_Second_Run_Processes_Nothing()
    {
        var owner = (await SeedUserAsync()).Id;
        await MarkPendingLegacyAsync(await UploadImageAsync(owner, "a.png", 10));
        await MarkPendingLegacyAsync(await UploadImageAsync(owner, "b.png", 11));

        var first = await _backfill.RunAsync(new MetadataBackfillOptions());
        Assert.Equal(2, first.Processed);

        // A fresh upload extracts at the current version on upload, so after the
        // first backfill everything is current and a re-run is a no-op.
        var second = await _backfill.RunAsync(new MetadataBackfillOptions());
        Assert.Equal(0, second.Examined);
        Assert.Equal(0, second.Processed);
    }

    [Fact]
    public async Task FailedOnly_Targets_Only_Failed_Rows()
    {
        var owner = (await SeedUserAsync()).Id;
        var failed = await UploadImageAsync(owner, "a.png", 10);
        var pending = await UploadImageAsync(owner, "b.png", 11);

        // One row is "failed", one is "pending" (legacy).
        var failedMeta = await _db.BlobMetadata.SingleAsync(m => m.BlobObjectId == failed);
        failedMeta.ExtractionStatus = MetadataStatuses.Failed;
        failedMeta.ExtractionVersion = EmbeddedImageMetadataExtractor.Version;
        await _db.SaveChangesAsync();
        await MarkPendingLegacyAsync(pending);

        var result = await _backfill.RunAsync(new MetadataBackfillOptions { FailedOnly = true });

        // Only the failed row is examined; the pending one is untouched.
        Assert.Equal(1, result.Examined);
        var pendingAfter = await _db.BlobMetadata.AsNoTracking().SingleAsync(m => m.BlobObjectId == pending);
        Assert.Equal(MetadataStatuses.Pending, pendingAfter.ExtractionStatus);
    }

    [Fact]
    public async Task Limit_Caps_The_Number_Processed()
    {
        var owner = (await SeedUserAsync()).Id;
        for (var i = 0; i < 3; i++)
        {
            await MarkPendingLegacyAsync(await UploadImageAsync(owner, $"f{i}.png", 10 + i));
        }

        var result = await _backfill.RunAsync(new MetadataBackfillOptions { Limit = 2 });

        Assert.Equal(2, result.Examined);
        Assert.Equal(2, result.Processed);
        // One legacy row remains pending.
        Assert.Equal(1, await _db.BlobMetadata.CountAsync(m => m.ExtractionStatus == MetadataStatuses.Pending));
    }

    [Fact]
    public async Task Log_Lines_Contain_Only_Counts_Not_Raw_Metadata()
    {
        var owner = (await SeedUserAsync()).Id;
        await MarkPendingLegacyAsync(await UploadImageAsync(owner, "a.png", 10));

        var lines = new List<string>();
        await _backfill.RunAsync(new MetadataBackfillOptions(), line => lines.Add(line));

        Assert.NotEmpty(lines);
        var joined = string.Join("\n", lines);
        // Numbers + the word "backfill" only — never extracted fields, paths,
        // sha, blob ids, or raw metadata keys.
        foreach (var needle in new[]
                 {
                     "DateTimeOriginal", "GpsLatitude", "Make", "Model", "Serial",
                     "RawMetadataJson", "StorageKey", "objects/", "sha256",
                 })
        {
            Assert.DoesNotContain(needle, joined, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("processed", joined);
    }
}
