using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Ai;

// Cluster-to-person merge + detach/reassign semantics
// (feature/people-ux-polish-cluster-merge). Owner-private, Private-Vault-excluded,
// no storage internals.
public sealed class PeopleClusterMergeTests
{
    private const string FaceProfileKey = "det-face-embedding-v1";
    private const int Dim = 32;

    private static readonly string[] Forbidden =
    {
        "EmbeddingBytes", "embeddingBytes", "StorageKey", "storageKey", "BlobObjectId", "blobObjectId",
        "Sha256", "sha256", "/storage/objects/", "PrivateVaultId", "privateVaultId", "ProfileId", "at NubArca.",
    };

    private static SqliteWebApplicationFactory Factory()
    {
        var f = new SqliteWebApplicationFactory(
            new Dictionary<string, string?> { ["Ai:Enabled"] = "true" },
            poolHost: true);
        f.EnsureDatabaseCreated();
        return f;
    }

    private static async Task<Guid> SeedProfileAsync(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
        await registry.SeedDeterministicProfilesAsync();
        return (await registry.GetProfileByKeyAsync(FaceProfileKey))!.Id;
    }

    private static float[] OneHot(int i) { var v = new float[Dim]; v[i] = 1f; return v; }

    private sealed record SeededFace(Guid FaceId, Guid FileId, Guid BlobId);

