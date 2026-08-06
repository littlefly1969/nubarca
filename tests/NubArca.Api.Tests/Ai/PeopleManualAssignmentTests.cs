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

// Manual face-assignment UX (feature/people-manual-assignment-ux): assign a single
// face to a person (existing or new), move it (one-person-per-face), remove it,
// dismiss (ignore) it, and browse unassigned faces. Plus clustering safety: manual
// choices survive re-runs and removed faces become eligible again. Owner-private,
// Private-Vault-excluded, no storage internals.
public sealed class PeopleManualAssignmentTests
{
    private const string FaceProfileKey = "det-face-embedding-v1";
    private const int Dim = 32;

    private static readonly string[] Forbidden =
    {
        "EmbeddingBytes", "embeddingBytes", "StorageKey", "storageKey", "storage_key",
        "BlobObjectId", "blobObjectId", "Sha256", "sha256", "/storage/objects/",
        "PrivateVaultId", "privateVaultId", "ProfileId", "profileId", "at NubArca.",
    };

    private static void AssertNoLeak(string text)
    {
        foreach (var n in Forbidden)
        {
            Assert.DoesNotContain(n, text, StringComparison.Ordinal);
        }
    }

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

    private static float[] OneHot(int i)
    {
        var v = new float[Dim];
        v[i] = 1f;
        return v;
    }

    private sealed record SeededFace(Guid FaceId, Guid FileId, Guid BlobId);

    // Seed a face: blob + owner FileItem (optionally vaulted) + blob-level detection
    // + optional completed embedding.
    private static async Task<SeededFace> SeedFaceAsync(
        SqliteWebApplicationFactory f, Guid ownerId, Guid profileId, float[]? vector = null, Guid? vaultId = null)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

        var blobId = Guid.NewGuid();
        db.BlobObjects.Add(new BlobObject
        {
            Id = blobId, Sha256 = $"sha-{blobId:N}", SizeBytes = 1,
            StorageKey = $"sk/{blobId:N}", ReferenceCount = 1, CreatedAt = DateTime.UtcNow,
        });
        var fileId = Guid.NewGuid();
        db.FileItems.Add(new FileItem
        {
            Id = fileId, OwnerUserId = ownerId, BlobObjectId = blobId,
            Name = $"photo-{fileId:N}.png", MimeType = "image/png", SizeBytes = 1,
            PrivateVaultId = vaultId, CreatedAt = DateTime.UtcNow, EffectiveDateTaken = DateTime.UtcNow,
        });
        var faceId = Guid.NewGuid();
        db.FaceDetections.Add(new FaceDetection
        {
            Id = faceId, BlobObjectId = blobId, ProfileId = profileId, FaceIndex = 0,
            BoundingBoxX = 0.1, BoundingBoxY = 0.1, BoundingBoxWidth = 0.2, BoundingBoxHeight = 0.2,
            DetectionScore = 0.9, LandmarksJson = "[]", CreatedAt = DateTime.UtcNow,
        });
        if (vector is not null)
        {
            db.FaceEmbeddings.Add(new FaceEmbedding
            {
                Id = Guid.NewGuid(), FaceDetectionId = faceId, ProfileId = profileId,
                EmbeddingBytes = ser.Serialize(vector, Dim), Dimension = Dim,
                EmbeddingStatus = AiArtifactStatuses.Completed, CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return new SeededFace(faceId, fileId, blobId);
    }

    private static async Task<Guid> CreateVaultAsync(SqliteWebApplicationFactory f, Guid ownerId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vault = new PrivateVault
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerId, DisplayName = "Private",
            PasswordHash = "x", EncryptionMode = PrivateVaultEncryptionModes.None, CreatedAt = DateTime.UtcNow,
        };
        db.PrivateVaults.Add(vault);
        await db.SaveChangesAsync();
        return vault.Id;
    }

    private static async Task<int> AssignmentCountAsync(SqliteWebApplicationFactory f, Guid faceId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PersonFaceAssignments.CountAsync(a => a.FaceDetectionId == faceId);
    }

    private static async Task<Guid?> AssignedPersonAsync(SqliteWebApplicationFactory f, Guid faceId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var a = await db.PersonFaceAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.FaceDetectionId == faceId);
        return a?.PersonId;
    }

    // ---- assign / move / remove ------------------------------------------

