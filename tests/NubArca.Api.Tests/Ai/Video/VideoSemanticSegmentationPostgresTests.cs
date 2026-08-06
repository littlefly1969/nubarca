using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Tests.Integration;
using Npgsql;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-01: the manifest invariants that must be enforced by the DATABASE, not
// only by application code. SQLite success is not proof of PostgreSQL behaviour
// — the unique indexes, check constraints and FK cascades are verified here
// against the real migration on a real PostgreSQL container.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class VideoSemanticSegmentationPostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    public VideoSemanticSegmentationPostgresTests(PostgresContainerFixture fixture)
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

    private async Task<Guid> SeedBlobAsync()
    {
        await using var db = new AppDbContext(_dbOptions!);
        var sha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = sha,
            SizeBytes = 1024,
            StorageKey = $"objects/{sha[..2]}/{sha[2..4]}/{sha}",
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.BlobObjects.Add(blob);
        await db.SaveChangesAsync();
        return blob.Id;
    }

    private static VideoSemanticIndex NewIndex(Guid blobId, int version) => new()
    {
        Id = Guid.NewGuid(),
        BlobObjectId = blobId,
        SegmentationVersion = version,
        Status = AiArtifactStatuses.Completed,
        AttemptCount = 1,
        DurationMilliseconds = 60_000,
        SegmentCount = 1,
        SampleCount = 1,
        CreatedAt = DateTime.UtcNow,
        CompletedAt = DateTime.UtcNow,
    };

    private static async Task<PostgresException> ExpectPostgresErrorAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(action);
        return Assert.IsType<PostgresException>(ex.InnerException);
    }

    [SkippableFact]
    public async Task Migration_Creates_The_Three_Manifest_Tables()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using var db = new AppDbContext(_dbOptions!);
        foreach (var table in new[]
        {
            "video_semantic_indexes", "video_semantic_segments", "video_semantic_samples",
        })
        {
            var exists = await db.Database
                .SqlQuery<bool>($"SELECT to_regclass({table}) IS NOT NULL AS \"Value\"")
                .SingleAsync();
            Assert.True(exists, $"{table} was not created by the migration.");
        }
    }

    [SkippableFact]
    public async Task One_Manifest_Per_Blob_And_Version_Is_Enforced_By_A_Unique_Index()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var blobId = await SeedBlobAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoSemanticIndexes.Add(NewIndex(blobId, 1));
        await db.SaveChangesAsync();

        db.VideoSemanticIndexes.Add(NewIndex(blobId, 1));
        var error = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());

        Assert.Equal("23505", error.SqlState);   // unique_violation
        Assert.Contains("ux_video_semantic_indexes_blob_version", error.ConstraintName);
    }

    [SkippableFact]
    public async Task A_Different_Version_Coexists_With_The_Completed_One()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var blobId = await SeedBlobAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoSemanticIndexes.Add(NewIndex(blobId, 1));
        db.VideoSemanticIndexes.Add(NewIndex(blobId, 2));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.VideoSemanticIndexes.CountAsync(i => i.BlobObjectId == blobId));
    }

    [SkippableFact]
    public async Task Duplicate_Segment_Ordinals_Are_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var blobId = await SeedBlobAsync();

        await using var db = new AppDbContext(_dbOptions!);
        var index = NewIndex(blobId, 1);
        db.VideoSemanticIndexes.Add(index);
        db.VideoSemanticSegments.Add(new VideoSemanticSegment
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = index.Id, SegmentIndex = 0,
            StartMilliseconds = 0, EndMilliseconds = 1000,
            BoundaryReason = VideoSemanticBoundaryReasons.Start, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        db.VideoSemanticSegments.Add(new VideoSemanticSegment
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = index.Id, SegmentIndex = 0,
            StartMilliseconds = 1000, EndMilliseconds = 2000,
            BoundaryReason = VideoSemanticBoundaryReasons.Scene, CreatedAt = DateTime.UtcNow,
        });
        var error = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());

        Assert.Equal("23505", error.SqlState);
        Assert.Contains("ux_video_semantic_segments_index_ordinal", error.ConstraintName);
    }

    [SkippableFact]
    public async Task Duplicate_Sample_Ordinals_Are_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var blobId = await SeedBlobAsync();

        await using var db = new AppDbContext(_dbOptions!);
        var index = NewIndex(blobId, 1);
        var segmentId = Guid.NewGuid();
        db.VideoSemanticIndexes.Add(index);
        db.VideoSemanticSegments.Add(new VideoSemanticSegment
        {
            Id = segmentId, VideoSemanticIndexId = index.Id, SegmentIndex = 0,
            StartMilliseconds = 0, EndMilliseconds = 1000,
            BoundaryReason = VideoSemanticBoundaryReasons.Start, CreatedAt = DateTime.UtcNow,
        });
        db.VideoSemanticSamples.Add(new VideoSemanticSample
        {
            Id = Guid.NewGuid(), VideoSemanticSegmentId = segmentId, SampleIndex = 0,
            TimestampMilliseconds = 500,
            SelectionReason = VideoSemanticSelectionReasons.Midpoint, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        db.VideoSemanticSamples.Add(new VideoSemanticSample
        {
            Id = Guid.NewGuid(), VideoSemanticSegmentId = segmentId, SampleIndex = 0,
            TimestampMilliseconds = 600,
            SelectionReason = VideoSemanticSelectionReasons.Interior, CreatedAt = DateTime.UtcNow,
        });
        var error = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());

        Assert.Equal("23505", error.SqlState);
        Assert.Contains("ux_video_semantic_samples_segment_ordinal", error.ConstraintName);
    }

    [SkippableFact]
    public async Task An_Inverted_Or_Empty_Interval_Is_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var blobId = await SeedBlobAsync();

        await using var db = new AppDbContext(_dbOptions!);
        var index = NewIndex(blobId, 1);
        db.VideoSemanticIndexes.Add(index);
        await db.SaveChangesAsync();

        db.VideoSemanticSegments.Add(new VideoSemanticSegment
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = index.Id, SegmentIndex = 0,
            StartMilliseconds = 5_000, EndMilliseconds = 5_000,   // zero length
            BoundaryReason = VideoSemanticBoundaryReasons.Start, CreatedAt = DateTime.UtcNow,
        });
        var error = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());

        Assert.Equal("23514", error.SqlState);   // check_violation
        Assert.Contains("ck_video_semantic_segments_interval", error.ConstraintName);
    }

    [SkippableFact]
    public async Task A_Non_Positive_Segmentation_Version_Is_Rejected()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var blobId = await SeedBlobAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoSemanticIndexes.Add(NewIndex(blobId, 0));
        var error = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());

        Assert.Equal("23514", error.SqlState);
        Assert.Contains("ck_video_semantic_indexes_version_positive", error.ConstraintName);
    }

    [SkippableFact]
    public async Task Deleting_A_Manifest_Cascades_To_Segments_And_Samples()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var blobId = await SeedBlobAsync();

        await using var db = new AppDbContext(_dbOptions!);
        var index = NewIndex(blobId, 1);
        var segmentId = Guid.NewGuid();
        db.VideoSemanticIndexes.Add(index);
        db.VideoSemanticSegments.Add(new VideoSemanticSegment
        {
            Id = segmentId, VideoSemanticIndexId = index.Id, SegmentIndex = 0,
            StartMilliseconds = 0, EndMilliseconds = 60_000,
            BoundaryReason = VideoSemanticBoundaryReasons.Start, CreatedAt = DateTime.UtcNow,
        });
        db.VideoSemanticSamples.Add(new VideoSemanticSample
        {
            Id = Guid.NewGuid(), VideoSemanticSegmentId = segmentId, SampleIndex = 0,
            TimestampMilliseconds = 30_000,
            SelectionReason = VideoSemanticSelectionReasons.Midpoint, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // The retry path relies on this: removing the head removes the tree, so
        // a rebuilt manifest can never inherit orphan children.
        db.VideoSemanticIndexes.Remove(await db.VideoSemanticIndexes.SingleAsync(i => i.Id == index.Id));
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.VideoSemanticSegments.CountAsync());
        Assert.Equal(0, await db.VideoSemanticSamples.CountAsync());
    }

    [SkippableFact]
    public async Task Deleting_The_Blob_Removes_Its_Manifests()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var blobId = await SeedBlobAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoSemanticIndexes.Add(NewIndex(blobId, 1));
        await db.SaveChangesAsync();

        db.BlobObjects.Remove(await db.BlobObjects.SingleAsync(b => b.Id == blobId));
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.VideoSemanticIndexes.CountAsync());
    }
}
