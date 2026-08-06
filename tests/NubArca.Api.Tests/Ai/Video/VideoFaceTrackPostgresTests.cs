using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Tests.Integration;
using Npgsql;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VFACE-01: the canonical-track invariants that must be enforced by the
// DATABASE — the analysis scope key, the track ordinal key, the temporal and
// unit-range check constraints, the FK cascades that keep tracks consistent with
// the manifest tree, and the coexistence of analysis versions and profiles.
// Verified against the real migration on a real PostgreSQL container.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class VideoFaceTrackPostgresTests : IAsyncLifetime
{
    private const int Dim = 8;

    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    public VideoFaceTrackPostgresTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        if (!_fixture.Available)
        {
            return;
        }

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!)
            .Options;
        await _fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- seeding -----------------------------------------------------------

    private sealed record Seeded(Guid BlobId, Guid IndexId);

    private async Task<Seeded> SeedManifestAsync(int version = 1)
    {
        await using var db = new AppDbContext(_dbOptions!);
        var sha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        db.BlobObjects.Add(new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = sha,
            SizeBytes = 1024,
            StorageKey = $"objects/{sha[..2]}/{sha[2..4]}/{sha}",
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        });
        var blobId = db.ChangeTracker.Entries<BlobObject>().Single().Entity.Id;

        var index = new VideoSemanticIndex
        {
            Id = Guid.NewGuid(), BlobObjectId = blobId, SegmentationVersion = version,
            Status = AiArtifactStatuses.Completed, AttemptCount = 1,
            DurationMilliseconds = 60_000, SegmentCount = 1, SampleCount = 1,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        db.VideoSemanticIndexes.Add(index);
        db.VideoSemanticSegments.Add(new VideoSemanticSegment
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = index.Id, SegmentIndex = 0,
            StartMilliseconds = 0, EndMilliseconds = 60_000,
            BoundaryReason = VideoSemanticBoundaryReasons.Start, CreatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        return new Seeded(blobId, index.Id);
    }

    private async Task<Guid> SeedProfileAsync(string keySuffix = "a")
    {
        await using var db = new AppDbContext(_dbOptions!);
        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = $"m-{Guid.NewGuid():N}", Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face,
            Dimension = Dim, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = $"p-{keySuffix}-{Guid.NewGuid():N}", AiModelId = model.Id,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face,
            Dimension = Dim, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private static VideoFaceAnalysisStatus NewAnalysis(
        Guid indexId, Guid detectionProfileId, Guid embeddingProfileId, int analysisVersion = 1) => new()
    {
        Id = Guid.NewGuid(),
        VideoSemanticIndexId = indexId,
        AnalysisVersion = analysisVersion,
        DetectionProfileId = detectionProfileId,
        EmbeddingProfileId = embeddingProfileId,
        Status = VideoFaceAnalysisStatuses.Completed,
        PlannedFrameCount = 10,
        ProcessedFrameCount = 10,
        FailedFrameCount = 0,
        TrackCount = 1,
        AttemptCount = 1,
        CreatedAt = DateTime.UtcNow,
        CompletedAt = DateTime.UtcNow,
    };

    private static VideoFaceTrack NewTrack(Guid analysisId, int trackIndex = 0) => new()
    {
        Id = Guid.NewGuid(),
        VideoFaceAnalysisStatusId = analysisId,
        TrackIndex = trackIndex,
        StartMilliseconds = 1_000,
        EndMilliseconds = 5_000,
        RepresentativeTimestampMilliseconds = 3_000,
        DetectionCount = 4,
        EmbeddingBytes = new byte[Dim * sizeof(float)],
        EmbeddingDimension = Dim,
        QualityScore = 0.5,
        RepresentativeBoundingBoxX = 0.2,
        RepresentativeBoundingBoxY = 0.2,
        RepresentativeBoundingBoxWidth = 0.3,
        RepresentativeBoundingBoxHeight = 0.3,
        CreatedAt = DateTime.UtcNow,
    };

    private static async Task<PostgresException> ExpectPostgresErrorAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(action);
        return Assert.IsType<PostgresException>(ex.InnerException);
    }

    // ---- schema ------------------------------------------------------------

    [SkippableFact]
    public async Task Migration_Creates_The_Face_Track_Tables()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using var db = new AppDbContext(_dbOptions!);
        foreach (var table in new[] { "video_face_analysis_statuses", "video_face_tracks" })
        {
            var exists = await db.Database
                .SqlQuery<bool>($"SELECT to_regclass({table}) IS NOT NULL AS \"Value\"")
                .SingleAsync();
            Assert.True(exists, $"{table} was not created by the migration.");
        }
    }

    [SkippableFact]
    public async Task The_Migration_Does_Not_Touch_The_Photo_Face_Substrate()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using var db = new AppDbContext(_dbOptions!);
        // The migration creates two NEW tables and analyses nothing: every
        // existing face/person table is still there and still empty.
        Assert.Equal(0, await db.FaceDetections.CountAsync());
        Assert.Equal(0, await db.FaceEmbeddings.CountAsync());
        Assert.Equal(0, await db.People.CountAsync());
        Assert.Equal(0, await db.PersonFaceAssignments.CountAsync());
        Assert.Equal(0, await db.VideoFaceAnalysisStatuses.CountAsync());
        Assert.Equal(0, await db.VideoFaceTracks.CountAsync());
        Assert.Equal(0, await db.BackgroundJobs.CountAsync());
    }

    // ---- scope + ordinal keys ----------------------------------------------

    [SkippableFact]
    public async Task One_Analysis_Per_Manifest_Version_And_Profile_Pair_Is_Enforced()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var seeded = await SeedManifestAsync();
        var profileId = await SeedProfileAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoFaceAnalysisStatuses.Add(NewAnalysis(seeded.IndexId, profileId, profileId));
        await db.SaveChangesAsync();

        db.VideoFaceAnalysisStatuses.Add(NewAnalysis(seeded.IndexId, profileId, profileId));
        var error = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());

        Assert.Equal("23505", error.SqlState);   // unique_violation
        Assert.Contains("ux_video_face_analysis_statuses_scope", error.ConstraintName);
    }

    [SkippableFact]
    public async Task One_Track_Per_Analysis_And_Ordinal_Is_Enforced()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var seeded = await SeedManifestAsync();
        var profileId = await SeedProfileAsync();

        await using var db = new AppDbContext(_dbOptions!);
        var analysis = NewAnalysis(seeded.IndexId, profileId, profileId);
        db.VideoFaceAnalysisStatuses.Add(analysis);
        db.VideoFaceTracks.Add(NewTrack(analysis.Id));
        await db.SaveChangesAsync();

        db.VideoFaceTracks.Add(NewTrack(analysis.Id));
        var error = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());

        Assert.Equal("23505", error.SqlState);
        Assert.Contains("ux_video_face_tracks_analysis_ordinal", error.ConstraintName);
    }

    // ---- coexistence --------------------------------------------------------

    [SkippableFact]
    public async Task Analysis_Versions_Coexist_For_The_Same_Manifest_And_Profile()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var seeded = await SeedManifestAsync();
        var profileId = await SeedProfileAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoFaceAnalysisStatuses.Add(NewAnalysis(seeded.IndexId, profileId, profileId, 1));
        db.VideoFaceAnalysisStatuses.Add(NewAnalysis(seeded.IndexId, profileId, profileId, 2));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.VideoFaceAnalysisStatuses
            .CountAsync(s => s.VideoSemanticIndexId == seeded.IndexId));
    }

    [SkippableFact]
    public async Task Profiles_Are_Isolated_For_The_Same_Manifest_And_Version()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var seeded = await SeedManifestAsync();
        var profileA = await SeedProfileAsync("a");
        var profileB = await SeedProfileAsync("b");

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoFaceAnalysisStatuses.Add(NewAnalysis(seeded.IndexId, profileA, profileA));
        db.VideoFaceAnalysisStatuses.Add(NewAnalysis(seeded.IndexId, profileB, profileB));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.VideoFaceAnalysisStatuses
            .CountAsync(s => s.VideoSemanticIndexId == seeded.IndexId));
    }

    [SkippableFact]
    public async Task Segmentation_Versions_Coexist_Through_Their_Own_Manifests()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var v1 = await SeedManifestAsync(version: 1);
        var v2 = await SeedManifestAsync(version: 2);
        var profileId = await SeedProfileAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoFaceAnalysisStatuses.Add(NewAnalysis(v1.IndexId, profileId, profileId));
        db.VideoFaceAnalysisStatuses.Add(NewAnalysis(v2.IndexId, profileId, profileId));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.VideoFaceAnalysisStatuses.CountAsync());
    }

    // ---- check constraints ---------------------------------------------------

    [SkippableTheory]
    [InlineData(5_000L, 1_000L, 3_000L)]        // end before start
    [InlineData(1_000L, 5_000L, 500L)]          // representative before start
    [InlineData(1_000L, 5_000L, 6_000L)]        // representative after end
    public async Task An_Out_Of_Order_Interval_Is_Rejected(
        long start, long end, long representative)
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var seeded = await SeedManifestAsync();
        var profileId = await SeedProfileAsync();

        await using var db = new AppDbContext(_dbOptions!);
        var analysis = NewAnalysis(seeded.IndexId, profileId, profileId);
        db.VideoFaceAnalysisStatuses.Add(analysis);
        await db.SaveChangesAsync();

        var track = NewTrack(analysis.Id);
        track.StartMilliseconds = start;
        track.EndMilliseconds = end;
        track.RepresentativeTimestampMilliseconds = representative;
        db.VideoFaceTracks.Add(track);

        var error = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());
        Assert.Equal("23514", error.SqlState);   // check_violation
        Assert.Contains("ck_video_face_tracks_interval_ordered", error.ConstraintName);
    }

    [SkippableFact]
    public async Task A_Track_Without_Detections_Or_A_Dimension_Is_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var seeded = await SeedManifestAsync();
        var profileId = await SeedProfileAsync();

        await using var db = new AppDbContext(_dbOptions!);
        var analysis = NewAnalysis(seeded.IndexId, profileId, profileId);
        db.VideoFaceAnalysisStatuses.Add(analysis);
        await db.SaveChangesAsync();

        var empty = NewTrack(analysis.Id);
        empty.DetectionCount = 0;
        db.VideoFaceTracks.Add(empty);
        var detectionError = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());
        Assert.Contains("ck_video_face_tracks_detections_positive", detectionError.ConstraintName);
        db.ChangeTracker.Clear();

        var dimensionless = NewTrack(analysis.Id, trackIndex: 1);
        dimensionless.EmbeddingDimension = 0;
        db.VideoFaceTracks.Add(dimensionless);
        var dimensionError = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());
        Assert.Contains("ck_video_face_tracks_dimension_positive", dimensionError.ConstraintName);
    }

    [SkippableFact]
    public async Task An_Out_Of_Range_Quality_Or_Bounding_Box_Is_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var seeded = await SeedManifestAsync();
        var profileId = await SeedProfileAsync();

        await using var db = new AppDbContext(_dbOptions!);
        var analysis = NewAnalysis(seeded.IndexId, profileId, profileId);
        db.VideoFaceAnalysisStatuses.Add(analysis);
        await db.SaveChangesAsync();

        var badQuality = NewTrack(analysis.Id);
        badQuality.QualityScore = 1.5;
        db.VideoFaceTracks.Add(badQuality);
        var qualityError = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());
        Assert.Contains("ck_video_face_tracks_quality_unit_range", qualityError.ConstraintName);
        db.ChangeTracker.Clear();

        var badBox = NewTrack(analysis.Id, trackIndex: 1);
        badBox.RepresentativeBoundingBoxWidth = 1.4;
        db.VideoFaceTracks.Add(badBox);
        var boxError = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());
        Assert.Contains("ck_video_face_tracks_bbox_unit_range", boxError.ConstraintName);
    }

    [SkippableFact]
    public async Task A_Non_Positive_Analysis_Version_Is_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var seeded = await SeedManifestAsync();
        var profileId = await SeedProfileAsync();

        await using var db = new AppDbContext(_dbOptions!);
        var analysis = NewAnalysis(seeded.IndexId, profileId, profileId);
        analysis.AnalysisVersion = 0;
        db.VideoFaceAnalysisStatuses.Add(analysis);

        var error = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());
        Assert.Equal("23514", error.SqlState);
        Assert.Contains("ck_video_face_analysis_statuses_version_positive", error.ConstraintName);
    }

    // ---- cascades ------------------------------------------------------------

    [SkippableFact]
    public async Task Deleting_A_Manifest_Cascades_To_Analyses_And_Tracks()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var seeded = await SeedManifestAsync();
        var profileId = await SeedProfileAsync();

        await using var db = new AppDbContext(_dbOptions!);
        var analysis = NewAnalysis(seeded.IndexId, profileId, profileId);
        db.VideoFaceAnalysisStatuses.Add(analysis);
        db.VideoFaceTracks.Add(NewTrack(analysis.Id));
        await db.SaveChangesAsync();

        var index = await db.VideoSemanticIndexes.SingleAsync(i => i.Id == seeded.IndexId);
        db.VideoSemanticIndexes.Remove(index);
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.VideoFaceAnalysisStatuses.CountAsync());
        Assert.Equal(0, await db.VideoFaceTracks.CountAsync());
    }

    [SkippableFact]
    public async Task Deleting_An_Analysis_Cascades_To_Its_Tracks_Only()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var seeded = await SeedManifestAsync();
        var profileId = await SeedProfileAsync();

        await using var db = new AppDbContext(_dbOptions!);
        var v1 = NewAnalysis(seeded.IndexId, profileId, profileId, 1);
        var v2 = NewAnalysis(seeded.IndexId, profileId, profileId, 2);
        db.VideoFaceAnalysisStatuses.AddRange(v1, v2);
        db.VideoFaceTracks.Add(NewTrack(v1.Id));
        db.VideoFaceTracks.Add(NewTrack(v2.Id));
        await db.SaveChangesAsync();

        db.VideoFaceAnalysisStatuses.Remove(
            await db.VideoFaceAnalysisStatuses.SingleAsync(s => s.Id == v1.Id));
        await db.SaveChangesAsync();

        var remaining = Assert.Single(await db.VideoFaceTracks.ToListAsync());
        Assert.Equal(v2.Id, remaining.VideoFaceAnalysisStatusId);
    }

    [SkippableFact]
    public async Task An_In_Use_Face_Profile_Cannot_Be_Deleted()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var seeded = await SeedManifestAsync();
        var profileId = await SeedProfileAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoFaceAnalysisStatuses.Add(NewAnalysis(seeded.IndexId, profileId, profileId));
        await db.SaveChangesAsync();

        // Raw SQL: EF's change tracker would sever the relationship in memory
        // before the server ever saw the statement, and the point here is that
        // the DATABASE refuses to orphan an analysis from its face package.
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database
            .ExecuteSqlAsync($"DELETE FROM ai_profiles WHERE \"Id\" = {profileId}"));

        Assert.Equal("23503", error.SqlState);   // foreign_key_violation
    }
}
