using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Video;
using NubArca.Api.Ai.Video.Faces;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VFACE-01: eligibility, idempotency, per-frame failure isolation, outcome
// semantics and the canonical-evidence invariants of one analysis attempt.
//
// A fake streaming extractor stands in for FFmpeg (emitting REAL JPEGs, so the
// pixel-size gate runs its true decode path) and fake face backends stand in for
// the detector/recognizer — no binary and no model are needed.
public sealed class VideoFaceAnalysisServiceTests : IDisposable
{
    private const int Dim = 4;

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly BlobService _blobs;
    private readonly FileItemService _files;
    private readonly AiVectorSerializer _serializer = new();
    private readonly FakeFrameStreamExtractor _extractor = new();
    private readonly FakeFaceBackend _backend = new();

    public VideoFaceAnalysisServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-vface-{Guid.NewGuid():N}");
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

    private VideoFaceAnalysisOptions _options = NewOptions();

    private static VideoFaceAnalysisOptions NewOptions() => new()
    {
        Enabled = true,
        AnalysisVersion = 1,
        FrameIntervalMilliseconds = 1000,
        MaximumFramesPerSegment = 60,
        MaximumFramesPerVideo = 900,
        MaximumFacesPerFrame = 8,
        MinimumDetectionConfidence = 0.5,
        MinimumFaceSizePixels = 16,
        QualityReferenceFaceSizePixels = 64,
        MinimumQualityScore = 0.05,
        MaximumTrackGapMilliseconds = 2000,
        MinimumTrackDetections = 3,
        ProcessTimeoutSeconds = 600,
    };

