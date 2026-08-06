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
using NubArca.Api.Ai.Video.Faces;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VFACE-01: the job surface (single blob, bounded backfill, limit, failed-only,
// checkpointing, gating, eligibility recheck) and the SCHEDULING SEAM — face
// analysis is enqueued only after a temporal manifest actually COMPLETED, never
// from upload, never for failed/skipped manifests, and never as a consequence of
// VSEM-02 visual embeddings.
public sealed class VideoFaceAnalysisJobTests : IDisposable
{
    private const int Dim = 4;

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly BlobService _blobs;
    private readonly FileItemService _files;
    private readonly AiVectorSerializer _serializer = new();
    private readonly StubFrameExtractor _extractor = new();
    private readonly StubFaceBackend _backend = new();
    private readonly AiOptions _aiOptions = new() { Enabled = true };
    private readonly VideoFaceAnalysisOptions _faceOptions = new()
    {
        Enabled = true,
        AnalysisVersion = 1,
        FrameIntervalMilliseconds = 1000,
        MaximumFramesPerSegment = 10,
        MaximumFramesPerVideo = 10,
        MinimumDetectionConfidence = 0.5,
        MinimumFaceSizePixels = 16,
        QualityReferenceFaceSizePixels = 64,
        MinimumQualityScore = 0.05,
        MaximumTrackGapMilliseconds = 2000,
        MinimumTrackDetections = 3,
        ProcessTimeoutSeconds = 600,
    };

    private readonly VideoSemanticSegmentationOptions _segmentationOptions = new()
    {
        Enabled = true, SegmentationVersion = 1,
        MinimumSegmentSeconds = 2, TargetSegmentSeconds = 8, MaximumSegmentSeconds = 60,
    };

    public VideoFaceAnalysisJobTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-vfacejob-{Guid.NewGuid():N}");
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

    private VideoFaceAnalysisBackfillService NewBackfill()
        => new(
            _db,
            new VideoFaceAnalysisService(
                _db, _blobs, _extractor, _serializer, Options.Create(_faceOptions),
                TimeProvider.System, NullLogger<VideoFaceAnalysisService>.Instance),
            Options.Create(_segmentationOptions),
            Options.Create(_faceOptions));

    private AiVideosFacesBackfillJobHandler NewHandler(IAiBackendResolver? resolver = null)
        => new(
            Options.Create(_aiOptions),
            Options.Create(_faceOptions),
            resolver ?? new FakeResolver(_backend, _profile),
            new AiProfileRegistry(_db, TimeProvider.System),
            new NoopDiagnostics(),
            NewBackfill());

    private static JobContext NewContext(
        object payload, string? checkpoint = null, int? sliceItemBudget = null)
        => new(
            Guid.NewGuid(), JsonSerializer.Serialize(payload), _ => { }, CancellationToken.None,
            (_, _, _, _) => Task.CompletedTask, TimeProvider.System, JobScheduling.Compute,
            checkpoint, sliceNumber: 0, sliceDeadline: null, sliceItemBudget: sliceItemBudget);

    private VideoFaceAnalysisScheduler NewScheduler(IJobQueue queue)
        => new(
            queue, new AiProfileRegistry(_db, TimeProvider.System),
            Options.Create(_aiOptions), Options.Create(_faceOptions),
            NullLogger<VideoFaceAnalysisScheduler>.Instance);

    // ---- seeding -----------------------------------------------------------

    private AiProfile? _profile;

    private async Task<AiProfile> SeedProfileAsync(bool isDefault = true)
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = $"m-{Guid.NewGuid():N}", Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face,
            Dimension = Dim, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = $"p-{Guid.NewGuid():N}", AiModelId = model.Id,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face,
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

    private async Task<(Guid BlobId, Guid IndexId, Guid FileId)> SeedVideoWithManifestAsync(
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
            DurationMilliseconds = 10_000, SegmentCount = 1, SampleCount = 1,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        _db.VideoSemanticIndexes.Add(index);
        _db.VideoSemanticSegments.Add(new VideoSemanticSegment
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = index.Id, SegmentIndex = 0,
            StartMilliseconds = 0, EndMilliseconds = 10_000,
            BoundaryReason = VideoSemanticBoundaryReasons.Start, CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return (file.BlobObjectId, index.Id, file.Id);
    }

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

    // ---- job modes ---------------------------------------------------------

    [Fact]
    public async Task Single_Blob_Payload_Analyses_Only_That_Blob()
    {
        var owner = await SeedUserAsync();
        await SeedProfileAsync();
        var (target, targetIndex, _) = await SeedVideoWithManifestAsync(owner, 1);
        await SeedVideoWithManifestAsync(owner, 2);

        await NewHandler().ExecuteAsync(
            NewContext(new VideoFaceAnalysisJobPayload(BlobObjectId: target)),
            CancellationToken.None);

        var analysis = Assert.Single(await _db.VideoFaceAnalysisStatuses.AsNoTracking().ToListAsync());
        Assert.Equal(targetIndex, analysis.VideoSemanticIndexId);
    }

    [Fact]
    public async Task Bounded_Backfill_Analyses_Every_Eligible_Blob()
    {
        var owner = await SeedUserAsync();
        await SeedProfileAsync();
        for (var i = 0; i < 4; i++)
        {
            await SeedVideoWithManifestAsync(owner, i);
        }

        await NewHandler().ExecuteAsync(
            NewContext(new VideoFaceAnalysisJobPayload()), CancellationToken.None);

        Assert.Equal(4, await _db.VideoFaceAnalysisStatuses.CountAsync(
            s => s.Status == VideoFaceAnalysisStatuses.Completed));
        Assert.True(await _db.VideoFaceTracks.CountAsync() >= 4);
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
            _backend, _backend, profile, new VideoFaceAnalysisBackfillOptions { Limit = 2 });

        Assert.Equal(2, result.Examined);
        Assert.Equal(2, await _db.VideoFaceAnalysisStatuses.CountAsync());
    }

    [Fact]
    public async Task Failed_Only_Targets_Failures_And_Leaves_Fresh_Blobs_Alone()
    {
        var owner = await SeedUserAsync();
        var profile = await SeedProfileAsync();
        var (_, failedIndex, _) = await SeedVideoWithManifestAsync(owner, 1);
        await SeedVideoWithManifestAsync(owner, 2);   // fresh — must be untouched

        _db.VideoFaceAnalysisStatuses.Add(new VideoFaceAnalysisStatus
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = failedIndex, AnalysisVersion = 1,
            DetectionProfileId = profile.Id, EmbeddingProfileId = profile.Id,
            Status = VideoFaceAnalysisStatuses.Failed, PlannedFrameCount = 10,
            FailedFrameCount = 10, ErrorCode = VideoFaceErrorCodes.FrameExtractFailed,
            AttemptCount = 1, CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await NewBackfill().RunAsync(
            _backend, _backend, profile, new VideoFaceAnalysisBackfillOptions { FailedOnly = true });

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Completed);
        var repaired = await _db.VideoFaceAnalysisStatuses.AsNoTracking()
            .SingleAsync(s => s.VideoSemanticIndexId == failedIndex);
        Assert.Equal(VideoFaceAnalysisStatuses.Completed, repaired.Status);
        Assert.Equal(1, await _db.VideoFaceAnalysisStatuses.CountAsync());
    }

    [Fact]
    public async Task Dry_Run_Counts_Without_Writing_Or_Extracting()
    {
        var owner = await SeedUserAsync();
        var profile = await SeedProfileAsync();
        await SeedVideoWithManifestAsync(owner, 1);
        await SeedVideoWithManifestAsync(owner, 2);

        var result = await NewBackfill().RunAsync(
            detector: null, embedder: null, profile,
            new VideoFaceAnalysisBackfillOptions { DryRun = true });

        Assert.True(result.DryRun);
        Assert.Equal(2, result.Examined);
        Assert.Equal(0, await _db.VideoFaceAnalysisStatuses.CountAsync());
        Assert.Equal(0, _extractor.Runs);
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

        var first = NewContext(new VideoFaceAnalysisJobPayload(), sliceItemBudget: 1);
        await NewHandler().ExecuteAsync(first, CancellationToken.None);

        Assert.True(first.ContinuationRequested);
        var parsed = AiBackfillCheckpoint.TryParse(first.ContinuationCheckpoint);
        Assert.NotNull(parsed?.CursorBlobId);
        Assert.Equal(1, await _db.VideoFaceAnalysisStatuses.CountAsync());

        var checkpoint = first.ContinuationCheckpoint;
        for (var slice = 0; slice < 5 && checkpoint is not null; slice++)
        {
            var next = NewContext(new VideoFaceAnalysisJobPayload(), checkpoint, sliceItemBudget: 1);
            await NewHandler().ExecuteAsync(next, CancellationToken.None);
            checkpoint = next.ContinuationRequested ? next.ContinuationCheckpoint : null;
        }

        Assert.Equal(3, await _db.VideoFaceAnalysisStatuses.CountAsync(
            s => s.Status == VideoFaceAnalysisStatuses.Completed));
    }

    [Fact]
    public async Task Rerunning_A_Finished_Backfill_Is_A_No_Op()
    {
        var owner = await SeedUserAsync();
        await SeedProfileAsync();
        await SeedVideoWithManifestAsync(owner, 1);

        await NewHandler().ExecuteAsync(
            NewContext(new VideoFaceAnalysisJobPayload()), CancellationToken.None);
        var runsAfterFirst = _extractor.Runs;

        await NewHandler().ExecuteAsync(
            NewContext(new VideoFaceAnalysisJobPayload()), CancellationToken.None);

        Assert.Equal(runsAfterFirst, _extractor.Runs);
        Assert.Equal(1, await _db.VideoFaceAnalysisStatuses.CountAsync());
    }

    [Fact]
    public async Task Eligibility_Lost_Before_Execution_Skips_Permanently()
    {
        var owner = await SeedUserAsync();
        await SeedProfileAsync();
        var (target, _, fileId) = await SeedVideoWithManifestAsync(owner, 1);

        // The job was enqueued while the file was live; by the time it runs the
        // only reference is gone.
        var file = await _db.FileItems.SingleAsync(f => f.Id == fileId);
        file.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await NewHandler().ExecuteAsync(
            NewContext(new VideoFaceAnalysisJobPayload(BlobObjectId: target)), CancellationToken.None);

        Assert.Equal(0, _extractor.Runs);
        Assert.Empty(await _db.VideoFaceTracks.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Analysis_Does_Not_Depend_On_Visual_Embeddings()
    {
        var owner = await SeedUserAsync();
        await SeedProfileAsync();
        await SeedVideoWithManifestAsync(owner, 1);

        // No VideoSemanticSampleEmbedding / VideoSemanticEmbeddingStatus rows
        // exist at all: face analysis must still run to completion.
        Assert.Equal(0, await _db.VideoSemanticEmbeddingStatuses.CountAsync());

        await NewHandler().ExecuteAsync(
            NewContext(new VideoFaceAnalysisJobPayload()), CancellationToken.None);

        Assert.Equal(1, await _db.VideoFaceAnalysisStatuses.CountAsync(
            s => s.Status == VideoFaceAnalysisStatuses.Completed));
        Assert.Equal(0, await _db.VideoSemanticEmbeddingStatuses.CountAsync());
        Assert.Equal(0, await _db.VideoSemanticSampleEmbeddings.CountAsync());
    }

    [Fact]
    public async Task Rejects_A_Non_Positive_Limit_Or_Version()
    {
        await SeedProfileAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => NewHandler().ExecuteAsync(
            NewContext(new VideoFaceAnalysisJobPayload(Limit: 0)), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => NewHandler().ExecuteAsync(
            NewContext(new VideoFaceAnalysisJobPayload(SegmentationVersion: 0)), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => NewHandler().ExecuteAsync(
            NewContext(new VideoFaceAnalysisJobPayload(AnalysisVersion: 0)), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_A_Mismatched_Detection_And_Embedding_Profile_Pair()
    {
        await SeedProfileAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => NewHandler().ExecuteAsync(
            NewContext(new VideoFaceAnalysisJobPayload(
                DetectionProfileKey: "detector-a", EmbeddingProfileKey: "recognizer-b")),
            CancellationToken.None));
    }

    // ---- gating ------------------------------------------------------------

    [Fact]
    public async Task Disabled_Video_Face_Capability_Is_A_Clean_No_Op()
    {
        var owner = await SeedUserAsync();
        await SeedProfileAsync();
        await SeedVideoWithManifestAsync(owner, 1);
        _faceOptions.Enabled = false;

        var context = NewContext(new VideoFaceAnalysisJobPayload());
        await NewHandler().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, _extractor.Runs);
        Assert.Equal(0, await _db.VideoFaceAnalysisStatuses.CountAsync());
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
            NewContext(new VideoFaceAnalysisJobPayload()), CancellationToken.None);

        Assert.Equal(0, _extractor.Runs);
        Assert.Equal(0, await _db.VideoFaceAnalysisStatuses.CountAsync());
    }

    [Fact]
    public async Task An_Unavailable_Provider_No_Ops_Without_Per_Blob_Rows()
    {
        var owner = await SeedUserAsync();
        await SeedProfileAsync();
        await SeedVideoWithManifestAsync(owner, 1);

        await NewHandler(new FakeResolver(backend: null, profile: null)).ExecuteAsync(
            NewContext(new VideoFaceAnalysisJobPayload()), CancellationToken.None);

        // Provider unavailable is an ENVIRONMENT state: no analysis rows, no
        // tracks, no failure — the blob stays implicitly pending.
        Assert.Equal(0, await _db.VideoFaceAnalysisStatuses.CountAsync());
        Assert.Equal(0, await _db.VideoFaceTracks.CountAsync());
    }

    [Fact]
    public void Face_Analysis_Job_Runs_In_The_Compute_Band()
    {
        Assert.Equal(
            JobScheduling.Compute,
            JobScheduling.DefaultPriorityFor(JobTypes.AiVideosFacesBackfill));
    }

    [Fact]
    public void Registration_Registers_The_Face_Analysis_Job_Surface()
    {
        // API/CLI/worker parity: all hosts register through AddAiSubstrate, so
        // the descriptors' presence here is the presence everywhere.
        var services = new ServiceCollection();
        services.AddAiSubstrate();

        Assert.Contains(services, d => d.ImplementationType == typeof(AiVideosFacesBackfillJobHandler));
        Assert.Contains(services, d => d.ImplementationType == typeof(VideoFaceAnalysisScheduler));
        Assert.Contains(services, d => d.ImplementationType == typeof(VideoFaceAnalysisOptionsValidator));
        Assert.Contains(services, d => d.ServiceType == typeof(VideoFaceAnalysisService));
        Assert.Contains(services, d => d.ServiceType == typeof(VideoFaceAnalysisBackfillService));
        Assert.Contains(services, d => d.ServiceType == typeof(IVideoSemanticFrameStreamExtractor));
    }

    // ---- scheduling seam ---------------------------------------------------

    [Fact]
    public async Task Segmentation_Completion_Schedules_Face_Analysis_For_That_Blob()
    {
        var owner = await SeedUserAsync();
        var blobId = await SeedProbedVideoAsync(owner, 1);
        var scheduler = new RecordingFaceScheduler();

        await NewSegmentationBackfill(new StubSegmenter(), scheduler)
            .RunAsync(new VideoSemanticBackfillOptions());

        Assert.Equal([(blobId, 1)], scheduler.Scheduled);
    }

    [Fact]
    public async Task Incomplete_Segmentation_Schedules_No_Face_Analysis()
    {
        var owner = await SeedUserAsync();
        await SeedProbedVideoAsync(owner, 1, codec: null);          // permanent skip
        await SeedProbedVideoAsync(owner, 2);
        var scheduler = new RecordingFaceScheduler();

        var segmenter = new StubSegmenter { FailureCode = VideoSemanticErrorCodes.ProcessTimeout };
        await NewSegmentationBackfill(segmenter, scheduler).RunAsync(new VideoSemanticBackfillOptions());

        Assert.Empty(scheduler.Scheduled);
    }

    [Fact]
    public async Task Scheduler_Uses_A_Blob_Version_And_Profile_Idempotency_Key()
    {
        var profile = await SeedProfileAsync();
        _aiOptions.FaceProfileKey = profile.Key;
        var queue = new RecordingJobQueue();
        var blobId = Guid.NewGuid();

        Assert.True(await NewScheduler(queue).TryScheduleForBlobAsync(blobId, 1));

        var enqueued = Assert.Single(queue.Enqueued);
        Assert.Equal(JobTypes.AiVideosFacesBackfill, enqueued.Type);
        Assert.Equal(
            $"postingest:video:faces:{blobId:N}:1:1:{profile.Key}:{profile.Key}",
            enqueued.IdempotencyKey);
        Assert.DoesNotContain("Owner", enqueued.PayloadJson);
        Assert.DoesNotContain("FileItem", enqueued.PayloadJson);
        Assert.DoesNotContain("Person", enqueued.PayloadJson);
        Assert.DoesNotContain("StorageKey", enqueued.PayloadJson);
        Assert.DoesNotContain(".mp4", enqueued.PayloadJson);
    }

    [Fact]
    public async Task Scheduler_Enqueues_Nothing_When_Disabled_Or_Without_A_Usable_Profile()
    {
        var queue = new RecordingJobQueue();

        _faceOptions.Enabled = false;
        Assert.False(await NewScheduler(queue).TryScheduleForBlobAsync(Guid.NewGuid(), 1));

        // Enabled but NO face profile exists at all.
        _faceOptions.Enabled = true;
        Assert.False(await NewScheduler(queue).TryScheduleForBlobAsync(Guid.NewGuid(), 1));

        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task A_Scheduling_Failure_Never_Breaks_The_Segmentation_Run()
    {
        var profile = await SeedProfileAsync();
        _aiOptions.FaceProfileKey = profile.Key;

        Assert.False(await NewScheduler(new ThrowingJobQueue())
            .TryScheduleForBlobAsync(Guid.NewGuid(), 1));
    }

    // ---- helpers -----------------------------------------------------------

    private VideoSemanticSegmentationBackfillService NewSegmentationBackfill(
        IVideoSemanticSegmenter segmenter, IVideoFaceAnalysisScheduler faceScheduler)
        => new(
            _db,
            new VideoSemanticSegmentationService(
                _db, _blobs, segmenter, Options.Create(_segmentationOptions),
                TimeProvider.System, NullLogger<VideoSemanticSegmentationService>.Instance),
            embeddingScheduler: null,
            faceScheduler: faceScheduler);

    private sealed class StubSegmenter : IVideoSemanticSegmenter
    {
        public string? FailureCode { get; set; }

        public Task<VideoSemanticSegmenterResult> DetectSceneCandidatesAsync(
            Func<CancellationToken, Task<Stream>> openBlobContent, CancellationToken cancellationToken)
            => Task.FromResult(FailureCode is null
                ? VideoSemanticSegmenterResult.Ok(new[] { 20.0, 40.0 })
                : VideoSemanticSegmenterResult.Failure(FailureCode));
    }

    private sealed class RecordingFaceScheduler : IVideoFaceAnalysisScheduler
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
        private readonly StubFaceBackend? _backend;
        private readonly AiProfile? _profile;

        public FakeResolver(StubFaceBackend? backend, AiProfile? profile)
        {
            _backend = backend;
            _profile = profile;
        }

        public Task<AiBackendResolution<T>> ResolveForCapabilityAsync<T>(
            string capability, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => Task.FromResult(Resolve<T>(capability));

        public Task<AiBackendResolution<T>> ResolveForProfileKeyAsync<T>(
            string profileKey, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => Task.FromResult(Resolve<T>(AiCapabilities.FaceEmbedding));

        public Task<AiResolution> GetCapabilityAvailabilityAsync(
            string capability, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private AiBackendResolution<T> Resolve<T>(string capability) where T : class, IAiBackend
            => _backend is T backend && _profile is not null
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

    private sealed class StubFrameExtractor : IVideoSemanticFrameStreamExtractor
    {
        private static readonly byte[] Frame = Jpeg();

        public int Runs { get; private set; }
        public int? LastFrameMaxEdge { get; private set; }

        public async Task<string?> ExtractFramesStreamingAsync(
            Func<CancellationToken, Task<Stream>> openBlobContent,
            IReadOnlyList<VideoSemanticFrameRequest> requests,
            int frameMaxEdge,
            Func<VideoSemanticFrameResult, CancellationToken, Task> onFrame,
            CancellationToken cancellationToken)
        {
            Runs++;
            LastFrameMaxEdge = frameMaxEdge;
            foreach (var request in requests)
            {
                await onFrame(
                    new VideoSemanticFrameResult(
                        request.SampleId, request.TimestampMilliseconds, Frame, null),
                    cancellationToken);
            }

            return null;
        }

        private static byte[] Jpeg()
        {
            using var image = new Image<Rgb24>(128, 128);
            using var buffer = new MemoryStream();
            image.Save(buffer, new JpegEncoder());
            return buffer.ToArray();
        }
    }

    // One stable face in every frame, so every eligible blob yields exactly one
    // track and the assertions can focus on the JOB surface.
    private sealed class StubFaceBackend : IFaceDetector, IFaceEmbedder
    {
        public string Provider => AiProviders.Deterministic;

        public bool Supports(string capability)
            => capability is AiCapabilities.FaceDetection or AiCapabilities.FaceEmbedding;

        public Task<AiFaceDetectionResult> DetectFacesAsync(
            ReadOnlyMemory<byte> imageBytes, AiProfile profile,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AiFaceDetectionResult(
                [new DetectedFace(0.3, 0.3, 0.4, 0.4, 0.95, [new FaceLandmark(0.5, 0.5)])]));

        public Task<AiEmbeddingResult> EmbedFaceAsync(
            ReadOnlyMemory<byte> faceCropBytes, AiProfile profile,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AiEmbeddingResult([1f, 0f, 0f, 0f], Dim, "cosine"));
    }
}
