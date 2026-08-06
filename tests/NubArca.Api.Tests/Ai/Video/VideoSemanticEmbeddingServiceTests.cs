using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Video;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-02: eligibility, idempotency, per-sample failure isolation, retry and
// aggregate-status semantics of one embedding attempt. A fake extractor stands
// in for FFmpeg and a fake embedder for SigLIP2, so no binary and no model are
// needed; the vector layer runs its real SQLite path (pgvector unavailable →
// clean no-op, canonical rows untouched).
public sealed class VideoSemanticEmbeddingServiceTests : IDisposable
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
    private readonly VideoVisualEmbeddingOptions _videoOptions = new();

    public VideoSemanticEmbeddingServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-vembed-{Guid.NewGuid():N}");
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

    private VideoSemanticEmbeddingService NewService()
        => new(
            _db, _blobs, _extractor, _serializer,
            new VideoSemanticSampleVectorIndexService(_db, _serializer, TimeProvider.System),
            Options.Create(_videoOptions),
            TimeProvider.System, NullLogger<VideoSemanticEmbeddingService>.Instance);

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

    private async Task<AiProfile> SeedProfileAsync(int dimension = Dim)
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = $"m-{Guid.NewGuid():N}", Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.ImageEmbedding, Modality = AiModalities.Image,
            Dimension = dimension, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = $"p-{Guid.NewGuid():N}", AiModelId = model.Id,
            Capability = AiCapabilities.ImageEmbedding, Modality = AiModalities.Image,
            Dimension = dimension, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(profile);
        await _db.SaveChangesAsync();
        return profile;
    }

    private sealed record SeededVideo(Guid BlobId, Guid FileId, Guid IndexId, List<Guid> SampleIds);

    private async Task<SeededVideo> SeedVideoWithManifestAsync(
        Guid owner, int samples = 2, string manifestStatus = "completed", int version = 1)
    {
        var file = await _files.CreateAsync(
            owner, null, $"v-{Guid.NewGuid():N}.mp4", "video/mp4",
            new MemoryStream(Guid.NewGuid().ToByteArray()));

        var index = new VideoSemanticIndex
        {
            Id = Guid.NewGuid(), BlobObjectId = file.BlobObjectId, SegmentationVersion = version,
            Status = manifestStatus, AttemptCount = 1,
            DurationMilliseconds = 60_000, SegmentCount = 1, SampleCount = samples,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        var segment = new VideoSemanticSegment
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = index.Id, SegmentIndex = 0,
            StartMilliseconds = 0, EndMilliseconds = 60_000,
            BoundaryReason = VideoSemanticBoundaryReasons.Start, CreatedAt = DateTime.UtcNow,
        };
        _db.VideoSemanticIndexes.Add(index);
        _db.VideoSemanticSegments.Add(segment);

        var sampleIds = new List<Guid>();
        for (var i = 0; i < samples; i++)
        {
            var sample = new VideoSemanticSample
            {
                Id = Guid.NewGuid(), VideoSemanticSegmentId = segment.Id, SampleIndex = i,
                TimestampMilliseconds = 10_000 + i * 10_000,
                SelectionReason = VideoSemanticSelectionReasons.Interior, CreatedAt = DateTime.UtcNow,
            };
            sampleIds.Add(sample.Id);
            _db.VideoSemanticSamples.Add(sample);
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return new SeededVideo(file.BlobObjectId, file.Id, index.Id, sampleIds);
    }

    private Task<VideoSemanticEmbeddingStatus?> LoadAggregateAsync(Guid indexId, Guid profileId)
        => _db.VideoSemanticEmbeddingStatuses.AsNoTracking()
            .FirstOrDefaultAsync(s => s.VideoSemanticIndexId == indexId && s.ProfileId == profileId);

    private async Task MoveToVaultAsync(Guid ownerUserId, Guid fileId)
    {
        var vault = new PrivateVault
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerUserId, DisplayName = "Private",
            PasswordHash = "x", EncryptionMode = PrivateVaultEncryptionModes.None,
            CreatedAt = DateTime.UtcNow,
        };
        _db.PrivateVaults.Add(vault);
        var file = await _db.FileItems.IgnoreQueryFilters().SingleAsync(f => f.Id == fileId);
        file.PrivateVaultId = vault.Id;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    // ---- happy path --------------------------------------------------------

    [Fact]
    public async Task Embeds_Every_Sample_And_Completes_The_Aggregate()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner, samples: 3);
        var profile = await SeedProfileAsync();

        var outcome = await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        Assert.Equal(VideoSemanticEmbeddingOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(3, outcome.ExpectedSampleCount);
        Assert.Equal(3, outcome.CompletedSampleCount);
        Assert.Equal(0, outcome.FailedSampleCount);

        var rows = await _db.VideoSemanticSampleEmbeddings.AsNoTracking().ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(AiArtifactStatuses.Completed, r.Status);
            Assert.Equal(Dim, r.Dimension);
            // Canonical representation: the serializer's packed float32 bytes.
            Assert.Equal(_serializer.Serialize(_embedder.Vector), r.EmbeddingBytes);
            Assert.NotNull(r.CompletedAt);
        });

        var aggregate = await LoadAggregateAsync(video.IndexId, profile.Id);
        Assert.Equal(VideoSemanticEmbeddingStatuses.Completed, aggregate!.Status);
        Assert.Null(aggregate.ErrorCode);
        Assert.NotNull(aggregate.CompletedAt);
        Assert.Equal(1, aggregate.AttemptCount);
    }

    [Fact]
    public async Task Extraction_Uses_The_Video_Embedding_Frame_Edge()
    {
        // VFACE-01C: VSEM-02 keeps its OWN resolution setting. The face-analysis
        // section cannot reach this pipeline — it is not even bound here.
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner, samples: 1);
        var profile = await SeedProfileAsync();
        _videoOptions.FrameMaxEdge = 1024;

        await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        Assert.Equal(1024, _extractor.LastFrameMaxEdge);
    }

    [Fact]
    public async Task The_Default_Video_Embedding_Frame_Edge_Is_Unchanged()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner, samples: 1);
        var profile = await SeedProfileAsync();

        await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        Assert.Equal(768, new VideoVisualEmbeddingOptions().FrameMaxEdge);
        Assert.Equal(768, _extractor.LastFrameMaxEdge);
    }

    [Fact]
    public async Task No_Frame_Or_Blob_Derivative_Is_Ever_Persisted()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        var blobsBefore = await _db.BlobObjects.CountAsync();
        var thumbsBefore = await _db.FileThumbnails.CountAsync();

        await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        Assert.Equal(blobsBefore, await _db.BlobObjects.CountAsync());
        Assert.Equal(thumbsBefore, await _db.FileThumbnails.CountAsync());
    }

    // ---- validation failures ------------------------------------------------

    [Fact]
    public async Task A_Wrong_Dimension_Vector_Fails_The_Sample_Retryably()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner, samples: 1);
        var profile = await SeedProfileAsync();
        _embedder.Vector = new float[Dim + 1];
        _embedder.Vector[0] = 1f;

        var outcome = await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        Assert.Equal(VideoSemanticEmbeddingOutcomeKind.Failed, outcome.Kind);
        var row = await _db.VideoSemanticSampleEmbeddings.AsNoTracking().SingleAsync();
        Assert.Equal(AiArtifactStatuses.Failed, row.Status);
        Assert.Equal(VideoSemanticErrorCodes.EmbeddingDimensionMismatch, row.ErrorCode);
        Assert.Empty(row.EmbeddingBytes);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public async Task A_Non_Finite_Vector_Fails_The_Sample_Retryably(float bad)
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner, samples: 1);
        var profile = await SeedProfileAsync();
        _embedder.Vector = [1f, bad, 0f, 0f];

        await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        var row = await _db.VideoSemanticSampleEmbeddings.AsNoTracking().SingleAsync();
        Assert.Equal(VideoSemanticErrorCodes.EmbeddingInvalidVector, row.ErrorCode);
    }

    [Fact]
    public async Task An_Inference_Exception_Fails_Only_That_Sample()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner, samples: 2);
        var profile = await SeedProfileAsync();
        _embedder.ThrowOnCall = 1;   // second frame's inference throws

        var outcome = await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        Assert.Equal(VideoSemanticEmbeddingOutcomeKind.Partial, outcome.Kind);
        var rows = await _db.VideoSemanticSampleEmbeddings.AsNoTracking().ToListAsync();
        Assert.Equal(1, rows.Count(r => r.Status == AiArtifactStatuses.Completed));
        var failed = rows.Single(r => r.Status == AiArtifactStatuses.Failed);
        Assert.Equal(VideoSemanticErrorCodes.ProviderInference, failed.ErrorCode);
    }

    // ---- profile guards -----------------------------------------------------

    [Fact]
    public async Task A_Non_Image_Embedding_Profile_Writes_Nothing()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        profile.Capability = AiCapabilities.FaceEmbedding;

        var outcome = await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        Assert.Equal(VideoSemanticEmbeddingOutcomeKind.NotEligible, outcome.Kind);
        Assert.Equal(0, await _db.VideoSemanticSampleEmbeddings.CountAsync());
        Assert.Equal(0, await _db.VideoSemanticEmbeddingStatuses.CountAsync());
        Assert.Equal(0, _extractor.Batches);
    }

    // ---- idempotency + retry -----------------------------------------------

    [Fact]
    public async Task A_Completed_Aggregate_Is_Never_Reprocessed()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();

        await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);
        _db.ChangeTracker.Clear();
        _extractor.Batches = 0;
        _embedder.Calls = 0;

        var second = await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        Assert.Equal(VideoSemanticEmbeddingOutcomeKind.AlreadyTerminal, second.Kind);
        Assert.Equal(0, _extractor.Batches);   // no staging, no FFmpeg, no inference
        Assert.Equal(0, _embedder.Calls);
    }

    [Fact]
    public async Task Retry_Processes_Only_Failed_Samples_And_Preserves_Completed_Ones()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner, samples: 3);
        var profile = await SeedProfileAsync();
        _extractor.FailSampleIds.Add(video.SampleIds[1]);

        var first = await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);
        Assert.Equal(VideoSemanticEmbeddingOutcomeKind.Partial, first.Kind);
        Assert.Equal(2, first.CompletedSampleCount);
        var completedBytes = (await _db.VideoSemanticSampleEmbeddings.AsNoTracking()
            .Where(e => e.Status == AiArtifactStatuses.Completed).ToListAsync())
            .ToDictionary(e => e.VideoSemanticSampleId, e => e.EmbeddingBytes);

        _db.ChangeTracker.Clear();
        _extractor.FailSampleIds.Clear();
        _extractor.LastRequests = null;
        _embedder.Vector = [0f, 1f, 0f, 0f];   // a rerun would now produce DIFFERENT bytes

        var second = await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        Assert.Equal(VideoSemanticEmbeddingOutcomeKind.Completed, second.Kind);
        // Only the previously failed sample was re-extracted…
        Assert.Equal([video.SampleIds[1]], _extractor.LastRequests!.Select(r => r.SampleId));
        // …and the completed rows kept their original vectors.
        foreach (var (sampleId, bytes) in completedBytes)
        {
            var row = await _db.VideoSemanticSampleEmbeddings.AsNoTracking()
                .SingleAsync(e => e.VideoSemanticSampleId == sampleId);
            Assert.Equal(bytes, row.EmbeddingBytes);
        }

        var aggregate = await LoadAggregateAsync(video.IndexId, profile.Id);
        Assert.Equal(VideoSemanticEmbeddingStatuses.Completed, aggregate!.Status);
        Assert.Equal(2, aggregate.AttemptCount);
    }

    [Fact]
    public async Task All_Samples_Failing_Yields_A_Failed_Retryable_Aggregate()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner, samples: 2);
        var profile = await SeedProfileAsync();
        _extractor.FailSampleIds.Add(video.SampleIds[0]);
        _extractor.FailSampleIds.Add(video.SampleIds[1]);

        var outcome = await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        Assert.Equal(VideoSemanticEmbeddingOutcomeKind.Failed, outcome.Kind);
        var aggregate = await LoadAggregateAsync(video.IndexId, profile.Id);
        Assert.Equal(VideoSemanticEmbeddingStatuses.Failed, aggregate!.Status);
        Assert.Equal(VideoSemanticErrorCodes.FrameExtraction, aggregate.ErrorCode);
        Assert.Equal(0, aggregate.CompletedSampleCount);
        Assert.Equal(2, aggregate.FailedSampleCount);
    }

    [Fact]
    public async Task A_Staging_Failure_Is_Batch_Level_And_Touches_No_Sample_Rows()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _extractor.StagingErrorCode = VideoSemanticErrorCodes.TemporaryStorage;

        var outcome = await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        Assert.Equal(VideoSemanticEmbeddingOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(VideoSemanticErrorCodes.TemporaryStorage, outcome.ErrorCode);
        Assert.Equal(0, await _db.VideoSemanticSampleEmbeddings.CountAsync());
        var aggregate = await LoadAggregateAsync(video.IndexId, profile.Id);
        Assert.Equal(VideoSemanticEmbeddingStatuses.Failed, aggregate!.Status);
    }

    // ---- profile isolation --------------------------------------------------

    [Fact]
    public async Task Different_Profiles_Embed_Independently()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner, samples: 1);
        var profileA = await SeedProfileAsync();
        var profileB = await SeedProfileAsync();

        await NewService().ProcessBlobAsync(_embedder, profileA, video.BlobId, 1);
        _db.ChangeTracker.Clear();
        await NewService().ProcessBlobAsync(_embedder, profileB, video.BlobId, 1);

        Assert.Equal(2, await _db.VideoSemanticSampleEmbeddings.CountAsync());
        Assert.Equal(2, await _db.VideoSemanticEmbeddingStatuses.CountAsync());
        Assert.NotNull(await LoadAggregateAsync(video.IndexId, profileA.Id));
        Assert.NotNull(await LoadAggregateAsync(video.IndexId, profileB.Id));
    }

    // ---- eligibility --------------------------------------------------------

    [Fact]
    public async Task A_Missing_Or_Failed_Manifest_Writes_Nothing()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner, manifestStatus: "failed");
        var profile = await SeedProfileAsync();

        var outcome = await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        Assert.Equal(VideoSemanticEmbeddingOutcomeKind.NotEligible, outcome.Kind);
        Assert.Equal(0, await _db.VideoSemanticEmbeddingStatuses.CountAsync());
        Assert.Equal(0, _extractor.Batches);
    }

    [Fact]
    public async Task Eligibility_Lost_Before_Execution_Is_A_Clean_Skip()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        var file = await _db.FileItems.SingleAsync(f => f.Id == video.FileId);
        file.MediaLibraryState = MediaLibraryState.Excluded;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var outcome = await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        Assert.Equal(VideoSemanticEmbeddingOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(VideoSemanticErrorCodes.NoEligibleReference, outcome.ErrorCode);
        Assert.Equal(0, await _db.VideoSemanticSampleEmbeddings.CountAsync());
        Assert.Equal(0, _extractor.Batches);
    }

    [Fact]
    public async Task Vault_Only_References_Are_Skipped()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        await MoveToVaultAsync(owner, video.FileId);

        var outcome = await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        Assert.Equal(VideoSemanticEmbeddingOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(VideoSemanticErrorCodes.NoEligibleReference, outcome.ErrorCode);
    }

    [Fact]
    public async Task Mixed_Normal_And_Vault_References_Embed_Once()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner, samples: 1);
        var profile = await SeedProfileAsync();

        // A second reference to DIFFERENT bytes is a different blob; to share
        // the blob we re-upload identical content. Here we simply add a second
        // FileItem on the same blob and vault it.
        var duplicate = await _db.FileItems.AsNoTracking().SingleAsync(f => f.Id == video.FileId);
        var vaulted = new FileItem
        {
            Id = Guid.NewGuid(), OwnerUserId = owner, Name = "secret.mp4",
            MimeType = duplicate.MimeType, SizeBytes = duplicate.SizeBytes,
            BlobObjectId = video.BlobId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.FileItems.Add(vaulted);
        await _db.SaveChangesAsync();
        await MoveToVaultAsync(owner, vaulted.Id);

        var outcome = await NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1);

        // The normal reference makes the blob eligible; the embedding rows name
        // no FileItem, so they cannot expose the vaulted one.
        Assert.Equal(VideoSemanticEmbeddingOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(1, await _db.VideoSemanticSampleEmbeddings.CountAsync());
    }

    // ---- cancellation -------------------------------------------------------

    [Fact]
    public async Task Cancellation_Propagates_And_Records_No_Aggregate()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _extractor.ThrowCancelled = true;

        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewService().ProcessBlobAsync(_embedder, profile, video.BlobId, 1, cts.Token));

        Assert.Equal(0, await _db.VideoSemanticEmbeddingStatuses.CountAsync());
    }

    // ---- fakes -------------------------------------------------------------

    private sealed class FakeFrameExtractor : IVideoSemanticFrameExtractor
    {
        private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

        public int Batches { get; set; }
        public HashSet<Guid> FailSampleIds { get; } = [];
        public string? StagingErrorCode { get; set; }
        public bool ThrowCancelled { get; set; }
        public IReadOnlyList<VideoSemanticFrameRequest>? LastRequests { get; set; }

        public int? LastFrameMaxEdge { get; private set; }

        public Task<VideoSemanticFrameBatchResult> ExtractFramesAsync(
            Func<CancellationToken, Task<Stream>> openBlobContent,
            IReadOnlyList<VideoSemanticFrameRequest> requests,
            int frameMaxEdge,
            CancellationToken cancellationToken)
        {
            Batches++;
            LastRequests = requests;
            LastFrameMaxEdge = frameMaxEdge;
            if (ThrowCancelled)
            {
                throw new OperationCanceledException();
            }

            if (StagingErrorCode is not null)
            {
                return Task.FromResult(VideoSemanticFrameBatchResult.StagingFailure(StagingErrorCode));
            }

            var frames = requests
                .Select(r => FailSampleIds.Contains(r.SampleId)
                    ? new VideoSemanticFrameResult(
                        r.SampleId, r.TimestampMilliseconds, null, VideoSemanticErrorCodes.FrameExtraction)
                    : new VideoSemanticFrameResult(r.SampleId, r.TimestampMilliseconds, Jpeg, null))
                .ToList();
            return Task.FromResult(new VideoSemanticFrameBatchResult(null, frames));
        }
    }

    private sealed class FakeImageEmbedder : IImageEmbedder
    {
        public float[] Vector { get; set; } = [1f, 0f, 0f, 0f];
        public int Calls { get; set; }
        public int? ThrowOnCall { get; set; }

        public string Provider => AiProviders.Deterministic;

        public bool Supports(string capability) => capability == AiCapabilities.ImageEmbedding;

        public Task<AiEmbeddingResult> EmbedImageAsync(
            ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken cancellationToken = default)
        {
            if (ThrowOnCall is int at && Calls == at)
            {
                Calls++;
                throw new InvalidOperationException("simulated inference failure");
            }

            Calls++;
            return Task.FromResult(new AiEmbeddingResult(Vector, Vector.Length, "cosine"));
        }
    }
}
