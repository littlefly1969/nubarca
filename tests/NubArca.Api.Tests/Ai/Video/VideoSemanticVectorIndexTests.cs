using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Ai.Video;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-02: the EXACT in-process fallback of the sample vector index. On SQLite
// the pgvector backend is structurally unavailable, so every search here runs
// the mandatory canonical-bytes fallback — same contract, same ordering rules
// as the accelerated path.
public sealed class VideoSemanticVectorIndexTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly AiVectorSerializer _serializer = new();

    public VideoSemanticVectorIndexTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private VideoSemanticSampleVectorIndexService NewService()
        => new(_db, _serializer, TimeProvider.System);

    // ---- seeding -----------------------------------------------------------

    private async Task<Guid> SeedProfileAsync()
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = $"m-{Guid.NewGuid():N}", Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.ImageEmbedding, Modality = AiModalities.Image,
            Dimension = 4, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = $"p-{Guid.NewGuid():N}", AiModelId = model.Id,
            Capability = AiCapabilities.ImageEmbedding, Modality = AiModalities.Image,
            Dimension = 4, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(profile);
        await _db.SaveChangesAsync();
        return profile.Id;
    }

    private async Task<Guid> SeedSampleAsync()
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
        _db.BlobObjects.Add(blob);
        _db.VideoSemanticIndexes.Add(index);
        _db.VideoSemanticSegments.Add(segment);
        _db.VideoSemanticSamples.Add(sample);
        await _db.SaveChangesAsync();
        return sample.Id;
    }

    private async Task SeedEmbeddingAsync(
        Guid sampleId, Guid profileId, float[] vector, string status = AiArtifactStatuses.Completed)
    {
        _db.VideoSemanticSampleEmbeddings.Add(new VideoSemanticSampleEmbedding
        {
            Id = Guid.NewGuid(), VideoSemanticSampleId = sampleId, ProfileId = profileId,
            EmbeddingBytes = status == AiArtifactStatuses.Completed
                ? _serializer.Serialize(vector)
                : Array.Empty<byte>(),
            Dimension = status == AiArtifactStatuses.Completed ? vector.Length : 0,
            Status = status, AttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
            CompletedAt = status == AiArtifactStatuses.Completed ? DateTime.UtcNow : null,
        });
        await _db.SaveChangesAsync();
    }

    // ---- exact fallback ranking --------------------------------------------

    [Fact]
    public async Task Ranks_Candidates_By_Cosine_With_Pgvector_Unavailable()
    {
        var profileId = await SeedProfileAsync();
        var near = await SeedSampleAsync();
        var far = await SeedSampleAsync();
        await SeedEmbeddingAsync(near, profileId, [1f, 0f, 0f, 0f]);
        await SeedEmbeddingAsync(far, profileId, [0f, 1f, 0f, 0f]);

        var service = NewService();
        Assert.False(await service.IsBackendAvailableAsync(1152));   // SQLite: no pgvector

        var ranked = await service.SearchWithinCandidatesAsync(
            profileId, [1f, 0.1f, 0f, 0f], [near, far], take: 10);

        Assert.Equal(2, ranked.Count);
        Assert.Equal(near, ranked[0].SampleId);
        Assert.True(ranked[0].Score > ranked[1].Score);
    }

    [Fact]
    public async Task A_Different_Profile_Never_Scores()
    {
        var profileA = await SeedProfileAsync();
        var profileB = await SeedProfileAsync();
        var sample = await SeedSampleAsync();
        await SeedEmbeddingAsync(sample, profileB, [1f, 0f, 0f, 0f]);

        var ranked = await NewService().SearchWithinCandidatesAsync(
            profileA, [1f, 0f, 0f, 0f], [sample], take: 10);

        Assert.Empty(ranked);   // profile isolation: B's rows are invisible to A
    }

    [Fact]
    public async Task Only_The_Explicit_Candidate_Scope_Is_Searched()
    {
        var profileId = await SeedProfileAsync();
        var inside = await SeedSampleAsync();
        var outside = await SeedSampleAsync();
        await SeedEmbeddingAsync(inside, profileId, [0.5f, 0.5f, 0f, 0f]);
        await SeedEmbeddingAsync(outside, profileId, [1f, 0f, 0f, 0f]);   // the better match

        var ranked = await NewService().SearchWithinCandidatesAsync(
            profileId, [1f, 0f, 0f, 0f], [inside], take: 10);

        // The out-of-scope sample never appears, even though it scores higher:
        // there is no unrestricted search path to leak through.
        Assert.Single(ranked);
        Assert.Equal(inside, ranked[0].SampleId);
    }

    [Fact]
    public async Task An_Empty_Candidate_Scope_Returns_Empty_And_An_Oversized_One_Throws()
    {
        var profileId = await SeedProfileAsync();
        var service = NewService();

        Assert.Empty(await service.SearchWithinCandidatesAsync(
            profileId, [1f, 0f, 0f, 0f], Array.Empty<Guid>(), take: 10));

        var oversized = Enumerable.Range(0, VideoSemanticSampleVectorIndexService.MaxCandidateScope + 1)
            .Select(_ => Guid.NewGuid()).ToList();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchWithinCandidatesAsync(profileId, [1f, 0f, 0f, 0f], oversized, take: 10));
    }

    [Fact]
    public async Task Failed_And_Dimension_Mismatched_Rows_Are_Excluded()
    {
        var profileId = await SeedProfileAsync();
        var failed = await SeedSampleAsync();
        var mismatched = await SeedSampleAsync();
        var good = await SeedSampleAsync();
        await SeedEmbeddingAsync(failed, profileId, [1f, 0f, 0f, 0f], AiArtifactStatuses.Failed);
        await SeedEmbeddingAsync(mismatched, profileId, [1f, 0f]);       // wrong dimension
        await SeedEmbeddingAsync(good, profileId, [1f, 0f, 0f, 0f]);

        var ranked = await NewService().SearchWithinCandidatesAsync(
            profileId, [1f, 0f, 0f, 0f], [failed, mismatched, good], take: 10);

        Assert.Single(ranked);
        Assert.Equal(good, ranked[0].SampleId);
    }

    [Fact]
    public async Task Take_Bounds_The_Result_With_A_Deterministic_Tie_Break()
    {
        var profileId = await SeedProfileAsync();
        var a = await SeedSampleAsync();
        var b = await SeedSampleAsync();
        var c = await SeedSampleAsync();
        await SeedEmbeddingAsync(a, profileId, [1f, 0f, 0f, 0f]);
        await SeedEmbeddingAsync(b, profileId, [1f, 0f, 0f, 0f]);   // identical score
        await SeedEmbeddingAsync(c, profileId, [0f, 1f, 0f, 0f]);

        var ranked = await NewService().SearchWithinCandidatesAsync(
            profileId, [1f, 0f, 0f, 0f], [a, b, c], take: 2);

        Assert.Equal(2, ranked.Count);
        // Equal scores order by sample id — stable across runs.
        var expectedFirst = a.CompareTo(b) < 0 ? a : b;
        Assert.Equal(expectedFirst, ranked[0].SampleId);
    }

    // ---- sync/no-op surface on an unavailable backend ----------------------

    [Fact]
    public async Task Sync_And_Maintenance_Are_Clean_No_Ops_Without_Pgvector()
    {
        var profileId = await SeedProfileAsync();
        var sample = await SeedSampleAsync();
        await SeedEmbeddingAsync(sample, profileId, [1f, 0f, 0f, 0f]);
        var embeddingId = (await _db.VideoSemanticSampleEmbeddings.SingleAsync()).Id;

        var service = NewService();
        var unsupported = await service.SyncEmbeddingAsync(
            embeddingId, sample, profileId, [1f, 0f, 0f, 0f], dimension: 4);
        Assert.Equal(VectorUpsertOutcome.SkippedUnsupported, unsupported);

        var unavailable = await service.SyncEmbeddingAsync(
            embeddingId, sample, profileId, new float[1152], dimension: 1152);
        Assert.Equal(VectorUpsertOutcome.SkippedUnavailable, unavailable);

        Assert.False(await service.IsIndexedAsync(embeddingId));
        Assert.Equal(0, await service.DeleteStaleAsync(profileId));

        // The canonical row is untouched by any of the above.
        Assert.Equal(1, await _db.VideoSemanticSampleEmbeddings
            .CountAsync(e => e.Status == AiArtifactStatuses.Completed));
    }
}
