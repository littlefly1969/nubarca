using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Ai;

// People/Face clustering v0 — owner-private clustering + People endpoints + admin
// thresholds. Faces are seeded directly with controlled 32-dim vectors (the
// deterministic face-embedding profile, det-face-embedding-v1) so cosine grouping
// is assertable without ONNX weights. pgvector-only similar-faces behavior is
// covered in the Postgres integration suite.
public sealed class PeopleClusteringTests
{
    private const string FaceProfileKey = "det-face-embedding-v1";
    private const int Dim = 32;

    private static readonly string[] Forbidden =
    {
        "EmbeddingBytes", "embeddingBytes", "StorageKey", "storageKey", "storage_key",
        "BlobObjectId", "blobObjectId", "Sha256", "sha256", "/storage/objects/",
        "PasswordHash", "TokenHash", "PayloadJson", "PrivateVaultId", "privateVaultId",
        "ProfileId", "profileId", "at NubArca.",
    };

    private static void AssertNoLeak(string text)
    {
        foreach (var n in Forbidden)
        {
            Assert.DoesNotContain(n, text, StringComparison.Ordinal);
        }
    }

    private static SqliteWebApplicationFactory Factory(params (string Key, string Value)[] settings)
    {
        var dict = settings.ToDictionary(s => s.Key, s => (string?)s.Value);
        var f = new SqliteWebApplicationFactory(dict, poolHost: true);
        f.EnsureDatabaseCreated();
        return f;
    }

    private static async Task<Guid> SeedProfileAsync(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
        await registry.SeedDeterministicProfilesAsync();
        var profile = await registry.GetProfileByKeyAsync(FaceProfileKey);
        return profile!.Id;
    }

    private static float[] OneHot(int i, float scale = 1f)
    {
        var v = new float[Dim];
        v[i] = scale;
        return v;
    }

    // Vector at ~0.5 cosine to OneHot(0): [1, sqrt(3), 0, …] (service normalizes).
    private static float[] HalfCosine()
    {
        var v = new float[Dim];
        v[0] = 1f;
        v[1] = 1.7320508f;
        return v;
    }

    private sealed record SeededFace(Guid FaceId, Guid FileId, Guid BlobId);

    // Seed one face: a fresh blob + owner FileItem (optionally vaulted, optionally
    // media-library-Excluded) + a blob-level FaceDetection + a FaceEmbedding with
    // the given vector.
    private static async Task<SeededFace> SeedFaceAsync(
        AppDbContext db, IAiVectorSerializer serializer, Guid ownerId, Guid profileId, float[] vector,
        Guid? vaultId = null, Guid? existingBlobId = null,
        MediaLibraryState mediaLibraryState = MediaLibraryState.Active)
    {
        var blobId = existingBlobId ?? Guid.NewGuid();
        if (existingBlobId is null)
        {
            db.BlobObjects.Add(new BlobObject
            {
                Id = blobId,
                Sha256 = $"sha-{blobId:N}",
                SizeBytes = 1,
                StorageKey = $"sk/{blobId:N}",
                ReferenceCount = 1,
                CreatedAt = DateTime.UtcNow,
            });
        }

        var fileId = Guid.NewGuid();
        db.FileItems.Add(new FileItem
        {
            Id = fileId,
            OwnerUserId = ownerId,
            BlobObjectId = blobId,
            Name = $"photo-{fileId:N}.png",
            MimeType = "image/png",
            SizeBytes = 1,
            PrivateVaultId = vaultId,
            MediaLibraryState = mediaLibraryState,
            CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
        });

        var faceId = Guid.NewGuid();
        // If this blob already has a detection (shared blob), reuse it.
        var detection = existingBlobId is not null
            ? await db.FaceDetections.FirstOrDefaultAsync(d => d.BlobObjectId == blobId && d.ProfileId == profileId)
            : null;
        if (detection is null)
        {
            detection = new FaceDetection
            {
                Id = faceId,
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
            db.FaceDetections.Add(detection);
            db.FaceEmbeddings.Add(new FaceEmbedding
            {
                Id = Guid.NewGuid(),
                FaceDetectionId = detection.Id,
                ProfileId = profileId,
                EmbeddingBytes = serializer.Serialize(vector, Dim),
                Dimension = Dim,
                EmbeddingStatus = AiArtifactStatuses.Completed,
                CreatedAt = DateTime.UtcNow,
            });
        }

        // Persist immediately so a later shared-blob seed finds the detection and
        // the unique (blob, profile, faceIndex) index is respected.
        await db.SaveChangesAsync();
        return new SeededFace(detection.Id, fileId, blobId);
    }

    private static async Task<Guid> CreateVaultAsync(AppDbContext db, Guid ownerId)
    {
        var vault = new PrivateVault
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerId,
            DisplayName = "Private",
            PasswordHash = "x",
            EncryptionMode = PrivateVaultEncryptionModes.None,
            CreatedAt = DateTime.UtcNow,
        };
        db.PrivateVaults.Add(vault);
        return vault.Id;
    }

