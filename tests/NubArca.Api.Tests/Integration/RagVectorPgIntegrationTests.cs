using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Domain.Rag;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Storage;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// REAL pgvector, via Testcontainers (pgvector/pgvector:pg17).
//
// The unit tests run on SQLite, where the vector backend reports itself
// unavailable — which is the correct place to prove that the canonical
// embeddings are the truth and the accelerator is optional. This is the other
// half: that the accelerator, when present, is DOMAIN-scoped and PROFILE-scoped
// in the database rather than in a filter applied to whatever came back.
//
// Vectors are controlled unit vectors inserted directly, so cosine ordering is
// something the test can assert rather than hope for. Skipped when Docker or
// the pgvector image is unavailable.
[Collection(PgVectorIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class RagVectorPgIntegrationTests : IAsyncLifetime
{
    private const int Dimension = RagVectorIndexService.SupportedDimension;

    private readonly PgVectorContainerFixture _fixture;

    public RagVectorPgIntegrationTests(PgVectorContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Vector_Search_Is_Domain_Scoped_Profile_Scoped_And_Ordered()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var factory = new PostgresWebApplicationFactory(
            _fixture.ConnectionString!,
            new Dictionary<string, string?> { ["Ai:Enabled"] = "true" });

        var suffix = Guid.NewGuid().ToString("N")[..8];
        Guid profileA, profileB, helpNear, helpFar, repositoryChunk;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

            var model = AddModel(db, $"rag-text-{suffix}");
            profileA = AddProfile(db, $"rag-a-{suffix}", model.Id).Id;
            profileB = AddProfile(db, $"rag-b-{suffix}", model.Id).Id;

            // Two domains. `helpNear` and `helpFar` are Product Help; the third
            // chunk is repository-only and sits at the SAME point in the space as
            // the query, so a missing domain filter would put it first.
            helpNear = AddChunk(db, RagDomains.ProductHelp, $"docs/help/a-{suffix}.md", "near");
            helpFar = AddChunk(db, RagDomains.ProductHelp, $"docs/help/b-{suffix}.md", "far");
            repositoryChunk = AddChunk(
                db, RagDomains.NubArcaRepository, $"src/Secret-{suffix}.cs", "repository-only");
            await db.SaveChangesAsync();

            AddEmbedding(db, serializer, helpNear, profileA, Unit(0));
            AddEmbedding(db, serializer, helpFar, profileA, Unit(1));
            AddEmbedding(db, serializer, repositoryChunk, profileA, Unit(0));
            // The SAME chunk under a different profile, pointing the other way.
            // A search that mixed profiles would compare two coordinate systems.
            AddEmbedding(db, serializer, helpNear, profileB, Unit(2));
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var vectors = scope.ServiceProvider.GetRequiredService<RagVectorIndexService>();
            var profile = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .AiProfiles.FirstAsync(p => p.Id == profileA);

            Assert.True(await vectors.IsBackendAvailableAsync(Dimension));

            var sync = await vectors.SyncProfileAsync(profile, limit: null, dryRun: false);
            Assert.True(sync.Available);
            Assert.Equal(3, sync.Synced);
            Assert.Equal(3, sync.VectorIndexed);
            Assert.Equal(0, sync.Failed);
            Assert.Equal(0, sync.SkippedDimensionMismatch);

            // Idempotent: a second pass indexes nothing new.
            var again = await vectors.SyncProfileAsync(profile, limit: null, dryRun: false);
            Assert.Equal(0, again.Synced);
            Assert.Equal(3, again.VectorIndexed);

            var results = await vectors.SearchAsync(
                RagDomains.ProductHelp, profileA, Unit(0), take: 10);

            Assert.NotNull(results);
            // Domain scoping is done IN the query: the repository chunk is at the
            // exact query point and must not appear at all.
            Assert.DoesNotContain(results!, r => r.ChunkId == repositoryChunk);
            Assert.Equal(new[] { helpNear, helpFar }, results!.Select(r => r.ChunkId).ToArray());
            Assert.True(results[0].Score > results[1].Score);
            Assert.Equal(1.0, results[0].Score, 3);

            // Profile scoping: B indexed nothing, so B searches nothing.
            var profileBResults = await vectors.SearchAsync(
                RagDomains.ProductHelp, profileB, Unit(0), take: 10);
            Assert.Empty(profileBResults!);
        }
    }

    [SkippableFact]
    public async Task An_Unsupported_Dimension_Falls_Back_Cleanly()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var factory = new PostgresWebApplicationFactory(
            _fixture.ConnectionString!, new Dictionary<string, string?>());
        var suffix = Guid.NewGuid().ToString("N")[..8];

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vectors = scope.ServiceProvider.GetRequiredService<RagVectorIndexService>();

        var model = AddModel(db, $"rag-wide-{suffix}", dimension: 1024);
        var profile = AddProfile(db, $"rag-wide-{suffix}", model.Id, dimension: 1024);
        await db.SaveChangesAsync();

        // No table exists for 1024, and there is no truncation or padding into
        // the 384 one: a coerced vector is not the vector the model produced,
        // and nothing downstream would notice.
        Assert.False(await vectors.IsBackendAvailableAsync(1024));

        var sync = await vectors.SyncProfileAsync(profile, limit: null, dryRun: false);
        Assert.False(sync.Available);
        Assert.Equal(RagFailureReasons.EmbeddingDimensionUnsupported, sync.Reason);

        Assert.Equal(
            RagVectorUpsertOutcome.SkippedUnsupported,
            await vectors.TryUpsertAsync(
                Guid.NewGuid(), Guid.NewGuid(), profile.Id, new float[1024], 1024));
    }

    [SkippableFact]
    public async Task Stale_Vector_Rows_Are_Removed_With_Their_Canonical_Embedding()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        await using var factory = new PostgresWebApplicationFactory(
            _fixture.ConnectionString!, new Dictionary<string, string?>());
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Guid profileId, chunkId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            var model = AddModel(db, $"rag-stale-{suffix}");
            profileId = AddProfile(db, $"rag-stale-{suffix}", model.Id).Id;
            chunkId = AddChunk(db, RagDomains.ProductHelp, $"docs/help/s-{suffix}.md", "stale");
            await db.SaveChangesAsync();
            AddEmbedding(db, serializer, chunkId, profileId, Unit(0));
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var vectors = scope.ServiceProvider.GetRequiredService<RagVectorIndexService>();
            var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);

            await vectors.SyncProfileAsync(profile, null, false);
            Assert.Equal(1, await vectors.CountIndexedAsync(profileId));

            // The chunk's text changed, so its canonical embedding is dropped —
            // the vector describes text that no longer exists.
            db.RagChunkEmbeddings.RemoveRange(
                await db.RagChunkEmbeddings.Where(e => e.ChunkId == chunkId).ToListAsync());
            await db.SaveChangesAsync();

            Assert.Equal(0, await vectors.CountIndexedAsync(profileId));
        }
    }

    [SkippableFact]
    public async Task Search_Never_Returns_A_Raw_Vector()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        // The result type carries a chunk id and a rounded score. There is no
        // member that could hold a vector, which is a stronger statement than a
        // caller that receives one and promises not to use it.
        var properties = typeof(RagVectorNeighbor).GetProperties();
        Assert.Equal(2, properties.Length);
        Assert.DoesNotContain(properties, p =>
            p.PropertyType == typeof(float[]) || p.PropertyType == typeof(byte[]));
        await Task.CompletedTask;
    }

    // ---- fixtures ------------------------------------------------------------

    /// A one-hot unit vector. Cosine between two of them is 1 when the index
    /// matches and 0 when it does not, so ordering is arithmetic rather than
    /// approximate.
    private static float[] Unit(int index)
    {
        var vector = new float[Dimension];
        vector[index] = 1f;
        return vector;
    }

    private static AiModel AddModel(AppDbContext db, string key, int dimension = Dimension)
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = key,
            Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.TextEmbedding,
            Modality = AiModalities.Text,
            Dimension = dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        return model;
    }

    private static AiProfile AddProfile(
        AppDbContext db, string key, Guid modelId, int dimension = Dimension)
    {
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = key,
            AiModelId = modelId,
            Capability = AiCapabilities.TextEmbedding,
            Modality = AiModalities.Text,
            Dimension = dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.AiProfiles.Add(profile);
        return profile;
    }

    private static Guid AddChunk(AppDbContext db, string domain, string sourceKey, string body)
    {
        var source = new RagSource
        {
            Id = Guid.NewGuid(),
            SourceKey = sourceKey,
            Path = sourceKey,
            Title = sourceKey,
            SourceKind = RagSourceKinds.Documentation,
            ContentHash = RagHash.Sha256Hex(body),
            Language = RagLanguages.English,
            CodeLanguage = RagCodeLanguages.Markdown,
            CreatedAt = DateTime.UtcNow,
        };
        db.RagSources.Add(source);
        db.RagDomainSources.Add(new RagDomainSource
        {
            Id = Guid.NewGuid(),
            DomainKey = domain,
            SourceId = source.Id,
            Revision = "pg-revision",
            Priority = 60,
            CreatedAt = DateTime.UtcNow,
        });
        var chunk = new RagChunk
        {
            Id = Guid.NewGuid(),
            SourceId = source.Id,
            Ordinal = 1,
            Heading = "Section",
            Text = body,
            TextHash = RagHash.Sha256Hex(body),
            Language = RagLanguages.English,
            CreatedAt = DateTime.UtcNow,
        };
        db.RagChunks.Add(chunk);
        return chunk.Id;
    }

    private static void AddEmbedding(
        AppDbContext db, IAiVectorSerializer serializer, Guid chunkId, Guid profileId, float[] vector)
        => db.RagChunkEmbeddings.Add(new RagChunkEmbedding
        {
            Id = Guid.NewGuid(),
            ChunkId = chunkId,
            ProfileId = profileId,
            EmbeddingBytes = serializer.Serialize(vector, Dimension),
            Dimension = Dimension,
            CreatedAt = DateTime.UtcNow,
        });
}
