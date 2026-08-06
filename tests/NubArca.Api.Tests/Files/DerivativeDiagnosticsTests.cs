using System.Text;
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

// Slice 99: durable derivative-failure diagnostics, retry policy, and operator
// visibility. Directly constructs the generation + diagnostics + backfill graph
// (mirrors FileThumbnailServiceTests) so each failure mode is exercised
// deterministically, with a mutable clock for the backoff/retry assertions.
public sealed class DerivativeDiagnosticsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly LocalFileSystemBlobStorage _storage;
    private readonly BlobService _blobService;
    private readonly MutableTimeProvider _clock;
    private readonly FileThumbnailService _thumbnails;
    private readonly DerivativeDiagnosticsService _diagnostics;
    private readonly MediaDerivativesBackfillService _backfill;
    private readonly FileItemService _files;

    public DerivativeDiagnosticsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);
        _storage = new LocalFileSystemBlobStorage(Options.Create(new BlobStorageOptions { RootPath = _storageRoot }));
        _clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero));
        _blobService = new BlobService(_storage, _db, _clock);
        _diagnostics = new DerivativeDiagnosticsService(_db, _clock);
        (_thumbnails, _backfill, _files) = Build(new ImageProcessingOptions());
    }

    private (FileThumbnailService, MediaDerivativesBackfillService, FileItemService) Build(ImageProcessingOptions options)
    {
        var thumbs = new FileThumbnailService(
            _db, _blobService, _storage, new SyntheticVideoPosterProvider(),
            _clock, NullLogger<FileThumbnailService>.Instance, Options.Create(options));
        var backfill = new MediaDerivativesBackfillService(
            _db, thumbs, mediaLibrary: null, _diagnostics, _clock);
        var files = new FileItemService(_db, _blobService, thumbs, _clock);
        return (thumbs, backfill, files);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch { /* best effort */ }
    }

    // ---- fixtures ----------------------------------------------------------

    private async Task<Guid> SeedUserAsync(string email = "owner@example.com")
    {
        var u = new User { Id = Guid.NewGuid(), Email = email, DisplayName = "Owner", CreatedAt = _clock.GetUtcNow().UtcDateTime };
        _db.Users.Add(u);
        await _db.SaveChangesAsync();
        return u.Id;
    }

    // Creates a FileItem with NO thumbnails, then forces its BlobMetadata to look
    // like an image candidate (so the backfill attempts it regardless of what
    // the bytes actually sniff as). Returns the FileItem.
    private async Task<FileItem> SeedImageCandidateAsync(
        Guid ownerId, string name, byte[] bytes, string contentType = "image/jpeg", string detectedFormat = "JPEG")
    {
        var file = await _files.CreateAsync(
            ownerId, null, name, contentType, new MemoryStream(bytes), generateSmallThumbnail: false);
        var meta = await _db.BlobMetadata.FirstOrDefaultAsync(m => m.BlobObjectId == file.BlobObjectId);
        if (meta is null)
        {
            meta = new BlobMetadata
            {
                Id = Guid.NewGuid(),
                BlobObjectId = file.BlobObjectId,
                SizeBytes = file.SizeBytes,
                ExtractionStatus = MetadataStatuses.Completed,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            };
            _db.BlobMetadata.Add(meta);
        }
        meta.MediaCategory = MediaCategories.Image;
        meta.DetectedContentType = contentType;
        meta.DetectedFormat = detectedFormat;
        await _db.SaveChangesAsync();
        return file;
    }

    private static byte[] ValidPng(int seed) => ImageFixtures.PlainPng(16 + seed, 16 + seed);

    // Identify reads dimensions, full decode throws → decode_failed.
    private static byte[] DecodeFailingPng(int seed) => ImageFixtures.UndecodablePng(seed);

    private Task<DerivativeDiagnostic?> DiagAsync(Guid fileItemId, string size) =>
        _db.DerivativeDiagnostics.AsNoTracking()
            .FirstOrDefaultAsync(d => d.FileItemId == fileItemId && d.Size == size);

    // ---- success path ------------------------------------------------------

    [Fact]
    public async Task Valid_Image_Generates_Thumbnails_And_Records_No_Diagnostic()
    {
        var owner = await SeedUserAsync();
        var file = await SeedImageCandidateAsync(owner, "ok.png", ValidPng(3), "image/png", "PNG");

        await _backfill.RunAsync(new MediaDerivativesBackfillOptions());

        var sizes = await _db.FileThumbnails.AsNoTracking()
            .Where(t => t.FileItemId == file.Id).Select(t => t.Size).OrderBy(s => s).ToListAsync();
        Assert.Equal(new[] { "medium", "small" }, sizes);
        Assert.Equal(0, await _db.DerivativeDiagnostics.CountAsync());
    }

    // ---- failure recording -------------------------------------------------

    [Fact]
    public async Task Decode_Failure_Records_DecodeFailed_Permanent_For_Each_Size()
    {
        var owner = await SeedUserAsync();
        var file = await SeedImageCandidateAsync(owner, "broken.png", DecodeFailingPng(1), "image/png", "PNG");

        await _backfill.RunAsync(new MediaDerivativesBackfillOptions());

        foreach (var size in new[] { ThumbnailSizes.Small, ThumbnailSizes.Medium })
        {
            var d = await DiagAsync(file.Id, size);
            Assert.NotNull(d);
            Assert.Equal(DerivativeStatuses.FailedPermanent, d!.Status);
            Assert.Equal(DerivativeErrorCodes.DecodeFailed, d.ErrorCode);
            Assert.Equal("image/png", d.DetectedContentType);
            Assert.Equal(1, d.AttemptCount);
            Assert.Null(d.NextRetryAt);
        }
        // No fake thumbnail rows for a failure.
        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task Unidentifiable_Bytes_Record_UnsupportedFormat_Permanent()
    {
        var owner = await SeedUserAsync();
        var file = await SeedImageCandidateAsync(
            owner, "notimage.bin", Encoding.UTF8.GetBytes("definitely not an image at all"),
            "image/tiff", "TIFF");

        await _backfill.RunAsync(new MediaDerivativesBackfillOptions());

        var d = await DiagAsync(file.Id, ThumbnailSizes.Small);
        Assert.NotNull(d);
        Assert.Equal(DerivativeStatuses.FailedPermanent, d!.Status);
        Assert.Equal(DerivativeErrorCodes.UnsupportedFormat, d.ErrorCode);
        // The detected format snapshot lets the operator aggregate "how many TIFF".
        Assert.Equal("image/tiff", d.DetectedContentType);
    }

    [Fact]
    public async Task TooLarge_Input_Bytes_Record_TooLargeBytes_Permanent()
    {
        var owner = await SeedUserAsync();
        var file = await SeedImageCandidateAsync(owner, "big.png", ValidPng(8), "image/png", "PNG");

        // Rebuild with a tiny input-byte cap so the valid PNG is rejected.
        var (_, tinyBackfill, _) = Build(new ImageProcessingOptions { MaxThumbnailInputBytes = 8 });
        await tinyBackfill.RunAsync(new MediaDerivativesBackfillOptions());

        var d = await DiagAsync(file.Id, ThumbnailSizes.Small);
        Assert.NotNull(d);
        Assert.Equal(DerivativeStatuses.FailedPermanent, d!.Status);
        Assert.Equal(DerivativeErrorCodes.TooLargeBytes, d.ErrorCode);
        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task Source_Bytes_Missing_Records_SourceBlobMissing_Transient()
    {
        var owner = await SeedUserAsync();
        var file = await SeedImageCandidateAsync(owner, "gone.png", ValidPng(5), "image/png", "PNG");

        // Wipe the physical bytes but keep every DB row — the backfill should
        // detect the missing source and record a TRANSIENT diagnostic (a restore
        // can fix it), never a permanent failure.
        foreach (var f in Directory.EnumerateFiles(_storageRoot, "*", SearchOption.AllDirectories))
        {
            File.Delete(f);
        }

        await _backfill.RunAsync(new MediaDerivativesBackfillOptions());

        var d = await DiagAsync(file.Id, ThumbnailSizes.Small);
        Assert.NotNull(d);
        Assert.Equal(DerivativeStatuses.FailedTransient, d!.Status);
        Assert.Equal(DerivativeErrorCodes.SourceBlobMissing, d.ErrorCode);
        Assert.NotNull(d.NextRetryAt);
    }

    // ---- clearing / superseding on success --------------------------------

    [Fact]
    public async Task Successful_Generation_Clears_A_Due_Failure_Diagnostic()
    {
        var owner = await SeedUserAsync();
        var file = await SeedImageCandidateAsync(owner, "recovers.png", ValidPng(2), "image/png", "PNG");

        // A prior transient failure already due for retry (NextRetryAt in the past).
        await _diagnostics.RecordAsync(
            file.Id, ThumbnailSizes.Small, DerivativeStatuses.FailedTransient,
            DerivativeErrorCodes.StorageError, "image/png", "PNG",
            DerivativeBackends.ImageSharp, DerivativeGenerators.ImageVersion);
        await _db.DerivativeDiagnostics
            .Where(d => d.FileItemId == file.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.NextRetryAt, _clock.GetUtcNow().UtcDateTime.AddMinutes(-1)));

        await _backfill.RunAsync(new MediaDerivativesBackfillOptions());

        // Thumbnail now exists and the stale diagnostic is gone (req #7).
        Assert.True(await _db.FileThumbnails.AnyAsync(t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Small));
        Assert.Null(await DiagAsync(file.Id, ThumbnailSizes.Small));
    }

    [Fact]
    public async Task PruneResolved_Supersedes_Diagnostic_When_Thumbnail_Exists()
    {
        var owner = await SeedUserAsync();
        var file = await SeedImageCandidateAsync(owner, "lazy.png", ValidPng(4), "image/png", "PNG");

        // Simulate the lazy endpoint having produced the small thumbnail (which
        // does not touch diagnostics), leaving a stale permanent diagnostic.
        Assert.NotNull(await _thumbnails.EnsureAsync(file.Id, owner, ThumbnailSizes.Small));
        await _diagnostics.RecordAsync(
            file.Id, ThumbnailSizes.Small, DerivativeStatuses.FailedPermanent,
            DerivativeErrorCodes.DecodeFailed, "image/png", "PNG",
            DerivativeBackends.ImageSharp, DerivativeGenerators.ImageVersion);

        var removed = await _diagnostics.PruneResolvedAsync();

        Assert.Equal(1, removed);
        Assert.Null(await DiagAsync(file.Id, ThumbnailSizes.Small));
    }

    // ---- retry policy ------------------------------------------------------

    [Fact]
    public async Task Permanent_Failure_Is_Skipped_By_Default_And_Reattempted_With_RetryFailed()
    {
        var owner = await SeedUserAsync();
        var file = await SeedImageCandidateAsync(owner, "perm.png", DecodeFailingPng(7), "image/png", "PNG");

        await _backfill.RunAsync(new MediaDerivativesBackfillOptions());
        var first = await DiagAsync(file.Id, ThumbnailSizes.Small);
        Assert.Equal(1, first!.AttemptCount);

        // Default re-run: the blocking permanent diagnostic excludes the file —
        // no new attempt (AttemptCount unchanged, no re-decode).
        var skipRun = await _backfill.RunAsync(new MediaDerivativesBackfillOptions());
        Assert.Equal(0, skipRun.Stats.ImagesProcessed);
        Assert.Equal(1, (await DiagAsync(file.Id, ThumbnailSizes.Small))!.AttemptCount);

        // Forced retry: the file is attempted again (AttemptCount increments).
        await _backfill.RunAsync(new MediaDerivativesBackfillOptions { RetryFailed = true });
        Assert.Equal(2, (await DiagAsync(file.Id, ThumbnailSizes.Small))!.AttemptCount);
    }

    [Fact]
    public async Task Transient_Backoff_Blocks_Until_Due_Then_Default_Run_Retries()
    {
        var owner = await SeedUserAsync();
        var file = await SeedImageCandidateAsync(owner, "transient.png", ValidPng(6), "image/png", "PNG");

        // Pre-record a transient failure on a VALID image, with backoff in the
        // future, so the default backfill must skip it for now.
        await _diagnostics.RecordAsync(
            file.Id, ThumbnailSizes.Small, DerivativeStatuses.FailedTransient,
            DerivativeErrorCodes.StorageError, "image/png", "PNG",
            DerivativeBackends.ImageSharp, DerivativeGenerators.ImageVersion);
        var pre = await DiagAsync(file.Id, ThumbnailSizes.Small);
        Assert.NotNull(pre!.NextRetryAt);
        Assert.True(pre.NextRetryAt > _clock.GetUtcNow().UtcDateTime);

        var blocked = await _backfill.RunAsync(new MediaDerivativesBackfillOptions());
        Assert.Equal(0, blocked.Stats.ImagesProcessed);
        Assert.False(await _db.FileThumbnails.AnyAsync(t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Small));

        // Advance past the backoff: now the default run retries and succeeds.
        _clock.Advance(TimeSpan.FromHours(1));
        await _backfill.RunAsync(new MediaDerivativesBackfillOptions());
        Assert.True(await _db.FileThumbnails.AnyAsync(t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Small));
        Assert.Null(await DiagAsync(file.Id, ThumbnailSizes.Small));
    }

    [Fact]
    public async Task Recorder_Transient_Sets_Increasing_Backoff_And_Attempts()
    {
        var owner = await SeedUserAsync();
        var file = await SeedImageCandidateAsync(owner, "b.png", ValidPng(9), "image/png", "PNG");

        var start = _clock.GetUtcNow().UtcDateTime;
        await _diagnostics.RecordAsync(
            file.Id, ThumbnailSizes.Small, DerivativeStatuses.FailedTransient,
            DerivativeErrorCodes.StorageError, "image/png", "PNG", DerivativeBackends.ImageSharp, 1);
        var a = await DiagAsync(file.Id, ThumbnailSizes.Small);
        Assert.Equal(1, a!.AttemptCount);
        Assert.Equal(start.AddMinutes(15), a.NextRetryAt);

        await _diagnostics.RecordAsync(
            file.Id, ThumbnailSizes.Small, DerivativeStatuses.FailedTransient,
            DerivativeErrorCodes.StorageError, "image/png", "PNG", DerivativeBackends.ImageSharp, 1);
        var b = await DiagAsync(file.Id, ThumbnailSizes.Small);
        Assert.Equal(2, b!.AttemptCount);
        Assert.Equal(start.AddMinutes(30), b.NextRetryAt);
        Assert.Equal(a.FirstAttemptedAt, b.FirstAttemptedAt); // first-attempt preserved
    }

    // ---- cancellation ------------------------------------------------------

    [Fact]
    public async Task Cancellation_Does_Not_Record_A_Permanent_Failure()
    {
        var owner = await SeedUserAsync();
        var file = await SeedImageCandidateAsync(owner, "cancel.png", DecodeFailingPng(11), "image/png", "PNG");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _backfill.RunAsync(new MediaDerivativesBackfillOptions(), cancellationToken: cts.Token));

        // The cancelled run recorded nothing — the file is NOT marked as a
        // permanent failure; a later real attempt is still free to record it.
        Assert.Equal(0, await _db.DerivativeDiagnostics.CountAsync());
        await _backfill.RunAsync(new MediaDerivativesBackfillOptions());
        Assert.Equal(DerivativeStatuses.FailedPermanent,
            (await DiagAsync(file.Id, ThumbnailSizes.Small))!.Status);
    }

    // ---- storage cleanliness ----------------------------------------------

    [Fact]
    public async Task Refcount_Stays_Clean_After_Failures()
    {
        var owner = await SeedUserAsync();
        var bad = await SeedImageCandidateAsync(owner, "bad.png", DecodeFailingPng(13), "image/png", "PNG");
        // 900px so small (768) resizes but medium (≤1920) stays native — two
        // distinct derived blobs rather than a dedup of one.
        var good = await SeedImageCandidateAsync(owner, "good.png", ImageFixtures.PlainPng(900, 900), "image/png", "PNG");

        await _backfill.RunAsync(new MediaDerivativesBackfillOptions());

        // The failed source blob keeps exactly its single FileItem reference and
        // created no derived blob.
        var badBlob = await _db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == bad.BlobObjectId);
        Assert.Equal(1, badBlob.ReferenceCount);
        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == bad.Id));
        Assert.Equal(2, await _db.FileThumbnails.CountAsync(t => t.FileItemId == good.Id));
        // No refcount leak: a failed generation that releases its derived blob
        // must never leave a zero/negative-ref blob behind. Every blob here
        // (source + derived) is referenced at least once.
        Assert.False(await _db.BlobObjects.AnyAsync(b => b.ReferenceCount < 1));
    }

    // ---- slicing safety ----------------------------------------------------

    [Fact]
    public async Task Slicing_Records_Diagnostics_Without_Looping_On_Failed_Items()
    {
        var owner = await SeedUserAsync();
        await SeedImageCandidateAsync(owner, "f1.png", DecodeFailingPng(21), "image/png", "PNG");
        var good = await SeedImageCandidateAsync(owner, "g1.png", ValidPng(7), "image/png", "PNG");
        await SeedImageCandidateAsync(owner, "f2.png", DecodeFailingPng(22), "image/png", "PNG");

        // Yield after every single item; loop slices like the worker would.
        string? checkpoint = null;
        var slices = 0;
        var totalExamined = 0;
        for (var i = 0; i < 50; i++)
        {
            var result = await _backfill.RunAsync(
                new MediaDerivativesBackfillOptions(),
                checkpointJson: checkpoint,
                shouldYield: processed => processed >= 1);
            slices++;
            totalExamined += result.Examined;
            if (!result.MoreWorkRemaining) break;
            checkpoint = result.NextCheckpointJson;
        }

        // Terminated (did not loop forever), the good image generated, and each
        // failed image recorded exactly one diagnostic per size.
        Assert.True(slices < 50, "backfill slicing did not terminate");
        Assert.True(totalExamined <= 6, $"unexpected re-processing of failed items: examined {totalExamined}");
        Assert.Equal(2, await _db.FileThumbnails.CountAsync(t => t.FileItemId == good.Id));
        Assert.Equal(4, await _db.DerivativeDiagnostics.CountAsync()); // 2 failed × (small+medium)
        Assert.True(await _db.DerivativeDiagnostics.AsNoTracking()
            .AllAsync(d => d.Status == DerivativeStatuses.FailedPermanent));
    }

    // ---- aggregation -------------------------------------------------------

    [Fact]
    public async Task Summary_Breaks_Down_By_Status_Code_And_Format()
    {
        var owner = await SeedUserAsync();
        await SeedImageCandidateAsync(owner, "t1.tif", Encoding.UTF8.GetBytes("xx not tiff 1"), "image/tiff", "TIFF");
        await SeedImageCandidateAsync(owner, "t2.tif", Encoding.UTF8.GetBytes("yy not tiff 2"), "image/tiff", "TIFF");
        await SeedImageCandidateAsync(owner, "j1.png", DecodeFailingPng(31), "image/jpeg", "JPEG");

        await _backfill.RunAsync(new MediaDerivativesBackfillOptions());

        var summary = await _diagnostics.SummariseAsync();
        var small = summary.Sizes.Single(s => s.Size == ThumbnailSizes.Small);
        Assert.Equal(3, small.FailedPermanent);
        Assert.Equal(2, small.ByErrorCode.Single(c => c.ErrorCode == DerivativeErrorCodes.UnsupportedFormat).Count);
        Assert.Equal(1, small.ByErrorCode.Single(c => c.ErrorCode == DerivativeErrorCodes.DecodeFailed).Count);
        Assert.Equal(2, small.TopFormats.Single(f => f.DetectedContentType == "image/tiff").Count);
        Assert.NotNull(summary.LastFailureAt);
    }
}