    private static FaceSettings Settings(double cluster) =>
        new(cluster, 0.30, 0.35, 0.20, 0.95, 50, 1.0);

    // ---- clustering service ----------------------------------------------

    [Fact]
    public async Task Clustering_Groups_Similar_Faces_And_Is_Owner_Scoped()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var profileId = await SeedProfileAsync(f);
        var (ownerA, _) = await f.CreateAuthenticatedClientAsync("a@example.com");
        var (ownerB, _) = await f.CreateAuthenticatedClientAsync("b@example.com");

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            // Owner A: 3 identical faces (cluster) + 1 orthogonal (singleton).
            await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
            await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
            await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
            await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(5));
            // Owner B: 1 identical-to-A face — must NOT join A's cluster.
            await SeedFaceAsync(db, ser, ownerB, profileId, OneHot(0));
            await db.SaveChangesAsync();

            var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
            var clustering = scope.ServiceProvider.GetRequiredService<FaceClusteringService>();
            var outcome = await clustering.ClusterOwnerAsync(ownerA, profile, Settings(0.40));

            Assert.Equal(1, outcome.GroupsCreated);
            Assert.Equal(3, outcome.FacesGrouped);

            var clusters = await db.FaceClusters.Where(c => c.OwnerUserId == ownerA).ToListAsync();
            var cluster = Assert.Single(clusters);
            Assert.Equal(3, cluster.MemberCount);
            // Owner B has no clusters (only 1 face).
            Assert.Empty(await db.FaceClusters.Where(c => c.OwnerUserId == ownerB).ToListAsync());
        }
    }

    [Fact]
    public async Task Same_Blob_Face_Clusters_Independently_Per_Owner()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var profileId = await SeedProfileAsync(f);
        var (ownerA, _) = await f.CreateAuthenticatedClientAsync("a@example.com");
        var (ownerB, _) = await f.CreateAuthenticatedClientAsync("b@example.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

        // A shared blob (one blob-level FaceDetection), referenced by BOTH owners.
        var shared = await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
        await SeedFaceAsync(db, ser, ownerB, profileId, OneHot(0), existingBlobId: shared.BlobId);
        // Each owner also has an identical private face so a cluster of 2 forms.
        await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
        await SeedFaceAsync(db, ser, ownerB, profileId, OneHot(0));
        await db.SaveChangesAsync();

        var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
        var clustering = scope.ServiceProvider.GetRequiredService<FaceClusteringService>();
        await clustering.ClusterOwnerAsync(ownerA, profile, Settings(0.40));
        await clustering.ClusterOwnerAsync(ownerB, profile, Settings(0.40));

        // The shared face is a member of BOTH owners' clusters — separate rows,
        // no shared identity.
        var membershipOwners = await (
            from m in db.FaceClusterMembers
            join c in db.FaceClusters on m.FaceClusterId equals c.Id
            where m.FaceDetectionId == shared.FaceId
            select c.OwnerUserId).ToListAsync();
        Assert.Contains(ownerA, membershipOwners);
        Assert.Contains(ownerB, membershipOwners);
    }

    [Fact]
    public async Task Clustering_Excludes_Private_Vault_Faces()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var profileId = await SeedProfileAsync(f);
        var (ownerA, _) = await f.CreateAuthenticatedClientAsync("a@example.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
        var vaultId = await CreateVaultAsync(db, ownerA);

        await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
        await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
        var vaulted = await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0), vaultId: vaultId);
        await db.SaveChangesAsync();

        var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
        var clustering = scope.ServiceProvider.GetRequiredService<FaceClusteringService>();
        var outcome = await clustering.ClusterOwnerAsync(ownerA, profile, Settings(0.40));

        Assert.Equal(2, outcome.FacesConsidered); // vaulted face excluded
        var memberFaceIds = await db.FaceClusterMembers.Select(m => m.FaceDetectionId).ToListAsync();
        Assert.DoesNotContain(vaulted.FaceId, memberFaceIds);
    }

    // ---- Slice 3: media-library (Excluded) exclusion from the candidate set ----

    private static async Task SetStateAsync(AppDbContext db, Guid fileId, MediaLibraryState state)
    {
        var file = await db.FileItems.IgnoreQueryFilters().FirstAsync(x => x.Id == fileId);
        file.MediaLibraryState = state;
        await db.SaveChangesAsync();
    }

    // (1) active-only reference → included; (2) excluded-only reference → excluded.
    [Fact]
    public async Task Clustering_Includes_Active_And_Excludes_Excluded_Only_Faces()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var profileId = await SeedProfileAsync(f);
        var (ownerA, _) = await f.CreateAuthenticatedClientAsync("a@example.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

        await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0)); // Active
        await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0)); // Active
        var excluded = await SeedFaceAsync(
            db, ser, ownerA, profileId, OneHot(0), mediaLibraryState: MediaLibraryState.Excluded);
        await db.SaveChangesAsync();

        var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
        var clustering = scope.ServiceProvider.GetRequiredService<FaceClusteringService>();
        var outcome = await clustering.ClusterOwnerAsync(ownerA, profile, Settings(0.40));

        Assert.Equal(2, outcome.FacesConsidered); // only the two Active faces
        var cluster = Assert.Single(await db.FaceClusters.Where(c => c.OwnerUserId == ownerA).ToListAsync());
        Assert.Equal(2, cluster.MemberCount);
        var memberFaceIds = await db.FaceClusterMembers.Select(m => m.FaceDetectionId).ToListAsync();
        Assert.DoesNotContain(excluded.FaceId, memberFaceIds);
    }

    // (3) blob referenced by BOTH an Active and an Excluded file → still eligible
    // via the Active reference.
    [Fact]
    public async Task Clustering_Keeps_Blob_Referenced_By_Active_And_Excluded()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var profileId = await SeedProfileAsync(f);
        var (ownerA, _) = await f.CreateAuthenticatedClientAsync("a@example.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

        // One blob-level face, referenced by an Active file AND an Excluded file.
        var shared = await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0)); // Active ref
        await SeedFaceAsync(
            db, ser, ownerA, profileId, OneHot(0),
            existingBlobId: shared.BlobId, mediaLibraryState: MediaLibraryState.Excluded);
        // A second Active identical face so a cluster of 2 forms.
        await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
        await db.SaveChangesAsync();

        var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
        var clustering = scope.ServiceProvider.GetRequiredService<FaceClusteringService>();
        var outcome = await clustering.ClusterOwnerAsync(ownerA, profile, Settings(0.40));

        Assert.Equal(2, outcome.FacesConsidered); // shared (via Active) + the extra
        var memberFaceIds = await db.FaceClusterMembers.Select(m => m.FaceDetectionId).ToListAsync();
        Assert.Contains(shared.FaceId, memberFaceIds); // stays eligible via the Active file
    }

    // (4) a face whose only reference is a Personal (vault) file → excluded.
    [Fact]
    public async Task Clustering_Excludes_Personal_Only_Faces()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var profileId = await SeedProfileAsync(f);
        var (ownerA, _) = await f.CreateAuthenticatedClientAsync("a@example.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
        var vaultId = await CreateVaultAsync(db, ownerA);

        await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0)); // Active
        await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0)); // Active
        var personal = await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0), vaultId: vaultId);
        await db.SaveChangesAsync();

        var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
        var clustering = scope.ServiceProvider.GetRequiredService<FaceClusteringService>();
        var outcome = await clustering.ClusterOwnerAsync(ownerA, profile, Settings(0.40));

        Assert.Equal(2, outcome.FacesConsidered);
        var memberFaceIds = await db.FaceClusterMembers.Select(m => m.FaceDetectionId).ToListAsync();
        Assert.DoesNotContain(personal.FaceId, memberFaceIds);
    }

    // (5) enqueue-then-exclude race + (6) no artifact deletion + (7) restore.
    // Each run re-evaluates eligibility, so excluding a candidate after it was
    // first grouped simply drops it on the next run, and restoring re-includes
    // it — all without deleting any detection / embedding / earlier work.
    [Fact]
    public async Task Reclustering_Reevaluates_Eligibility_Preserves_Artifacts_And_Restores()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var profileId = await SeedProfileAsync(f);
        var (ownerA, _) = await f.CreateAuthenticatedClientAsync("a@example.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

        await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
        await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
        var later = await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
        await db.SaveChangesAsync();

        var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
        var clustering = scope.ServiceProvider.GetRequiredService<FaceClusteringService>();

        // First run (all Active): a cluster of 3.
        var first = await clustering.ClusterOwnerAsync(ownerA, profile, Settings(0.40));
        Assert.Equal(3, first.FacesConsidered);
        Assert.Equal(3, (await db.FaceClusters.Where(c => c.OwnerUserId == ownerA).SingleAsync()).MemberCount);

        // A file was excluded AFTER it was first grouped. The re-run re-checks
        // eligibility and skips it — no error.
        await SetStateAsync(db, later.FileId, MediaLibraryState.Excluded);
        var afterExclude = await clustering.ClusterOwnerAsync(ownerA, profile, Settings(0.40));
        Assert.Equal(2, afterExclude.FacesConsidered);
        var membersAfterExclude = await db.FaceClusterMembers.Select(m => m.FaceDetectionId).ToListAsync();
        Assert.DoesNotContain(later.FaceId, membersAfterExclude);

        // No facial artifacts were deleted by exclusion / re-clustering.
        Assert.Equal(3, await db.FaceDetections.CountAsync());
        Assert.Equal(3, await db.FaceEmbeddings.CountAsync());

        // Restore → eligible again on the next run.
        await SetStateAsync(db, later.FileId, MediaLibraryState.Active);
        var afterRestore = await clustering.ClusterOwnerAsync(ownerA, profile, Settings(0.40));
        Assert.Equal(3, afterRestore.FacesConsidered);
        var membersAfterRestore = await db.FaceClusterMembers.Select(m => m.FaceDetectionId).ToListAsync();
        Assert.Contains(later.FaceId, membersAfterRestore);
    }

    [Fact]
    public async Task Clustering_Uses_Configured_Cluster_Threshold()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var profileId = await SeedProfileAsync(f);
        var (ownerA, _) = await f.CreateAuthenticatedClientAsync("a@example.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
        await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
        await SeedFaceAsync(db, ser, ownerA, profileId, HalfCosine()); // ~0.5 cosine to the first
        await db.SaveChangesAsync();

        var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
        var clustering = scope.ServiceProvider.GetRequiredService<FaceClusteringService>();

        // Threshold 0.40 ≤ 0.5 → they cluster.
        var low = await clustering.ClusterOwnerAsync(ownerA, profile, Settings(0.40));
        Assert.Equal(1, low.GroupsCreated);

        // Threshold 0.60 > 0.5 → they do not.
        var high = await clustering.ClusterOwnerAsync(ownerA, profile, Settings(0.60));
        Assert.Equal(0, high.GroupsCreated);
    }

    [Fact]
    public async Task Clustering_Excludes_Assigned_And_Preserves_Ignored()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var profileId = await SeedProfileAsync(f);
        var (ownerA, _) = await f.CreateAuthenticatedClientAsync("a@example.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
        var f1 = await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
        await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
        await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
        await db.SaveChangesAsync();
        var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
        var clustering = scope.ServiceProvider.GetRequiredService<FaceClusteringService>();

        // First run: one cluster of 3.
        await clustering.ClusterOwnerAsync(ownerA, profile, Settings(0.40));
        var firstCluster = await db.FaceClusters.FirstAsync(c => c.OwnerUserId == ownerA);
        Assert.Equal(3, firstCluster.MemberCount);

        // Ignore its faces (per-face) → excluded from future suggestions. Ignoring
        // is now purely per-face (IgnoredFace rows), not a cluster-status pin.
        var memberIds = await db.FaceClusterMembers
            .Where(m => m.FaceClusterId == firstCluster.Id)
            .Select(m => m.FaceDetectionId).ToListAsync();
        foreach (var id in memberIds)
        {
            db.IgnoredFaces.Add(new IgnoredFace { Id = Guid.NewGuid(), OwnerUserId = ownerA, FaceDetectionId = id, CreatedAt = DateTime.UtcNow });
        }
        await db.SaveChangesAsync();

        var afterIgnore = await clustering.ClusterOwnerAsync(ownerA, profile, Settings(0.40));
        Assert.Equal(0, afterIgnore.GroupsCreated); // all 3 excluded by per-face ignore

        // Restore the ignored faces so the assignment part starts clean.
        db.IgnoredFaces.RemoveRange(await db.IgnoredFaces.Where(i => i.OwnerUserId == ownerA).ToListAsync());
        await db.SaveChangesAsync();

        // Assigning a face to a person also removes it from clustering.
        db.FaceClusters.RemoveRange(await db.FaceClusters.Where(c => c.OwnerUserId == ownerA).ToListAsync());
        var person = new Person { Id = Guid.NewGuid(), OwnerUserId = ownerA, DisplayName = "X", CreatedAt = DateTime.UtcNow };
        db.People.Add(person);
        db.PersonFaceAssignments.Add(new PersonFaceAssignment
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerA, PersonId = person.Id, FaceDetectionId = f1.FaceId,
            Source = PersonFaceAssignmentSources.UserConfirmed, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var afterAssign = await clustering.ClusterOwnerAsync(ownerA, profile, Settings(0.40));
        Assert.Equal(2, afterAssign.FacesConsidered); // the assigned face excluded
    }

    // ---- job gating ------------------------------------------------------

    [Fact]
    public async Task Cluster_Job_No_Op_When_Disabled_By_Default()
    {
        using var f = Factory(("Ai:Enabled", "true")); // clustering flag OFF
        var profileId = await SeedProfileAsync(f);
        var (ownerA, _) = await f.CreateAuthenticatedClientAsync("a@example.com");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
            await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
            await db.SaveChangesAsync();
        }

        Assert.Equal(JobStatuses.Succeeded, await RunClusterJobAsync(f));
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.FaceClusters.CountAsync());
        }
    }

    [Fact]
    public async Task Cluster_Job_Builds_Clusters_When_Enabled()
    {
        using var f = Factory(("Ai:Enabled", "true"), ("Ai:FaceClusteringEnabled", "true"));
        var profileId = await SeedProfileAsync(f);
        var (ownerA, _) = await f.CreateAuthenticatedClientAsync("a@example.com");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
            await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
            await db.SaveChangesAsync();
        }

        Assert.Equal(JobStatuses.Succeeded, await RunClusterJobAsync(f));
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.FaceClusters.AnyAsync(c => c.OwnerUserId == ownerA));
        }
    }

    private static async Task<string> RunClusterJobAsync(SqliteWebApplicationFactory f)
    {
        Guid jobId;
        using (var scope = f.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            jobId = (await queue.EnqueueAsync(JobTypes.AiFacesClusterBackfill, new AiBackfillJobPayload())).Id;
        }
        for (var i = 0; i < 50; i++)
        {
            using var scope = f.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(10) == 0) break;
        }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.BackgroundJobs.Where(j => j.Id == jobId).Select(j => j.Status).SingleAsync();
        }
    }

    // ---- endpoints -------------------------------------------------------

    [Fact]
    public async Task People_Crud_And_Suggested_Group_Assign_Flow()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var profileId = await SeedProfileAsync(f);
        var (ownerA, client) = await f.CreateAuthenticatedClientAsync("a@example.com");

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
            await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
            await db.SaveChangesAsync();
            var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
            await scope.ServiceProvider.GetRequiredService<FaceClusteringService>()
                .ClusterOwnerAsync(ownerA, profile, Settings(0.40));
        }

        // Suggested groups present.
        var groups = await client.GetFromJsonAsync<List<SuggestedGroupDto>>("/api/people/suggested-groups");
        Assert.NotNull(groups);
        var group = Assert.Single(groups!);
        Assert.Equal(2, group.FaceCount);
        Assert.NotNull(group.Representative);

        // Assign a name → person created, group confirmed.
        var assign = await client.PostAsJsonAsync(
            $"/api/people/groups/{group.GroupId}/assign", new { name = "Alice" });
        assign.EnsureSuccessStatusCode();
        var person = await assign.Content.ReadFromJsonAsync<PersonDto>();
        Assert.Equal("Alice", person!.Name);
        Assert.Equal(2, person.FaceCount);

        // People list shows Alice; suggestions no longer include the group.
        var people = await client.GetFromJsonAsync<List<PersonDto>>("/api/people");
        Assert.Contains(people!, p => p.Name == "Alice");
        var groupsAfter = await client.GetFromJsonAsync<List<SuggestedGroupDto>>("/api/people/suggested-groups");
        Assert.Empty(groupsAfter!);

        // Person photos + rename + no-leak.
        var photos = await client.GetAsync($"/api/people/{person.PersonId}/photos");
        photos.EnsureSuccessStatusCode();
        AssertNoLeak(await photos.Content.ReadAsStringAsync());

        var renamed = await client.PutAsJsonAsync($"/api/people/{person.PersonId}", new { name = "Alice R" });
        renamed.EnsureSuccessStatusCode();
        var renamedDto = await renamed.Content.ReadFromJsonAsync<PersonDto>();
        Assert.Equal("Alice R", renamedDto!.Name);

        AssertNoLeak(await (await client.GetAsync("/api/people")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Add_And_Remove_Face_From_Person()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var profileId = await SeedProfileAsync(f);
        var (ownerA, client) = await f.CreateAuthenticatedClientAsync("a@example.com");
        Guid faceId;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            faceId = (await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0))).FaceId;
            await db.SaveChangesAsync();
        }

        var create = await client.PostAsJsonAsync("/api/people", new { name = "Bob" });
        var person = await create.Content.ReadFromJsonAsync<PersonDto>();

        var add = await client.PostAsJsonAsync($"/api/people/{person!.PersonId}/faces", new { faceId });
        Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);
        Assert.Equal(1, (await client.GetFromJsonAsync<PersonDto>($"/api/people/{person.PersonId}"))!.FaceCount);

        var remove = await client.DeleteAsync($"/api/people/{person.PersonId}/faces/{faceId}");
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<PersonDto>($"/api/people/{person.PersonId}"))!.FaceCount);
    }

    [Fact]
    public async Task Cross_Owner_Person_Is_Not_Found()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        await SeedProfileAsync(f);
        var (_, clientA) = await f.CreateAuthenticatedClientAsync("a@example.com");
        var (_, clientB) = await f.CreateAuthenticatedClientAsync("b@example.com");

        var create = await clientA.PostAsJsonAsync("/api/people", new { name = "Alice" });
        var person = await create.Content.ReadFromJsonAsync<PersonDto>();

        var foreign = await clientB.GetAsync($"/api/people/{person!.PersonId}");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var foreignRename = await clientB.PutAsJsonAsync($"/api/people/{person.PersonId}", new { name = "hax" });
        Assert.Equal(HttpStatusCode.NotFound, foreignRename.StatusCode);
    }

    [Fact]
    public async Task Suggested_Groups_Exclude_Vaulted_Members()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var profileId = await SeedProfileAsync(f);
        var (ownerA, client) = await f.CreateAuthenticatedClientAsync("a@example.com");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            var vaultId = await CreateVaultAsync(db, ownerA);
            // Two visible + one vaulted; cluster forms from the two visible only.
            await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
            await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
            await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0), vaultId: vaultId);
            await db.SaveChangesAsync();
            var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
            await scope.ServiceProvider.GetRequiredService<FaceClusteringService>()
                .ClusterOwnerAsync(ownerA, profile, Settings(0.40));
        }

        var groups = await client.GetFromJsonAsync<List<SuggestedGroupDto>>("/api/people/suggested-groups");
        var group = Assert.Single(groups!);
        Assert.Equal(2, group.FaceCount); // vaulted member never counted/surfaced
    }

    [Fact]
    public async Task Similar_Faces_Is_Graceful_When_Vector_Backend_Unavailable()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var profileId = await SeedProfileAsync(f);
        var (ownerA, client) = await f.CreateAuthenticatedClientAsync("a@example.com");
        Guid faceId;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            faceId = (await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0))).FaceId;
            await db.SaveChangesAsync();
        }
        var person = await (await client.PostAsJsonAsync("/api/people", new { name = "Bob" })).Content.ReadFromJsonAsync<PersonDto>();
        await client.PostAsJsonAsync($"/api/people/{person!.PersonId}/faces", new { faceId });

        // SQLite has no pgvector → graceful unavailable, never a 500.
        var resp = await client.GetAsync($"/api/people/{person.PersonId}/similar-faces?minSimilarity=0.35");
        resp.EnsureSuccessStatusCode();
        var page = await resp.Content.ReadFromJsonAsync<SimilarFacesPage>();
        Assert.False(page!.ProfileAvailable);
        Assert.Equal("vector-backend-unavailable", page.UnavailableReason);
    }

    [Fact]
    public async Task People_Endpoints_Require_Authentication()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var anon = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/people")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/people/suggested-groups")).StatusCode);
    }

    // ---- admin face settings write ---------------------------------------

    [Fact]
    public async Task Admin_Can_Read_And_Update_Thresholds_And_It_Affects_Clustering()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var profileId = await SeedProfileAsync(f);
        var (ownerA, _) = await f.CreateAuthenticatedClientAsync("a@example.com");
        var adminId = await f.SeedUserAsync("admin@example.com");
        await f.PromoteToAdminAsync(adminId);
        var admin = await f.LoginAsync("admin@example.com");

        // GET returns active values.
        var get = await admin.GetAsync("/api/admin/ai/face-settings");
        get.EnsureSuccessStatusCode();
        Assert.Contains("clusterSimilarityThreshold", await get.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // Seed two ~0.5-cosine faces for owner A.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            await SeedFaceAsync(db, ser, ownerA, profileId, OneHot(0));
            await SeedFaceAsync(db, ser, ownerA, profileId, HalfCosine());
            await db.SaveChangesAsync();
        }

        // Raise the cluster threshold above 0.5 → the pair no longer groups.
        var put = await admin.PutAsJsonAsync("/api/admin/ai/face-settings", new { clusterSimilarityThreshold = 0.6 });
        put.EnsureSuccessStatusCode();

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = await scope.ServiceProvider.GetRequiredService<IFaceSettingsProvider>().GetAsync();
            Assert.Equal(0.6, settings.ClusterSimilarityThreshold);
            var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
            var outcome = await scope.ServiceProvider.GetRequiredService<FaceClusteringService>()
                .ClusterOwnerAsync(ownerA, profile, settings);
            Assert.Equal(0, outcome.GroupsCreated);
        }
    }

    [Fact]
    public async Task Admin_Settings_Put_Validates_Ranges()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var adminId = await f.SeedUserAsync("admin@example.com");
        await f.PromoteToAdminAsync(adminId);
        var admin = await f.LoginAsync("admin@example.com");

        // min >= max is rejected.
        var bad = await admin.PutAsJsonAsync("/api/admin/ai/face-settings",
            new { searchMinSimilarity = 0.8, searchMaxSimilarity = 0.5 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // out of [0,1] rejected.
        var bad2 = await admin.PutAsJsonAsync("/api/admin/ai/face-settings",
            new { clusterSimilarityThreshold = 1.5 });
        Assert.Equal(HttpStatusCode.BadRequest, bad2.StatusCode);
    }

    [Fact]
    public async Task Non_Admin_Cannot_Update_Settings()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        var (_, client) = await f.CreateAuthenticatedClientAsync("a@example.com");
        var resp = await client.PutAsJsonAsync("/api/admin/ai/face-settings", new { clusterSimilarityThreshold = 0.5 });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
