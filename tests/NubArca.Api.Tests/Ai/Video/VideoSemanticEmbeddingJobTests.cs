using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Ai.Video;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-02: the job surface (single blob, bounded backfill, limit, failed-only,
// checkpointing, gating) and the SCHEDULING SEAM — embeddings are enqueued
// only after a temporal manifest actually COMPLETED, never from upload, never
// for failed/skipped manifests.
public sealed class VideoSemanticEmbeddingJobTests : IDisposable
{
    private const int Dim = 4;

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly BlobService _blobs;
    private readonly FileItemService _files;
    private readonly AiVectorSerializer _serializer = new();
    private readonly FakeFrameExtractor _extractor = new();
    private readonly FakeImageEmbedder _embedder = new();
    private readonly AiOptions _aiOptions = new() { Enabled = true };
    private readonly VideoVisualEmbeddingOptions _videoOptions = new() { Enabled = true };
    private readonly VideoSemanticSegmentationOptions _segmentationOptions = new()
    {
        Enabled = true, SegmentationVersion = 1,
        MinimumSegmentSeconds = 2, TargetSegmentSeconds = 8, MaximumSegmentSeconds = 60,
    };

    public VideoSemanticEmbeddingJobTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-vembedjob-{Guid.NewGuid():N}");
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

    // ---- builders ----------------------------------------------------------

    private VideoSemanticEmbeddingBackfillService NewBackfill()
        => new(
            _db,
            new VideoSemanticEmbeddingService(
                _db, _blobs, _extractor, _serializer,
                new VideoSemanticSampleVectorIndexService(_db, _serializer, TimeProvider.System),
                Options.Create(_videoOptions),
                TimeProvider.System, NullLogger<VideoSemanticEmbeddingService>.Instance),
            Options.Create(_segmentationOptions));

    private AiVideosEmbeddingsBackfillJobHandler NewHandler(IAiBackendResolver? resolver = null)
        => new(
            Options.Create(_aiOptions),
            Options.Create(_videoOptions),
            resolver ?? new FakeResolver(_embedder, _profile!),
            new AiProfileRegistry(_db, TimeProvider.System),
            new NoopDiagnostics(),
            NewBackfill());

    private static JobContext NewContext(
        object payload, string? checkpoint = null, int? sliceItemBudget = null)
        => new(
            Guid.NewGuid(), JsonSerializer.Serialize(payload), _ => { }, CancellationToken.None,
            (_, _, _, _) => Task.CompletedTask, TimeProvider.System, JobScheduling.Compute,
            checkpoint, sliceNumber: 0, sliceDeadline: null, sliceItemBudget: sliceItemBudget);

    // ---- seeding -----------------------------------------------------------

    private AiProfile? _profile;

    private async Task<AiProfile> SeedProfileAsync(bool isDefault = true)
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = $"m-{Guid.NewGuid():N}", Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.ImageEmbedding, Modality = AiModalities.Image,
            Dimension = Dim, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = $"p-{Guid.NewGuid():N}", AiModelId = model.Id,
            Capability = AiCapabilities.ImageEmbedding, Modality = AiModalities.Image,
            Dimension = Dim, DistanceMetric = "cosine", Enabled = true, IsDefault = isDefault,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(profile);
        await _db.SaveChangesAsync();
        _profile = profile;
        return profile;
    }

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

    private async Task<(Guid BlobId, Guid IndexId)> SeedVideoWithManifestAsync(
        Guid owner, int seed, string manifestStatus = "completed")
    {
        var bytes = new byte[64];
        bytes[0] = (byte)seed;
        bytes[1] = (byte)(seed >> 8);
        var file = await _files.CreateAsync(
            owner, null, $"v{seed}.mp4", "video/mp4", new MemoryStream(bytes));

        var index = new VideoSemanticIndex
        {
            Id = Guid.NewGuid(), BlobObjectId = file.BlobObjectId, SegmentationVersion = 1,
            Status = manifestStatus, AttemptCount = 1,
            DurationMilliseconds = 60_000, SegmentCount = 1, SampleCount = 1,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        var segment = new VideoSemanticSegment
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = index.Id, SegmentIndex = 0,
            StartMilliseconds = 0, EndMilliseconds = 60_000,
            BoundaryReason = VideoSemanticBoundaryReasons.Start, CreatedAt = DateTime.UtcNow,
        };
        var sample = new VideoSemanticSample
        {
            Id = Guid.NewGuid(), VideoSemanticSegmentId = segment.Id, SampleIndex = 0,
            TimestampMilliseconds = 30_000,
            SelectionReason = VideoSemanticSelectionReasons.Midpoint, CreatedAt = DateTime.UtcNow,
        };
        _db.VideoSemanticIndexes.Add(index);
        _db.VideoSemanticSegments.Add(segment);
        _db.VideoSemanticSamples.Add(sample);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return (file.BlobObjectId, index.Id);
    }

    // ---- job modes ---------------------------------------------------------

    [Fact]
    public async Task Single_Blob_Payload_Embeds_Only_That_Blob()
    {
        var owner = await SeedUserAsync();
        await SeedProfileAsync();
        var (target, targetIndex) = await SeedVideoWithManifestAsync(owner, 1);
        await SeedVideoWithManifestAsync(owner, 2);

        await NewHandler().ExecuteAsync(
            NewContext(new VideoSemanticEmbeddingsJobPayload(BlobObjectId: target)),
            CancellationToken.None);

        var aggregates = await _db.VideoSemanticEmbeddingStatuses.AsNoTracking().ToListAsync();
        Assert.Single(aggregates);
        Assert.Equal(targetIndex, aggregates[0].VideoSemanticIndexId);
    }

    [Fact]
    public async Task Bounded_Backfill_Embeds_Every_Eligible_Blob()
    {
        var owner = await SeedUserAsync();
        await SeedProfileAsync();
        for (var i = 0; i < 4; i++)
        {
            await SeedVideoWithManifestAsync(owner, i);
        }

        await NewHandler().ExecuteAsync(
            NewContext(new VideoSemanticEmbeddingsJobPayload()), CancellationToken.None);

        Assert.Equal(4, await _db.VideoSemanticEmbeddingStatuses.CountAsync(
            s => s.Status == VideoSemanticEmbeddingStatuses.Completed));
    }

    [Fact]
    public async Task Limit_Bounds_The_Run()
    {
        var owner = await SeedUserAsync();
        var profile = await SeedProfileAsync();
        for (var i = 0; i < 5; i++)
        {
            await SeedVideoWithManifestAsync(owner, i);
        }

        var result = await NewBackfill().RunAsync(
            _embedder, profile, new VideoSemanticEmbeddingBackfillOptions { Limit = 2 });

        Assert.Equal(2, result.Examined);
        Assert.Equal(2, await _db.VideoSemanticEmbeddingStatuses.CountAsync());
    }

    [Fact]
    public async Task Failed_Only_Targets_Failures_And_Leaves_Fresh_Blobs_Alone()
    {
        var owner = await SeedUserAsync();
        var profile = await SeedProfileAsync();
        var (_, failedIndex) = await SeedVideoWithManifestAsync(owner, 1);
        await SeedVideoWithManifestAsync(owner, 2);   // fresh — must be untouched

        _db.VideoSemanticEmbeddingStatuses.Add(new VideoSemanticEmbeddingStatus
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = failedIndex, ProfileId = profile.Id,
            Status = VideoSemanticEmbeddingStatuses.Failed, ExpectedSampleCount = 1,
            FailedSampleCount = 1, ErrorCode = VideoSemanticErrorCodes.FrameExtraction,
            AttemptCount = 1, CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await NewBackfill().RunAsync(
            _embedder, profile, new VideoSemanticEmbeddingBackfillOptions { FailedOnly = true });

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Completed);
        var repaired = await _db.VideoSemanticEmbeddingStatuses.AsNoTracking()
            .SingleAsync(s => s.VideoSemanticIndexId == failedIndex);
        Assert.Equal(VideoSemanticEmbeddingStatuses.Completed, repaired.Status);
        Assert.Equal(1, await _db.VideoSemanticEmbeddingStatuses.CountAsync());
    }

    [Fact]
    public async Task Dry_Run_Counts_Without_Writing()
    {
        var owner = await SeedUserAsync();
        var profile = await SeedProfileAsync();
        await SeedVideoWithManifestAsync(owner, 1);
        await SeedVideoWithManifestAsync(owner, 2);

        var result = await NewBackfill().RunAsync(
            _embedder, profile, new VideoSemanticEmbeddingBackfillOptions { DryRun = true });

        Assert.True(result.DryRun);
        Assert.Equal(2, result.Examined);
        Assert.Equal(0, await _db.VideoSemanticEmbeddingStatuses.CountAsync());
        Assert.Equal(0, _extractor.Batches);
    }

    [Fact]
    public async Task Checkpoints_Between_Blobs_And_Resumes_Without_Repeating()
    {
        var owner = await SeedUserAsync();
        await SeedProfileAsync();
        for (var i = 0; i < 3; i++)
        {
            await SeedVideoWithManifestAsync(owner, i);
        }

        var first = NewContext(new VideoSemanticEmbeddingsJobPayload(), sliceItemBudget: 1);
        await NewHandler().ExecuteAsync(first, CancellationToken.None);

        Assert.True(first.ContinuationRequested);
        var parsed = AiBackfillCheckpoint.TryParse(first.ContinuationCheckpoint);
        Assert.NotNull(parsed?.CursorBlobId);
        Assert.Equal(1, await _db.VideoSemanticEmbeddingStatuses.CountAsync());

        var checkpoint = first.ContinuationCheckpoint;
        for (var slice = 0; slice < 5 && checkpoint is not null; slice++)
        {
            var next = NewContext(new VideoSemanticEmbeddingsJobPayload(), checkpoint, sliceItemBudget: 1);
            await NewHandler().ExecuteAsync(next, CancellationToken.None);
            checkpoint = next.ContinuationRequested ? next.ContinuationCheckpoint : null;
        }

        Assert.Equal(3, await _db.VideoSemanticEmbeddingStatuses.CountAsync(
            s => s.Status == VideoSemanticEmbeddingStatuses.Completed));
    }

    [Fact]
    public async Task Duplicate_FileItem_References_Reuse_One_Blob_Level_Run()
    {
        var owner = await SeedUserAsync();
        await SeedProfileAsync();
        var (blobId, _) = await SeedVideoWithManifestAsync(owner, 1);

        // A dedup upload of identical bytes: a second FileItem on the SAME blob.
        var bytes = new byte[64];
        bytes[0] = 1;
        var second = await _files.CreateAsync(
            owner, null, "copy.mp4", "video/mp4", new MemoryStream(bytes));
        Assert.Equal(blobId, second.BlobObjectId);

        var result = await NewBackfill().RunAsync(
            _embedder, _profile!, new VideoSemanticEmbeddingBackfillOptions());

        Assert.Equal(1, result.Examined);   // one blob, not one per FileItem
        Assert.Equal(1, await _db.VideoSemanticEmbeddingStatuses.CountAsync());
        Assert.Equal(1, _extractor.Batches);
    }

    [Fact]
    public async Task Rejects_A_Non_Positive_Limit_Or_Version()
    {
        await SeedProfileAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => NewHandler().ExecuteAsync(
            NewContext(new VideoSemanticEmbeddingsJobPayload(Limit: 0)), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => NewHandler().ExecuteAsync(
            NewContext(new VideoSemanticEmbeddingsJobPayload(SegmentationVersion: 0)), CancellationToken.None));
    }

    // ---- gating ------------------------------------------------------------

    [Fact]
    public async Task Disabled_Video_Capability_Is_A_Clean_No_Op()
    {
        var owner = await SeedUserAsync();
        await SeedProfileAsync();
        await SeedVideoWithManifestAsync(owner, 1);
        _videoOptions.Enabled = false;

        var context = NewContext(new VideoSemanticEmbeddingsJobPayload());
        await NewHandler().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, _extractor.Batches);
        Assert.Equal(0, await _db.VideoSemanticEmbeddingStatuses.CountAsync());
        Assert.False(context.ContinuationRequested);
    }

    [Fact]
    public async Task Disabled_Ai_Master_Switch_Is_A_Clean_No_Op()
    {
        var owner = await SeedUserAsync();
        await SeedProfileAsync();
        await SeedVideoWithManifestAsync(owner, 1);
        _aiOptions.Enabled = false;

        await NewHandler().ExecuteAsync(
            NewContext(new VideoSemanticEmbeddingsJobPayload()), CancellationToken.None);

        Assert.Equal(0, _extractor.Batches);
        Assert.Equal(0, await _db.VideoSemanticEmbeddingStatuses.CountAsync());
    }

    [Fact]
    public async Task An_Unavailable_Provider_No_Ops_Without_Per_Blob_Rows()
    {
        var owner = await SeedUserAsync();
        await SeedProfileAsync();
        await SeedVideoWithManifestAsync(owner, 1);

        var unavailable = new FakeResolver(embedder: null, profile: null);
        await NewHandler(unavailable).ExecuteAsync(
            NewContext(new VideoSemanticEmbeddingsJobPayload()), CancellationToken.None);

        // Provider unavailable is an ENVIRONMENT state: no aggregate rows, no
        // sample rows, no failure — the blob stays implicitly pending.
        Assert.Equal(0, await _db.VideoSemanticEmbeddingStatuses.CountAsync());
        Assert.Equal(0, await _db.VideoSemanticSampleEmbeddings.CountAsync());
    }

    [Fact]
    public void Embeddings_Job_Runs_In_The_Compute_Band()
    {
        Assert.Equal(
            JobScheduling.Compute,
            JobScheduling.DefaultPriorityFor(JobTypes.AiVideosEmbeddingsBackfill));
    }

    [Fact]
    public void Registration_Registers_The_Embedding_Job_Surface()
    {
        // API/CLI/worker parity: all hosts register through AddAiSubstrate, so
        // the descriptors' presence here is the presence everywhere.
        var services = new ServiceCollection();
        services.AddAiSubstrate();

        Assert.Contains(services, d => d.ImplementationType == typeof(AiVideosEmbeddingsBackfillJobHandler));
        Assert.Contains(services, d => d.ImplementationType == typeof(FfmpegVideoSemanticFrameExtractor));
        Assert.Contains(services, d => d.ImplementationType == typeof(VideoSemanticEmbeddingScheduler));
        Assert.Contains(services, d => d.ServiceType == typeof(VideoSemanticSampleVectorIndexService));
        Assert.Contains(services, d => d.ImplementationType == typeof(VideoVisualEmbeddingOptionsValidator));
    }

    // ---- scheduling seam ---------------------------------------------------

    [Fact]
    public async Task Segmentation_Completion_Schedules_Embeddings_For_That_Blob()
    {
        var owner = await SeedUserAsync();
        var blobId = await SeedProbedVideoAsync(owner, 1);
        var scheduler = new RecordingEmbeddingScheduler();

        await NewSegmentationBackfill(new StubSegmenter(), scheduler)
            .RunAsync(new VideoSemanticBackfillOptions());

        Assert.Equal([(blobId, 1)], scheduler.Scheduled);
    }

    [Fact]
    public async Task Failed_Or_Skipped_Segmentation_Schedules_Nothing()
    {
        var owner = await SeedUserAsync();
        await SeedProbedVideoAsync(owner, 1, codec: null);          // permanent skip
        var failing = await SeedProbedVideoAsync(owner, 2);
        var scheduler = new RecordingEmbeddingScheduler();

        var segmenter = new StubSegmenter { FailureCode = VideoSemanticErrorCodes.ProcessTimeout };
        await NewSegmentationBackfill(segmenter, scheduler).RunAsync(new VideoSemanticBackfillOptions());

        Assert.Empty(scheduler.Scheduled);
        Assert.True(await _db.VideoSemanticIndexes.AnyAsync(
            i => i.BlobObjectId == failing && i.Status == AiArtifactStatuses.Failed));
    }

    [Fact]
    public async Task An_Already_Terminal_Manifest_Schedules_Nothing()
    {
        var owner = await SeedUserAsync();
        await SeedProbedVideoAsync(owner, 1);
        var scheduler = new RecordingEmbeddingScheduler();

        await NewSegmentationBackfill(new StubSegmenter(), scheduler)
            .RunAsync(new VideoSemanticBackfillOptions());
        scheduler.Scheduled.Clear();

        // Second run: the manifest is AlreadyTerminal — no re-schedule.
        await NewSegmentationBackfill(new StubSegmenter(), scheduler)
            .RunAsync(new VideoSemanticBackfillOptions());

        Assert.Empty(scheduler.Scheduled);
    }

    [Fact]
    public async Task Scheduler_Uses_A_Blob_Version_And_Profile_Idempotency_Key()
    {
        var profile = await SeedProfileAsync();
        _aiOptions.PhotoSimilarityProfileKey = profile.Key;
        var queue = new RecordingJobQueue();
        var scheduler = NewEmbeddingScheduler(queue);
        var blobId = Guid.NewGuid();

        Assert.True(await scheduler.TryScheduleForBlobAsync(blobId, 1));

        var enqueued = Assert.Single(queue.Enqueued);
        Assert.Equal(JobTypes.AiVideosEmbeddingsBackfill, enqueued.Type);
        Assert.Equal($"postingest:video:embed:{blobId:N}:1:{profile.Key}", enqueued.IdempotencyKey);
        Assert.DoesNotContain("Owner", enqueued.PayloadJson);
        Assert.DoesNotContain("FileItem", enqueued.PayloadJson);
        Assert.DoesNotContain("StorageKey", enqueued.PayloadJson);
        Assert.DoesNotContain(".mp4", enqueued.PayloadJson);
    }

    [Fact]
    public async Task Scheduler_Enqueues_Nothing_When_Disabled_Or_Without_A_Usable_Profile()
    {
        var queue = new RecordingJobQueue();

        // Capability disabled.
        _videoOptions.Enabled = false;
        Assert.False(await NewEmbeddingScheduler(queue).TryScheduleForBlobAsync(Guid.NewGuid(), 1));

        // Enabled but NO image-embedding profile exists at all.
        _videoOptions.Enabled = true;
        Assert.False(await NewEmbeddingScheduler(queue).TryScheduleForBlobAsync(Guid.NewGuid(), 1));

        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task A_Scheduling_Failure_Never_Breaks_The_Segmentation_Run()
    {
        var profile = await SeedProfileAsync();
        _aiOptions.PhotoSimilarityProfileKey = profile.Key;
        var scheduler = NewEmbeddingScheduler(new ThrowingJobQueue());

        Assert.False(await scheduler.TryScheduleForBlobAsync(Guid.NewGuid(), 1));
    }

    // ---- helpers -----------------------------------------------------------

    private async Task<Guid> SeedProbedVideoAsync(Guid owner, int seed, string? codec = "h264")
    {
        var bytes = new byte[64];
        bytes[0] = (byte)(100 + seed);
        var file = await _files.CreateAsync(
            owner, null, $"probed{seed}.mp4", "video/mp4", new MemoryStream(bytes));

        var meta = await _db.BlobMetadata.SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
        meta.MediaCategory = MediaCategories.Video;
        meta.VideoExtractionStatus = MetadataStatuses.Completed;
        meta.VideoExtractionVersion = 1;
        meta.DurationSeconds = 60;
        meta.VideoCodec = codec;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return file.BlobObjectId;
    }

    private VideoSemanticSegmentationBackfillService NewSegmentationBackfill(
        IVideoSemanticSegmenter segmenter, IVideoSemanticEmbeddingScheduler scheduler)
        => new(
            _db,
            new VideoSemanticSegmentationService(
                _db, _blobs, segmenter, Options.Create(_segmentationOptions),
                TimeProvider.System, NullLogger<VideoSemanticSegmentationService>.Instance),
            scheduler);

    private VideoSemanticEmbeddingScheduler NewEmbeddingScheduler(IJobQueue queue)
        => new(
            queue, new AiProfileRegistry(_db, TimeProvider.System),
            Options.Create(_aiOptions), Options.Create(_videoOptions),
            NullLogger<VideoSemanticEmbeddingScheduler>.Instance);

    private sealed class StubSegmenter : IVideoSemanticSegmenter
    {
        public string? FailureCode { get; set; }

        public Task<VideoSemanticSegmenterResult> DetectSceneCandidatesAsync(
            Func<CancellationToken, Task<Stream>> openBlobContent, CancellationToken cancellationToken)
            => Task.FromResult(FailureCode is null
                ? VideoSemanticSegmenterResult.Ok(new[] { 20.0, 40.0 })
                : VideoSemanticSegmenterResult.Failure(FailureCode));
    }

    private sealed class RecordingEmbeddingScheduler : IVideoSemanticEmbeddingScheduler
    {
        public List<(Guid BlobId, int Version)> Scheduled { get; } = [];

        public Task<bool> TryScheduleForBlobAsync(
            Guid blobObjectId, int segmentationVersion, CancellationToken cancellationToken = default)
        {
            Scheduled.Add((blobObjectId, segmentationVersion));
            return Task.FromResult(true);
        }
    }

    private sealed class FakeResolver : IAiBackendResolver
    {
        private readonly IImageEmbedder? _embedder;
        private readonly AiProfile? _profile;

        public FakeResolver(IImageEmbedder? embedder, AiProfile? profile)
        {
            _embedder = embedder;
            _profile = profile;
        }

        public Task<AiBackendResolution<T>> ResolveForCapabilityAsync<T>(
            string capability, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => Task.FromResult(Resolve<T>(capability));

        public Task<AiBackendResolution<T>> ResolveForProfileKeyAsync<T>(
            string profileKey, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => Task.FromResult(Resolve<T>(AiCapabilities.ImageEmbedding));

        public Task<AiResolution> GetCapabilityAvailabilityAsync(
            string capability, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private AiBackendResolution<T> Resolve<T>(string capability) where T : class, IAiBackend
            => _embedder is T backend && _profile is not null
                ? AiBackendResolution<T>.Available(
                    backend, AiResolution.Available(capability, AiProviders.Deterministic, _profile))
                : AiBackendResolution<T>.Unavailable(
                    AiResolution.Unavailable(capability, AiUnavailableReasons.ProviderUnavailable));
    }

    private sealed class NoopDiagnostics : NubArca.Api.Ai.Diagnostics.IAiDiagnosticsWriter
    {
        public Task RecordProviderUnavailableAsync(
            string capability, Guid? profileId, string reasonCode, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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

    private sealed class FakeFrameExtractor : IVideoSemanticFrameExtractor
    {
        private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

        public int Batches { get; set; }

        public int? LastFrameMaxEdge { get; private set; }

        public Task<VideoSemanticFrameBatchResult> ExtractFramesAsync(
            Func<CancellationToken, Task<Stream>> openBlobContent,
            IReadOnlyList<VideoSemanticFrameRequest> requests,
            int frameMaxEdge,
            CancellationToken cancellationToken)
        {
            Batches++;
            LastFrameMaxEdge = frameMaxEdge;
            var frames = requests
                .Select(r => new VideoSemanticFrameResult(r.SampleId, r.TimestampMilliseconds, Jpeg, null))
                .ToList();
            return Task.FromResult(new VideoSemanticFrameBatchResult(null, frames));
        }
    }

    private sealed class FakeImageEmbedder : IImageEmbedder
    {
        public string Provider => AiProviders.Deterministic;

        public bool Supports(string capability) => capability == AiCapabilities.ImageEmbedding;

        public Task<AiEmbeddingResult> EmbedImageAsync(
            ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken cancellationToken = default)
            => Task.FromResult(new AiEmbeddingResult([1f, 0f, 0f, 0f], Dim, "cosine"));
    }
}
