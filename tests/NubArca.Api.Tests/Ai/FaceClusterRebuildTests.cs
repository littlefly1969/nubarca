using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Access;
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

// "Ricalcola cluster volti": the owner rebuilding their OWN automatic face
// groups from the Cloud hub.
//
// The point of the whole slice is a boundary, so that is what most of this file
// is about. The administrative `ai-faces-cluster-backfill` walks every eligible
// owner; this one must cluster EXACTLY the account that asked, must not need any
// administration authority to be watched, and must not let one account learn
// anything about another's run.
public sealed class FaceClusterRebuildTests
{
    private const string FaceProfileKey = "det-face-embedding-v1";
    private const int Dim = 32;

    private static SqliteWebApplicationFactory Factory() => Factory(clustering: true);

    private static SqliteWebApplicationFactory Factory(bool clustering)
    {
        var f = new SqliteWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["Ai:Enabled"] = "true",
                ["Ai:FaceClusteringEnabled"] = clustering ? "true" : "false",
                ["Ai:FaceProfileKey"] = FaceProfileKey,
            },
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

    private static async Task<SeededFace> SeedFaceAsync(
        SqliteWebApplicationFactory f, Guid ownerId, Guid profileId, float[] vector)
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
            CreatedAt = DateTime.UtcNow, EffectiveDateTaken = DateTime.UtcNow,
        });
        var faceId = Guid.NewGuid();
        db.FaceDetections.Add(new FaceDetection
        {
            Id = faceId, BlobObjectId = blobId, ProfileId = profileId, FaceIndex = 0,
            BoundingBoxX = 0.1, BoundingBoxY = 0.1, BoundingBoxWidth = 0.2, BoundingBoxHeight = 0.2,
            DetectionScore = 0.9, FaceQualityScore = 0.8, LandmarksJson = "[]", CreatedAt = DateTime.UtcNow,
        });
        db.FaceEmbeddings.Add(new FaceEmbedding
        {
            Id = Guid.NewGuid(), FaceDetectionId = faceId, ProfileId = profileId,
            EmbeddingBytes = ser.Serialize(vector, Dim), Dimension = Dim,
            EmbeddingStatus = AiArtifactStatuses.Completed, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return new SeededFace(faceId, fileId, blobId);
    }

    // Run the handler directly, the way the worker would.
    private static async Task RunOwnerJobAsync(SqliteWebApplicationFactory f, Guid ownerUserId)
    {
        using var scope = f.Services.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>()
            .Single(h => h.JobType == JobTypes.AiFacesClusterOwner);
        var context = new JobContext(
            Guid.NewGuid(),
            JsonSerializer.Serialize(new FaceOwnerClusterJobPayload(ownerUserId, FaceProfileKey)),
            _ => { }, CancellationToken.None, (_, _, _, _) => Task.CompletedTask,
            TimeProvider.System, JobScheduling.Compute, null,
            sliceNumber: 0, sliceDeadline: null, sliceItemBudget: null);
        await handler.ExecuteAsync(context, CancellationToken.None);
    }

    private static async Task<List<FaceCluster>> ClustersAsync(SqliteWebApplicationFactory f, Guid ownerId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FaceClusters.AsNoTracking()
            .Where(c => c.OwnerUserId == ownerId).OrderBy(c => c.Id).ToListAsync();
    }

    // ---- the permission itself -------------------------------------------

    [Fact]
    public void Permission_Is_An_Assignable_Feature_Under_People()
    {
        var definition = PermissionCatalog.Find(Permissions.PeopleClusterRebuild);

        Assert.NotNull(definition);
        Assert.Equal("people.cluster.rebuild", Permissions.PeopleClusterRebuild);
        Assert.Equal(PermissionGroups.Features, definition!.Group);
        // Not an administration surface: it acts on the holder's own data only.
        Assert.False(definition.Administrative);
        Assert.False(definition.AdministratorOnly);
        // Rebuilding People's suggestions without being able to use People would
        // be authority with nowhere to go.
        Assert.Equal(Permissions.PeopleAccess, definition.Parent);
        Assert.Contains(Permissions.PeopleClusterRebuild, PermissionCatalog.AssignableKeys);
    }

    [Fact]
    public void Built_In_Role_Defaults_Carry_It_For_Administrator_And_Member_Only()
    {
        Assert.Contains(Permissions.PeopleClusterRebuild, RoleDefaults.AdministratorPermissions);
        // Member is derived from the non-administrative keys, so a FRESH
        // installation gets it without anybody listing it by hand.
        Assert.Contains(Permissions.PeopleClusterRebuild, RoleDefaults.MemberPermissions);
        Assert.DoesNotContain(Permissions.PeopleClusterRebuild, RoleDefaults.RestrictedPermissions);
    }

    [Fact]
    public async Task Role_Service_Refuses_The_Capability_Without_Its_Parent()
    {
        using var f = Factory();

        // The dependency is enforced where roles are PERSISTED, not only drawn:
        // a hand-crafted request must not be able to store the child alone.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            f.CreateRoleAsync("Orphan rebuild", Permissions.PeopleClusterRebuild));

        // With the parent it is a perfectly ordinary role.
        var roleKey = await f.CreateRoleAsync(
            "Rebuilders", Permissions.PeopleAccess, Permissions.PeopleClusterRebuild);
        Assert.NotNull(roleKey);
    }

    // ---- authorization ----------------------------------------------------

    [Fact]
    public async Task Administrator_And_Member_May_Start_Their_Own_Rebuild()
    {
        using var f = Factory();
        await SeedProfileAsync(f);

        var (adminId, admin) = await f.CreateAuthenticatedClientAsync("admin@example.com");
        await f.PromoteToAdminAsync(adminId);
        var (memberId, member) = await f.CreateAuthenticatedClientAsync("member@example.com");

        foreach (var (client, userId) in new[] { (admin, adminId), (member, memberId) })
        {
            var resp = await client.PostAsync("/api/people/cluster-rebuild", null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<StartResponse>();
            Assert.NotEqual(Guid.Empty, body!.JobId);
            Assert.Equal(JobStatuses.Queued, body.Status);
            Assert.False(body.AlreadyQueued);

            // The job clusters the CALLER — an id the request had no way to name.
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == body.JobId);
            Assert.Equal(JobTypes.AiFacesClusterOwner, job.Type);
            var payload = JsonSerializer.Deserialize<FaceOwnerClusterJobPayload>(job.PayloadJson)!;
            Assert.Equal(userId, payload.OwnerUserId);
        }
    }

    [Fact]
    public async Task Without_The_Permission_Both_Endpoints_Are_403()
    {
        using var f = Factory();
        await SeedProfileAsync(f);

        // People, but not the rebuild capability.
        var (_, client) = await f.CreatePermissionClientAsync("plain@example.com", Permissions.PeopleAccess);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync("/api/people/cluster-rebuild", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/people/cluster-rebuild/{Guid.NewGuid()}")).StatusCode);

        // Authenticated is not the same as permitted: anonymous is 401.
        var anon = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsync("/api/people/cluster-rebuild", null)).StatusCode);
    }

    [Fact]
    public async Task One_Owner_Cannot_Watch_Another_Owners_Rebuild()
    {
        using var f = Factory();
        await SeedProfileAsync(f);

        var (_, a) = await f.CreateAuthenticatedClientAsync("a@example.com");
        var (_, b) = await f.CreateAuthenticatedClientAsync("b@example.com");

        var started = await (await a.PostAsync("/api/people/cluster-rebuild", null))
            .Content.ReadFromJsonAsync<StartResponse>();

        // Its owner sees it…
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/api/people/cluster-rebuild/{started!.JobId}")).StatusCode);
        // …and nobody else does. 404, not 403: a status endpoint must not confirm
        // that somebody else's job id is real.
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/people/cluster-rebuild/{started.JobId}")).StatusCode);
    }

    [Fact]
    public async Task The_Status_Endpoint_Answers_Only_For_Owner_Cluster_Jobs()
    {
        using var f = Factory();
        await SeedProfileAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();

        // The GLOBAL backfill is not this user's business, whatever its id.
        Guid globalJobId;
        using (var scope = f.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            globalJobId = (await queue.EnqueueAsync(
                JobTypes.AiFacesClusterBackfill, new AiBackfillJobPayload())).Id;
        }

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/people/cluster-rebuild/{globalJobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/people/cluster-rebuild/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task The_Status_Response_Carries_No_Internals()
    {
        using var f = Factory();
        await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();

        var started = await (await client.PostAsync("/api/people/cluster-rebuild", null))
            .Content.ReadFromJsonAsync<StartResponse>();
        var raw = await (await client.GetAsync($"/api/people/cluster-rebuild/{started!.JobId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("\"status\"", raw, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "payloadJson", "PayloadJson", "ownerUserId", "OwnerUserId", "lockOwner",
                     "LockOwner", "checkpoint", "Checkpoint", "profileKey", "ProfileKey",
                     "idempotencyKey", "IdempotencyKey", FaceProfileKey, "at NubArca.",
                 })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(ownerId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
    }

    // ---- idempotency ------------------------------------------------------

    [Fact]
    public async Task A_Second_Click_Joins_The_Run_Already_In_Flight()
    {
        using var f = Factory();
        await SeedProfileAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();

        var first = await (await client.PostAsync("/api/people/cluster-rebuild", null))
            .Content.ReadFromJsonAsync<StartResponse>();
        var second = await (await client.PostAsync("/api/people/cluster-rebuild", null))
            .Content.ReadFromJsonAsync<StartResponse>();

        Assert.Equal(first!.JobId, second!.JobId);
        Assert.False(first.AlreadyQueued);
        Assert.True(second.AlreadyQueued);

        // One run, not two racing over the same owner's clusters.
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.BackgroundJobs.CountAsync(j => j.Type == JobTypes.AiFacesClusterOwner));
    }

    [Fact]
    public async Task A_Finished_Run_Does_Not_Block_The_Next_One()
    {
        using var f = Factory();
        await SeedProfileAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();

        var first = await (await client.PostAsync("/api/people/cluster-rebuild", null))
            .Content.ReadFromJsonAsync<StartResponse>();

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.BackgroundJobs.SingleAsync(j => j.Id == first!.JobId);
            job.Status = JobStatuses.Succeeded;
            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var second = await (await client.PostAsync("/api/people/cluster-rebuild", null))
            .Content.ReadFromJsonAsync<StartResponse>();

        Assert.NotEqual(first!.JobId, second!.JobId);
        Assert.False(second.AlreadyQueued);
    }

    // ---- the unavailable contract ----------------------------------------

    [Fact]
    public async Task Clustering_Turned_Off_Refuses_Rather_Than_Queueing_A_No_Op()
    {
        using var f = Factory(clustering: false);
        await SeedProfileAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();

        var resp = await client.PostAsync("/api/people/cluster-rebuild", null);

        // A queued job here would "succeed" and change nothing, which is worse
        // than saying so.
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.BackgroundJobs.CountAsync(j => j.Type == JobTypes.AiFacesClusterOwner));
    }

    // ---- ONE owner, and only one -----------------------------------------

    // The test this whole slice exists for. If the owner job ever grew the
    // backfill's owner enumeration, this is what would catch it.
    [Fact]
    public async Task The_Owner_Job_Clusters_The_Payload_Owner_And_Nobody_Else()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerA, _) = await f.CreateAuthenticatedClientAsync("a@example.com");
        var (ownerB, _) = await f.CreateAuthenticatedClientAsync("b@example.com");

        // Both owners have a clusterable pair of identical faces.
        foreach (var owner in new[] { ownerA, ownerB })
        {
            await SeedFaceAsync(f, owner, profileId, OneHot(0));
            await SeedFaceAsync(f, owner, profileId, OneHot(0));
        }

        await RunOwnerJobAsync(f, ownerA);

        // A got groups…
        Assert.NotEmpty(await ClustersAsync(f, ownerA));
        // …and B was not touched at all, although B was equally eligible and the
        // GLOBAL backfill would have clustered them in the same pass.
        Assert.Empty(await ClustersAsync(f, ownerB));
    }

    [Fact]
    public async Task A_Rebuild_Replaces_Auto_Groups_And_Preserves_Every_User_Decision()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, _) = await f.CreateAuthenticatedClientAsync();

        // Free faces that will group, a face the owner confirmed on a person,
        // and a face the owner ignored.
        await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var confirmed = await SeedFaceAsync(f, ownerId, profileId, OneHot(3));
        var ignored = await SeedFaceAsync(f, ownerId, profileId, OneHot(4));

        Guid personId;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var person = new Person
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerId, DisplayName = "Alice", CreatedAt = DateTime.UtcNow,
            };
            db.People.Add(person);
            personId = person.Id;
            db.PersonFaceAssignments.Add(new PersonFaceAssignment
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerId, PersonId = person.Id,
                FaceDetectionId = confirmed.FaceId, Source = PersonFaceAssignmentSources.UserConfirmed,
                CreatedAt = DateTime.UtcNow,
            });
            db.IgnoredFaces.Add(new IgnoredFace
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerId, FaceDetectionId = ignored.FaceId,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await RunOwnerJobAsync(f, ownerId);
        var first = await ClustersAsync(f, ownerId);
        Assert.NotEmpty(first);

        // Running it again REPLACES the automatic layer rather than stacking a
        // second copy of it on top.
        await RunOwnerJobAsync(f, ownerId);
        var second = await ClustersAsync(f, ownerId);
        Assert.Equal(first.Count, second.Count);

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // The decisions the owner made by hand are exactly as they were.
            Assert.True(await db.People.AnyAsync(p => p.Id == personId && !p.IsArchived));
            Assert.True(await db.PersonFaceAssignments.AnyAsync(
                a => a.PersonId == personId && a.FaceDetectionId == confirmed.FaceId));
            Assert.True(await db.IgnoredFaces.AnyAsync(i => i.FaceDetectionId == ignored.FaceId));

            // …and neither of those faces was swept back into a suggestion.
            var clustered = await (
                from m in db.FaceClusterMembers
                join c in db.FaceClusters on m.FaceClusterId equals c.Id
                where c.OwnerUserId == ownerId
                select m.FaceDetectionId).ToListAsync();
            Assert.DoesNotContain(confirmed.FaceId, clustered);
            Assert.DoesNotContain(ignored.FaceId, clustered);
        }
    }

    [Fact]
    public async Task The_Owner_Job_Refuses_A_Payload_With_No_Owner()
    {
        using var f = Factory();
        await SeedProfileAsync(f);
        var (ownerId, _) = await f.CreateAuthenticatedClientAsync();
        var profileId = await SeedProfileAsync(f);
        await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        await SeedFaceAsync(f, ownerId, profileId, OneHot(0));

        using (var scope = f.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetServices<IJobHandler>()
                .Single(h => h.JobType == JobTypes.AiFacesClusterOwner);
            var context = new JobContext(
                Guid.NewGuid(), "{}", _ => { }, CancellationToken.None,
                (_, _, _, _) => Task.CompletedTask, TimeProvider.System, JobScheduling.Compute,
                null, sliceNumber: 0, sliceDeadline: null, sliceItemBudget: null);
            await handler.ExecuteAsync(context, CancellationToken.None);
        }

        // A job with no owner has no correct scope to fall back to, so it must
        // cluster nothing rather than guess one.
        Assert.Empty(await ClustersAsync(f, ownerId));
    }

    private sealed record StartResponse(Guid JobId, string Status, bool AlreadyQueued);
}
