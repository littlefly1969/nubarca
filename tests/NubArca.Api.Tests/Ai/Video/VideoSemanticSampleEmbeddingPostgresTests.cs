using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Tests.Integration;
using Npgsql;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-02: the canonical-embedding invariants that must be enforced by the
// DATABASE — the unique (sample, profile) key, the FK cascades that keep the
// embedding tree consistent with the manifest tree, and the coexistence of
// profiles and segmentation versions. Verified against the real migration on a
// real PostgreSQL container.
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class VideoSemanticSampleEmbeddingPostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    public VideoSemanticSampleEmbeddingPostgresTests(PostgresContainerFixture fixture)
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

    private sealed record Seeded(Guid BlobId, Guid IndexId, Guid SegmentId, Guid SampleId);

    private async Task<Seeded> SeedManifestAsync(int version = 1)
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

        var index = new VideoSemanticIndex
        {
            Id = Guid.NewGuid(), BlobObjectId = blob.Id, SegmentationVersion = version,
            Status = AiArtifactStatuses.Completed, AttemptCount = 1,
            DurationMilliseconds = 60_000, SegmentCount = 1, SampleCount = 1,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        db.VideoSemanticIndexes.Add(index);

        var segment = new VideoSemanticSegment
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = index.Id, SegmentIndex = 0,
            StartMilliseconds = 0, EndMilliseconds = 60_000,
            BoundaryReason = VideoSemanticBoundaryReasons.Start, CreatedAt = DateTime.UtcNow,
        };
        db.VideoSemanticSegments.Add(segment);

        var sample = new VideoSemanticSample
        {
            Id = Guid.NewGuid(), VideoSemanticSegmentId = segment.Id, SampleIndex = 0,
            TimestampMilliseconds = 30_000,
            SelectionReason = VideoSemanticSelectionReasons.Midpoint, CreatedAt = DateTime.UtcNow,
        };
        db.VideoSemanticSamples.Add(sample);

        await db.SaveChangesAsync();
        return new Seeded(blob.Id, index.Id, segment.Id, sample.Id);
    }

    private async Task<Guid> SeedProfileAsync(string keySuffix = "a")
    {
        await using var db = new AppDbContext(_dbOptions!);
        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = $"m-{Guid.NewGuid():N}", Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.ImageEmbedding, Modality = AiModalities.Image,
            Dimension = 8, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = $"p-{keySuffix}-{Guid.NewGuid():N}", AiModelId = model.Id,
            Capability = AiCapabilities.ImageEmbedding, Modality = AiModalities.Image,
            Dimension = 8, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private static VideoSemanticSampleEmbedding NewEmbedding(Guid sampleId, Guid profileId) => new()
    {
        Id = Guid.NewGuid(),
        VideoSemanticSampleId = sampleId,
        ProfileId = profileId,
        EmbeddingBytes = new byte[32],
        Dimension = 8,
        Status = AiArtifactStatuses.Completed,
        AttemptCount = 1,
        CreatedAt = DateTime.UtcNow,
        CompletedAt = DateTime.UtcNow,
    };

    private static async Task<PostgresException> ExpectPostgresErrorAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(action);
        return Assert.IsType<PostgresException>(ex.InnerException);
    }

    // ---- schema ------------------------------------------------------------

    [SkippableFact]
    public async Task Migration_Creates_The_Embedding_Tables()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        await using var db = new AppDbContext(_dbOptions!);
        foreach (var table in new[]
        {
            "video_semantic_sample_embeddings", "video_semantic_embedding_statuses",
        })
        {
            var exists = await db.Database
                .SqlQuery<bool>($"SELECT to_regclass({table}) IS NOT NULL AS \"Value\"")
                .SingleAsync();
            Assert.True(exists, $"{table} was not created by the migration.");
        }
    }

    // ---- canonical embedding invariants ------------------------------------

    [SkippableFact]
    public async Task One_Embedding_Per_Sample_And_Profile_Is_Enforced_By_A_Unique_Index()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var seeded = await SeedManifestAsync();
        var profileId = await SeedProfileAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoSemanticSampleEmbeddings.Add(NewEmbedding(seeded.SampleId, profileId));
        await db.SaveChangesAsync();

        db.VideoSemanticSampleEmbeddings.Add(NewEmbedding(seeded.SampleId, profileId));
        var error = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());

        Assert.Equal("23505", error.SqlState);   // unique_violation
        Assert.Contains("ux_video_semantic_sample_embeddings_sample_profile", error.ConstraintName);
    }

    [SkippableFact]
    public async Task Multiple_Profiles_Coexist_For_The_Same_Sample()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var seeded = await SeedManifestAsync();
        var profileA = await SeedProfileAsync("a");
        var profileB = await SeedProfileAsync("b");

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoSemanticSampleEmbeddings.Add(NewEmbedding(seeded.SampleId, profileA));
        db.VideoSemanticSampleEmbeddings.Add(NewEmbedding(seeded.SampleId, profileB));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.VideoSemanticSampleEmbeddings
            .CountAsync(e => e.VideoSemanticSampleId == seeded.SampleId));
    }

    [SkippableFact]
    public async Task Multiple_Segmentation_Versions_Coexist_Through_Their_Own_Sample_Trees()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var v1 = await SeedManifestAsync(version: 1);
        var v2 = await SeedManifestAsync(version: 2);
        var profileId = await SeedProfileAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoSemanticSampleEmbeddings.Add(NewEmbedding(v1.SampleId, profileId));
        db.VideoSemanticSampleEmbeddings.Add(NewEmbedding(v2.SampleId, profileId));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.VideoSemanticSampleEmbeddings.CountAsync());
    }

    [SkippableFact]
    public async Task Deleting_A_Manifest_Cascades_To_Embeddings_And_Aggregate_Statuses()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var seeded = await SeedManifestAsync();
        var profileId = await SeedProfileAsync();

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoSemanticSampleEmbeddings.Add(NewEmbedding(seeded.SampleId, profileId));
        db.VideoSemanticEmbeddingStatuses.Add(new VideoSemanticEmbeddingStatus
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = seeded.IndexId, ProfileId = profileId,
            Status = VideoSemanticEmbeddingStatuses.Completed, ExpectedSampleCount = 1,
            CompletedSampleCount = 1, AttemptCount = 1,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Removing the manifest head removes segments → samples → embeddings,
        // and the aggregate rows directly; nothing can orphan.
        db.VideoSemanticIndexes.Remove(
            await db.VideoSemanticIndexes.SingleAsync(i => i.Id == seeded.IndexId));
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.VideoSemanticSampleEmbeddings.CountAsync());
        Assert.Equal(0, await db.VideoSemanticEmbeddingStatuses.CountAsync());
    }

    [SkippableFact]
    public async Task One_Aggregate_Status_Per_Manifest_And_Profile_Is_Enforced()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var seeded = await SeedManifestAsync();
        var profileId = await SeedProfileAsync();

        VideoSemanticEmbeddingStatus NewStatus() => new()
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = seeded.IndexId, ProfileId = profileId,
            Status = VideoSemanticEmbeddingStatuses.Partial, ExpectedSampleCount = 2,
            CompletedSampleCount = 1, FailedSampleCount = 1, AttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
        };

        await using var db = new AppDbContext(_dbOptions!);
        db.VideoSemanticEmbeddingStatuses.Add(NewStatus());
        await db.SaveChangesAsync();

        db.VideoSemanticEmbeddingStatuses.Add(NewStatus());
        var error = await ExpectPostgresErrorAsync(() => db.SaveChangesAsync());

        Assert.Equal("23505", error.SqlState);
        Assert.Contains("ux_video_semantic_embedding_statuses_index_profile", error.ConstraintName);
    }
}