    private VideoFaceAnalysisService NewService()
        => new(
            _db, _blobs, _extractor, _serializer, Options.Create(_options),
            TimeProvider.System, NullLogger<VideoFaceAnalysisService>.Instance);

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
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face,
            Dimension = dimension, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = $"p-{Guid.NewGuid():N}", AiModelId = model.Id,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face,
            Dimension = dimension, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(profile);
        await _db.SaveChangesAsync();
        return profile;
    }

    private sealed record SeededVideo(Guid BlobId, Guid FileId, Guid IndexId);

    private async Task<SeededVideo> SeedVideoWithManifestAsync(
        Guid owner, long durationMs = 10_000, string manifestStatus = "completed", int version = 1)
    {
        var file = await _files.CreateAsync(
            owner, null, $"v-{Guid.NewGuid():N}.mp4", "video/mp4",
            new MemoryStream(Guid.NewGuid().ToByteArray()));

        var index = new VideoSemanticIndex
        {
            Id = Guid.NewGuid(), BlobObjectId = file.BlobObjectId, SegmentationVersion = version,
            Status = manifestStatus, AttemptCount = 1,
            DurationMilliseconds = durationMs, SegmentCount = 1, SampleCount = 1,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        _db.VideoSemanticIndexes.Add(index);
        _db.VideoSemanticSegments.Add(new VideoSemanticSegment
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = index.Id, SegmentIndex = 0,
            StartMilliseconds = 0, EndMilliseconds = durationMs,
            BoundaryReason = VideoSemanticBoundaryReasons.Start, CreatedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return new SeededVideo(file.BlobObjectId, file.Id, index.Id);
    }

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

    private Task<VideoFaceAnalysisStatus?> LoadAsync(Guid indexId, Guid profileId, int analysisVersion = 1)
        => _db.VideoFaceAnalysisStatuses.AsNoTracking().FirstOrDefaultAsync(
            s => s.VideoSemanticIndexId == indexId
                && s.AnalysisVersion == analysisVersion
                && s.DetectionProfileId == profileId
                && s.EmbeddingProfileId == profileId);

    private Task<List<VideoFaceTrack>> LoadTracksAsync(Guid analysisId)
        => _db.VideoFaceTracks.AsNoTracking()
            .Where(t => t.VideoFaceAnalysisStatusId == analysisId)
            .OrderBy(t => t.TrackIndex)
            .ToListAsync();

    // ---- happy path --------------------------------------------------------

    [Fact]
    public async Task Produces_Canonical_Tracks_For_Two_Simultaneous_Faces()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.TwoPeople();

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(10, outcome.PlannedFrameCount);
        Assert.Equal(10, outcome.ProcessedFrameCount);
        Assert.Equal(0, outcome.FailedFrameCount);
        Assert.Equal(2, outcome.TrackCount);

        var analysis = await LoadAsync(video.IndexId, profile.Id);
        Assert.Equal(VideoFaceAnalysisStatuses.Completed, analysis!.Status);
        Assert.Null(analysis.ErrorCode);
        Assert.NotNull(analysis.CompletedAt);

        var tracks = await LoadTracksAsync(analysis.Id);
        Assert.Equal(2, tracks.Count);
        Assert.Equal(new[] { 0, 1 }, tracks.Select(t => t.TrackIndex));
        Assert.All(tracks, t =>
        {
            Assert.True(t.DetectionCount > 0);
            Assert.Equal(Dim, t.EmbeddingDimension);
            Assert.Equal(Dim * sizeof(float), t.EmbeddingBytes.Length);
            Assert.InRange(
                t.RepresentativeTimestampMilliseconds, t.StartMilliseconds, t.EndMilliseconds);
            Assert.InRange(t.QualityScore, 0d, 1d);
            // Gate 4: no crop is persisted by this slice.
            Assert.Null(t.RepresentativeCropBlobObjectId);
        });
    }

    [Fact]
    public async Task Track_Embeddings_Are_Finite_And_Normalized()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.OnePerson();

        await NewService().ProcessBlobAsync(_backend, _backend, profile, video.BlobId, 1, 1);

        var analysis = await LoadAsync(video.IndexId, profile.Id);
        var track = Assert.Single(await LoadTracksAsync(analysis!.Id));
        var vector = _serializer.Deserialize(track.EmbeddingBytes, track.EmbeddingDimension);
        Assert.All(vector, v => Assert.True(float.IsFinite(v)));
        Assert.Equal(1d, Math.Sqrt(vector.Sum(v => (double)v * v)), 4);
    }

    [Fact]
    public async Task No_Frame_Crop_Or_Derivative_Blob_Is_Ever_Persisted()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.OnePerson();
        var blobsBefore = await _db.BlobObjects.CountAsync();
        var thumbsBefore = await _db.FileThumbnails.CountAsync();
        var previewsBefore = await _db.FacePreviews.CountAsync();

        await NewService().ProcessBlobAsync(_backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(blobsBefore, await _db.BlobObjects.CountAsync());
        Assert.Equal(thumbsBefore, await _db.FileThumbnails.CountAsync());
        Assert.Equal(previewsBefore, await _db.FacePreviews.CountAsync());
    }

    [Fact]
    public async Task The_Photo_Face_Substrate_Is_Never_Touched()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.TwoPeople();

        await NewService().ProcessBlobAsync(_backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Empty(await _db.FaceDetections.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.FaceEmbeddings.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.People.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.PersonFaceAssignments.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.FaceClusters.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.BlobAiArtifactStatuses.AsNoTracking().ToListAsync());
    }

    // ---- terminal non-retryable outcomes ------------------------------------

    [Fact]
    public async Task No_Faces_Found_Is_Terminal_And_Never_Retried()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.NoFaces();

        var first = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Skipped, first.Kind);
        Assert.Equal(VideoFaceErrorCodes.NoFacesFound, first.ErrorCode);
        Assert.Equal(0, first.TrackCount);

        var callsAfterFirst = _backend.DetectCalls;
        var second = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.AlreadyTerminal, second.Kind);
        Assert.Equal(callsAfterFirst, _backend.DetectCalls);
    }

    [Fact]
    public async Task Faces_Without_A_Long_Enough_Track_Are_Terminal_Too()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        // One person, visible in a single frame only: real evidence, but below
        // the minimum-detections floor.
        _backend.OnePerson(onlyFrameTimestamps: [500]);

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(VideoFaceErrorCodes.NoTracksRetained, outcome.ErrorCode);
        Assert.Empty(await _db.VideoFaceTracks.AsNoTracking().ToListAsync());
    }

    // ---- failure isolation ---------------------------------------------------

    [Fact]
    public async Task A_Partial_Frame_Failure_Still_Yields_Tracks()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.OnePerson();
        _extractor.FailTimestamps.Add(500);
        _extractor.FailTimestamps.Add(1500);

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Partial, outcome.Kind);
        Assert.Equal(10, outcome.PlannedFrameCount);
        Assert.Equal(8, outcome.ProcessedFrameCount);
        Assert.Equal(2, outcome.FailedFrameCount);
        Assert.Equal(VideoFaceErrorCodes.FrameExtractFailed, outcome.ErrorCode);
        Assert.True(outcome.TrackCount > 0);
    }

    [Fact]
    public async Task All_Frames_Failing_Is_A_Retryable_Failure_With_No_Tracks()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.OnePerson();
        _extractor.FailAll = true;

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(0, outcome.ProcessedFrameCount);
        Assert.Equal(VideoFaceErrorCodes.FrameExtractFailed, outcome.ErrorCode);
        Assert.Empty(await _db.VideoFaceTracks.AsNoTracking().ToListAsync());

        // Failed is NOT terminal: the next run re-analyses the blob.
        _extractor.FailAll = false;
        var retry = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);
        Assert.Equal(VideoFaceAnalysisOutcomeKind.Completed, retry.Kind);
    }

    [Fact]
    public async Task A_Staging_Failure_Fails_The_Whole_Attempt_Retryably()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _extractor.StagingErrorCode = VideoSemanticErrorCodes.BlobStorage;

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(VideoFaceErrorCodes.BlobStorage, outcome.ErrorCode);
    }

    [Fact]
    public async Task A_Detector_Throw_Fails_Only_Its_Own_Frame()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.OnePerson();
        _backend.ThrowDetectionAt.Add(2500);

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Partial, outcome.Kind);
        Assert.Equal(1, outcome.FailedFrameCount);
        Assert.Equal(VideoFaceErrorCodes.FaceDetectionFailed, outcome.ErrorCode);
        Assert.True(outcome.TrackCount > 0);
    }

    [Fact]
    public async Task A_Wrong_Dimension_Embedding_Never_Reaches_A_Track()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.OnePerson();
        _backend.EmbeddingDimensionOverride = Dim + 1;

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(VideoFaceErrorCodes.NoFacesFound, outcome.ErrorCode);
        Assert.Empty(await _db.VideoFaceTracks.AsNoTracking().ToListAsync());
    }

    // ---- eligibility ---------------------------------------------------------

    [Fact]
    public async Task An_Incomplete_Manifest_Writes_Nothing()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner, manifestStatus: AiArtifactStatuses.Failed);
        var profile = await SeedProfileAsync();

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.NotEligible, outcome.Kind);
        Assert.Equal(VideoFaceErrorCodes.SegmentationMissing, outcome.ErrorCode);
        Assert.Empty(await _db.VideoFaceAnalysisStatuses.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_Non_Face_Profile_Writes_Nothing()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        profile.Capability = AiCapabilities.ImageEmbedding;

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.NotEligible, outcome.Kind);
        Assert.Equal(VideoFaceErrorCodes.ProfileMissing, outcome.ErrorCode);
        Assert.Empty(await _db.VideoFaceAnalysisStatuses.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Duplicate_File_References_Share_One_Analysis_And_One_Track_Set()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.OnePerson();

        // A second owner uploading identical bytes dedups onto the SAME blob.
        var other = await SeedUserAsync();
        var duplicate = await _files.CreateAsync(
            other, null, "copy.mp4", "video/mp4",
            await _blobs.OpenContentAsync(video.BlobId));
        Assert.Equal(video.BlobId, duplicate.BlobObjectId);

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Completed, outcome.Kind);
        var analysis = Assert.Single(await _db.VideoFaceAnalysisStatuses.AsNoTracking().ToListAsync());
        var tracks = await LoadTracksAsync(analysis.Id);
        Assert.NotEmpty(tracks);
        // Canonical evidence carries no owner or file identity at all.
        Assert.All(tracks, t => Assert.Null(t.RepresentativeCropBlobObjectId));
    }

    [Fact]
    public async Task A_Blob_With_A_Vault_Reference_And_A_Normal_One_Is_Analysed()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.OnePerson();

        var other = await SeedUserAsync();
        var duplicate = await _files.CreateAsync(
            other, null, "copy.mp4", "video/mp4",
            await _blobs.OpenContentAsync(video.BlobId));
        await MoveToVaultAsync(other, duplicate.Id);

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Completed, outcome.Kind);
    }

    [Fact]
    public async Task A_Vault_Only_Blob_Is_Skipped_Permanently()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        await MoveToVaultAsync(owner, video.FileId);

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(VideoFaceErrorCodes.NoEligibleReference, outcome.ErrorCode);
        Assert.Equal(0, _backend.DetectCalls);
        Assert.Empty(await _db.VideoFaceTracks.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_Deleted_Reference_Is_Skipped_Permanently()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        var file = await _db.FileItems.SingleAsync(f => f.Id == video.FileId);
        file.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(VideoFaceErrorCodes.NoEligibleReference, outcome.ErrorCode);
    }

    // ---- versioning + idempotency -------------------------------------------

    [Fact]
    public async Task A_Completed_Analysis_Is_Never_Rebuilt()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.OnePerson();

        await NewService().ProcessBlobAsync(_backend, _backend, profile, video.BlobId, 1, 1);
        var callsAfterFirst = _backend.DetectCalls;
        var trackIdsAfterFirst = (await _db.VideoFaceTracks.AsNoTracking().ToListAsync())
            .Select(t => t.Id).OrderBy(id => id).ToList();

        var second = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.AlreadyTerminal, second.Kind);
        Assert.Equal(callsAfterFirst, _backend.DetectCalls);
        Assert.Equal(
            trackIdsAfterFirst,
            (await _db.VideoFaceTracks.AsNoTracking().ToListAsync())
                .Select(t => t.Id).OrderBy(id => id).ToList());
    }

    [Fact]
    public async Task A_New_Analysis_Version_Coexists_With_The_Previous_One()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.OnePerson();

        await NewService().ProcessBlobAsync(_backend, _backend, profile, video.BlobId, 1, 1);
        await NewService().ProcessBlobAsync(_backend, _backend, profile, video.BlobId, 1, 2);

        var analyses = await _db.VideoFaceAnalysisStatuses.AsNoTracking()
            .OrderBy(s => s.AnalysisVersion).ToListAsync();
        Assert.Equal(2, analyses.Count);
        Assert.Equal(new[] { 1, 2 }, analyses.Select(a => a.AnalysisVersion));
        Assert.All(analyses, a => Assert.NotEmpty(LoadTracksAsync(a.Id).GetAwaiter().GetResult()));
    }

    [Fact]
    public async Task Reanalysing_A_Failed_Attempt_Replaces_Its_Track_Set()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.OnePerson();
        _extractor.FailTimestamps.Add(500);

        var first = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);
        Assert.Equal(VideoFaceAnalysisOutcomeKind.Partial, first.Kind);
        var firstTrackIds = (await _db.VideoFaceTracks.AsNoTracking().ToListAsync())
            .Select(t => t.Id).ToList();

        _extractor.FailTimestamps.Clear();
        var second = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Completed, second.Kind);
        var analysis = Assert.Single(await _db.VideoFaceAnalysisStatuses.AsNoTracking().ToListAsync());
        Assert.Equal(2, analysis.AttemptCount);
        var tracks = await LoadTracksAsync(analysis.Id);
        Assert.NotEmpty(tracks);
        Assert.Empty(tracks.Select(t => t.Id).Intersect(firstTrackIds));
        Assert.Equal(new[] { 0 }, tracks.Select(t => t.TrackIndex));
    }

    [Fact]
    public async Task Two_Profiles_Produce_Independent_Analyses()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var first = await SeedProfileAsync();
        var second = await SeedProfileAsync();
        _backend.OnePerson();

        await NewService().ProcessBlobAsync(_backend, _backend, first, video.BlobId, 1, 1);
        await NewService().ProcessBlobAsync(_backend, _backend, second, video.BlobId, 1, 1);

        var analyses = await _db.VideoFaceAnalysisStatuses.AsNoTracking().ToListAsync();
        Assert.Equal(2, analyses.Count);
        Assert.Equal(
            new[] { first.Id, second.Id }.OrderBy(id => id),
            analyses.Select(a => a.DetectionProfileId).OrderBy(id => id));
        Assert.All(analyses, a => Assert.Equal(a.DetectionProfileId, a.EmbeddingProfileId));
    }

    // ---- cancellation ---------------------------------------------------------

    [Fact]
    public async Task Cancellation_Stays_Cancellation_And_Writes_No_Row()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.OnePerson();
        using var cts = new CancellationTokenSource();
        _extractor.OnFrame = () => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewService().ProcessBlobAsync(
                _backend, _backend, profile, video.BlobId, 1, 1, cts.Token));

        Assert.Empty(await _db.VideoFaceAnalysisStatuses.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.VideoFaceTracks.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task The_Analysis_Budget_Records_A_Retryable_Timeout()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _backend.OnePerson();
        _options = NewOptions();
        _options.ProcessTimeoutSeconds = 1;
        _extractor.DelayPerFrame = TimeSpan.FromMilliseconds(400);

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(VideoFaceErrorCodes.Timeout, outcome.ErrorCode);

        var analysis = await LoadAsync(video.IndexId, profile.Id);
        Assert.Equal(VideoFaceAnalysisStatuses.Failed, analysis!.Status);
    }

    // ---- bounds ---------------------------------------------------------------

    [Fact]
    public async Task Sampling_Respects_The_Per_Video_Frame_Cap()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner, durationMs: 600_000);
        var profile = await SeedProfileAsync();
        _options = NewOptions();
        _options.MaximumFramesPerSegment = 40;
        _options.MaximumFramesPerVideo = 25;
        _backend.NoFaces();

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.True(outcome.PlannedFrameCount <= 25);
        Assert.Equal(outcome.PlannedFrameCount, _extractor.RequestedFrames);
    }

    [Fact]
    public async Task Faces_Below_The_Pixel_Gate_Are_Never_Accepted()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        // The fake emits 128x64 frames; a 0.05-wide box is ~6 px across.
        _backend.OnePerson(size: 0.05);
        _options = NewOptions();
        _options.MinimumFaceSizePixels = 16;

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(VideoFaceErrorCodes.NoFacesFound, outcome.ErrorCode);
    }

    [Fact]
    public async Task The_Pixel_Gate_Uses_The_Real_Frame_Geometry()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        // Same normalized box, but a VERTICAL frame: the short edge is what the
        // gate measures, so orientation changes the verdict.
        _extractor.FrameWidth = 64;
        _extractor.FrameHeight = 512;
        _backend.OnePerson(size: 0.2);
        _options = NewOptions();
        _options.MinimumFaceSizePixels = 20;

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        // 0.2 * 64 = 12.8 px on the narrow axis → below the 20 px gate.
        Assert.Equal(VideoFaceAnalysisOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(VideoFaceErrorCodes.NoFacesFound, outcome.ErrorCode);
    }

    [Fact]
    public async Task Extraction_Uses_The_Face_Frame_Edge_Not_The_Video_Embedding_One()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _options = NewOptions();
        _options.FrameMaxEdge = 1280;
        _backend.NoFaces();

        await NewService().ProcessBlobAsync(_backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(1280, _extractor.RequestedFrameMaxEdge);
        // The SigLIP2 video-embedding default plays no part in this pipeline.
        Assert.NotEqual(
            new VideoVisualEmbeddingOptions().FrameMaxEdge, _extractor.RequestedFrameMaxEdge);
    }

    [Fact]
    public async Task The_Default_Face_Frame_Edge_Reaches_The_Extractor()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _options = NewOptions();
        _options.FrameMaxEdge = VideoFaceAnalysisOptions.DefaultFrameMaxEdge;
        _backend.NoFaces();

        await NewService().ProcessBlobAsync(_backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(768, _extractor.RequestedFrameMaxEdge);
    }

    [Fact]
    public async Task Faces_Per_Frame_Are_Capped()
    {
        var owner = await SeedUserAsync();
        var video = await SeedVideoWithManifestAsync(owner);
        var profile = await SeedProfileAsync();
        _options = NewOptions();
        _options.MaximumFacesPerFrame = 2;
        // A frame wide enough that six side-by-side faces all clear the pixel
        // gate, so this test isolates the CAP and nothing else.
        _extractor.FrameWidth = 512;
        _extractor.FrameHeight = 256;
        _backend.Crowd(faces: 6);

        var outcome = await NewService().ProcessBlobAsync(
            _backend, _backend, profile, video.BlobId, 1, 1);

        Assert.Equal(VideoFaceAnalysisOutcomeKind.Completed, outcome.Kind);
        Assert.True(outcome.TrackCount <= 2, $"expected at most 2 tracks, got {outcome.TrackCount}.");
    }

    // ---- fakes ---------------------------------------------------------------

    private sealed class FakeFrameStreamExtractor : IVideoSemanticFrameStreamExtractor
    {
        public int FrameWidth { get; set; } = 128;
        public int FrameHeight { get; set; } = 64;
        public HashSet<long> FailTimestamps { get; } = [];
        public bool FailAll { get; set; }
        public string? StagingErrorCode { get; set; }
        public Action? OnFrame { get; set; }
        public TimeSpan DelayPerFrame { get; set; } = TimeSpan.Zero;
        public int RequestedFrames { get; private set; }
        public int? RequestedFrameMaxEdge { get; private set; }

        public async Task<string?> ExtractFramesStreamingAsync(
            Func<CancellationToken, Task<Stream>> openBlobContent,
            IReadOnlyList<VideoSemanticFrameRequest> requests,
            int frameMaxEdge,
            Func<VideoSemanticFrameResult, CancellationToken, Task> onFrame,
            CancellationToken cancellationToken)
        {
            RequestedFrames = requests.Count;
            RequestedFrameMaxEdge = frameMaxEdge;
            if (StagingErrorCode is not null)
            {
                return StagingErrorCode;
            }

            var jpeg = Jpeg(FrameWidth, FrameHeight);
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OnFrame?.Invoke();
                if (DelayPerFrame > TimeSpan.Zero)
                {
                    await Task.Delay(DelayPerFrame, cancellationToken);
                }

                var failed = FailAll || FailTimestamps.Contains(request.TimestampMilliseconds);
                await onFrame(
                    new VideoSemanticFrameResult(
                        request.SampleId, request.TimestampMilliseconds,
                        failed ? null : jpeg,
                        failed ? VideoSemanticErrorCodes.FrameExtraction : null),
                    cancellationToken);
            }

            return null;
        }

        private static byte[] Jpeg(int width, int height)
        {
            using var image = new Image<Rgb24>(width, height);
            using var buffer = new MemoryStream();
            image.Save(buffer, new JpegEncoder());
            return buffer.ToArray();
        }
    }

    // A scripted detector + recognizer pair. Each configured "person" has a fixed
    // normalized position and a fixed identity vector, so the tracker's behaviour
    // over the frame sequence is fully determined by the test.
    private sealed class FakeFaceBackend : IFaceDetector, IFaceEmbedder
    {
        private readonly List<Person> _people = new();
        private long[]? _onlyFrames;
        private readonly List<float[]> _pendingVectors = new();

        public int DetectCalls { get; private set; }
        public HashSet<long> ThrowDetectionAt { get; } = [];
        public int? EmbeddingDimensionOverride { get; set; }

        public string Provider => AiProviders.Deterministic;

        public bool Supports(string capability)
            => capability is AiCapabilities.FaceDetection or AiCapabilities.FaceEmbedding;

        public void NoFaces()
        {
            _people.Clear();
            _onlyFrames = null;
        }

        public void OnePerson(double size = 0.4, long[]? onlyFrameTimestamps = null)
        {
            _people.Clear();
            _people.Add(new Person(0.30, 0.30, size, [1f, 0f, 0f, 0f]));
            _onlyFrames = onlyFrameTimestamps;
        }

        public void TwoPeople()
        {
            _people.Clear();
            _people.Add(new Person(0.05, 0.30, 0.35, [1f, 0f, 0f, 0f]));
            _people.Add(new Person(0.60, 0.30, 0.35, [0f, 1f, 0f, 0f]));
            _onlyFrames = null;
        }

        public void Crowd(int faces)
        {
            _people.Clear();
            for (var i = 0; i < faces; i++)
            {
                var vector = new float[4];
                vector[i % 4] = 1f;
                _people.Add(new Person(0.02 + (i * 0.16), 0.30, 0.14, vector));
            }

            _onlyFrames = null;
        }

        public Task<AiFaceDetectionResult> DetectFacesAsync(
            ReadOnlyMemory<byte> imageBytes, AiProfile profile,
            CancellationToken cancellationToken = default)
        {
            DetectCalls++;
            _pendingVectors.Clear();

            // The service does not tell the detector which timestamp it is on, so
            // the fake replays its script by call ordinal (frames arrive in plan
            // order, one call each).
            var timestamp = 500 + ((DetectCalls - 1) * 1000L);
            if (ThrowDetectionAt.Contains(timestamp))
            {
                throw new InvalidOperationException("simulated detection failure");
            }

            if (_onlyFrames is not null && !_onlyFrames.Contains(timestamp))
            {
                return Task.FromResult(new AiFaceDetectionResult(Array.Empty<DetectedFace>()));
            }

            var faces = new List<DetectedFace>();
            foreach (var person in _people)
            {
                faces.Add(new DetectedFace(
                    person.X, person.Y, person.Size, person.Size, 0.95,
                    [new FaceLandmark(person.X, person.Y)]));
                _pendingVectors.Add(person.Vector);
            }

            return Task.FromResult(new AiFaceDetectionResult(faces));
        }

        public Task<AiEmbeddingResult> EmbedFaceAsync(
            ReadOnlyMemory<byte> faceCropBytes, AiProfile profile,
            CancellationToken cancellationToken = default)
        {
            var vector = _pendingVectors.Count > 0 ? _pendingVectors[0] : [1f, 0f, 0f, 0f];
            if (_pendingVectors.Count > 0)
            {
                _pendingVectors.RemoveAt(0);
            }

            if (EmbeddingDimensionOverride is int dimension)
            {
                vector = new float[dimension];
                vector[0] = 1f;
            }

            return Task.FromResult(new AiEmbeddingResult(vector, vector.Length, "cosine"));
        }

        private readonly record struct Person(double X, double Y, double Size, float[] Vector);
    }
}
