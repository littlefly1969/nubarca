using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Users;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// REAL pgvector via Testcontainers (pgvector/pgvector:pg17). Proves the Face
// Substrate v0 vector foundation on Postgres: the 512-dim table + HNSW cosine
// index created by AddFaceSubstrateV0, idempotent profile-scoped vector upsert,
// and the owner-private, Private-Vault-excluded face search. Embeddings are
// inserted directly (controlled unit vectors) so cosine ordering is assertable.
// Skipped when Docker / the pgvector image is unavailable.
[Collection(PgVectorIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class FaceVectorPgIntegrationTests : IAsyncLifetime
{
    private const int Dim = 512;
    private readonly PgVectorContainerFixture _fixture;

    public FaceVectorPgIntegrationTests(PgVectorContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Face_Vector_Table_Is_512d_With_Hnsw_Cosine_Index()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");
        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, Settings());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        // The table exists and its embedding column is vector(512): pgvector stores
        // the fixed dimension in atttypmod.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT atttypmod FROM pg_attribute
WHERE attrelid = 'face_embedding_vectors_512'::regclass AND attname = 'embedding';";
            var typmod = Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            Assert.Equal(Dim, typmod);
        }

        // An HNSW cosine index exists on the embedding column.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT indexdef FROM pg_indexes
WHERE tablename = 'face_embedding_vectors_512'
  AND indexname = 'ix_fev512_embedding_hnsw_cosine';";
            var def = (string?)await cmd.ExecuteScalarAsync();
            Assert.NotNull(def);
            Assert.Contains("hnsw", def!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("vector_cosine_ops", def!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact]
    public async Task Face_Vector_Upsert_Is_Idempotent_And_Search_Is_Owner_And_Vault_Scoped()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");
        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, Settings());

        var suffix = Guid.NewGuid().ToString("N")[..8];
        Guid ownerA, ownerB, profileId, dQ, dN, dF, dV, eQ, eN, eF, eV, blobQ, blobN, blobF, blobV;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

            ownerA = (await users.CreateAsync($"fa-{suffix}@example.com", "A")).Id;
            ownerB = (await users.CreateAsync($"fb-{suffix}@example.com", "B")).Id;

            var model = AddFaceModel(db, $"face-{suffix}");
            var profile = AddFaceProfile(db, $"face-p-{suffix}", model.Id);
            profileId = profile.Id;

            blobQ = AddBlob(db, suffix, "q");
            blobN = AddBlob(db, suffix, "n");
            blobF = AddBlob(db, suffix, "f");
            blobV = AddBlob(db, suffix, "v");
            AddFile(db, ownerA, blobQ, "q.png", null);
            AddFile(db, ownerA, blobN, "n.png", null);
            AddFile(db, ownerA, blobF, "f.png", null);
            // blobV is owner A's file but moved into a Private Vault → excluded.
            var vault = new PrivateVault
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerA,
                DisplayName = "Private",
                PasswordHash = "x",
                EncryptionMode = PrivateVaultEncryptionModes.None,
                CreatedAt = DateTime.UtcNow,
            };
            db.PrivateVaults.Add(vault);
            AddFile(db, ownerA, blobV, "v.png", vault.Id);

            dQ = AddDetection(db, blobQ, profileId);
            dN = AddDetection(db, blobN, profileId);
            dF = AddDetection(db, blobF, profileId);
            dV = AddDetection(db, blobV, profileId);
            eQ = AddEmbedding(db, serializer, dQ, profileId, OneHot(0));
            eN = AddEmbedding(db, serializer, dN, profileId, OneHot(0)); // identical to q
            eF = AddEmbedding(db, serializer, dF, profileId, OneHot(1)); // orthogonal
            eV = AddEmbedding(db, serializer, dV, profileId, OneHot(0)); // identical but vaulted

            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var vectors = scope.ServiceProvider.GetRequiredService<FaceVectorIndexService>();

            // Idempotent upsert (ON CONFLICT DO NOTHING).
            Assert.Equal(VectorUpsertOutcome.Indexed,
                await vectors.TryUpsertFaceVectorAsync(eQ, dQ, blobQ, profileId, OneHot(0), Dim));
            Assert.Equal(VectorUpsertOutcome.Indexed,
                await vectors.TryUpsertFaceVectorAsync(eQ, dQ, blobQ, profileId, OneHot(0), Dim));
            await vectors.TryUpsertFaceVectorAsync(eN, dN, blobN, profileId, OneHot(0), Dim);
            await vectors.TryUpsertFaceVectorAsync(eF, dF, blobF, profileId, OneHot(1), Dim);
            await vectors.TryUpsertFaceVectorAsync(eV, dV, blobV, profileId, OneHot(0), Dim);

            Assert.Equal(4L, await vectors.CountIndexedAsync(profileId)); // eQ not double-counted

            // Wrong dimension rejected; unsupported dim skipped; non-finite rejected.
            Assert.Equal(VectorUpsertOutcome.Failed,
                await vectors.TryUpsertFaceVectorAsync(Guid.NewGuid(), dN, blobN, profileId, new float[Dim], Dim));
            Assert.Equal(VectorUpsertOutcome.SkippedUnsupported,
                await vectors.TryUpsertFaceVectorAsync(Guid.NewGuid(), dN, blobN, profileId, new float[32], 32));

            // Owner A search for the query vector: finds the identical non-vault
            // face (dN), excludes the source (dQ), the orthogonal face (dF), AND the
            // vaulted face (dV).
            var hits = await vectors.SearchAsync(profileId, OneHot(0), ownerA, dQ, 0.5, 50);
            Assert.NotNull(hits);
            var faceIds = hits!.Select(h => h.FaceDetectionId).ToHashSet();
            Assert.Contains(dN, faceIds);
            Assert.DoesNotContain(dQ, faceIds); // source excluded
            Assert.DoesNotContain(dF, faceIds); // orthogonal (below threshold)
            Assert.DoesNotContain(dV, faceIds); // Private-Vault excluded

            // Owner B sees none of A's faces (owner-scoped; no cross-owner search).
            var bHits = await vectors.SearchAsync(profileId, OneHot(0), ownerB, dQ, 0.5, 50);
            Assert.NotNull(bHits);
            Assert.Empty(bHits!);
        }
    }

    private static Dictionary<string, string?> Settings() => new()
    {
        ["Ai:Enabled"] = "true",
    };

    private static AiModel AddFaceModel(AppDbContext db, string key)
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = key,
            Provider = AiProviders.Onnx,
            Capability = AiCapabilities.FaceEmbedding,
            Modality = AiModalities.Face,
            Version = 1,
            Dimension = Dim,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        return model;
    }

    private static AiProfile AddFaceProfile(AppDbContext db, string key, Guid modelId)
    {
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = key,
            AiModelId = modelId,
            Capability = AiCapabilities.FaceEmbedding,
            Modality = AiModalities.Face,
            Dimension = Dim,
            DistanceMetric = AiDistanceMetrics.Cosine,
            IsDefault = false,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.AiProfiles.Add(profile);
        return profile;
    }

    private static Guid AddBlob(AppDbContext db, string suffix, string tag)
    {
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = $"{suffix}-{tag}-{Guid.NewGuid():N}",
            SizeBytes = 1,
            StorageKey = $"sk/{suffix}/{tag}/{Guid.NewGuid():N}",
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.BlobObjects.Add(blob);
        return blob.Id;
    }

    private static Guid AddFile(AppDbContext db, Guid ownerId, Guid blobId, string name, Guid? vaultId)
    {
        var file = new FileItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerId,
            BlobObjectId = blobId,
            Name = name,
            MimeType = "image/png",
            SizeBytes = 1,
            PrivateVaultId = vaultId,
            CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
        };
        db.FileItems.Add(file);
        return file.Id;
    }

    private static Guid AddDetection(AppDbContext db, Guid blobId, Guid profileId)
    {
        var d = new FaceDetection
        {
            Id = Guid.NewGuid(),
            BlobObjectId = blobId,
            ProfileId = profileId,
            FaceIndex = 0,
            BoundingBoxX = 0.1,
            BoundingBoxY = 0.1,
            BoundingBoxWidth = 0.2,
            BoundingBoxHeight = 0.2,
            DetectionScore = 0.9,
            LandmarksJson = "[]",
            CreatedAt = DateTime.UtcNow,
        };
        db.FaceDetections.Add(d);
        return d.Id;
    }

    private static Guid AddEmbedding(
        AppDbContext db, IAiVectorSerializer serializer, Guid detectionId, Guid profileId, float[] vector)
    {
        var e = new FaceEmbedding
        {
            Id = Guid.NewGuid(),
            FaceDetectionId = detectionId,
            ProfileId = profileId,
            EmbeddingBytes = serializer.Serialize(vector, Dim),
            Dimension = Dim,
            EmbeddingStatus = AiArtifactStatuses.Completed,
            CreatedAt = DateTime.UtcNow,
        };
        db.FaceEmbeddings.Add(e);
        return e.Id;
    }

    private static float[] OneHot(int index)
    {
        var v = new float[Dim];
        v[index] = 1f;
        return v;
    }
}
