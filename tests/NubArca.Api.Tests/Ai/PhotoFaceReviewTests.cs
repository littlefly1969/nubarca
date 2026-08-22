using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Ai;

// Photo-centric review of undecided faces.
//
// The face-at-a-time pool answers "which FACES are undecided"; this answers
// "which PHOTOS still need work", and lets one photo be finished in one act.
// Both must agree on what "undecided" means, which is why the service shares a
// single membership predicate between the listing and the bulk action — a photo
// that could be emptied and still come back would be worse than no action.
public sealed class PhotoFaceReviewTests
{
    private const string PhotosRoute = "/api/people/photos-with-unassigned-faces";

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
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = $"fm-{Guid.NewGuid():N}", Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Image,
            Dimension = 32, DistanceMetric = AiDistanceMetrics.Cosine,
            Version = 1, Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = $"fp-{Guid.NewGuid():N}", AiModelId = model.Id,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Image,
            Dimension = 32, DistanceMetric = AiDistanceMetrics.Cosine,
            IsDefault = true, Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        db.AiProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    /// One photo carrying `faceCount` detected faces — the shape the pool is
    /// actually made of, which a one-face-per-photo seeder cannot express.
    private static async Task<(Guid FileId, List<Guid> FaceIds)> SeedPhotoAsync(
        SqliteWebApplicationFactory f, Guid ownerId, Guid profileId, int faceCount)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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

        var faceIds = new List<Guid>();
        for (var i = 0; i < faceCount; i++)
        {
            var faceId = Guid.NewGuid();
            faceIds.Add(faceId);
            db.FaceDetections.Add(new FaceDetection
            {
                Id = faceId, BlobObjectId = blobId, ProfileId = profileId, FaceIndex = i,
                BoundingBoxX = 0.1, BoundingBoxY = 0.1, BoundingBoxWidth = 0.2, BoundingBoxHeight = 0.2,
                DetectionScore = 0.9, LandmarksJson = "[]", CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return (fileId, faceIds);
    }

    private static async Task AssignAsync(
        SqliteWebApplicationFactory f, Guid ownerId, Guid faceId, string name)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var person = new Person
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerId, DisplayName = name, CreatedAt = DateTime.UtcNow,
        };
        db.People.Add(person);
        db.PersonFaceAssignments.Add(new PersonFaceAssignment
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerId, PersonId = person.Id,
            FaceDetectionId = faceId, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<List<(Guid FileItemId, int Count, List<Guid> FaceIds)>> PhotosAsync(
        HttpClient client)
    {
        var response = await client.GetAsync($"{PhotosRoute}?limit=50");
        response.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return root.GetProperty("items").EnumerateArray()
            .Select(i => (
                Guid.Parse(i.GetProperty("fileItemId").GetString()!),
                i.GetProperty("unassignedCount").GetInt32(),
                i.GetProperty("faceIds").EnumerateArray()
                    .Select(x => Guid.Parse(x.GetString()!)).ToList()))
            .ToList();
    }

    [Fact]
    public async Task Photos_List_Counts_Only_Undecided_Faces_And_Drops_Finished_Photos()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();

        var busy = await SeedPhotoAsync(f, ownerId, profileId, 3);
        var light = await SeedPhotoAsync(f, ownerId, profileId, 1);
        var mixed = await SeedPhotoAsync(f, ownerId, profileId, 3);
        var finished = await SeedPhotoAsync(f, ownerId, profileId, 2);

        // Two of `mixed` are already decided; both of `finished` are.
        await AssignAsync(f, ownerId, mixed.FaceIds[0], "Mario");
        await IgnoreAsync(f, ownerId, mixed.FaceIds[1]);
        await AssignAsync(f, ownerId, finished.FaceIds[0], "Lucia");
        await IgnoreAsync(f, ownerId, finished.FaceIds[1]);

        var photos = await PhotosAsync(client);
        var byId = photos.ToDictionary(p => p.FileItemId, p => p);

        // A photo with nothing left to decide is not work, so it is not listed.
        Assert.DoesNotContain(finished.FileId, byId.Keys);

        Assert.Equal(3, byId[busy.FileId].Count);
        Assert.Equal(1, byId[light.FileId].Count);
        // Assigned and ignored faces are not counted, and not offered.
        Assert.Equal(1, byId[mixed.FileId].Count);
        Assert.Equal(new[] { mixed.FaceIds[2] }, byId[mixed.FileId].FaceIds);

        // Most-undecided first: the order that empties the backlog fastest.
        var order = photos.Select(p => p.Count).ToList();
        Assert.Equal(order.OrderByDescending(x => x).ToList(), order);
    }

    [Fact]
    public async Task Ignoring_A_Photo_Touches_Only_Its_Own_Undecided_Faces()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();

        var target = await SeedPhotoAsync(f, ownerId, profileId, 3);
        var bystander = await SeedPhotoAsync(f, ownerId, profileId, 2);
        await AssignAsync(f, ownerId, target.FaceIds[0], "Mario");

        var response = await client.PostAsync(
            $"/api/people/photos/{target.FileId}/ignore-unassigned-faces", null);
        response.EnsureSuccessStatusCode();
        var ignored = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("ignored").GetInt32();

        // The two still-undecided faces, and only those: the assigned one is a
        // decision already taken and must not be undone by a bulk ignore.
        Assert.Equal(2, ignored);
        Assert.False(await IsIgnoredAsync(f, ownerId, target.FaceIds[0]));
        Assert.True(await IsIgnoredAsync(f, ownerId, target.FaceIds[1]));
        Assert.True(await IsIgnoredAsync(f, ownerId, target.FaceIds[2]));
        Assert.Equal(1, await AssignmentCountAsync(f, target.FaceIds[0]));

        // Another photo's faces are untouched, and it is still listed as work.
        foreach (var faceId in bystander.FaceIds)
        {
            Assert.False(await IsIgnoredAsync(f, ownerId, faceId));
        }

        var photos = await PhotosAsync(client);
        Assert.DoesNotContain(target.FileId, photos.Select(p => p.FileItemId));
        Assert.Contains(bystander.FileId, photos.Select(p => p.FileItemId));

        // Idempotent: nothing left to ignore is 0, not an error.
        var again = await client.PostAsync(
            $"/api/people/photos/{target.FileId}/ignore-unassigned-faces", null);
        again.EnsureSuccessStatusCode();
        Assert.Equal(0, JsonDocument.Parse(await again.Content.ReadAsStringAsync())
            .RootElement.GetProperty("ignored").GetInt32());
    }