    private static async Task<SeededFace> SeedFaceAsync(
        SqliteWebApplicationFactory f, Guid ownerId, Guid profileId, float[]? vector = null, Guid? vaultId = null)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
        var blobId = Guid.NewGuid();
        db.BlobObjects.Add(new BlobObject { Id = blobId, Sha256 = $"sha-{blobId:N}", SizeBytes = 1, StorageKey = $"sk/{blobId:N}", ReferenceCount = 1, CreatedAt = DateTime.UtcNow });
        var fileId = Guid.NewGuid();
        db.FileItems.Add(new FileItem { Id = fileId, OwnerUserId = ownerId, BlobObjectId = blobId, Name = $"p-{fileId:N}.png", MimeType = "image/png", SizeBytes = 1, PrivateVaultId = vaultId, CreatedAt = DateTime.UtcNow, EffectiveDateTaken = DateTime.UtcNow });
        var faceId = Guid.NewGuid();
        db.FaceDetections.Add(new FaceDetection { Id = faceId, BlobObjectId = blobId, ProfileId = profileId, FaceIndex = 0, BoundingBoxX = 0.1, BoundingBoxY = 0.1, BoundingBoxWidth = 0.2, BoundingBoxHeight = 0.2, DetectionScore = 0.9, LandmarksJson = "[]", CreatedAt = DateTime.UtcNow });
        if (vector is not null)
        {
            db.FaceEmbeddings.Add(new FaceEmbedding { Id = Guid.NewGuid(), FaceDetectionId = faceId, ProfileId = profileId, EmbeddingBytes = ser.Serialize(vector, Dim), Dimension = Dim, EmbeddingStatus = AiArtifactStatuses.Completed, CreatedAt = DateTime.UtcNow });
        }
        await db.SaveChangesAsync();
        return new SeededFace(faceId, fileId, blobId);
    }

    private static async Task<Guid> CreateVaultAsync(SqliteWebApplicationFactory f, Guid ownerId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vault = new PrivateVault { Id = Guid.NewGuid(), OwnerUserId = ownerId, DisplayName = "Private", PasswordHash = "x", EncryptionMode = PrivateVaultEncryptionModes.None, CreatedAt = DateTime.UtcNow };
        db.PrivateVaults.Add(vault);
        await db.SaveChangesAsync();
        return vault.Id;
    }

    private static async Task<Guid> SeedClusterAsync(
        SqliteWebApplicationFactory f, Guid ownerId, Guid profileId, IEnumerable<Guid> faceIds,
        string status = FaceClusterStatuses.Suggested)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = faceIds.ToList();
        var clusterId = Guid.NewGuid();
        db.FaceClusters.Add(new FaceCluster
        {
            Id = clusterId, OwnerUserId = ownerId, ProfileId = profileId, Status = status,
            ConfidenceAggregate = 0.9, MemberCount = ids.Count, RepresentativeFaceDetectionId = ids.FirstOrDefault(),
            ClusterKey = $"test:{clusterId:N}", CreatedAt = DateTime.UtcNow,
        });
        foreach (var fid in ids)
        {
            db.FaceClusterMembers.Add(new FaceClusterMember { Id = Guid.NewGuid(), FaceClusterId = clusterId, FaceDetectionId = fid, SimilarityScore = 0.9, MembershipSource = FaceClusterMemberSources.AutoCluster, CreatedAt = DateTime.UtcNow });
        }
        await db.SaveChangesAsync();
        return clusterId;
    }

    private static async Task<Guid?> AssignedPersonAsync(SqliteWebApplicationFactory f, Guid faceId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.PersonFaceAssignments.AsNoTracking().FirstOrDefaultAsync(a => a.FaceDetectionId == faceId))?.PersonId;
    }

    private static async Task<string> ClusterStatusAsync(SqliteWebApplicationFactory f, Guid clusterId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.FaceClusters.AsNoTracking().FirstAsync(c => c.Id == clusterId)).Status;
    }

    // ---- detach / reassign semantics -------------------------------------

    [Fact]
    public async Task Reassign_Sequence_Leaves_Only_Final_Person()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var face = await SeedFaceAsync(f, ownerId, profileId);

        var p1 = await (await client.PostAsJsonAsync($"/api/people/faces/{face.FaceId}/assign", new { name = "P1" })).Content.ReadFromJsonAsync<PersonDto>();
        await client.DeleteAsync($"/api/people/faces/{face.FaceId}/assignment");
        var p2 = await (await client.PostAsJsonAsync($"/api/people/faces/{face.FaceId}/assign", new { name = "P2" })).Content.ReadFromJsonAsync<PersonDto>();
        await client.DeleteAsync($"/api/people/faces/{face.FaceId}/assignment");
        var p3 = await (await client.PostAsJsonAsync($"/api/people/faces/{face.FaceId}/assign", new { name = "P3" })).Content.ReadFromJsonAsync<PersonDto>();

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.PersonFaceAssignments.Where(a => a.FaceDetectionId == face.FaceId).ToListAsync();
        var only = Assert.Single(rows);
        Assert.Equal(p3!.PersonId, only.PersonId);
        Assert.NotEqual(p1!.PersonId, only.PersonId);
        Assert.NotEqual(p2!.PersonId, only.PersonId);
        // Removal never created an ignore mark → the face is not banned.
        Assert.Equal(0, await db.IgnoredFaces.CountAsync(i => i.FaceDetectionId == face.FaceId));
    }

    [Fact]
    public async Task Ignored_Face_Is_Distinct_From_Removed_Unassigned_Face()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var removed = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var ignored = await SeedFaceAsync(f, ownerId, profileId, OneHot(1));

        // removed: assign then remove → unassigned again (appears in the pool).
        await client.PostAsJsonAsync($"/api/people/faces/{removed.FaceId}/assign", new { name = "X" });
        await client.DeleteAsync($"/api/people/faces/{removed.FaceId}/assignment");
        // ignored: dismissed → does NOT appear in the pool.
        await client.PostAsync($"/api/people/faces/{ignored.FaceId}/ignore", null);

        var page = await (await client.GetAsync("/api/people/unassigned-faces?limit=50")).Content.ReadFromJsonAsync<UnassignedFacesPage>();
        var ids = page!.Items.Select(i => i.FaceId).ToHashSet();
        Assert.Contains(removed.FaceId, ids);       // removed → back in the pool
        Assert.DoesNotContain(ignored.FaceId, ids); // ignored → hidden
    }

    // ---- cluster-to-person merge -----------------------------------------

    [Fact]
    public async Task Cluster_Assign_Assigns_All_Eligible_Unassigned_And_Confirms()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var a = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var b = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var c = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var clusterId = await SeedClusterAsync(f, ownerId, profileId, new[] { a.FaceId, b.FaceId, c.FaceId });
        var person = await (await client.PostAsJsonAsync("/api/people", new { name = "Group" })).Content.ReadFromJsonAsync<PersonDto>();

        var resp = await client.PostAsJsonAsync($"/api/people/{person!.PersonId}/clusters/{clusterId}/assign", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var summary = await resp.Content.ReadFromJsonAsync<ClusterAssignSummaryDto>();
        Assert.Equal(3, summary!.AssignedCount);
        Assert.Equal(0, summary.SkippedAlreadyAssignedCount);

        Assert.Equal(person.PersonId, await AssignedPersonAsync(f, a.FaceId));
        Assert.Equal(person.PersonId, await AssignedPersonAsync(f, c.FaceId));
        // Cluster is confirmed → leaves the suggested queue.
        Assert.Equal(FaceClusterStatuses.Confirmed, await ClusterStatusAsync(f, clusterId));
        var suggested = await (await client.GetAsync("/api/people/suggested-groups")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(clusterId.ToString(), suggested);
        AssertNoLeak(await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cluster_Assign_Skips_Faces_On_Other_People_By_Default_And_Moves_With_Flag()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var shared = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var free = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var other = await (await client.PostAsJsonAsync("/api/people", new { name = "Other" })).Content.ReadFromJsonAsync<PersonDto>();
        var target = await (await client.PostAsJsonAsync("/api/people", new { name = "Target" })).Content.ReadFromJsonAsync<PersonDto>();
        // Pre-assign `shared` to Other.
        await client.PostAsJsonAsync($"/api/people/faces/{shared.FaceId}/assign", new { personId = other!.PersonId });
        var clusterId = await SeedClusterAsync(f, ownerId, profileId, new[] { shared.FaceId, free.FaceId });

        // Default: only `free` is assigned; `shared` stays on Other.
        var s1 = await (await client.PostAsJsonAsync($"/api/people/{target!.PersonId}/clusters/{clusterId}/assign", new { })).Content.ReadFromJsonAsync<ClusterAssignSummaryDto>();
        Assert.Equal(1, s1!.AssignedCount);
        Assert.Equal(1, s1.SkippedAlreadyAssignedCount);
        Assert.Equal(other.PersonId, await AssignedPersonAsync(f, shared.FaceId)); // not stolen

        // With moveAssigned: `shared` is reassigned to Target.
        var s2 = await (await client.PostAsJsonAsync($"/api/people/{target.PersonId}/clusters/{clusterId}/assign", new { moveAssigned = true })).Content.ReadFromJsonAsync<ClusterAssignSummaryDto>();
        Assert.Equal(0, s2!.SkippedAlreadyAssignedCount);
        Assert.Equal(target.PersonId, await AssignedPersonAsync(f, shared.FaceId));
    }

    [Fact]
    public async Task Cluster_Assign_Excludes_Ignored_And_Vaulted_Faces()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var good = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var ignored = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var vaulted = await SeedFaceAsync(f, ownerId, profileId, OneHot(0), vaultId: await CreateVaultAsync(f, ownerId));
        await client.PostAsync($"/api/people/faces/{ignored.FaceId}/ignore", null);
        var clusterId = await SeedClusterAsync(f, ownerId, profileId, new[] { good.FaceId, ignored.FaceId, vaulted.FaceId });
        var person = await (await client.PostAsJsonAsync("/api/people", new { name = "P" })).Content.ReadFromJsonAsync<PersonDto>();

        var s = await (await client.PostAsJsonAsync($"/api/people/{person!.PersonId}/clusters/{clusterId}/assign", new { })).Content.ReadFromJsonAsync<ClusterAssignSummaryDto>();
        Assert.Equal(1, s!.AssignedCount);          // only `good`
        Assert.Equal(1, s.SkippedIgnoredCount);
        Assert.Equal(1, s.SkippedIneligibleCount);  // vaulted
        Assert.Null(await AssignedPersonAsync(f, ignored.FaceId));
        Assert.Null(await AssignedPersonAsync(f, vaulted.FaceId));
    }

    [Fact]
    public async Task Cluster_Assign_DryRun_Does_Not_Persist()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var a = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var clusterId = await SeedClusterAsync(f, ownerId, profileId, new[] { a.FaceId });
        var person = await (await client.PostAsJsonAsync("/api/people", new { name = "P" })).Content.ReadFromJsonAsync<PersonDto>();

        var s = await (await client.PostAsJsonAsync($"/api/people/{person!.PersonId}/clusters/{clusterId}/assign", new { dryRun = true })).Content.ReadFromJsonAsync<ClusterAssignSummaryDto>();
        Assert.Equal(1, s!.AssignedCount);
        Assert.Null(await AssignedPersonAsync(f, a.FaceId));                       // nothing persisted
        Assert.Equal(FaceClusterStatuses.Suggested, await ClusterStatusAsync(f, clusterId)); // unchanged
    }

    [Fact]
    public async Task Cluster_Assign_Is_Owner_Scoped_And_CrossOwner_Is_404()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, _) = await f.CreateAuthenticatedClientAsync("owner@example.com");
        var (_, other) = await f.CreateAuthenticatedClientAsync("other@example.com");
        var a = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var clusterId = await SeedClusterAsync(f, ownerId, profileId, new[] { a.FaceId });
        // Person belongs to the other owner; cluster to the first owner → 404.
        var otherPerson = await (await other.PostAsJsonAsync("/api/people", new { name = "O" })).Content.ReadFromJsonAsync<PersonDto>();

        var resp = await other.PostAsJsonAsync($"/api/people/{otherPerson!.PersonId}/clusters/{clusterId}/assign", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Null(await AssignedPersonAsync(f, a.FaceId));
    }

    [Fact]
    public async Task Manual_Cluster_Assignment_Survives_Reclustering()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var a = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var b = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var clusterId = await SeedClusterAsync(f, ownerId, profileId, new[] { a.FaceId, b.FaceId });
        var person = await (await client.PostAsJsonAsync("/api/people", new { name = "Group" })).Content.ReadFromJsonAsync<PersonDto>();
        await client.PostAsJsonAsync($"/api/people/{person!.PersonId}/clusters/{clusterId}/assign", new { });

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
        var clustering = scope.ServiceProvider.GetRequiredService<FaceClusteringService>();
        await clustering.ClusterOwnerAsync(ownerId, profile, new FaceSettings(0.40, 0.30, 0.35, 0.20, 0.95, 50, 1.0));

        Assert.Equal(person.PersonId, await AssignedPersonAsync(f, a.FaceId));
        Assert.Equal(person.PersonId, await AssignedPersonAsync(f, b.FaceId));
    }

    [Fact]
    public async Task Group_Faces_Returns_Surfaceable_Members_And_CrossOwner_Is_404()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync("owner@example.com");
        var (_, other) = await f.CreateAuthenticatedClientAsync("other@example.com");
        var a = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var b = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var clusterId = await SeedClusterAsync(f, ownerId, profileId, new[] { a.FaceId, b.FaceId });

        var resp = await client.GetAsync($"/api/people/groups/{clusterId}/faces");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains(a.FaceId.ToString(), raw);
        Assert.Contains(b.FaceId.ToString(), raw);
        AssertNoLeak(raw);

        // Cross-owner → generic 404.
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/people/groups/{clusterId}/faces")).StatusCode);
    }

    private static void AssertNoLeak(string text)
    {
        foreach (var n in Forbidden)
        {
            Assert.DoesNotContain(n, text, StringComparison.Ordinal);
        }
    }
}
