using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Users;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// REAL pgvector: owner-private People similar-faces search (threshold filtering,
// monotonic superset, owner scope, Private-Vault exclusion). Faces are inserted
// with controlled 512-d unit vectors + their pgvector rows, one face assigned to
// a Person as the query. Skipped when Docker / the pgvector image is unavailable.
[Collection(PgVectorIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class PeopleSimilarFacesPgIntegrationTests : IAsyncLifetime
{
    private const int Dim = 512;
    private readonly PgVectorContainerFixture _fixture;

    public PeopleSimilarFacesPgIntegrationTests(PgVectorContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Similar_Faces_Threshold_Superset_Owner_And_Vault_Scoped()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var profileKey = $"face-people-{suffix}";
        var settings = new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:FaceProfileKey"] = profileKey, // the active face profile for search
        };
        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, settings);

        const int near = 5;
        const int far = 3;
        Guid ownerA, ownerB, personId, profileId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            var vectors = scope.ServiceProvider.GetRequiredService<FaceVectorIndexService>();

            ownerA = (await users.CreateAsync($"pa-{suffix}@example.com", "A")).Id;
            ownerB = (await users.CreateAsync($"pb-{suffix}@example.com", "B")).Id;
            var model = AddModel(db, $"m-{suffix}");
            var profile = AddProfile(db, profileKey, model.Id);
            profileId = profile.Id;

            // Query face (assigned to the person) — identical to the near set.
            var q = await AddFaceAsync(db, ser, vectors, ownerA, profileId, OneHot(0), null);
            for (var i = 0; i < near; i++)
                await AddFaceAsync(db, ser, vectors, ownerA, profileId, OneHot(0), null);
            for (var i = 0; i < far; i++)
                await AddFaceAsync(db, ser, vectors, ownerA, profileId, FarVec(), null);

            // A vaulted identical face (must never surface) + an owner-B identical
            // face (owner scope excludes it).
            var vault = new PrivateVault
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerA, DisplayName = "Private",
                PasswordHash = "x", EncryptionMode = PrivateVaultEncryptionModes.None, CreatedAt = DateTime.UtcNow,
            };
            db.PrivateVaults.Add(vault);
            await AddFaceAsync(db, ser, vectors, ownerA, profileId, OneHot(0), vault.Id);
            await AddFaceAsync(db, ser, vectors, ownerB, profileId, OneHot(0), null);

            var person = new Person { Id = Guid.NewGuid(), OwnerUserId = ownerA, DisplayName = "P", CreatedAt = DateTime.UtcNow };
            db.People.Add(person);
            db.PersonFaceAssignments.Add(new PersonFaceAssignment
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerA, PersonId = person.Id, FaceDetectionId = q.FaceId,
                Source = PersonFaceAssignmentSources.UserConfirmed, CreatedAt = DateTime.UtcNow,
            });
            personId = person.Id;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();

            // Thresholds stay within the admin band [0.20, 0.95]. Far set sits at
            // ~0.3 cosine: excluded at 0.5, included at 0.25.
            var strict = await CollectAsync(people, ownerA, personId, 0.5);
            var broad = await CollectAsync(people, ownerA, personId, 0.25);

            // Strict (>=0.5) returns only the near set; the assigned query face,
            // the far set, the vaulted face, and owner B's face never appear.
            Assert.Equal(near, strict.Count);
            // Broad (>=0.25) adds the far set → superset.
            Assert.Equal(near + far, broad.Count);
            Assert.True(strict.ToHashSet().IsSubsetOf(broad.ToHashSet()));

            // Owner B gets nothing for A's person (cross-owner 404 handled at API;
            // service returns null for a foreign person).
            var foreign = await people.FindSimilarFacesAsync(ownerB, personId, 0.0, 50, null);
            Assert.Null(foreign);
        }
    }

    private static async Task<List<Guid>> CollectAsync(PeopleService people, Guid owner, Guid personId, double threshold)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 50; guard++)
        {
            var page = await people.FindSimilarFacesAsync(owner, personId, threshold, 4, cursor);
            Assert.NotNull(page);
            Assert.True(page!.ProfileAvailable);
            ids.AddRange(page.Items.Select(i => i.FaceId));
            if (!page.HasMore || page.NextCursor is null) break;
            cursor = page.NextCursor;
        }
        return ids;
    }

    private sealed record Seeded(Guid FaceId, Guid FileId, Guid BlobId);

    private static async Task<Seeded> AddFaceAsync(
        AppDbContext db, IAiVectorSerializer ser, FaceVectorIndexService vectors,
        Guid ownerId, Guid profileId, float[] vector, Guid? vaultId)
    {
        var blobId = Guid.NewGuid();
        db.BlobObjects.Add(new BlobObject
        {
            Id = blobId, Sha256 = $"sha-{blobId:N}", SizeBytes = 1,
            StorageKey = $"sk/{blobId:N}", ReferenceCount = 1, CreatedAt = DateTime.UtcNow,
        });
        var fileId = Guid.NewGuid();
        db.FileItems.Add(new FileItem
        {
            Id = fileId, OwnerUserId = ownerId, BlobObjectId = blobId, Name = $"f-{fileId:N}.png",
            MimeType = "image/png", SizeBytes = 1, PrivateVaultId = vaultId,
            CreatedAt = DateTime.UtcNow, EffectiveDateTaken = DateTime.UtcNow,
        });
        var detId = Guid.NewGuid();
        db.FaceDetections.Add(new FaceDetection
        {
            Id = detId, BlobObjectId = blobId, ProfileId = profileId, FaceIndex = 0,
            BoundingBoxX = 0.1, BoundingBoxY = 0.1, BoundingBoxWidth = 0.2, BoundingBoxHeight = 0.2,
            DetectionScore = 0.9, LandmarksJson = "[]", CreatedAt = DateTime.UtcNow,
        });
        var embId = Guid.NewGuid();
        db.FaceEmbeddings.Add(new FaceEmbedding
        {
            Id = embId, FaceDetectionId = detId, ProfileId = profileId,
            EmbeddingBytes = ser.Serialize(vector, Dim), Dimension = Dim,
            EmbeddingStatus = AiArtifactStatuses.Completed, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        await vectors.TryUpsertFaceVectorAsync(embId, detId, blobId, profileId, vector, Dim);
        return new Seeded(detId, fileId, blobId);
    }

    private static AiModel AddModel(AppDbContext db, string key)
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = key, Provider = AiProviders.Onnx,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face, Version = 1,
            Dimension = Dim, DistanceMetric = AiDistanceMetrics.Cosine, Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        return model;
    }

    private static AiProfile AddProfile(AppDbContext db, string key, Guid modelId)
    {
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = key, AiModelId = modelId,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face,
            Dimension = Dim, DistanceMetric = AiDistanceMetrics.Cosine, IsDefault = false,
            Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiProfiles.Add(profile);
        return profile;
    }

    private static float[] OneHot(int i)
    {
        var v = new float[Dim];
        v[i] = 1f;
        return v;
    }

    // ~0.3 cosine to OneHot(0): [1, 3.18, 0, …] (normalized at insert time).
    private static float[] FarVec()
    {
        var v = new float[Dim];
        v[0] = 1f;
        v[1] = 3.18f;
        return v;
    }
}
