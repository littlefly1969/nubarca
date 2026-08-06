using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Ai.Video;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Tests.Integration;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-02 on REAL pgvector (pgvector/pgvector:pg17): sync, accelerated
// candidate-scoped search, stale cleanup, sync retry after a canonical
// rebuild, and the guarantee that vector failures never destroy canonical
// rows. Skipped when Docker/pgvector is unavailable (the SQLite suite covers
// the exact fallback).
[Collection(PgVectorIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class VideoSemanticVectorPgIntegrationTests : IAsyncLifetime
{
    private readonly PgVectorContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;
    private readonly AiVectorSerializer _serializer = new();

    public VideoSemanticVectorPgIntegrationTests(PgVectorContainerFixture fixture)
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

    private const int Dim = VideoSemanticSampleVectorIndexService.SupportedDimension;

    private static float[] Unit(int axis)
    {
        var v = new float[Dim];
        v[axis] = 1f;
        return v;
    }

    // ---- seeding -----------------------------------------------------------

    private async Task<Guid> SeedProfileAsync(AppDbContext db)
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = $"m-{Guid.NewGuid():N}", Provider = AiProviders.Onnx,
            Capability = AiCapabilities.ImageEmbedding, Modality = AiModalities.Image,
            Dimension = Dim, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = $"p-{Guid.NewGuid():N}", AiModelId = model.Id,
            Capability = AiCapabilities.ImageEmbedding, Modality = AiModalities.Image,
            Dimension = Dim, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        db.AiProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private async Task<Guid> SeedSampleAsync(AppDbContext db)
    {
        var sha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(), Sha256 = sha, SizeBytes = 10,
            StorageKey = $"objects/{sha[..2]}/{sha[2..4]}/{sha}",
            ReferenceCount = 1, CreatedAt = DateTime.UtcNow,
        };
        var index = new VideoSemanticIndex
        {
            Id = Guid.NewGuid(), BlobObjectId = blob.Id, SegmentationVersion = 1,
            Status = AiArtifactStatuses.Completed, AttemptCount = 1,
            DurationMilliseconds = 10_000, SegmentCount = 1, SampleCount = 1,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        var segment = new VideoSemanticSegment
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = index.Id, SegmentIndex = 0,
            StartMilliseconds = 0, EndMilliseconds = 10_000,
            BoundaryReason = VideoSemanticBoundaryReasons.Start, CreatedAt = DateTime.UtcNow,
        };
        var sample = new VideoSemanticSample
        {
            Id = Guid.NewGuid(), VideoSemanticSegmentId = segment.Id, SampleIndex = 0,
            TimestampMilliseconds = 5_000,
            SelectionReason = VideoSemanticSelectionReasons.Midpoint, CreatedAt = DateTime.UtcNow,
        };
        db.BlobObjects.Add(blob);
        db.VideoSemanticIndexes.Add(index);
        db.VideoSemanticSegments.Add(segment);
        db.VideoSemanticSamples.Add(sample);
        await db.SaveChangesAsync();
        return sample.Id;
    }

    private async Task<Guid> SeedCanonicalAsync(
        AppDbContext db, Guid sampleId, Guid profileId, float[] vector)
    {
        var row = new VideoSemanticSampleEmbedding
        {
            Id = Guid.NewGuid(), VideoSemanticSampleId = sampleId, ProfileId = profileId,
            EmbeddingBytes = _serializer.Serialize(vector), Dimension = vector.Length,
            Status = AiArtifactStatuses.Completed, AttemptCount = 1,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        db.VideoSemanticSampleEmbeddings.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    // ---- lifecycle ---------------------------------------------------------

    [SkippableFact]
    public async Task Full_Sync_Search_Stale_Cleanup_And_Fallback_Consistency()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var db = new AppDbContext(_dbOptions!);
        var service = new VideoSemanticSampleVectorIndexService(db, _serializer, TimeProvider.System);
        Assert.True(await service.IsBackendAvailableAsync(Dim));

        var profileId = await SeedProfileAsync(db);
        var near = await SeedSampleAsync(db);
        var far = await SeedSampleAsync(db);
        var nearRow = await SeedCanonicalAsync(db, near, profileId, Unit(0));
        var farRow = await SeedCanonicalAsync(db, far, profileId, Unit(1));

        // Sync both mirrors.
        Assert.Equal(VectorUpsertOutcome.Indexed,
            await service.SyncEmbeddingAsync(nearRow, near, profileId, Unit(0), Dim));
        Assert.Equal(VectorUpsertOutcome.Indexed,
            await service.SyncEmbeddingAsync(farRow, far, profileId, Unit(1), Dim));
        Assert.True(await service.IsIndexedAsync(nearRow));
        Assert.Equal(2, await service.CountIndexedAsync(profileId));

        // Accelerated candidate-scoped search: exact restricted ranking.
        var query = Unit(0);
        query[1] = 0.1f;
        var ranked = await service.SearchWithinCandidatesAsync(profileId, query, [near, far], take: 10);
        Assert.Equal(2, ranked.Count);
        Assert.Equal(near, ranked[0].SampleId);
        Assert.True(ranked[0].Score > ranked[1].Score);

        // Score consistency: the accelerated scores equal true cosine
        // similarity (the same definition the exact fallback computes), so the
        // two paths rank identically.
        static double Cosine(float[] a, float[] b)
        {
            double dot = 0, na = 0, nb = 0;
            for (var i = 0; i < a.Length; i++)
            {
                dot += (double)a[i] * b[i];
                na += (double)a[i] * a[i];
                nb += (double)b[i] * b[i];
            }

            return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }

        Assert.True(Math.Abs(ranked[0].Score - Cosine(query, Unit(0))) < 1e-4);
        Assert.True(Math.Abs(ranked[1].Score - Cosine(query, Unit(1))) < 1e-4);

        // Candidate scope is respected on the accelerated path too.
        var scoped = await service.SearchWithinCandidatesAsync(profileId, query, [far], take: 10);
        Assert.Single(scoped);
        Assert.Equal(far, scoped[0].SampleId);

        // Stale cleanup: flip one canonical row to failed → its mirror goes.
        var row = await db.VideoSemanticSampleEmbeddings.SingleAsync(e => e.Id == farRow);
        row.Status = AiArtifactStatuses.Failed;
        row.EmbeddingBytes = Array.Empty<byte>();
        row.Dimension = 0;
        await db.SaveChangesAsync();
        Assert.Equal(1, await service.DeleteStaleAsync(profileId));
        Assert.False(await service.IsIndexedAsync(farRow));
        Assert.True(await service.IsIndexedAsync(nearRow));
    }

    [SkippableFact]
    public async Task Resync_After_A_Canonical_Rebuild_Replaces_The_Old_Vector()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var db = new AppDbContext(_dbOptions!);
        var service = new VideoSemanticSampleVectorIndexService(db, _serializer, TimeProvider.System);
        var profileId = await SeedProfileAsync(db);
        var sample = await SeedSampleAsync(db);
        var rowId = await SeedCanonicalAsync(db, sample, profileId, Unit(0));

        await service.SyncEmbeddingAsync(rowId, sample, profileId, Unit(0), Dim);

        // The profile version changed and the sample was safely rebuilt: same
        // row id, new vector. Retry-sync must REPLACE the mirror, not keep
        // serving the old vector.
        await service.SyncEmbeddingAsync(rowId, sample, profileId, Unit(5), Dim);

        var oldAxis = await service.SearchWithinCandidatesAsync(profileId, Unit(0), [sample], take: 1);
        var newAxis = await service.SearchWithinCandidatesAsync(profileId, Unit(5), [sample], take: 1);
        Assert.True(newAxis[0].Score > 0.99);
        Assert.True(oldAxis[0].Score < 0.01);
    }

    [SkippableFact]
    public async Task A_Vector_Sync_Failure_Never_Destroys_The_Canonical_Embedding()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var db = new AppDbContext(_dbOptions!);
        var service = new VideoSemanticSampleVectorIndexService(db, _serializer, TimeProvider.System);
        var profileId = await SeedProfileAsync(db);
        var sample = await SeedSampleAsync(db);
        var rowId = await SeedCanonicalAsync(db, sample, profileId, Unit(0));

        // A non-finite vector is rejected by the mirror layer…
        var bad = Unit(0);
        bad[3] = float.NaN;
        Assert.Equal(VectorUpsertOutcome.Failed,
            await service.SyncEmbeddingAsync(rowId, sample, profileId, bad, Dim));

        // …but the canonical row is retained, still searchable via the exact
        // fallback, and a later sync retry succeeds.
        Assert.Equal(1, await db.VideoSemanticSampleEmbeddings
            .CountAsync(e => e.Id == rowId && e.Status == AiArtifactStatuses.Completed));
        Assert.False(await service.IsIndexedAsync(rowId));
        Assert.Equal(VectorUpsertOutcome.Indexed,
            await service.SyncEmbeddingAsync(rowId, sample, profileId, Unit(0), Dim));
    }

    [SkippableFact]
    public async Task A_Different_Profile_Is_Excluded_On_The_Accelerated_Path()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var db = new AppDbContext(_dbOptions!);
        var service = new VideoSemanticSampleVectorIndexService(db, _serializer, TimeProvider.System);
        var profileA = await SeedProfileAsync(db);
        var profileB = await SeedProfileAsync(db);
        var sample = await SeedSampleAsync(db);
        var rowId = await SeedCanonicalAsync(db, sample, profileB, Unit(0));
        await service.SyncEmbeddingAsync(rowId, sample, profileB, Unit(0), Dim);

        Assert.Empty(await service.SearchWithinCandidatesAsync(profileA, Unit(0), [sample], take: 10));
    }
}
