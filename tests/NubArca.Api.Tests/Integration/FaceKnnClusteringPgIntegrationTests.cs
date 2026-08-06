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

// REAL pgvector: scalable kNN face clustering (feature/people-clustering-pgvector-knn).
// Verifies the HNSW/cosine kNN path forms the expected clusters and honours every
// eligibility/privacy/manual-assignment invariant, and that it agrees with the
// exact O(n²) oracle on well-separated data. Skipped when Docker/pgvector absent.
[Collection(PgVectorIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class FaceKnnClusteringPgIntegrationTests : IAsyncLifetime
{
    private const int Dim = 512;
    private readonly PgVectorContainerFixture _fixture;

    public FaceKnnClusteringPgIntegrationTests(PgVectorContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Knn_Clusters_Separated_Groups_And_Excludes_Assigned_Ignored_Vaulted_CrossOwner()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var profileKey = $"face-knn-{suffix}";
        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:FaceProfileKey"] = profileKey,
            ["Ai:Face:ClusteringMode"] = "pgvector_knn",
        });

        Guid ownerA, ownerB, profileId;
        Guid assignedFace, ignoredFace, vaultedFace;
        var groupA = new List<Guid>();
        var groupB = new List<Guid>();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            var vectors = scope.ServiceProvider.GetRequiredService<FaceVectorIndexService>();

            ownerA = (await users.CreateAsync($"ka-{suffix}@example.com", "A")).Id;
            ownerB = (await users.CreateAsync($"kb-{suffix}@example.com", "B")).Id;
            var model = AddModel(db, $"m-{suffix}");
            profileId = AddProfile(db, profileKey, model.Id).Id;

            // Two well-separated groups of 3 identical faces (cosine 1.0 within, 0 across).
            for (var i = 0; i < 3; i++) groupA.Add((await AddFaceAsync(db, ser, vectors, ownerA, profileId, OneHot(0), null)).FaceId);
            for (var i = 0; i < 3; i++) groupB.Add((await AddFaceAsync(db, ser, vectors, ownerA, profileId, OneHot(5), null)).FaceId);

            // An identical-to-A face already ASSIGNED to a person → excluded.
            assignedFace = (await AddFaceAsync(db, ser, vectors, ownerA, profileId, OneHot(0), null)).FaceId;
            var person = new Person { Id = Guid.NewGuid(), OwnerUserId = ownerA, DisplayName = "P", CreatedAt = DateTime.UtcNow };
            db.People.Add(person);
            db.PersonFaceAssignments.Add(new PersonFaceAssignment
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerA, PersonId = person.Id, FaceDetectionId = assignedFace,
                Source = PersonFaceAssignmentSources.UserConfirmed, CreatedAt = DateTime.UtcNow,
            });

            // An identical-to-B face IGNORED → excluded.
            ignoredFace = (await AddFaceAsync(db, ser, vectors, ownerA, profileId, OneHot(5), null)).FaceId;
            db.IgnoredFaces.Add(new IgnoredFace { Id = Guid.NewGuid(), OwnerUserId = ownerA, FaceDetectionId = ignoredFace, CreatedAt = DateTime.UtcNow });

            // A vaulted identical-to-A face → excluded.
            var vault = new PrivateVault
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerA, DisplayName = "Private",
                PasswordHash = "x", EncryptionMode = PrivateVaultEncryptionModes.None, CreatedAt = DateTime.UtcNow,
            };
            db.PrivateVaults.Add(vault);
            vaultedFace = (await AddFaceAsync(db, ser, vectors, ownerA, profileId, OneHot(0), vault.Id)).FaceId;

            // Owner B's identical faces → never join A's clusters.
            await AddFaceAsync(db, ser, vectors, ownerB, profileId, OneHot(0), null);
            await AddFaceAsync(db, ser, vectors, ownerB, profileId, OneHot(0), null);

            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
            var clustering = scope.ServiceProvider.GetRequiredService<FaceClusteringService>();
            var settings = await scope.ServiceProvider.GetRequiredService<IFaceSettingsProvider>().GetAsync();

            var outcome = await clustering.ClusterOwnerAsync(ownerA, profile, settings);

            // Two clusters of 3 (the two eligible groups); assigned/ignored/vaulted excluded.
            Assert.Equal(2, outcome.GroupsCreated);
            Assert.Equal(6, outcome.FacesGrouped);

            var clusteredFaceIds = await (
                from m in db.FaceClusterMembers
                join c in db.FaceClusters on m.FaceClusterId equals c.Id
                where c.OwnerUserId == ownerA
                select m.FaceDetectionId).ToListAsync();

            Assert.DoesNotContain(assignedFace, clusteredFaceIds);
            Assert.DoesNotContain(ignoredFace, clusteredFaceIds);
            Assert.DoesNotContain(vaultedFace, clusteredFaceIds);
            foreach (var g in groupA) Assert.Contains(g, clusteredFaceIds);
            foreach (var g in groupB) Assert.Contains(g, clusteredFaceIds);

            // Owner B has its own separate cluster (never mixed with A).
            var bClusters = await db.FaceClusters.CountAsync(c => c.OwnerUserId == ownerB);
            Assert.Equal(0, bClusters); // B not clustered in this run (we only clustered A)
        }
    }

    [SkippableFact]
    public async Task Knn_Preserves_Manual_Assignment_And_Confirmed_Cluster_Across_Rerun()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var profileKey = $"face-knn2-{suffix}";
        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:FaceProfileKey"] = profileKey,
            ["Ai:Face:ClusteringMode"] = "pgvector_knn",
        });

        Guid ownerA, profileId;
        var faces = new List<Guid>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            var vectors = scope.ServiceProvider.GetRequiredService<FaceVectorIndexService>();
            ownerA = (await users.CreateAsync($"kc-{suffix}@example.com", "A")).Id;
            var model = AddModel(db, $"m2-{suffix}");
            profileId = AddProfile(db, profileKey, model.Id).Id;
            for (var i = 0; i < 3; i++) faces.Add((await AddFaceAsync(db, ser, vectors, ownerA, profileId, OneHot(0), null)).FaceId);
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
            var clustering = scope.ServiceProvider.GetRequiredService<FaceClusteringService>();
            var settings = await scope.ServiceProvider.GetRequiredService<IFaceSettingsProvider>().GetAsync();

            // First run → one suggested cluster; confirm it to a person.
            await clustering.ClusterOwnerAsync(ownerA, profile, settings);
            var group = await db.FaceClusters.FirstAsync(c => c.OwnerUserId == ownerA);
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            var person = await people.AssignGroupAsync(ownerA, group.Id, "Alice", null);
            Assert.NotNull(person);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
            var clustering = scope.ServiceProvider.GetRequiredService<FaceClusteringService>();
            var settings = await scope.ServiceProvider.GetRequiredService<IFaceSettingsProvider>().GetAsync();

            // Rerun → the manual assignments survive and the confirmed cluster stays.
            await clustering.ClusterOwnerAsync(ownerA, profile, settings);

            var assignedCount = await db.PersonFaceAssignments.CountAsync(a => a.OwnerUserId == ownerA);
            Assert.Equal(3, assignedCount);
            Assert.True(await db.FaceClusters.AnyAsync(c => c.OwnerUserId == ownerA && c.Status == FaceClusterStatuses.Confirmed));
            // No new suggested cluster was created for the already-assigned faces.
            Assert.Equal(0, await db.FaceClusters.CountAsync(c => c.OwnerUserId == ownerA && c.Status == FaceClusterStatuses.Suggested));
        }
    }

    [SkippableFact]
    public async Task Knn_And_Exact_Agree_On_Wellseparated_Data()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var profileKey = $"face-knn3-{suffix}";
        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:FaceProfileKey"] = profileKey,
        });

        Guid ownerA, profileId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            var vectors = scope.ServiceProvider.GetRequiredService<FaceVectorIndexService>();
            ownerA = (await users.CreateAsync($"kd-{suffix}@example.com", "A")).Id;
            var model = AddModel(db, $"m3-{suffix}");
            profileId = AddProfile(db, profileKey, model.Id).Id;
            for (var i = 0; i < 4; i++) await AddFaceAsync(db, ser, vectors, ownerA, profileId, OneHot(0), null);
            for (var i = 0; i < 4; i++) await AddFaceAsync(db, ser, vectors, ownerA, profileId, OneHot(7), null);
            for (var i = 0; i < 4; i++) await AddFaceAsync(db, ser, vectors, ownerA, profileId, OneHot(13), null);
            await db.SaveChangesAsync();
        }

        HashSet<string> Partition(List<(Guid cluster, Guid face)> rows) =>
            rows.GroupBy(r => r.cluster)
                .Select(g => string.Join(",", g.Select(x => x.face.ToString()).OrderBy(s => s)))
                .ToHashSet();

        HashSet<string> knnPartition, exactPartition;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
            var clustering = scope.ServiceProvider.GetRequiredService<FaceClusteringService>();
            var settings = await scope.ServiceProvider.GetRequiredService<IFaceSettingsProvider>().GetAsync();

            await clustering.ClusterOwnerAsync(ownerA, profile, settings); // kNN (default)
            knnPartition = Partition(await ClusterRowsAsync(db, ownerA));

            await clustering.ClusterOwnerExactAsync(ownerA, profile, settings); // exact oracle
            exactPartition = Partition(await ClusterRowsAsync(db, ownerA));
        }

        // Three well-separated groups → identical partition from both algorithms.
        Assert.Equal(3, knnPartition.Count);
        Assert.Equal(exactPartition, knnPartition);
    }

    private static async Task<List<(Guid cluster, Guid face)>> ClusterRowsAsync(AppDbContext db, Guid ownerId)
    {
        var rows = await (
            from m in db.FaceClusterMembers.AsNoTracking()
            join c in db.FaceClusters.AsNoTracking() on m.FaceClusterId equals c.Id
            where c.OwnerUserId == ownerId
            select new { c.Id, m.FaceDetectionId }).ToListAsync();
        return rows.Select(r => (r.Id, r.FaceDetectionId)).ToList();
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
}
