using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Ai.Video;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-01: the job surface (single blob, bounded backfill, limit, failed-only,
// checkpointing, disabled) and the SCHEDULING SEAM — segmentation is enqueued
// only after video metadata has actually been persisted, and nothing else about
// ingestion changes.
public sealed class VideoSemanticSegmentationJobTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly BlobService _blobs;
    private readonly FakeVideoMetadataExtractor _videoExtractor = new();
    private readonly FileItemService _files;
    private readonly StubSegmenter _segmenter = new();
    private readonly VideoSemanticSegmentationOptions _options = new()
    {
        Enabled = true,
        SegmentationVersion = 1,
        MinimumSegmentSeconds = 2,
        TargetSegmentSeconds = 8,
        MaximumSegmentSeconds = 60,
    };

    public VideoSemanticSegmentationJobTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-vsemjob-{Guid.NewGuid():N}");
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
            videoMetadataExtractor: _videoExtractor);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private VideoSemanticSegmentationBackfillService NewBackfill()
        => new(_db, new VideoSemanticSegmentationService(
            _db, _blobs, _segmenter, Options.Create(_options), TimeProvider.System,
            NullLogger<VideoSemanticSegmentationService>.Instance));

    private AiVideosSegmentsBackfillJobHandler NewHandler()
        => new(Options.Create(_options), NewBackfill());

    private static JobContext NewContext(
        object payload, string? checkpoint = null, int? sliceItemBudget = null)
        => new(
            Guid.NewGuid(), JsonSerializer.Serialize(payload), _ => { }, CancellationToken.None,
            (_, _, _, _) => Task.CompletedTask, TimeProvider.System, JobScheduling.Compute,
            checkpoint, sliceNumber: 0, sliceDeadline: null, sliceItemBudget: sliceItemBudget);

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

    private async Task<Guid> SeedProbedVideoAsync(Guid owner, int seed)
    {
        var bytes = new byte[64];
        bytes[0] = (byte)seed;
        bytes[1] = (byte)(seed >> 8);
        var file = await _files.CreateAsync(
            owner, null, $"v{seed}.mp4", "video/mp4", new MemoryStream(bytes));

        var meta = await _db.BlobMetadata.SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
        meta.MediaCategory = MediaCategories.Video;
        meta.VideoExtractionStatus = MetadataStatuses.Completed;
        meta.VideoExtractionVersion = 1;
        meta.DurationSeconds = 60;
        meta.VideoCodec = "h264";
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return file.BlobObjectId;
    }

    // ---- job modes ---------------------------------------------------------

    [Fact]
    public async Task Single_Blob_Payload_Segments_Only_That_Blob()
    {
        var owner = await SeedUserAsync();
        var target = await SeedProbedVideoAsync(owner, 1);
        await SeedProbedVideoAsync(owner, 2);

        await NewHandler().ExecuteAsync(
            NewContext(new VideoSemanticSegmentsJobPayload(BlobObjectId: target)), CancellationToken.None);

        var indexes = await _db.VideoSemanticIndexes.AsNoTracking().ToListAsync();
        Assert.Single(indexes);
        Assert.Equal(target, indexes[0].BlobObjectId);
    }

    [Fact]
    public async Task Bounded_Backfill_Processes_Every_Eligible_Blob()
    {
        var owner = await SeedUserAsync();
        for (var i = 0; i < 5; i++)
        {
            await SeedProbedVideoAsync(owner, i);
        }

        await NewHandler().ExecuteAsync(
            NewContext(new VideoSemanticSegmentsJobPayload()), CancellationToken.None);

        Assert.Equal(5, await _db.VideoSemanticIndexes.CountAsync(
            i => i.Status == AiArtifactStatuses.Completed));
    }

    [Fact]
    public async Task Limit_Bounds_The_Run()
    {
        var owner = await SeedUserAsync();
        for (var i = 0; i < 5; i++)
        {
            await SeedProbedVideoAsync(owner, i);
        }

        var result = await NewBackfill().RunAsync(new VideoSemanticBackfillOptions { Limit = 2 });

        Assert.Equal(2, result.Examined);
        Assert.Equal(2, await _db.VideoSemanticIndexes.CountAsync());
    }

    [Fact]
    public async Task Rejects_A_Non_Positive_Limit_Or_Version()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NewHandler().ExecuteAsync(
            NewContext(new VideoSemanticSegmentsJobPayload(Limit: 0)), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => NewHandler().ExecuteAsync(
            NewContext(new VideoSemanticSegmentsJobPayload(SegmentationVersion: 0)), CancellationToken.None));
    }

    [Fact]
    public async Task Failed_Only_Targets_Failures_And_Leaves_Fresh_Blobs_Alone()
    {
        var owner = await SeedUserAsync();
        var failedBlob = await SeedProbedVideoAsync(owner, 1);
        await SeedProbedVideoAsync(owner, 2);

        _db.VideoSemanticIndexes.Add(new VideoSemanticIndex
        {
            Id = Guid.NewGuid(), BlobObjectId = failedBlob, SegmentationVersion = 1,
            Status = AiArtifactStatuses.Failed, ErrorCode = VideoSemanticErrorCodes.ProcessTimeout,
            IsPermanentFailure = false, AttemptCount = 1, CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await NewBackfill().RunAsync(new VideoSemanticBackfillOptions { FailedOnly = true });

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Completed);
        var completed = await _db.VideoSemanticIndexes.AsNoTracking()
            .Where(i => i.Status == AiArtifactStatuses.Completed).ToListAsync();
        Assert.Single(completed);
        Assert.Equal(failedBlob, completed[0].BlobObjectId);
    }

    [Fact]
    public async Task Dry_Run_Counts_Without_Writing()
    {
        var owner = await SeedUserAsync();
        await SeedProbedVideoAsync(owner, 1);
        await SeedProbedVideoAsync(owner, 2);

        var result = await NewBackfill().RunAsync(new VideoSemanticBackfillOptions { DryRun = true });

        Assert.True(result.DryRun);
        Assert.Equal(2, result.Examined);
        Assert.Equal(0, await _db.VideoSemanticIndexes.CountAsync());
    }

    [Fact]
    public async Task Checkpoints_Between_Blobs_And_Resumes_Without_Repeating()
    {
        var owner = await SeedUserAsync();
        for (var i = 0; i < 4; i++)
        {
            await SeedProbedVideoAsync(owner, i);
        }

        // One blob per slice.
        var first = NewContext(new VideoSemanticSegmentsJobPayload(), sliceItemBudget: 1);
        await NewHandler().ExecuteAsync(first, CancellationToken.None);

        Assert.True(first.ContinuationRequested);
        Assert.NotNull(first.ContinuationCheckpoint);
        var parsed = AiBackfillCheckpoint.TryParse(first.ContinuationCheckpoint);
        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.CursorBlobId);
        Assert.Equal(1, await _db.VideoSemanticIndexes.CountAsync());

        // Resume until the queue drains; each slice must make progress.
        var checkpoint = first.ContinuationCheckpoint;
        for (var slice = 0; slice < 5 && checkpoint is not null; slice++)
        {
            var next = NewContext(new VideoSemanticSegmentsJobPayload(), checkpoint, sliceItemBudget: 1);
            await NewHandler().ExecuteAsync(next, CancellationToken.None);
            checkpoint = next.ContinuationRequested ? next.ContinuationCheckpoint : null;
        }

        Assert.Equal(4, await _db.VideoSemanticIndexes.CountAsync());
        Assert.Equal(4, await _db.VideoSemanticIndexes.CountAsync(
            i => i.Status == AiArtifactStatuses.Completed));
    }

    [Fact]
    public async Task Disabled_Capability_Is_A_Clean_No_Op()
    {
        var owner = await SeedUserAsync();
        await SeedProbedVideoAsync(owner, 1);
        _options.Enabled = false;

        var context = NewContext(new VideoSemanticSegmentsJobPayload());
        await NewHandler().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, _segmenter.Calls);                 // no FFmpeg
        Assert.Equal(0, await _db.VideoSemanticIndexes.CountAsync());
        Assert.False(context.ContinuationRequested);
    }

    // ---- scheduling seam ---------------------------------------------------

    [Fact]
    public async Task Metadata_Completion_Enqueues_Segmentation_For_That_Blob()
    {
        var owner = await SeedUserAsync();
        var file = await _files.CreateAsync(
            owner, null, "v.mp4", "video/mp4", new MemoryStream(new byte[64]));
        _videoExtractor.Result = CompletedProbe();

        var scheduler = new RecordingScheduler();
        var backfill = new VideoMetadataBackfillService(_db, _files, scheduler);

        await backfill.RunAsync(new MetadataBackfillOptions());

        Assert.Equal(new[] { file.BlobObjectId }, scheduler.Scheduled);
    }

    [Fact]
    public async Task Segmentation_Is_Not_Scheduled_Before_Metadata_Is_Available()
    {
        var owner = await SeedUserAsync();
        await _files.CreateAsync(owner, null, "v.mp4", "video/mp4", new MemoryStream(new byte[64]));
        // The probe FAILS: duration and stream info are still unknown, so
        // scheduling segmentation would be scheduling against unknown metadata.
        _videoExtractor.Result = VideoMetadataExtractionResult.ForStatus(
            MetadataStatuses.Failed, MetadataErrorCodes.Timeout, FfprobeVideoMetadataExtractor.Version);

        var scheduler = new RecordingScheduler();
        await new VideoMetadataBackfillService(_db, _files, scheduler).RunAsync(new MetadataBackfillOptions());

        Assert.Empty(scheduler.Scheduled);
    }

    [Fact]
    public async Task Image_Ingestion_Schedules_No_Segmentation()
    {
        var owner = await SeedUserAsync();
        await _files.CreateAsync(
            owner, null, "p.png", "image/png", new MemoryStream(ImageFixtures.PlainPng(width: 8)));

        var scheduler = new RecordingScheduler();
        await new VideoMetadataBackfillService(_db, _files, scheduler).RunAsync(new MetadataBackfillOptions());

        // Images are not video candidates at all — the pipeline is untouched.
        Assert.Empty(scheduler.Scheduled);
        Assert.Equal(0, await _db.VideoSemanticIndexes.CountAsync());
    }

    [Fact]
    public async Task Scheduler_Uses_A_Blob_And_Version_Only_Idempotency_Key()
    {
        var queue = new RecordingJobQueue();
        var scheduler = new VideoSemanticSegmentationScheduler(
            queue, Options.Create(_options), NullLogger<VideoSemanticSegmentationScheduler>.Instance);
        var blobId = Guid.NewGuid();

        Assert.True(await scheduler.TryScheduleForBlobAsync(blobId));
        Assert.True(await scheduler.TryScheduleForBlobAsync(blobId));

        Assert.Equal(2, queue.Enqueued.Count);
        Assert.All(queue.Enqueued, e =>
        {
            Assert.Equal(JobTypes.AiVideosSegmentsBackfill, e.Type);
            Assert.Equal($"postingest:video:segments:{blobId:N}:1", e.IdempotencyKey);
            // No owner id, no filename, no storage key, no path in the payload.
            Assert.DoesNotContain("Owner", e.PayloadJson);
            Assert.DoesNotContain("FileItem", e.PayloadJson);
            Assert.DoesNotContain("StorageKey", e.PayloadJson);
            Assert.DoesNotContain(".mp4", e.PayloadJson);
        });

        // A new version is a DIFFERENT key, so a reindex is schedulable while
        // the old manifest still stands.
        _options.SegmentationVersion = 2;
        await scheduler.TryScheduleForBlobAsync(blobId);
        Assert.Equal($"postingest:video:segments:{blobId:N}:2", queue.Enqueued[^1].IdempotencyKey);
    }

    [Fact]
    public async Task Scheduler_Enqueues_Nothing_When_The_Capability_Is_Disabled()
    {
        _options.Enabled = false;
        var queue = new RecordingJobQueue();
        var scheduler = new VideoSemanticSegmentationScheduler(
            queue, Options.Create(_options), NullLogger<VideoSemanticSegmentationScheduler>.Instance);

        Assert.False(await scheduler.TryScheduleForBlobAsync(Guid.NewGuid()));
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task A_Scheduling_Failure_Never_Breaks_The_Metadata_Probe()
    {
        var owner = await SeedUserAsync();
        await _files.CreateAsync(owner, null, "v.mp4", "video/mp4", new MemoryStream(new byte[64]));
        _videoExtractor.Result = CompletedProbe();

        var scheduler = new VideoSemanticSegmentationScheduler(
            new ThrowingJobQueue(), Options.Create(_options),
            NullLogger<VideoSemanticSegmentationScheduler>.Instance);

        var result = await new VideoMetadataBackfillService(_db, _files, scheduler)
            .RunAsync(new MetadataBackfillOptions());

        Assert.Equal(1, result.Completed);   // the probe still succeeded
    }

    [Fact]
    public void Segmentation_Job_Runs_In_The_Compute_Band()
    {
        Assert.Equal(
            JobScheduling.Compute,
            JobScheduling.DefaultPriorityFor(JobTypes.AiVideosSegmentsBackfill));
    }

    // ---- helpers -----------------------------------------------------------

    private static VideoMetadataExtractionResult CompletedProbe() => new()
    {
        Status = MetadataStatuses.Completed,
        Version = FfprobeVideoMetadataExtractor.Version,
        Width = 1920,
        Height = 1080,
        DurationSeconds = 60,
        VideoCodec = "h264",
    };

    private sealed class StubSegmenter : IVideoSemanticSegmenter
    {
        public int Calls { get; set; }

        public Task<VideoSemanticSegmenterResult> DetectSceneCandidatesAsync(
            Func<CancellationToken, Task<Stream>> openBlobContent, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(VideoSemanticSegmenterResult.Ok(new[] { 20.0, 40.0 }));
        }
    }

    private sealed class RecordingScheduler : IVideoSemanticSegmentationScheduler
    {
        public List<Guid> Scheduled { get; } = [];

        public Task<bool> TryScheduleForBlobAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
        {
            Scheduled.Add(blobObjectId);
            return Task.FromResult(true);
        }
    }

    private sealed record EnqueuedJob(string Type, string PayloadJson, string? IdempotencyKey);

    private class RecordingJobQueue : IJobQueue
    {
        public List<EnqueuedJob> Enqueued { get; } = [];

        public virtual Task<BackgroundJob> EnqueueAsync<TPayload>(
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

        public Task<AdminJobPage> ListAdminJobsAsync(
            AdminJobFilter filter, int page, int pageSize, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AdminJobSummary?> GetAdminJobAsync(Guid jobId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingJobQueue : RecordingJobQueue
    {
        public override Task<BackgroundJob> EnqueueAsync<TPayload>(
            string type, TPayload payload, int? maxAttempts = null, int? priority = null,
            string? idempotencyKey = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("queue unavailable");
    }
}