    [Fact]
    public async Task Assign_Face_To_New_Person_Creates_Assignment()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var face = await SeedFaceAsync(f, ownerId, profileId);

        var resp = await client.PostAsJsonAsync($"/api/people/faces/{face.FaceId}/assign", new { name = "Alice" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<PersonDto>();
        Assert.Equal("Alice", body!.Name);
        Assert.Equal(1, body.FaceCount);

        Assert.Equal(1, await AssignmentCountAsync(f, face.FaceId));
        AssertNoLeak(await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Assign_Face_To_Another_Person_Moves_It_OnePerFace()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var face = await SeedFaceAsync(f, ownerId, profileId);

        var a = await (await client.PostAsJsonAsync($"/api/people/faces/{face.FaceId}/assign", new { name = "A" }))
            .Content.ReadFromJsonAsync<PersonDto>();
        var b = await (await client.PostAsJsonAsync("/api/people", new { name = "B" }))
            .Content.ReadFromJsonAsync<PersonDto>();

        // Move the face to B.
        var move = await client.PostAsJsonAsync($"/api/people/faces/{face.FaceId}/assign", new { personId = b!.PersonId });
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);

        Assert.Equal(1, await AssignmentCountAsync(f, face.FaceId)); // still exactly one
        Assert.Equal(b.PersonId, await AssignedPersonAsync(f, face.FaceId));
        Assert.NotEqual(a!.PersonId, await AssignedPersonAsync(f, face.FaceId));
    }

    [Fact]
    public async Task Remove_Face_Assignment_Makes_It_Unassigned()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var face = await SeedFaceAsync(f, ownerId, profileId);
        await client.PostAsJsonAsync($"/api/people/faces/{face.FaceId}/assign", new { name = "Alice" });

        var del = await client.DeleteAsync($"/api/people/faces/{face.FaceId}/assignment");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.Equal(0, await AssignmentCountAsync(f, face.FaceId));

        // A second delete is a generic 404 (nothing to remove).
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/people/faces/{face.FaceId}/assignment")).StatusCode);
    }