    [Fact]
    public async Task Another_Owners_Photo_Is_A_Generic_NotFound()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, _) = await f.CreateAuthenticatedClientAsync("owner@example.com");
        var (_, other) = await f.CreateAuthenticatedClientAsync("other@example.com");

        var photo = await SeedPhotoAsync(f, ownerId, profileId, 2);

        var response = await other.PostAsync(
            $"/api/people/photos/{photo.FileId}/ignore-unassigned-faces", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());

        // …and the faces really were left alone.
        foreach (var faceId in photo.FaceIds)
        {
            Assert.False(await IsIgnoredAsync(f, ownerId, faceId));
        }

        // The other owner also sees no photos at all: the listing is owner-scoped.
        var mine = await PhotosAsync(other);
        Assert.Empty(mine);
    }

    // ---- small helpers ----------------------------------------------------

    private static async Task IgnoreAsync(SqliteWebApplicationFactory f, Guid ownerId, Guid faceId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.IgnoredFaces.Add(new IgnoredFace
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerId, FaceDetectionId = faceId, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<bool> IsIgnoredAsync(SqliteWebApplicationFactory f, Guid ownerId, Guid faceId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.IgnoredFaces.AnyAsync(i => i.OwnerUserId == ownerId && i.FaceDetectionId == faceId);
    }

    private static async Task<int> AssignmentCountAsync(SqliteWebApplicationFactory f, Guid faceId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PersonFaceAssignments.CountAsync(a => a.FaceDetectionId == faceId);
    }
}
