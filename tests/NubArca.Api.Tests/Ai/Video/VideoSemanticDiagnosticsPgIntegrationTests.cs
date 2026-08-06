using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Ai.Video;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Tests.Integration;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-04C: VideoSemanticDiagnosticsService's canonical-vs-pgvector coverage
// query on REAL pgvector (pgvector/pgvector:pg17), not the SQLite fallback
// (VideoSemanticDiagnosticsServiceTests covers that path, where the vector
// backend always reports unavailable). Proves the diagnostics invariants:
// canonical rows are the source of truth, pgvector is acceleration-only, a
// missing mirror only reduces SYNCHRONIZED coverage (never canonical
// coverage), profiles are isolated, active/historical segmentation versions
// are never blended, and a stale mirror row is never counted as synchronized.
// Skipped when Docker/pgvector is unavailable (same policy as the sibling
// VideoSemanticVectorPgIntegrationTests).
[Collection(PgVectorIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class VideoSemanticDiagnosticsPgIntegrationTests : IAsyncLifetime
{
    private const int Dim = VideoSemanticSampleVectorIndexService.SupportedDimension;

    private readonly PgVectorContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;
    private readonly AiVectorSerializer _serializer = new();

    public VideoSemanticDiagnosticsPgIntegrationTests(PgVectorContainerFixture fixture)
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

    // A resolver that reports the given profile as the capability default
    // without needing a live inference backend — status queries never resolve
    // one, exactly like the production `ai video semantic status` command.
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
                    IsAvailable = true, Capability = capability, Provider = AiProviders.Onnx,
                    ProfileKey = _profile.Key, Dimension = _profile.Dimension,
                    DistanceMetric = _profile.DistanceMetric,
                });
    }

    private static VideoSemanticDiagnosticsService NewService(AppDbContext db, AiProfile? configuredProfile)
    {
        var aiOptions = configuredProfile is null
            ? new AiOptions { Enabled = true }
            : new AiOptions { Enabled = true, PhotoSimilarityProfileKey = configuredProfile.Key };

        return new VideoSemanticDiagnosticsService(
            db,
            Options.Create(new VideoSemanticSegmentationOptions { Enabled = true, SegmentationVersion = 1 }),
            Options.Create(new VideoVisualEmbeddingOptions { Enabled = true }),
            Options.Create(aiOptions),
            new AiProfileRegistry(db, TimeProvider.System),
            new SingleProfileResolver(configuredProfile),
            new VideoSemanticSampleVectorIndexService(db, new AiVectorSerializer(), TimeProvider.System));
    }

    // ---- seeding -------------------------------------------------------------

    private static float[] Unit(int axis)
    {
        var v = new float[Dim];
        v[axis] = 1f;
        return v;
    }

    private async Task<AiProfile> SeedProfileAsync(AppDbContext db, bool isDefault = true)
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
            Dimension = Dim, DistanceMetric = "cosine", Enabled = true, IsDefault = isDefault,
            CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        db.AiProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }

    // Blob + completed manifest + one segment + one sample — everything a
    // canonical sample embedding needs, at the given segmentation version.
    private async Task<Guid> SeedSampleAsync(AppDbContext db, int segmentationVersion = 1)
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
            Id = Guid.NewGuid(), BlobObjectId = blob.Id, SegmentationVersion = segmentationVersion,
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

    private async Task<Guid> SeedCanonicalAsync(AppDbContext db, Guid sampleId, Guid profileId, float[] vector)
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

    // ---- tests -----------------------------------------------------------

    [SkippableFact]
    public async Task Empty_State_Reports_Zero_Canonical_Synchronized_And_Missing()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var db = new AppDbContext(_dbOptions!);
        var profile = await SeedProfileAsync(db);

        var status = await NewService(db, profile).GetStatusAsync();

        Assert.True(status.PgvectorBackendAvailable);
        Assert.Equal(0, status.CanonicalEmbeddingsProfileWide);
        Assert.Equal(0, status.PgvectorSynchronizedProfileWide);
        Assert.Equal(0, status.PgvectorStaleOrMissingProfileWide);
    }

    [SkippableFact]
    public async Task Fully_Synchronized_Reports_Equal_Canonical_And_Synchronized()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var db = new AppDbContext(_dbOptions!);
        var profile = await SeedProfileAsync(db);
        var vectors = new VideoSemanticSampleVectorIndexService(db, _serializer, TimeProvider.System);

        const int n = 3;
        for (var i = 0; i < n; i++)
        {
            var sample = await SeedSampleAsync(db);
            var rowId = await SeedCanonicalAsync(db, sample, profile.Id, Unit(i % Dim));
            Assert.Equal(VectorUpsertOutcome.Indexed,
                await vectors.SyncEmbeddingAsync(rowId, sample, profile.Id, Unit(i % Dim), Dim));
        }

        var status = await NewService(db, profile).GetStatusAsync();

        Assert.Equal(n, status.CanonicalEmbeddingsProfileWide);
        Assert.Equal(n, status.PgvectorSynchronizedProfileWide);
        Assert.Equal(0, status.PgvectorStaleOrMissingProfileWide);
    }

    [SkippableFact]
    public async Task Partially_Synchronized_Reports_The_Missing_Delta()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var db = new AppDbContext(_dbOptions!);
        var profile = await SeedProfileAsync(db);
        var vectors = new VideoSemanticSampleVectorIndexService(db, _serializer, TimeProvider.System);

        const int canonical = 5;
        const int synced = 2;
        for (var i = 0; i < canonical; i++)
        {
            var sample = await SeedSampleAsync(db);
            var rowId = await SeedCanonicalAsync(db, sample, profile.Id, Unit(i % Dim));
            if (i < synced)
            {
                Assert.Equal(VectorUpsertOutcome.Indexed,
                    await vectors.SyncEmbeddingAsync(rowId, sample, profile.Id, Unit(i % Dim), Dim));
            }
            // The remaining canonical rows are deliberately left unsynchronized.
        }

        var status = await NewService(db, profile).GetStatusAsync();

        Assert.Equal(canonical, status.CanonicalEmbeddingsProfileWide);
        Assert.Equal(synced, status.PgvectorSynchronizedProfileWide);
        Assert.Equal(canonical - synced, status.PgvectorStaleOrMissingProfileWide);
    }

    [SkippableFact]
    public async Task Profile_Isolation_Excludes_A_Different_Profiles_Embeddings()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var db = new AppDbContext(_dbOptions!);
        var profileA = await SeedProfileAsync(db);
        var profileB = await SeedProfileAsync(db, isDefault: false);
        var vectors = new VideoSemanticSampleVectorIndexService(db, _serializer, TimeProvider.System);

        // Profile A: one canonical + synced row.
        var sampleA = await SeedSampleAsync(db);
        var rowA = await SeedCanonicalAsync(db, sampleA, profileA.Id, Unit(0));
        await vectors.SyncEmbeddingAsync(rowA, sampleA, profileA.Id, Unit(0), Dim);

        // Profile B: three canonical + synced rows — must never leak into A's status.
        for (var i = 0; i < 3; i++)
        {
            var sampleB = await SeedSampleAsync(db);
            var rowB = await SeedCanonicalAsync(db, sampleB, profileB.Id, Unit(i % Dim));
            await vectors.SyncEmbeddingAsync(rowB, sampleB, profileB.Id, Unit(i % Dim), Dim);
        }

        var statusA = await NewService(db, profileA).GetStatusAsync();

        Assert.Equal(1, statusA.CanonicalEmbeddingsProfileWide);
        Assert.Equal(1, statusA.PgvectorSynchronizedProfileWide);
    }

    [SkippableFact]
    public async Task Historical_Segmentation_Version_Is_Not_Blended_Into_Active_Coverage()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var db = new AppDbContext(_dbOptions!);
        // Active version is 1 (see NewService); seed one manifest at v1 and one at v2 (historical).
        await SeedSampleAsync(db, segmentationVersion: 1);
        await SeedSampleAsync(db, segmentationVersion: 2);

        var status = await NewService(db, configuredProfile: null).GetStatusAsync();

        Assert.Equal(1, status.ActiveSegmentationVersion);
        Assert.Equal(1, status.SegmentationCompleted); // v1 manifest only
        var historical = Assert.Single(status.HistoricalVersions);
        Assert.Equal(2, historical.SegmentationVersion);
        Assert.Equal(1, historical.Completed);
    }

    [SkippableFact]
    public async Task Stale_Vector_Row_Is_Not_Counted_As_Synchronized()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var db = new AppDbContext(_dbOptions!);
        var profile = await SeedProfileAsync(db);
        var vectors = new VideoSemanticSampleVectorIndexService(db, _serializer, TimeProvider.System);

        var sample = await SeedSampleAsync(db);
        var rowId = await SeedCanonicalAsync(db, sample, profile.Id, Unit(0));
        Assert.Equal(VectorUpsertOutcome.Indexed,
            await vectors.SyncEmbeddingAsync(rowId, sample, profile.Id, Unit(0), Dim));

        // The canonical row is invalidated by a rebuild attempt (flipped to
        // failed) WITHOUT running DeleteStaleAsync — the pgvector mirror row
        // is still physically present, exactly the "stale" state a real
        // failed re-embed leaves until the next sync pass.
        var canonicalRow = await db.VideoSemanticSampleEmbeddings.SingleAsync(e => e.Id == rowId);
        canonicalRow.Status = AiArtifactStatuses.Failed;
        canonicalRow.EmbeddingBytes = Array.Empty<byte>();
        canonicalRow.Dimension = 0;
        await db.SaveChangesAsync();

        // Sanity: the stale mirror row still physically exists (DeleteStaleAsync
        // was NOT called), so a naive count would have reported 1 here.
        Assert.True(await vectors.IsIndexedAsync(rowId));

        var status = await NewService(db, profile).GetStatusAsync();

        // Canonical coverage reflects the now-failed row (0 completed canonical
        // embeddings) — a missing/stale pgvector mirror never inflates it, and
        // the diagnostics "synchronized" number must not count the stale
        // mirror either.
        Assert.Equal(0, status.CanonicalEmbeddingsProfileWide);
        Assert.Equal(0, status.PgvectorSynchronizedProfileWide);
    }
}