    [Fact]
    public async Task Assign_CrossOwner_Face_Is_404()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, _) = await f.CreateAuthenticatedClientAsync("owner@example.com");
        var (_, other) = await f.CreateAuthenticatedClientAsync("other@example.com");
        var face = await SeedFaceAsync(f, ownerId, profileId);

        var resp = await other.PostAsJsonAsync($"/api/people/faces/{face.FaceId}/assign", new { name = "X" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(0, await AssignmentCountAsync(f, face.FaceId));
    }

    [Fact]
    public async Task Vaulted_Face_Cannot_Be_Assigned()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var vaultId = await CreateVaultAsync(f, ownerId);
        var face = await SeedFaceAsync(f, ownerId, profileId, vaultId: vaultId);

        var resp = await client.PostAsJsonAsync($"/api/people/faces/{face.FaceId}/assign", new { name = "X" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(0, await AssignmentCountAsync(f, face.FaceId));
    }

    // ---- unassigned faces -------------------------------------------------

    [Fact]
    public async Task Unassigned_Returns_Only_Unassigned_And_Excludes_Assigned_Ignored_Vaulted()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var free = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var assigned = await SeedFaceAsync(f, ownerId, profileId, OneHot(1));
        var ignored = await SeedFaceAsync(f, ownerId, profileId, OneHot(2));
        var vaulted = await SeedFaceAsync(f, ownerId, profileId, OneHot(3), vaultId: await CreateVaultAsync(f, ownerId));

        await client.PostAsJsonAsync($"/api/people/faces/{assigned.FaceId}/assign", new { name = "A" });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/people/faces/{ignored.FaceId}/ignore", null)).StatusCode);

        var resp = await client.GetAsync("/api/people/unassigned-faces?limit=50");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        var page = await resp.Content.ReadFromJsonAsync<UnassignedFacesPage>();

        var ids = page!.Items.Select(i => i.FaceId).ToHashSet();
        Assert.Contains(free.FaceId, ids);
        Assert.DoesNotContain(assigned.FaceId, ids);
        Assert.DoesNotContain(ignored.FaceId, ids);
        Assert.DoesNotContain(vaulted.FaceId, ids);
        AssertNoLeak(raw);
    }

    [Fact]
    public async Task Unassigned_Is_Owner_Scoped()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerA, clientA) = await f.CreateAuthenticatedClientAsync("a@example.com");
        var (ownerB, clientB) = await f.CreateAuthenticatedClientAsync("b@example.com");
        var faceA = await SeedFaceAsync(f, ownerA, profileId);
        var faceB = await SeedFaceAsync(f, ownerB, profileId);

        var pageA = await (await clientA.GetAsync("/api/people/unassigned-faces")).Content.ReadFromJsonAsync<UnassignedFacesPage>();
        var idsA = pageA!.Items.Select(i => i.FaceId).ToHashSet();
        Assert.Contains(faceA.FaceId, idsA);
        Assert.DoesNotContain(faceB.FaceId, idsA);
    }

    [Fact]
    public async Task Unassigned_HasEmbedding_Filter_Works()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var withEmb = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var without = await SeedFaceAsync(f, ownerId, profileId, vector: null);

        var withPage = await (await client.GetAsync("/api/people/unassigned-faces?hasEmbedding=true"))
            .Content.ReadFromJsonAsync<UnassignedFacesPage>();
        var withIds = withPage!.Items.Select(i => i.FaceId).ToHashSet();
        Assert.Contains(withEmb.FaceId, withIds);
        Assert.DoesNotContain(without.FaceId, withIds);
        Assert.All(withPage.Items, i => Assert.True(i.HasEmbedding));

        var allPage = await (await client.GetAsync("/api/people/unassigned-faces"))
            .Content.ReadFromJsonAsync<UnassignedFacesPage>();
        var allIds = allPage!.Items.Select(i => i.FaceId).ToHashSet();
        Assert.Contains(withEmb.FaceId, allIds);
        Assert.Contains(without.FaceId, allIds);
    }

    // ---- ignore -----------------------------------------------------------

    [Fact]
    public async Task Ignored_Faces_List_Returns_Ignored_And_Excludes_Others()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var ignored = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var free = await SeedFaceAsync(f, ownerId, profileId, OneHot(1));
        await client.PostAsync($"/api/people/faces/{ignored.FaceId}/ignore", null);

        var resp = await client.GetAsync("/api/people/ignored-faces?limit=50");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains(ignored.FaceId.ToString(), raw);
        Assert.DoesNotContain(free.FaceId.ToString(), raw);
        AssertNoLeak(raw);
    }

    [Fact]
    public async Task Ignore_Then_Unignore_Face_Roundtrips()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var face = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/people/faces/{face.FaceId}/ignore", null)).StatusCode);
        var afterIgnore = await (await client.GetAsync("/api/people/unassigned-faces")).Content.ReadFromJsonAsync<UnassignedFacesPage>();
        Assert.DoesNotContain(face.FaceId, afterIgnore!.Items.Select(i => i.FaceId));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/people/faces/{face.FaceId}/ignore")).StatusCode);
        var afterUnignore = await (await client.GetAsync("/api/people/unassigned-faces")).Content.ReadFromJsonAsync<UnassignedFacesPage>();
        Assert.Contains(face.FaceId, afterUnignore!.Items.Select(i => i.FaceId));
    }

    // ---- clustering safety ------------------------------------------------

    private static FaceSettings Settings(double cluster) => new(cluster, 0.30, 0.35, 0.20, 0.95, 50, 1.0);

    private static async Task ClusterAsync(SqliteWebApplicationFactory f, Guid ownerId, Guid profileId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
        var clustering = scope.ServiceProvider.GetRequiredService<FaceClusteringService>();
        await clustering.ClusterOwnerAsync(ownerId, profile, Settings(0.40));
    }

    [Fact]
    public async Task Clustering_Excludes_Assigned_And_Ignored_Faces()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        // 3 identical faces would normally form one cluster of 3.
        var a = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var b = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var c = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));

        // Assign one, ignore one → only one free identical face remains → no cluster.
        await client.PostAsJsonAsync($"/api/people/faces/{a.FaceId}/assign", new { name = "A" });
        await client.PostAsync($"/api/people/faces/{b.FaceId}/ignore", null);
        await ClusterAsync(f, ownerId, profileId);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // No suggested cluster contains the assigned or ignored face.
        var clusteredFaceIds = await (
            from m in db.FaceClusterMembers
            join cl in db.FaceClusters on m.FaceClusterId equals cl.Id
            where cl.OwnerUserId == ownerId
            select m.FaceDetectionId).ToListAsync();
        Assert.DoesNotContain(a.FaceId, clusteredFaceIds);
        Assert.DoesNotContain(b.FaceId, clusteredFaceIds);
    }

    [Fact]
    public async Task Manual_Assignment_Survives_Reclustering_And_Confirmed_Not_Recalculated()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var a = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var b = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));

        var person = await (await client.PostAsJsonAsync($"/api/people/faces/{a.FaceId}/assign", new { name = "A" }))
            .Content.ReadFromJsonAsync<PersonDto>();

        // Rerun clustering; the manual assignment must remain intact.
        await ClusterAsync(f, ownerId, profileId);
        Assert.Equal(person!.PersonId, await AssignedPersonAsync(f, a.FaceId));
        Assert.Equal(1, await AssignmentCountAsync(f, a.FaceId));
    }

    [Fact]
    public async Task Removed_Face_Becomes_Eligible_For_Clustering_Again()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        // Two identical faces; assign the group to a person, then remove one face.
        var a = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var b = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var c = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));

        await client.PostAsJsonAsync($"/api/people/faces/{a.FaceId}/assign", new { name = "A" });
        await client.PostAsJsonAsync($"/api/people/faces/{b.FaceId}/assign", new { name = "A" });
        await client.PostAsJsonAsync($"/api/people/faces/{c.FaceId}/assign", new { name = "A" });

        // Remove face c → it must be clusterable again (no longer pinned/assigned).
        await client.DeleteAsync($"/api/people/faces/{c.FaceId}/assignment");
        Assert.Equal(0, await AssignmentCountAsync(f, c.FaceId));

        // Seed another free identical face so c can group with it.
        var d = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        await ClusterAsync(f, ownerId, profileId);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clusteredFaceIds = await (
            from m in db.FaceClusterMembers
            join cl in db.FaceClusters on m.FaceClusterId equals cl.Id
            where cl.OwnerUserId == ownerId && cl.Status == FaceClusterStatuses.Suggested
            select m.FaceDetectionId).ToListAsync();
        Assert.Contains(c.FaceId, clusteredFaceIds); // eligible again
    }

    [Fact]
    public async Task Ignore_Group_Bulk_Ignores_All_Members_And_Hides_Group()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        // Two identical faces form one suggested group.
        var a = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var b = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        await ClusterAsync(f, ownerId, profileId);

        Guid groupId;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            groupId = await db.FaceClusters.Where(c => c.OwnerUserId == ownerId).Select(c => c.Id).FirstAsync();
        }

        var resp = await client.PostAsync($"/api/people/groups/{groupId}/ignore", null);
        resp.EnsureSuccessStatusCode();
        AssertNoLeak(await resp.Content.ReadAsStringAsync());

        // Every member face is now individually ignored (restorable one by one).
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.IgnoredFaces.AnyAsync(i => i.FaceDetectionId == a.FaceId));
            Assert.True(await db.IgnoredFaces.AnyAsync(i => i.FaceDetectionId == b.FaceId));
        }

        // Both appear in the "Ignorati" list and can be restored.
        var ignored = await client.GetFromJsonAsync<IgnoredFacesPage>("/api/people/ignored-faces");
        Assert.Equal(2, ignored!.Items.Count);

        // The group no longer surfaces in the suggested queue (all members ignored).
        var groups = await client.GetFromJsonAsync<List<SuggestedGroupDto>>("/api/people/suggested-groups");
        Assert.DoesNotContain(groups!, g => g.GroupId == groupId);

        // Re-clustering excludes ignored faces → they don't come back as a suggestion.
        await ClusterAsync(f, ownerId, profileId);
        var after = await client.GetFromJsonAsync<List<SuggestedGroupDto>>("/api/people/suggested-groups");
        Assert.Empty(after!);
    }

    [Fact]
    public async Task Ignore_Group_CrossOwner_Is_404()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        var resp = await client.PostAsync($"/api/people/groups/{Guid.NewGuid()}/ignore", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Assign_Requires_Authentication()
    {
        using var f = Factory();
        var anon = f.CreateClient();
        var resp = await anon.PostAsJsonAsync($"/api/people/faces/{Guid.NewGuid()}/assign", new { name = "X" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
