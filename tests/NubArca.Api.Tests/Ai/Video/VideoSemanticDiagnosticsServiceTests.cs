using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Ai.Video;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Media.Semantic;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-04: VideoSemanticDiagnosticsService — the aggregate-only status seam
// over the VSEM-01/02 substrate. Every assertion here is a COUNT; nothing in
// the DTO is a blob/file/owner id, path, storage key or vector, and active vs
// historical segmentation versions must never be blended into one number.
public sealed class VideoSemanticDiagnosticsServiceTests : IDisposable
{
    private const int Dim = 4;

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly BlobService _blobs;
    private readonly FileItemService _files;
    private readonly VideoSemanticSegmentationOptions _segmentationOptions = new()
    {
        Enabled = true, SegmentationVersion = 3,
        MinimumSegmentSeconds = 2, TargetSegmentSeconds = 8, MaximumSegmentSeconds = 60,
    };
    private readonly VideoVisualEmbeddingOptions _embeddingOptions = new() { Enabled = true };
    private readonly AiOptions _aiOptions = new() { Enabled = true };

    public VideoSemanticDiagnosticsServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-vdiag-{Guid.NewGuid():N}");
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
            videoMetadataExtractor: new FakeVideoMetadataExtractor());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private VideoSemanticDiagnosticsService NewService(AiProfile? configuredProfile = null)
    {
        var aiOptions = _aiOptions;
        if (configuredProfile is not null)
        {
            aiOptions = new AiOptions { Enabled = true, PhotoSimilarityProfileKey = configuredProfile.Key };
        }

        return new VideoSemanticDiagnosticsService(
            _db,
            Options.Create(_segmentationOptions),
            Options.Create(_embeddingOptions),
            Options.Create(aiOptions),
            new AiProfileRegistry(_db, TimeProvider.System),
            new SingleProfileResolver(configuredProfile),
            new VideoSemanticSampleVectorIndexService(_db, new AiVectorSerializer(), TimeProvider.System));
    }

    // ---- seeding -------------------------------------------------------------

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

    private async Task<Guid> SeedEligibleVideoBlobAsync(Guid owner, int seed)
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
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return file.BlobObjectId;
    }

    private async Task<Guid> SeedSegmentationIndexAsync(
        Guid blobId, int version, string status, string? errorCode = null, bool permanent = false)
    {
        var index = new VideoSemanticIndex
        {
            Id = Guid.NewGuid(), BlobObjectId = blobId, SegmentationVersion = version,
            Status = status, ErrorCode = errorCode, IsPermanentFailure = permanent, AttemptCount = 1,
            SegmentCount = status == AiArtifactStatuses.Completed ? 1 : 0,
            SampleCount = status == AiArtifactStatuses.Completed ? 1 : 0,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        _db.VideoSemanticIndexes.Add(index);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return index.Id;
    }

    private async Task<AiProfile> SeedProfileAsync()
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
            Dimension = Dim, DistanceMetric = "cosine", Enabled = true, IsDefault = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(profile);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return profile;
    }

    private async Task SeedEmbeddingStatusAsync(
        Guid indexId, Guid profileId, string status, int expected, int completed, int failed)
    {
        _db.VideoSemanticEmbeddingStatuses.Add(new VideoSemanticEmbeddingStatus
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = indexId, ProfileId = profileId,
            Status = status, ExpectedSampleCount = expected, CompletedSampleCount = completed,
            FailedSampleCount = failed, AttemptCount = 1, CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    // A resolver that reports the given profile as the capability default (or
    // "unavailable" when null) without needing a live backend.
    private sealed class SingleProfileResolver : IAiBackendResolver
    {
        private readonly AiProfile? _profile;
        public SingleProfileResolver(AiProfile? profile) => _profile = profile;

        public Task<AiBackendResolution<T>> ResolveForCapabilityAsync<T>(
            string capability, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => throw new NotSupportedException("Status queries never resolve a live backend.");

        public Task<AiBackendResolution<T>> ResolveForProfileKeyAsync<T>(
            string profileKey, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => throw new NotSupportedException("Status queries never resolve a live backend.");

        public Task<AiResolution> GetCapabilityAvailabilityAsync(
            string capability, CancellationToken cancellationToken = default)
            => Task.FromResult(_profile is null
                ? new AiResolution
                {
                    IsAvailable = false, Capability = capability,
                    UnavailableReason = AiUnavailableReasons.NoDefaultProfile,
                }
                : new AiResolution
                {
                    IsAvailable = true, Capability = capability, Provider = AiProviders.Deterministic,
                    ProfileKey = _profile.Key, Dimension = _profile.Dimension,
                    DistanceMetric = _profile.DistanceMetric,
                });
    }

    // ---- tests -----------------------------------------------------------

    [Fact]
    public async Task Empty_Database_Reports_Zero_Counts_And_Disabled_Profile()
    {
        var status = await NewService().GetStatusAsync();

        Assert.Equal(0, status.EligibleVideoBlobs);
        Assert.Equal(3, status.ActiveSegmentationVersion);
        Assert.Equal(0, status.SegmentationNotProcessed);
        Assert.Equal(0, status.SegmentationCompleted);
        Assert.Empty(status.HistoricalVersions);
        Assert.Null(status.ActiveEmbeddingProfileKey);
        Assert.False(status.ActiveEmbeddingProfileAvailable);
        Assert.Equal(0, status.SamplesExpected);
        Assert.False(status.PgvectorBackendAvailable);
        Assert.True(status.SegmentationEnabled);
        Assert.True(status.EmbeddingsEnabled);
    }

    [Fact]
    public async Task Segmentation_Counts_Split_By_Status_And_Capacity_Exceeded_At_Active_Version()
    {
        var owner = await SeedUserAsync();
        var b1 = await SeedEligibleVideoBlobAsync(owner, 1);
        var b2 = await SeedEligibleVideoBlobAsync(owner, 2);
        var b3 = await SeedEligibleVideoBlobAsync(owner, 3);
        var b4 = await SeedEligibleVideoBlobAsync(owner, 4);
        await SeedEligibleVideoBlobAsync(owner, 5); // left unprocessed

        await SeedSegmentationIndexAsync(b1, 3, AiArtifactStatuses.Completed);
        await SeedSegmentationIndexAsync(b2, 3, AiArtifactStatuses.Failed);
        await SeedSegmentationIndexAsync(
            b3, 3, AiArtifactStatuses.Skipped, VideoSemanticErrorCodes.SegmentationCapacityExceeded, permanent: true);
        await SeedSegmentationIndexAsync(
            b4, 3, AiArtifactStatuses.Skipped, VideoSemanticErrorCodes.NoEligibleReference, permanent: true);

        var status = await NewService().GetStatusAsync();

        Assert.Equal(5, status.EligibleVideoBlobs);
        Assert.Equal(1, status.SegmentationCompleted);
        Assert.Equal(1, status.SegmentationFailed);
        Assert.Equal(1, status.SegmentationCapacityExceeded);
        Assert.Equal(1, status.SegmentationSkipped); // the non-capacity skip only
        Assert.Equal(1, status.SegmentationNotProcessed); // b5
    }

    [Fact]
    public async Task Historical_Version_Counts_Are_Never_Mixed_Into_Active_Counts()
    {
        var owner = await SeedUserAsync();
        var active = await SeedEligibleVideoBlobAsync(owner, 1);
        var historicalOk = await SeedEligibleVideoBlobAsync(owner, 2);
        var historicalFailed = await SeedEligibleVideoBlobAsync(owner, 3);

        await SeedSegmentationIndexAsync(active, 3, AiArtifactStatuses.Completed);
        await SeedSegmentationIndexAsync(historicalOk, 1, AiArtifactStatuses.Completed);
        await SeedSegmentationIndexAsync(historicalFailed, 1, AiArtifactStatuses.Failed);

        var status = await NewService().GetStatusAsync();

        Assert.Equal(1, status.SegmentationCompleted); // active version only
        var v1 = Assert.Single(status.HistoricalVersions);
        Assert.Equal(1, v1.SegmentationVersion);
        Assert.Equal(1, v1.Completed);
        Assert.Equal(1, v1.Failed);
    }

    [Fact]
    public async Task Embedding_Manifest_Counts_Cover_Pending_Completed_Partial_Failed_Skipped()
    {
        var owner = await SeedUserAsync();
        var profile = await SeedProfileAsync();
        var pending = await SeedEligibleVideoBlobAsync(owner, 1);
        var completed = await SeedEligibleVideoBlobAsync(owner, 2);
        var partial = await SeedEligibleVideoBlobAsync(owner, 3);
        var failed = await SeedEligibleVideoBlobAsync(owner, 4);

        var pendingIndex = await SeedSegmentationIndexAsync(pending, 3, AiArtifactStatuses.Completed);
        var completedIndex = await SeedSegmentationIndexAsync(completed, 3, AiArtifactStatuses.Completed);
        var partialIndex = await SeedSegmentationIndexAsync(partial, 3, AiArtifactStatuses.Completed);
        var failedIndex = await SeedSegmentationIndexAsync(failed, 3, AiArtifactStatuses.Completed);

        await SeedEmbeddingStatusAsync(completedIndex, profile.Id, VideoSemanticEmbeddingStatuses.Completed, 2, 2, 0);
        await SeedEmbeddingStatusAsync(partialIndex, profile.Id, VideoSemanticEmbeddingStatuses.Partial, 2, 1, 1);
        await SeedEmbeddingStatusAsync(failedIndex, profile.Id, VideoSemanticEmbeddingStatuses.Failed, 1, 0, 1);
        // `pendingIndex` deliberately has no row — implicit pending.
        _ = pendingIndex;

        var status = await NewService(profile).GetStatusAsync();

        Assert.Equal(profile.Key, status.ActiveEmbeddingProfileKey);
        Assert.True(status.ActiveEmbeddingProfileAvailable);
        Assert.Equal(1, status.EmbeddingManifestsPending);
        Assert.Equal(1, status.EmbeddingManifestsCompleted);
        Assert.Equal(1, status.EmbeddingManifestsPartial);
        Assert.Equal(1, status.EmbeddingManifestsFailed);
        Assert.Equal(0, status.EmbeddingManifestsSkipped);
        Assert.Equal(5, status.SamplesExpected); // 2 + 2 + 1
        Assert.Equal(3, status.SamplesCanonicallyEmbedded); // 2 + 1 + 0
        Assert.Equal(2, status.SamplesFailedOrMissing);
    }

    [Fact]
    public async Task Pgvector_Backend_Is_Unavailable_On_Sqlite_So_Sync_Counts_Report_Zero()
    {
        var profile = await SeedProfileAsync();

        var status = await NewService(profile).GetStatusAsync();

        Assert.False(status.PgvectorBackendAvailable);
        Assert.Equal(0, status.PgvectorSynchronizedProfileWide);
        Assert.Equal(0, status.PgvectorStaleOrMissingProfileWide);
    }

    [Fact]
    public async Task Disabled_Features_Are_Reported_As_Disabled_Even_With_Existing_Data()
    {
        var owner = await SeedUserAsync();
        var blob = await SeedEligibleVideoBlobAsync(owner, 1);
        await SeedSegmentationIndexAsync(blob, 3, AiArtifactStatuses.Completed);

        _segmentationOptions.Enabled = false;
        _embeddingOptions.Enabled = false;

        var status = await NewService().GetStatusAsync();

        Assert.False(status.SegmentationEnabled);
        Assert.False(status.EmbeddingsEnabled);
        // Existing counts still surface — a disabled flag hides nothing already computed.
        Assert.Equal(1, status.SegmentationCompleted);
    }

    [Fact]
    public async Task Ranking_Window_Reports_The_Configured_Per_Modality_Constants()
    {
        var status = await NewService().GetStatusAsync();

        Assert.Equal(MediaSemanticSearchService.PerModalityTopK, status.MaxRankedPhotoCandidates);
        Assert.Equal(MediaSemanticSearchService.PerModalityTopK, status.MaxRankedVideoCandidates);
        Assert.Equal(SemanticMediaCursor.RankingVersion, status.RankingContractVersion);
    }

    [Fact]
    public void Status_Dto_Never_Exposes_Owner_Or_Storage_Identifiers()
    {
        var forbidden = new[]
        {
            "StorageKey", "BlobObjectId", "FileItemId", "OwnerUserId", "Sha256",
            "PayloadJson", "TokenHash", "EmbeddingBytes", "Vector", "Path",
        };

        foreach (var prop in typeof(VideoSemanticStatus).GetProperties())
        {
            Assert.DoesNotContain(forbidden, f => prop.Name.Contains(f, StringComparison.Ordinal));
            Assert.NotEqual(typeof(Guid), prop.PropertyType);
        }
    }
}
