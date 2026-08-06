using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Tests.Integration;
using Npgsql;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VFACE-02: the owner-decision invariants that must be enforced by the DATABASE —
// one decision per owner and track, the decision/person pairing, and above all
// the COMPOSITE foreign key that makes a cross-owner person assignment
// unrepresentable rather than merely rejected by the service.
//
// Verified against the real migration on a real PostgreSQL container.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class VideoFaceTrackPersonPostgresTests : IAsyncLifetime
{
    private const int Dim = 8;

    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    public VideoFaceTrackPersonPostgresTests(PostgresContainerFixture fixture)
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

    private async Task<Guid> SeedUserAsync()
    {
        await using var db = new AppDbContext(_dbOptions!);
        var user = new User
        {
            Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@example.com",
            DisplayName = "O", CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> SeedPersonAsync(Guid ownerUserId)
    {
        await using var db = new AppDbContext(_dbOptions!);
        var person = new Person
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerUserId, DisplayName = "Alice",
            CreatedAt = DateTime.UtcNow,
        };
        db.People.Add(person);
        await db.SaveChangesAsync();
        return person.Id;
    }

    private async Task<Guid> SeedTrackAsync()
    {
        await using var db = new AppDbContext(_dbOptions!);
        var sha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(), Sha256 = sha, SizeBytes = 1024,
            StorageKey = $"objects/{sha[..2]}/{sha[2..4]}/{sha}",
            ReferenceCount = 1, CreatedAt = DateTime.UtcNow,
        };
        db.BlobObjects.Add(blob);

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
            Dimension = Dim, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        db.AiProfiles.Add(profile);

        var index = new VideoSemanticIndex
        {
            Id = Guid.NewGuid(), BlobObjectId = blob.Id, SegmentationVersion = 1,
            Status = AiArtifactStatuses.Completed, AttemptCount = 1,
            DurationMilliseconds = 60_000, SegmentCount = 1, SampleCount = 1,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        db.VideoSemanticIndexes.Add(index);

        var analysis = new VideoFaceAnalysisStatus
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = index.Id, AnalysisVersion = 1,
            DetectionProfileId = profile.Id, EmbeddingProfileId = profile.Id,
            Status = VideoFaceAnalysisStatuses.Completed,
            PlannedFrameCount = 10, ProcessedFrameCount = 10, TrackCount = 1,
            AttemptCount = 1, CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        db.VideoFaceAnalysisStatuses.Add(analysis);

        var track = new VideoFaceTrack
        {
            Id = Guid.NewGuid(), VideoFaceAnalysisStatusId = analysis.Id, TrackIndex = 0,
            StartMilliseconds = 1_000, EndMilliseconds = 5_000,
            RepresentativeTimestampMilliseconds = 3_000, DetectionCount = 4,
            EmbeddingBytes = new byte[Dim * sizeof(float)], EmbeddingDimension = Dim,
            QualityScore = 0.5,
            RepresentativeBoundingBoxX = 0.2, RepresentativeBoundingBoxY = 0.2,
            RepresentativeBoundingBoxWidth = 0.3, RepresentativeBoundingBoxHeight = 0.3,
            CreatedAt = DateTime.UtcNow,
        };
        db.VideoFaceTracks.Add(track);

        await db.SaveChangesAsync();
        return track.Id;
    }

    private static VideoFaceTrackPersonDecision NewDecision(
        Guid ownerUserId, Guid trackId, Guid? personId,
        string decision = VideoFaceTrackDecisions.Assigned) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = ownerUserId,
        VideoFaceTrackId = trackId,
        PersonId = personId,
        Decision = decision,
        Source = VideoFaceTrackDecisionSources.User,
        CreatedAt = DateTime.UtcNow,
        ConfirmedAt = decision == VideoFaceTrackDecisions.Assigned ? DateTime.UtcNow : null,
    };

    private static async Task<PostgresException> ExpectPostgresErrorAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(action);
        return Assert.IsType<PostgresException>(ex.InnerException);
    }

    // ---- schema ------------------------------------------------------------

    [SkippableFact]
    public async Task Migration_Creates_The_Decision_Table_And_The_Person_Alternate_Key()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using var db = new AppDbContext(_dbOptions!);
        var tableExists = await db.Database
            .SqlQuery<bool>($"SELECT to_regclass('video_face_track_person_decisions') IS NOT NULL AS \"Value\"")
            .SingleAsync();
        Assert.True(tableExists);

        var alternateKeyExists = await db.Database
            .SqlQuery<bool>($@"
SELECT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'ak_people_id_owner' AND conrelid = 'people'::regclass
) AS ""Value""")
            .SingleAsync();
        Assert.True(alternateKeyExists, "the composite person key backing the same-owner FK is missing.");
    }

    [SkippableFact]
    public async Task The_Migration_Assigns_Nothing_And_Creates_Nobody()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using var db = new AppDbContext(_dbOptions!);
        Assert.Equal(0, await db.VideoFaceTrackPersonDecisions.CountAsync());
        Assert.Equal(0, await db.People.CountAsync());
        Assert.Equal(0, await db.PersonFaceAssignments.CountAsync());
        Assert.Equal(0, await db.BackgroundJobs.CountAsync());
        // Canonical evidence is untouched by the migration.
        Assert.Equal(0, await db.VideoFaceTracks.CountAsync());
        Assert.Equal(0, await db.VideoFaceAnalysisStatuses.CountAsync());
    }

    // ---- one decision per owner and track ----------------------------------

    [SkippableFact]
    public async Task A_Second_Decision_For_The_Same_Owner_And_Track_Is_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var ownerId = await SeedUserAsync();
        var personId = await SeedPersonAsync(ownerId);
        var trackId = await SeedTrackAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoFaceTrackPersonDecisions.Add(NewDecision(ownerId, trackId, personId));
        await db.SaveChangesAsync();

        db.VideoFaceTrackPersonDecisions.Add(NewDecision(ownerId, trackId, personId));
        var error = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());

        Assert.Equal("23505", error.SqlState);   // unique_violation
        Assert.Contains("ux_video_face_track_person_decisions_owner_track", error.ConstraintName);
    }

    [SkippableFact]
    public async Task Two_Owners_May_Decide_Differently_About_The_Same_Track()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var ownerA = await SeedUserAsync();
        var ownerB = await SeedUserAsync();
        var personA = await SeedPersonAsync(ownerA);
        var trackId = await SeedTrackAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoFaceTrackPersonDecisions.Add(NewDecision(ownerA, trackId, personA));
        db.VideoFaceTrackPersonDecisions.Add(
            NewDecision(ownerB, trackId, null, VideoFaceTrackDecisions.Ignored));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.VideoFaceTrackPersonDecisions
            .CountAsync(d => d.VideoFaceTrackId == trackId));
    }

    // ---- decision / person pairing -----------------------------------------

    [SkippableFact]
    public async Task An_Assignment_Without_A_Person_Is_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var ownerId = await SeedUserAsync();
        var trackId = await SeedTrackAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoFaceTrackPersonDecisions.Add(NewDecision(ownerId, trackId, personId: null));

        var error = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());
        Assert.Equal("23514", error.SqlState);   // check_violation
        Assert.Contains(
            "ck_video_face_track_person_decisions_person_matches_decision", error.ConstraintName);
    }

    [SkippableFact]
    public async Task An_Ignore_That_Names_A_Person_Is_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var ownerId = await SeedUserAsync();
        var personId = await SeedPersonAsync(ownerId);
        var trackId = await SeedTrackAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoFaceTrackPersonDecisions.Add(
            NewDecision(ownerId, trackId, personId, VideoFaceTrackDecisions.Ignored));

        var error = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());
        Assert.Equal("23514", error.SqlState);
        Assert.Contains(
            "ck_video_face_track_person_decisions_person_matches_decision", error.ConstraintName);
    }

    // ---- the same-owner guarantee ------------------------------------------

    [SkippableFact]
    public async Task Assigning_Another_Owners_Person_Is_Impossible()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var ownerA = await SeedUserAsync();
        var ownerB = await SeedUserAsync();
        var personB = await SeedPersonAsync(ownerB);
        var trackId = await SeedTrackAsync();

        await using var db = new AppDbContext(_dbOptions!);
        // Owner A tries to name owner B's person. The composite (PersonId,
        // OwnerUserId) foreign key has no matching row, so the DATABASE refuses —
        // this is not merely a service-level check.
        db.VideoFaceTrackPersonDecisions.Add(NewDecision(ownerA, trackId, personB));

        var error = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());
        Assert.Equal("23503", error.SqlState);   // foreign_key_violation
    }

    // ---- cascades ------------------------------------------------------------

    [SkippableFact]
    public async Task Re_Analysing_A_Video_Takes_Its_Decisions_With_The_Old_Tracks()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var ownerId = await SeedUserAsync();
        var personId = await SeedPersonAsync(ownerId);
        var trackId = await SeedTrackAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoFaceTrackPersonDecisions.Add(NewDecision(ownerId, trackId, personId));
        await db.SaveChangesAsync();

        db.VideoFaceTracks.Remove(await db.VideoFaceTracks.SingleAsync(t => t.Id == trackId));
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.VideoFaceTrackPersonDecisions.CountAsync());
        // The person survives: only the decision about the replaced evidence went.
        Assert.Equal(1, await db.People.CountAsync());
    }

    [SkippableFact]
    public async Task Deleting_A_Person_Removes_Their_Decisions_But_Not_The_Track()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var ownerId = await SeedUserAsync();
        var personId = await SeedPersonAsync(ownerId);
        var trackId = await SeedTrackAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoFaceTrackPersonDecisions.Add(NewDecision(ownerId, trackId, personId));
        await db.SaveChangesAsync();

        db.People.Remove(await db.People.SingleAsync(p => p.Id == personId));
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.VideoFaceTrackPersonDecisions.CountAsync());
        Assert.Equal(1, await db.VideoFaceTracks.CountAsync());
    }

    [SkippableFact]
    public async Task An_Owner_With_Decisions_Cannot_Be_Deleted_Silently()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var ownerId = await SeedUserAsync();
        var personId = await SeedPersonAsync(ownerId);
        var trackId = await SeedTrackAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoFaceTrackPersonDecisions.Add(NewDecision(ownerId, trackId, personId));
        await db.SaveChangesAsync();

        // Raw SQL: EF's change tracker would sever the relationship in memory
        // first, and the point is that the DATABASE holds the line.
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database
            .ExecuteSqlAsync($"DELETE FROM users WHERE \"Id\" = {ownerId}"));

        Assert.Equal("23503", error.SqlState);
    }
}
