using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Video;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-01: eligibility, idempotency, atomicity and failure classification of
// one segmentation attempt. A fake segmenter stands in for FFmpeg, so no real
// binary and no real media are needed.
public sealed class VideoSemanticSegmentationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly BlobService _blobs;
    private readonly FileItemService _files;
    private readonly FakeVideoSemanticSegmenter _segmenter = new();
    private readonly VideoSemanticSegmentationOptions _options = new()
    {
        Enabled = true,
        SegmentationVersion = 1,
        MinimumSegmentSeconds = 2,
        TargetSegmentSeconds = 8,
        MaximumSegmentSeconds = 60,
    };

    public VideoSemanticSegmentationServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-vsem-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);

        var storage = new LocalFileSystemBlobStorage(
            Options.Create(new BlobStorageOptions { RootPath = _storageRoot }));
        _blobs = new BlobService(storage, _db, TimeProvider.System);
        var thumbnails = new FileThumbnailService(
            _db, _blobs, storage, new SyntheticVideoPosterProvider(),
            TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()));
        _files = new FileItemService(
            _db, _blobs, thumbnails, TimeProvider.System,
            embeddedExtractor: new EmbeddedImageMetadataExtractor(),
            videoMetadataExtractor: new NoopVideoMetadataExtractor());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private VideoSemanticSegmentationService NewService(
        IVideoSemanticSegmenter? segmenter = null, AppDbContext? db = null)
        => new(
            db ?? _db, _blobs, segmenter ?? _segmenter, Options.Create(_options),
            TimeProvider.System, NullLogger<VideoSemanticSegmentationService>.Instance);

    // ---- seeding -----------------------------------------------------------

    private async Task<Guid> SeedUserAsync()
    {
        var user = new User
        {
            Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@example.com",
            DisplayName = "O", CreatedAt = DateTime.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<(Guid BlobId, Guid FileId)> SeedVideoAsync(
        Guid owner, double? durationSeconds = 60, string status = MetadataStatuses.Completed,
        string? codec = "h264", string category = MediaCategories.Video, byte[]? bytes = null)
    {
        var file = await _files.CreateAsync(
            owner, null, $"v-{Guid.NewGuid():N}.mp4", "video/mp4", new MemoryStream(bytes ?? new byte[64]));

        var meta = await _db.BlobMetadata.SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
        meta.MediaCategory = category;
        meta.VideoExtractionStatus = status;
        meta.VideoExtractionVersion = 1;
        meta.DurationSeconds = durationSeconds;
        meta.VideoCodec = codec;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        return (file.BlobObjectId, file.Id);
    }

    private async Task MoveToVaultAsync(Guid ownerUserId, Guid fileId)
    {
        var vault = await _db.PrivateVaults.FirstOrDefaultAsync(v => v.OwnerUserId == ownerUserId);
        if (vault is null)
        {
            vault = new PrivateVault
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerUserId, DisplayName = "Private",
                PasswordHash = "x", EncryptionMode = PrivateVaultEncryptionModes.None,
                CreatedAt = DateTime.UtcNow,
            };
            _db.PrivateVaults.Add(vault);
            await _db.SaveChangesAsync();
        }

        var file = await _db.FileItems.IgnoreQueryFilters().SingleAsync(f => f.Id == fileId);
        file.PrivateVaultId = vault.Id;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private Task<VideoSemanticIndex?> LoadIndexAsync(Guid blobId, int version)
        => _db.VideoSemanticIndexes.AsNoTracking()
            .FirstOrDefaultAsync(i => i.BlobObjectId == blobId && i.SegmentationVersion == version);

    // ---- happy path ---------------------------------------------------------

    [Fact]
    public async Task Builds_And_Persists_A_Complete_Manifest()
    {
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner, durationSeconds: 60);
        _segmenter.Candidates = [12.0, 30.0, 45.0];

        var outcome = await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticSegmentationOutcomeKind.Completed, outcome.Kind);
        Assert.Null(outcome.ErrorCode);

        var index = await LoadIndexAsync(blobId, 1);
        Assert.NotNull(index);
        Assert.Equal(AiArtifactStatuses.Completed, index!.Status);
        Assert.Equal(60_000, index.DurationMilliseconds);
        Assert.Equal(4, index.SegmentCount);
        Assert.Equal(1, index.AttemptCount);
        Assert.NotNull(index.CompletedAt);
        Assert.False(index.IsPermanentFailure);

        var segments = await _db.VideoSemanticSegments.AsNoTracking()
            .Where(s => s.VideoSemanticIndexId == index.Id)
            .OrderBy(s => s.SegmentIndex).ToListAsync();
        Assert.Equal(new long[] { 0, 12_000, 30_000, 45_000 },
            segments.Select(s => s.StartMilliseconds).ToArray());
        Assert.Equal(60_000, segments[^1].EndMilliseconds);

        var samples = await _db.VideoSemanticSamples.AsNoTracking()
            .Where(s => segments.Select(x => x.Id).Contains(s.VideoSemanticSegmentId)).ToListAsync();
        Assert.Equal(index.SampleCount, samples.Count);
        Assert.All(samples, s => Assert.True(s.TimestampMilliseconds >= 0));
    }

    [Fact]
    public async Task Manifest_Stores_No_Owner_Specific_Field()
    {
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner);
        _segmenter.Candidates = [20.0];

        await NewService().ProcessBlobAsync(blobId, 1);

        // The entity shape is the guarantee: there is nowhere to put an owner,
        // a FileItem, a person, a filename, a path or a storage key.
        var properties = _db.Model.FindEntityType(typeof(VideoSemanticIndex))!
            .GetProperties().Select(p => p.Name)
            .Concat(_db.Model.FindEntityType(typeof(VideoSemanticSegment))!.GetProperties().Select(p => p.Name))
            .Concat(_db.Model.FindEntityType(typeof(VideoSemanticSample))!.GetProperties().Select(p => p.Name))
            .ToList();

        Assert.DoesNotContain("OwnerUserId", properties);
        Assert.DoesNotContain("FileItemId", properties);
        Assert.DoesNotContain("PersonId", properties);
        Assert.DoesNotContain("Name", properties);
        Assert.DoesNotContain("StorageKey", properties);
        Assert.DoesNotContain("Path", properties);
    }

    // ---- eligibility --------------------------------------------------------

    [Fact]
    public async Task Non_Video_Blob_Is_Permanently_Skipped()
    {
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner, category: MediaCategories.Image);

        var outcome = await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticSegmentationOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(VideoSemanticErrorCodes.UnsupportedInput, outcome.ErrorCode);
        var index = await LoadIndexAsync(blobId, 1);
        Assert.True(index!.IsPermanentFailure);
        Assert.Equal(AiArtifactStatuses.Skipped, index.Status);
        Assert.Equal(0, await _db.VideoSemanticSegments.CountAsync());
    }

    [Fact]
    public async Task Missing_Metadata_Row_Is_A_Retryable_Failure()
    {
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner);
        var meta = await _db.BlobMetadata.SingleAsync(m => m.BlobObjectId == blobId);
        _db.BlobMetadata.Remove(meta);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var outcome = await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticSegmentationOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(VideoSemanticErrorCodes.MetadataMissing, outcome.ErrorCode);
        var index = await LoadIndexAsync(blobId, 1);
        Assert.False(index!.IsPermanentFailure);   // metadata arrives later → retry
    }

    [Fact]
    public async Task Metadata_Not_Yet_Probed_Is_A_Retryable_Failure_Not_A_Skip()
    {
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner, status: MetadataStatuses.Pending);

        var outcome = await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticSegmentationOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(VideoSemanticErrorCodes.MetadataMissing, outcome.ErrorCode);
        Assert.False((await LoadIndexAsync(blobId, 1))!.IsPermanentFailure);
    }

    // A NEGATIVE duration is already impossible to persist
    // (ck_blob_metadata_duration_non_negative), so the reachable invalid cases
    // are "absent", "zero" and "rounds to zero milliseconds".
    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(0.0004d)]
    public async Task Invalid_Duration_Is_Permanently_Skipped(double? duration)
    {
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner, durationSeconds: duration);

        var outcome = await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticSegmentationOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(VideoSemanticErrorCodes.InvalidDuration, outcome.ErrorCode);
    }

    // ---- capacity ----------------------------------------------------------
    // Both segment limits are hard: MaximumSegmentsPerVideo × MaximumSegmentSeconds
    // bounds the representable duration. At exactly the bound everything still
    // fits; one millisecond beyond it is a permanent per-version skip.

    [Fact]
    public async Task Duration_At_Exact_Capacity_Succeeds_With_All_Limits_Preserved()
    {
        _options.MaximumSegmentsPerVideo = 4;                  // capacity: 4 × 60 s
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner, durationSeconds: 240);

        var outcome = await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticSegmentationOutcomeKind.Completed, outcome.Kind);
        var index = await LoadIndexAsync(blobId, 1);
        Assert.Equal(240_000, index!.DurationMilliseconds);
        Assert.True(index.SegmentCount <= _options.MaximumSegmentsPerVideo);

        var segments = await _db.VideoSemanticSegments.AsNoTracking()
            .Where(s => s.VideoSemanticIndexId == index.Id).ToListAsync();
        Assert.All(segments, s => Assert.True(
            s.EndMilliseconds - s.StartMilliseconds <= _options.MaximumSegmentMilliseconds));
        Assert.Equal(240_000, segments.Max(s => s.EndMilliseconds));
    }

    [Fact]
    public async Task Duration_One_Millisecond_Beyond_Capacity_Is_Permanently_Skipped()
    {
        _options.MaximumSegmentsPerVideo = 4;                  // capacity: 240 000 ms
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner, durationSeconds: 240.001);

        var outcome = await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticSegmentationOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(VideoSemanticErrorCodes.SegmentationCapacityExceeded, outcome.ErrorCode);
        Assert.Equal(0, _segmenter.Calls);                     // rejected before FFmpeg

        var index = await LoadIndexAsync(blobId, 1);
        Assert.Equal(AiArtifactStatuses.Skipped, index!.Status);
        Assert.True(index.IsPermanentFailure);
        Assert.Equal(0, await _db.VideoSemanticSegments.CountAsync());
        Assert.Equal(0, await _db.VideoSemanticSamples.CountAsync());
    }

    [Fact]
    public async Task Capacity_Skip_Is_Not_Retried()
    {
        _options.MaximumSegmentsPerVideo = 4;
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner, durationSeconds: 240.001);

        await NewService().ProcessBlobAsync(blobId, 1);
        _db.ChangeTracker.Clear();
        _segmenter.Calls = 0;

        var second = await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticSegmentationOutcomeKind.AlreadyTerminal, second.Kind);
        Assert.Equal(VideoSemanticErrorCodes.SegmentationCapacityExceeded, second.ErrorCode);
        Assert.Equal(0, _segmenter.Calls);
    }

    [Fact]
    public async Task Blob_Without_A_Video_Stream_Is_Permanently_Skipped()
    {
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner, codec: null);

        var outcome = await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticSegmentationOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(VideoSemanticErrorCodes.NoVideoStream, outcome.ErrorCode);
    }

    [Fact]
    public async Task Duplicate_References_To_One_Blob_Share_A_Single_Manifest()
    {
        var owner = await SeedUserAsync();
        var (blobId, fileId) = await SeedVideoAsync(owner);

        // A dedup upload of identical bytes: a second FileItem on the SAME blob.
        var second = await _files.CreateAsync(
            owner, null, "copy.mp4", "video/mp4", new MemoryStream(new byte[64]));
        Assert.Equal(blobId, second.BlobObjectId);
        Assert.NotEqual(fileId, second.Id);

        _segmenter.Candidates = [20.0];
        var first = await NewService().ProcessBlobAsync(blobId, 1);
        var again = await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticSegmentationOutcomeKind.Completed, first.Kind);
        Assert.Equal(VideoSemanticSegmentationOutcomeKind.AlreadyTerminal, again.Kind);
        Assert.Equal(1, await _db.VideoSemanticIndexes.CountAsync(i => i.BlobObjectId == blobId));
    }

    [Fact]
    public async Task Mixed_Normal_And_Vault_References_Still_Produce_One_Manifest()
    {
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner);
        var vaulted = await _files.CreateAsync(
            owner, null, "secret.mp4", "video/mp4", new MemoryStream(new byte[64]));
        Assert.Equal(blobId, vaulted.BlobObjectId);
        await MoveToVaultAsync(owner, vaulted.Id);

        _segmenter.Candidates = [20.0];
        var outcome = await NewService().ProcessBlobAsync(blobId, 1);

        // The normal reference makes the blob eligible; the manifest names no
        // FileItem, so it cannot expose the vaulted one either.
        Assert.Equal(VideoSemanticSegmentationOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(1, await _db.VideoSemanticIndexes.CountAsync(i => i.BlobObjectId == blobId));
    }

    [Fact]
    public async Task Vault_Only_References_Are_Skipped()
    {
        var owner = await SeedUserAsync();
        var (blobId, fileId) = await SeedVideoAsync(owner);
        await MoveToVaultAsync(owner, fileId);

        var outcome = await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticSegmentationOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(VideoSemanticErrorCodes.NoEligibleReference, outcome.ErrorCode);
        Assert.Equal(0, await _db.VideoSemanticSegments.CountAsync());
    }

    [Fact]
    public async Task Deleted_Or_Excluded_References_Are_Skipped()
    {
        var owner = await SeedUserAsync();
        var (blobId, fileId) = await SeedVideoAsync(owner);
        var file = await _db.FileItems.SingleAsync(f => f.Id == fileId);
        file.MediaLibraryState = MediaLibraryState.Excluded;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var outcome = await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticErrorCodes.NoEligibleReference, outcome.ErrorCode);
    }

    // ---- idempotency + versions ---------------------------------------------

    [Fact]
    public async Task Same_Version_Rerun_Is_A_No_Op_And_Never_Duplicates()
    {
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner);
        _segmenter.Candidates = [20.0];

        await NewService().ProcessBlobAsync(blobId, 1);
        var segmentsAfterFirst = await _db.VideoSemanticSegments.CountAsync();
        _segmenter.Calls = 0;

        var second = await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticSegmentationOutcomeKind.AlreadyTerminal, second.Kind);
        Assert.Equal(0, _segmenter.Calls);          // no FFmpeg on a completed blob
        Assert.Equal(segmentsAfterFirst, await _db.VideoSemanticSegments.CountAsync());
    }

    [Fact]
    public async Task Failed_Attempt_Is_Retried_And_Rebuilt()
    {
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner);

        _segmenter.Failure = VideoSemanticErrorCodes.ProcessTimeout;
        var first = await NewService().ProcessBlobAsync(blobId, 1);
        Assert.Equal(VideoSemanticSegmentationOutcomeKind.Failed, first.Kind);
        var afterFailure = await LoadIndexAsync(blobId, 1);
        Assert.Equal(AiArtifactStatuses.Failed, afterFailure!.Status);
        Assert.False(afterFailure.IsPermanentFailure);
        Assert.Equal(1, afterFailure.AttemptCount);
        Assert.Equal(0, await _db.VideoSemanticSegments.CountAsync());

        _db.ChangeTracker.Clear();
        _segmenter.Failure = null;
        _segmenter.Candidates = [20.0];
        var second = await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticSegmentationOutcomeKind.Completed, second.Kind);
        var rebuilt = await LoadIndexAsync(blobId, 1);
        Assert.Equal(AiArtifactStatuses.Completed, rebuilt!.Status);
        Assert.Equal(2, rebuilt.AttemptCount);
        Assert.Equal(1, await _db.VideoSemanticIndexes.CountAsync(i => i.BlobObjectId == blobId));
    }

    [Fact]
    public async Task Permanent_Skip_Is_Not_Retried()
    {
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner, codec: null);

        await NewService().ProcessBlobAsync(blobId, 1);
        _db.ChangeTracker.Clear();
        _segmenter.Calls = 0;

        var second = await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticSegmentationOutcomeKind.AlreadyTerminal, second.Kind);
        Assert.Equal(0, _segmenter.Calls);
    }

    [Fact]
    public async Task Versions_Coexist_And_A_New_Version_Never_Overwrites_An_Old_One()
    {
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner);

        _segmenter.Candidates = [20.0];
        await NewService().ProcessBlobAsync(blobId, 1);
        _db.ChangeTracker.Clear();

        _segmenter.Candidates = [10.0, 40.0];
        await NewService().ProcessBlobAsync(blobId, 2);

        var v1 = await LoadIndexAsync(blobId, 1);
        var v2 = await LoadIndexAsync(blobId, 2);
        Assert.NotNull(v1);
        Assert.NotNull(v2);
        Assert.NotEqual(v1!.Id, v2!.Id);
        Assert.Equal(2, v1.SegmentCount);
        Assert.Equal(3, v2.SegmentCount);
        Assert.Equal(v1.SegmentCount + v2.SegmentCount, await _db.VideoSemanticSegments.CountAsync());
    }

    // ---- atomicity + cancellation ------------------------------------------

    [Fact]
    public async Task Exception_Before_Commit_Leaves_No_Completed_Partial_Manifest()
    {
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner);
        _segmenter.Candidates = [10.0, 20.0, 30.0, 40.0];

        // A DbContext whose SaveChanges fails once the segment rows are being
        // written: the whole transaction must roll back.
        await using var failing = new FailingSaveDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        var outcome = await NewService(db: failing).ProcessBlobAsync(blobId, 1);

        Assert.Equal(VideoSemanticSegmentationOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(VideoSemanticErrorCodes.Database, outcome.ErrorCode);

        // Nothing completed, no orphan children.
        var index = await LoadIndexAsync(blobId, 1);
        Assert.NotEqual(AiArtifactStatuses.Completed, index?.Status);
        Assert.Equal(0, await _db.VideoSemanticSegments.CountAsync());
        Assert.Equal(0, await _db.VideoSemanticSamples.CountAsync());
    }

    [Fact]
    public async Task Cancellation_Stays_Cancellation_And_Writes_Nothing()
    {
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner);
        _segmenter.ThrowCancelled = true;

        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewService().ProcessBlobAsync(blobId, 1, cts.Token));

        Assert.Null(await LoadIndexAsync(blobId, 1));
        Assert.Equal(0, await _db.VideoSemanticIndexes.CountAsync());
    }

    [Fact]
    public async Task No_Persistent_Frame_Derivative_Is_Created()
    {
        var owner = await SeedUserAsync();
        var (blobId, _) = await SeedVideoAsync(owner);
        var thumbnailsBefore = await _db.FileThumbnails.CountAsync();
        var blobsBefore = await _db.BlobObjects.CountAsync();

        _segmenter.Candidates = [20.0];
        await NewService().ProcessBlobAsync(blobId, 1);

        Assert.Equal(thumbnailsBefore, await _db.FileThumbnails.CountAsync());
        Assert.Equal(blobsBefore, await _db.BlobObjects.CountAsync());
    }

    // ---- helpers -----------------------------------------------------------

    private sealed class FakeVideoSemanticSegmenter : IVideoSemanticSegmenter
    {
        public IReadOnlyList<double> Candidates { get; set; } = Array.Empty<double>();
        public string? Failure { get; set; }
        public bool ThrowCancelled { get; set; }
        public int Calls { get; set; }

        public async Task<VideoSemanticSegmenterResult> DetectSceneCandidatesAsync(
            Func<CancellationToken, Task<Stream>> openBlobContent, CancellationToken cancellationToken)
        {
            Calls++;
            if (ThrowCancelled)
            {
                throw new OperationCanceledException();
            }

            // Exercise the real content-open path so a storage regression shows up.
            await using var stream = await openBlobContent(cancellationToken);

            return Failure is not null
                ? VideoSemanticSegmenterResult.Failure(Failure)
                : VideoSemanticSegmenterResult.Ok(Candidates);
        }
    }

    // Fails the SaveChanges that writes the manifest children (the second one
    // inside PersistAsync is the first for a blob with no previous attempt).
    private sealed class FailingSaveDbContext : AppDbContext
    {
        public FailingSaveDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        private bool _armed = true;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_armed && ChangeTracker.Entries<VideoSemanticSegment>().Any())
            {
                _armed = false;
                throw new DbUpdateException("simulated write failure");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
