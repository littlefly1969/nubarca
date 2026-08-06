using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Albums;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Albums;

// Gallery-as-operational-surface: bulk album add/remove of gallery-selected
// items, plus the gallery People include/exclude filters, the album constraint,
// and the owner-scoped similar-photo bridge. Owner-private, no storage/blob/
// face/person internals leak.
public sealed class GalleryBulkWorkflowTests : IDisposable
{
    private const string FaceProfileKey = "det-face-embedding-v1";
    private readonly SqliteWebApplicationFactory _factory;

    public GalleryBulkWorkflowTests()
    {
        _factory = new SqliteWebApplicationFactory(
            new Dictionary<string, string?> { ["Ai:Enabled"] = "true" },
            poolHost: true);
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await work(db);
    }

    private async Task<Guid> UploadFileAsync(Guid ownerId, string? name = null, string mime = "text/plain")
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        // Unique bytes AND name per file so dedup / duplicate-name rules don't collide.
        var tag = Guid.NewGuid().ToString("N");
        var f = await files.CreateAsync(ownerId, null, name ?? $"file-{tag}.txt", mime,
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"hello-{tag}")));
        return f.Id;
    }

    private async Task<Guid> SeedProfileAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
        await registry.SeedDeterministicProfilesAsync();
        return (await registry.GetProfileByKeyAsync(FaceProfileKey))!.Id;
    }

    // Seed an image FileItem directly (no BlobMetadata row → gallery membership
    // falls back to the client MIME "image/*", so it shows in /api/images).
    private async Task<(Guid FileId, Guid BlobId)> SeedImageAsync(Guid ownerId)
    {
        using var scope = _factory.Services.CreateScope();
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
        await db.SaveChangesAsync();
        return (fileId, blobId);
    }

    private async Task<Guid> SeedPersonAsync(Guid ownerId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var person = new Person { Id = Guid.NewGuid(), OwnerUserId = ownerId, DisplayName = name, CreatedAt = DateTime.UtcNow };
        db.People.Add(person);
        await db.SaveChangesAsync();
        return person.Id;
    }

    // Attach a person to an image's blob: one FaceDetection on the blob + a
    // PersonFaceAssignment linking it to the person.
    private async Task AttachPersonToImageAsync(Guid ownerId, Guid profileId, Guid blobId, Guid personId, int faceIndex)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var faceId = Guid.NewGuid();
        db.FaceDetections.Add(new FaceDetection
        {
            Id = faceId, BlobObjectId = blobId, ProfileId = profileId, FaceIndex = faceIndex,
            BoundingBoxX = 0.1, BoundingBoxY = 0.1, BoundingBoxWidth = 0.2, BoundingBoxHeight = 0.2,
            DetectionScore = 0.9, LandmarksJson = "[]", CreatedAt = DateTime.UtcNow,
        });
        db.PersonFaceAssignments.Add(new PersonFaceAssignment
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerId, PersonId = personId, FaceDetectionId = faceId,
            Source = PersonFaceAssignmentSources.ManualAdd, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<HashSet<Guid>> GalleryIdsAsync(HttpClient client, string query)
    {
        var resp = await client.GetFromJsonAsync<ImageListResponse>($"/api/images?limit=200&{query}");
        return resp!.Items.Select(i => i.Id).ToHashSet();
    }

    private async Task<AlbumDetail> CreateAlbumAsync(HttpClient client, string name)
    {
        var created = await client.PostAsJsonAsync("/api/albums", new { name });
        return (await created.Content.ReadFromJsonAsync<AlbumDetail>())!;
    }

    // ---- bulk add --------------------------------------------------------

    [Fact]
    public async Task Bulk_Add_Own_Files_Reports_Safe_Summary()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var f1 = await UploadFileAsync(owner);
        var f2 = await UploadFileAsync(owner);
        var album = await CreateAlbumAsync(client, "Bulk");

        var r = await client.PostAsJsonAsync($"/api/albums/{album.Id}/items/bulk",
            new { fileItemIds = new[] { f1, f2 } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var summary = await r.Content.ReadFromJsonAsync<BulkAlbumItemsResult>();
        Assert.Equal(2, summary!.Requested);
        Assert.Equal(2, summary.Succeeded);
        Assert.Equal(0, summary.Skipped);

        var items = await client.GetFromJsonAsync<AlbumItemSummary[]>($"/api/albums/{album.Id}/items");
        Assert.Equal(2, items!.Length);
    }

    [Fact]
    public async Task Bulk_Add_Is_Idempotent_On_Duplicates()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var f1 = await UploadFileAsync(owner);
        var album = await CreateAlbumAsync(client, "Idem");

        await client.PostAsJsonAsync($"/api/albums/{album.Id}/items/bulk", new { fileItemIds = new[] { f1 } });
        // Re-adding the same file (and a duplicate within the request) is skipped, not an error.
        var r = await client.PostAsJsonAsync($"/api/albums/{album.Id}/items/bulk",
            new { fileItemIds = new[] { f1, f1 } });
        var summary = await r.Content.ReadFromJsonAsync<BulkAlbumItemsResult>();
        Assert.Equal(2, summary!.Requested);
        Assert.Equal(0, summary.Succeeded);
        Assert.Equal(2, summary.Skipped);

        var items = await client.GetFromJsonAsync<AlbumItemSummary[]>($"/api/albums/{album.Id}/items");
        Assert.Single(items!);
    }

    [Fact]
    public async Task Bulk_Add_Cannot_Add_Another_Users_Files()
    {
        var aliceId = await _factory.SeedUserAsync("alice-bulk@example.com");
        var aliceFile = await UploadFileAsync(aliceId);

        var (owner, bob) = await _factory.CreateAuthenticatedClientAsync("bob-bulk@example.com");
        var bobFile = await UploadFileAsync(owner);
        var album = await CreateAlbumAsync(bob, "Bob");

        var r = await bob.PostAsJsonAsync($"/api/albums/{album.Id}/items/bulk",
            new { fileItemIds = new[] { bobFile, aliceFile } });
        var summary = await r.Content.ReadFromJsonAsync<BulkAlbumItemsResult>();
        // Alice's file is silently skipped (never leaks that it exists).
        Assert.Equal(2, summary!.Requested);
        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(1, summary.Skipped);
    }

    [Fact]
    public async Task Bulk_Add_To_Foreign_Album_Returns_404()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice-fa@example.com");
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync("bob-fa@example.com");
        var aliceAlbum = await CreateAlbumAsync(alice, "Alice");
        var bobFile = await UploadFileAsync(bobId);

        var r = await bob.PostAsJsonAsync($"/api/albums/{aliceAlbum.Id}/items/bulk",
            new { fileItemIds = new[] { bobFile } });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    // ---- bulk remove -----------------------------------------------------

    [Fact]
    public async Task Bulk_Remove_Removes_Membership_But_Keeps_Files()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var f1 = await UploadFileAsync(owner);
        var f2 = await UploadFileAsync(owner);
        var album = await CreateAlbumAsync(client, "R");
        await client.PostAsJsonAsync($"/api/albums/{album.Id}/items/bulk", new { fileItemIds = new[] { f1, f2 } });

        var r = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/albums/{album.Id}/items/bulk")
        {
            Content = JsonContent.Create(new { fileItemIds = new[] { f1, f2 } }),
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var summary = await r.Content.ReadFromJsonAsync<BulkAlbumItemsResult>();
        Assert.Equal(2, summary!.Succeeded);

        // Album is empty…
        var items = await client.GetFromJsonAsync<AlbumItemSummary[]>($"/api/albums/{album.Id}/items");
        Assert.Empty(items!);
        // …but the FileItems (and blobs) still exist.
        Assert.True(await InDbAsync(db => db.FileItems.AnyAsync(f => f.Id == f1 && f.DeletedAt == null)));
        Assert.True(await InDbAsync(db => db.FileItems.AnyAsync(f => f.Id == f2 && f.DeletedAt == null)));
    }

    [Fact]
    public async Task Bulk_Remove_From_Foreign_Album_Returns_404()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice-fr@example.com");
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync("bob-fr@example.com");
        var aliceAlbum = await CreateAlbumAsync(alice, "AliceR");
        var bobFile = await UploadFileAsync(bobId);

        var r = await bob.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/albums/{aliceAlbum.Id}/items/bulk")
        {
            Content = JsonContent.Create(new { fileItemIds = new[] { bobFile } }),
        });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Bulk_Endpoints_Require_Auth()
    {
        var anon = _factory.CreateClient();
        var id = Guid.NewGuid();
        var add = await anon.PostAsJsonAsync($"/api/albums/{id}/items/bulk", new { fileItemIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.Unauthorized, add.StatusCode);
    }

    // ---- gallery album constraint ----------------------------------------

    [Fact]
    public async Task Gallery_AlbumId_Returns_Only_Album_Members()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var (inAlbum, _) = await SeedImageAsync(owner);
        var (outside, _) = await SeedImageAsync(owner);
        var album = await CreateAlbumAsync(client, "Constrained");
        await client.PostAsJsonAsync($"/api/albums/{album.Id}/items/bulk", new { fileItemIds = new[] { inAlbum } });

        var ids = await GalleryIdsAsync(client, $"albumId={album.Id}");
        Assert.Contains(inAlbum, ids);
        Assert.DoesNotContain(outside, ids);
    }

    [Fact]
    public async Task Gallery_Foreign_AlbumId_Returns_404()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice-gc@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob-gc@example.com");
        var aliceAlbum = await CreateAlbumAsync(alice, "AliceGC");

        var r = await bob.GetAsync($"/api/images?albumId={aliceAlbum.Id}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    // ---- people filters --------------------------------------------------

    private sealed record PeopleFixture(
        Guid PersonA, Guid PersonB, Guid FileAB, Guid FileA, Guid FileB, Guid FileNone);

    private async Task<PeopleFixture> SeedPeopleFixtureAsync(Guid owner)
    {
        var profileId = await SeedProfileAsync();
        var personA = await SeedPersonAsync(owner, "Anna");
        var personB = await SeedPersonAsync(owner, "Bruno");

        var (fileAB, blobAB) = await SeedImageAsync(owner);
        await AttachPersonToImageAsync(owner, profileId, blobAB, personA, 0);
        await AttachPersonToImageAsync(owner, profileId, blobAB, personB, 1);

        var (fileA, blobA) = await SeedImageAsync(owner);
        await AttachPersonToImageAsync(owner, profileId, blobA, personA, 0);

        var (fileB, blobB) = await SeedImageAsync(owner);
        await AttachPersonToImageAsync(owner, profileId, blobB, personB, 0);

        var (fileNone, _) = await SeedImageAsync(owner);

        return new PeopleFixture(personA, personB, fileAB, fileA, fileB, fileNone);
    }

    [Fact]
    public async Task People_Include_All_Requires_Every_Selected_Person()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var fx = await SeedPeopleFixtureAsync(owner);

        var ids = await GalleryIdsAsync(client, $"includePeople={fx.PersonA},{fx.PersonB}&includePeopleMode=all");
        Assert.Contains(fx.FileAB, ids);
        Assert.DoesNotContain(fx.FileA, ids);
        Assert.DoesNotContain(fx.FileB, ids);
        Assert.DoesNotContain(fx.FileNone, ids);
    }

    [Fact]
    public async Task People_Include_Any_Matches_At_Least_One()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var fx = await SeedPeopleFixtureAsync(owner);

        var ids = await GalleryIdsAsync(client, $"includePeople={fx.PersonA},{fx.PersonB}&includePeopleMode=any");
        Assert.Contains(fx.FileAB, ids);
        Assert.Contains(fx.FileA, ids);
        Assert.Contains(fx.FileB, ids);
        Assert.DoesNotContain(fx.FileNone, ids);
    }

    [Fact]
    public async Task People_Include_Does_Not_Duplicate_When_Person_Has_Multiple_Faces_On_Same_Photo()
    {
        // Regression guard for the inverted (detection-first) EXISTS people
        // filter: a person present via SEVERAL face detections on the same blob
        // must yield the FileItem exactly once (EXISTS semi-join, no row
        // explosion), for both include-any and include-all.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var profileId = await SeedProfileAsync();
        var person = await SeedPersonAsync(owner, "Multi");
        var (file, blob) = await SeedImageAsync(owner);
        await AttachPersonToImageAsync(owner, profileId, blob, person, 0);
        await AttachPersonToImageAsync(owner, profileId, blob, person, 1);

        foreach (var mode in new[] { "any", "all" })
        {
            var body = await (await client.GetAsync(
                $"/api/images?limit=200&includePeople={person}&includePeopleMode={mode}"))
                .Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var occurrences = doc.RootElement.GetProperty("items").EnumerateArray()
                .Count(i => Guid.Parse(i.GetProperty("id").GetString()!) == file);
            Assert.Equal(1, occurrences);
        }
    }

    [Fact]
    public async Task People_Exclude_Removes_Matching_Items()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var fx = await SeedPeopleFixtureAsync(owner);

        var ids = await GalleryIdsAsync(client, $"excludePeople={fx.PersonA}");
        Assert.DoesNotContain(fx.FileAB, ids);
        Assert.DoesNotContain(fx.FileA, ids);
        Assert.Contains(fx.FileB, ids);
        Assert.Contains(fx.FileNone, ids);
    }

    [Fact]
    public async Task People_Include_And_Exclude_Combined()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var fx = await SeedPeopleFixtureAsync(owner);

        // Contains A but NOT B → only FileA (FileAB has B, so excluded).
        var ids = await GalleryIdsAsync(client,
            $"includePeople={fx.PersonA}&includePeopleMode=any&excludePeople={fx.PersonB}");
        Assert.Contains(fx.FileA, ids);
        Assert.DoesNotContain(fx.FileAB, ids);
        Assert.DoesNotContain(fx.FileB, ids);
        Assert.DoesNotContain(fx.FileNone, ids);
    }

    [Fact]
    public async Task People_Filter_Does_Not_Leak_Across_Owners()
    {
        var (aliceId, _) = await _factory.CreateAuthenticatedClientAsync("alice-pl@example.com");
        var fx = await SeedPeopleFixtureAsync(aliceId);

        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync("bob-pl@example.com");
        var (bobImg, _) = await SeedImageAsync(bobId);

        // Bob filtering by Alice's person id must simply return nothing of Bob's
        // (no error, no leak) — the owner-scoped join matches none of Bob's files.
        var ids = await GalleryIdsAsync(bob, $"includePeople={fx.PersonA}&includePeopleMode=any");
        Assert.Empty(ids);
        Assert.DoesNotContain(fx.FileAB, ids);
    }

    // ---- similar bridge --------------------------------------------------

    [Fact]
    public async Task Gallery_SimilarTo_Foreign_File_Returns_Empty_Not_Leak()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedImageAsync(owner);
        // A random/foreign query file → owner-scoped similarity returns null →
        // the gallery restricts to nothing (safe empty), never a foreign leak.
        var ids = await GalleryIdsAsync(client, $"similarTo={Guid.NewGuid()}");
        Assert.Empty(ids);
    }

    // ---- no leak ---------------------------------------------------------

    [Fact]
    public async Task Filtered_Gallery_And_Bulk_Responses_Contain_No_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var fx = await SeedPeopleFixtureAsync(owner);
        var album = await CreateAlbumAsync(client, "NL");

        var addBody = await (await client.PostAsJsonAsync($"/api/albums/{album.Id}/items/bulk",
            new { fileItemIds = new[] { fx.FileA } })).Content.ReadAsStringAsync();
        var galleryBody = await (await client.GetAsync(
            $"/api/images?limit=200&includePeople={fx.PersonA}&includePeopleMode=any")).Content.ReadAsStringAsync();

        foreach (var body in new[] { addBody, galleryBody })
        {
            foreach (var needle in new[]
            {
                "StorageKey", "storageKey", "BlobObjectId", "blobObjectId", "Sha256", "sha256",
                "EmbeddingBytes", "embeddingBytes", "ProfileId", "profileId", "FaceDetectionId",
                "faceDetectionId", "PersonId", "personId", "vector", "TokenHash",
            })
            {
                Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
            }
        }
    }
}
