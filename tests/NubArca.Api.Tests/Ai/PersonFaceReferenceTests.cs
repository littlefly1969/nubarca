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

// Multi-reference person templates: the lazily-bootstrapped, max-6, profile-scoped
// reference set that similar-face search queries with instead of one arbitrary
// assigned embedding. These cover the parts that need NO vector backend —
// selection, persistence, the cap, and invalidation. The ANN merge itself is
// proven against real pgvector in PeopleSimilarFacesPgIntegrationTests.
public sealed class PersonFaceReferenceTests
{
    private const string FaceProfileKey = "det-face-embedding-v1";
    private const int Dim = 32;

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

    private static float[] OneHot(int i, float scale = 1f)
    {
        var v = new float[Dim];
        v[i] = scale;
        return v;
    }

    // A vector that sits between two axes — used to build candidates that are
    // partially covered by an existing reference.
    private static float[] Blend(int a, int b, float weightB)
    {
        var v = new float[Dim];
        v[a] = 1f;
        v[b] = weightB;
        return v;
    }

    // An explicit point in the (e_a, e_b) plane, so a fixture can place candidates
    // at CHOSEN cosine distances from each other rather than at whatever a blend
    // happens to produce.
    private static float[] Plane(int a, int b, double x, double y)
    {
        var v = new float[Dim];
        v[a] = (float)x;
        v[b] = (float)y;
        return v;
    }

    private sealed record SeededFace(Guid FaceId, Guid FileId, Guid BlobId);

    private static async Task<SeededFace> SeedFaceAsync(
        SqliteWebApplicationFactory f, Guid ownerId, Guid profileId,
        float[]? vector = null, double quality = 0.5, string embeddingStatus = AiArtifactStatuses.Completed)
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
            DetectionScore = 0.9, FaceQualityScore = quality, LandmarksJson = "[]", CreatedAt = DateTime.UtcNow,
        });
        if (vector is not null)
        {
            db.FaceEmbeddings.Add(new FaceEmbedding
            {
                Id = Guid.NewGuid(), FaceDetectionId = faceId, ProfileId = profileId,
                EmbeddingBytes = embeddingStatus == AiArtifactStatuses.Completed
                    ? ser.Serialize(vector, Dim)
                    : Array.Empty<byte>(),
                Dimension = Dim, EmbeddingStatus = embeddingStatus, CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return new SeededFace(faceId, fileId, blobId);
    }

    private static async Task<Guid> CreatePersonWithFacesAsync(
        SqliteWebApplicationFactory f, Guid ownerId, params Guid[] faceIds)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var person = new Person { Id = Guid.NewGuid(), OwnerUserId = ownerId, DisplayName = "P", CreatedAt = DateTime.UtcNow };
        db.People.Add(person);
        foreach (var faceId in faceIds)
        {
            db.PersonFaceAssignments.Add(new PersonFaceAssignment
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerId, PersonId = person.Id, FaceDetectionId = faceId,
                Source = PersonFaceAssignmentSources.UserConfirmed, CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return person.Id;
    }

    private static async Task<IReadOnlyList<PersonFaceReferenceService.PersonReferenceVector>> EnsureAsync(
        SqliteWebApplicationFactory f, Guid ownerId, Guid personId, Guid profileId, double coverage = 0.9)
    {
        using var scope = f.Services.CreateScope();
        var references = scope.ServiceProvider.GetRequiredService<PersonFaceReferenceService>();
        return await references.EnsureAsync(ownerId, personId, profileId, coverage);
    }

    private static async Task<List<PersonFaceReference>> ReferenceRowsAsync(
        SqliteWebApplicationFactory f, Guid personId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PersonFaceReferences.AsNoTracking()
            .Where(r => r.PersonId == personId).OrderBy(r => r.Ordinal).ToListAsync();
    }

    private static async Task<Guid> CreateOwnerAsync(SqliteWebApplicationFactory f, string email = "owner@example.com")
    {
        var (ownerId, _) = await f.CreateAuthenticatedClientAsync(email);
        return ownerId;
    }

    private static async Task<IReadOnlyList<Guid>?> RebuildAsync(
        SqliteWebApplicationFactory f, Guid ownerId, Guid personId, Guid profileId)
    {
        using var scope = f.Services.CreateScope();
        var references = scope.ServiceProvider.GetRequiredService<PersonFaceReferenceService>();
        return await references.RebuildAsync(ownerId, personId, profileId);
    }

    private static async Task<List<Guid>> AssignedFaceIdsAsync(SqliteWebApplicationFactory f, Guid personId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PersonFaceAssignments.AsNoTracking()
            .Where(a => a.PersonId == personId).Select(a => a.FaceDetectionId).ToListAsync();
    }

    // The effective coverage boundary a rebuild uses: the CONFIGURED default
    // search threshold, never a caller's slider (AiOptions.Face defaults to 0.35).
    private const double DefaultCoverage = 0.35;

    // ---- bootstrap --------------------------------------------------------

    [Fact]
    public async Task Person_With_One_Valid_Embedding_Gets_Exactly_One_Reference()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);
        var face = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var personId = await CreatePersonWithFacesAsync(f, ownerId, face.FaceId);

        var refs = await EnsureAsync(f, ownerId, personId, profileId);

        Assert.Single(refs);
        Assert.Equal(face.FaceId, refs[0].FaceDetectionId);
        var rows = await ReferenceRowsAsync(f, personId);
        Assert.Single(rows);
        Assert.Equal(0, rows[0].Ordinal);
        Assert.Equal(profileId, rows[0].ProfileId);
    }

    [Fact]
    public async Task Bootstrap_Caps_References_At_Six_Even_With_Many_Distinct_Faces()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);

        // 12 mutually orthogonal faces: every one is maximally novel, so only the
        // hard cap can stop the selection.
        var faceIds = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            faceIds.Add((await SeedFaceAsync(f, ownerId, profileId, OneHot(i))).FaceId);
        }
        var personId = await CreatePersonWithFacesAsync(f, ownerId, faceIds.ToArray());

        var refs = await EnsureAsync(f, ownerId, personId, profileId);

        Assert.Equal(PersonFaceReferenceService.MaxPersonReferenceFaces, refs.Count);
        var rows = await ReferenceRowsAsync(f, personId);
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.InRange(r.Ordinal, 0, 5));
        Assert.Equal(6, rows.Select(r => r.Ordinal).Distinct().Count());
    }

    [Fact]
    public async Task Bootstrap_Uses_Only_Completed_Assigned_Embeddings()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);

        var completed = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var failed = await SeedFaceAsync(f, ownerId, profileId, OneHot(1), embeddingStatus: AiArtifactStatuses.Failed);
        var noEmbedding = await SeedFaceAsync(f, ownerId, profileId, vector: null);
        // Assigned to a DIFFERENT person → never a candidate for this one.
        var otherPersonFace = await SeedFaceAsync(f, ownerId, profileId, OneHot(3));
        // Completed and embedded, but not assigned to anybody.
        var unassigned = await SeedFaceAsync(f, ownerId, profileId, OneHot(4));

        var personId = await CreatePersonWithFacesAsync(
            f, ownerId, completed.FaceId, failed.FaceId, noEmbedding.FaceId);
        await CreatePersonWithFacesAsync(f, ownerId, otherPersonFace.FaceId);

        var refs = await EnsureAsync(f, ownerId, personId, profileId);

        Assert.Single(refs);
        Assert.Equal(completed.FaceId, refs[0].FaceDetectionId);
        var ids = (await ReferenceRowsAsync(f, personId)).Select(r => r.FaceDetectionId).ToHashSet();
        Assert.DoesNotContain(failed.FaceId, ids);
        Assert.DoesNotContain(noEmbedding.FaceId, ids);
        Assert.DoesNotContain(otherPersonFace.FaceId, ids);
        Assert.DoesNotContain(unassigned.FaceId, ids);
    }

    [Fact]
    public async Task Bootstrap_Stops_Early_When_Remaining_Faces_Are_Already_Covered()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);

        // Six copies of one appearance: after the first reference every other face
        // is above the coverage boundary, so an ordinary person stays at 1.
        var faceIds = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            faceIds.Add((await SeedFaceAsync(f, ownerId, profileId, OneHot(0), quality: 0.5 + (i * 0.01))).FaceId);
        }
        var personId = await CreatePersonWithFacesAsync(f, ownerId, faceIds.ToArray());

        var refs = await EnsureAsync(f, ownerId, personId, profileId, coverage: 0.9);
        Assert.Single(refs);

        // Adding one genuinely different appearance grows the set — this is the
        // long-age-span case, discovered from the vectors and not from a label.
        var distinct = await SeedFaceAsync(f, ownerId, profileId, OneHot(7));
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PersonFaceAssignments.Add(new PersonFaceAssignment
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerId, PersonId = personId,
                FaceDetectionId = distinct.FaceId, Source = PersonFaceAssignmentSources.UserConfirmed,
                CreatedAt = DateTime.UtcNow,
            });
            // Simulate the invalidation path: drop the persisted set so the next
            // request bootstraps over the full, now-wider assignment history.
            db.PersonFaceReferences.RemoveRange(db.PersonFaceReferences.Where(r => r.PersonId == personId));
            await db.SaveChangesAsync();
        }

        var wider = await EnsureAsync(f, ownerId, personId, profileId, coverage: 0.9);
        Assert.Equal(2, wider.Count);
        Assert.Contains(distinct.FaceId, wider.Select(r => r.FaceDetectionId));
    }

    [Fact]
    public async Task Bootstrap_Is_Persisted_And_Reused_Without_Rescanning()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);
        var a = await SeedFaceAsync(f, ownerId, profileId, OneHot(0), quality: 0.9);
        var b = await SeedFaceAsync(f, ownerId, profileId, OneHot(1), quality: 0.4);
        var personId = await CreatePersonWithFacesAsync(f, ownerId, a.FaceId, b.FaceId);

        var first = await EnsureAsync(f, ownerId, personId, profileId);
        var firstRows = await ReferenceRowsAsync(f, personId);

        var second = await EnsureAsync(f, ownerId, personId, profileId);
        var secondRows = await ReferenceRowsAsync(f, personId);

        // Same faces, same row identities: the second request read the persisted
        // set rather than deriving a new one.
        Assert.Equal(
            first.Select(r => r.FaceDetectionId).OrderBy(x => x),
            second.Select(r => r.FaceDetectionId).OrderBy(x => x));
        Assert.Equal(firstRows.Select(r => r.Id).OrderBy(x => x), secondRows.Select(r => r.Id).OrderBy(x => x));
    }

    // ---- invalidation / replenishment -------------------------------------

    [Fact]
    public async Task Reference_Whose_Face_Is_No_Longer_Assigned_Is_Dropped_And_Replaced()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);
        var a = await SeedFaceAsync(f, ownerId, profileId, OneHot(0), quality: 0.9);
        var b = await SeedFaceAsync(f, ownerId, profileId, OneHot(1), quality: 0.8);
        var c = await SeedFaceAsync(f, ownerId, profileId, OneHot(2), quality: 0.7);
        var personId = await CreatePersonWithFacesAsync(f, ownerId, a.FaceId, b.FaceId, c.FaceId);

        var initial = await EnsureAsync(f, ownerId, personId, profileId);
        Assert.Equal(3, initial.Count);

        // Remove `a` from the person WITHOUT touching the reference rows: a stale
        // reference must never keep steering the search.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PersonFaceAssignments.RemoveRange(
                db.PersonFaceAssignments.Where(x => x.PersonId == personId && x.FaceDetectionId == a.FaceId));
            await db.SaveChangesAsync();
        }

        var after = await EnsureAsync(f, ownerId, personId, profileId);
        Assert.DoesNotContain(a.FaceId, after.Select(r => r.FaceDetectionId));
        var rows = await ReferenceRowsAsync(f, personId);
        Assert.DoesNotContain(a.FaceId, rows.Select(r => r.FaceDetectionId));
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task Removing_A_Face_Through_The_Service_Drops_Its_Reference()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);
        var a = await SeedFaceAsync(f, ownerId, profileId, OneHot(0), quality: 0.9);
        var b = await SeedFaceAsync(f, ownerId, profileId, OneHot(1), quality: 0.8);
        var personId = await CreatePersonWithFacesAsync(f, ownerId, a.FaceId, b.FaceId);
        await EnsureAsync(f, ownerId, personId, profileId);

        using (var scope = f.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            Assert.True(await people.RemoveFaceFromPersonAsync(ownerId, personId, a.FaceId));
        }

        var rows = await ReferenceRowsAsync(f, personId);
        Assert.DoesNotContain(a.FaceId, rows.Select(r => r.FaceDetectionId));
    }

    [Fact]
    public async Task Ignoring_A_Face_Drops_Its_Reference()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);
        var a = await SeedFaceAsync(f, ownerId, profileId, OneHot(0), quality: 0.9);
        var personId = await CreatePersonWithFacesAsync(f, ownerId, a.FaceId);
        await EnsureAsync(f, ownerId, personId, profileId);
        Assert.Single(await ReferenceRowsAsync(f, personId));

        using (var scope = f.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            Assert.True(await people.IgnoreFaceAsync(ownerId, a.FaceId));
        }

        Assert.Empty(await ReferenceRowsAsync(f, personId));
    }

    [Fact]
    public async Task Moving_A_Reference_Face_To_Another_Person_Drops_The_Reference()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);
        var a = await SeedFaceAsync(f, ownerId, profileId, OneHot(0), quality: 0.9);
        var b = await SeedFaceAsync(f, ownerId, profileId, OneHot(1), quality: 0.8);
        var source = await CreatePersonWithFacesAsync(f, ownerId, a.FaceId, b.FaceId);
        await EnsureAsync(f, ownerId, source, profileId);
        Assert.Equal(2, (await ReferenceRowsAsync(f, source)).Count);

        Guid target;
        using (var scope = f.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            var dto = await people.AssignFaceAsync(ownerId, a.FaceId, personId: null, newPersonName: "Other");
            target = dto!.PersonId;
        }

        var sourceRows = await ReferenceRowsAsync(f, source);
        Assert.DoesNotContain(a.FaceId, sourceRows.Select(r => r.FaceDetectionId));
        Assert.Empty(await ReferenceRowsAsync(f, target)); // lazily bootstrapped later
    }

    [Fact]
    public async Task Archiving_A_Person_Removes_Its_References()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);
        var a = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var personId = await CreatePersonWithFacesAsync(f, ownerId, a.FaceId);
        await EnsureAsync(f, ownerId, personId, profileId);
        Assert.Single(await ReferenceRowsAsync(f, personId));

        using (var scope = f.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            Assert.True(await people.ArchivePersonAsync(ownerId, personId));
        }

        Assert.Empty(await ReferenceRowsAsync(f, personId));
    }

    [Fact]
    public async Task Incremental_Maintenance_Never_Exceeds_Six_References()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);

        var first = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var personId = await CreatePersonWithFacesAsync(f, ownerId, first.FaceId);
        await EnsureAsync(f, ownerId, personId, profileId);

        // Feed 15 more orthogonal faces one at a time through the normal add path.
        for (var i = 1; i <= 15; i++)
        {
            var face = await SeedFaceAsync(f, ownerId, profileId, OneHot(i));
            using var scope = f.Services.CreateScope();
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            Assert.True(await people.AddFaceToPersonAsync(ownerId, personId, face.FaceId));
        }

        var rows = await ReferenceRowsAsync(f, personId);
        Assert.Equal(PersonFaceReferenceService.MaxPersonReferenceFaces, rows.Count);
        Assert.Equal(6, rows.Select(r => r.Ordinal).Distinct().Count());
        Assert.All(rows, r => Assert.InRange(r.Ordinal, 0, 5));
        // Every reference is still a confirmed face of this person.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var row in rows)
            {
                Assert.True(await db.PersonFaceAssignments.AnyAsync(
                    x => x.PersonId == personId && x.FaceDetectionId == row.FaceDetectionId));
            }
        }
    }

    [Fact]
    public async Task Incremental_Maintenance_Adds_A_Novel_Face_But_Not_A_Covered_One()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);
        var seed = await SeedFaceAsync(f, ownerId, profileId, OneHot(0), quality: 0.9);
        var personId = await CreatePersonWithFacesAsync(f, ownerId, seed.FaceId);
        await EnsureAsync(f, ownerId, personId, profileId);

        // A near-duplicate of the existing reference is already represented.
        var covered = await SeedFaceAsync(f, ownerId, profileId, Blend(0, 1, 0.05f));
        // A clearly different appearance is not.
        var novel = await SeedFaceAsync(f, ownerId, profileId, OneHot(9));

        using (var scope = f.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            Assert.True(await people.AddFaceToPersonAsync(ownerId, personId, covered.FaceId));
            Assert.True(await people.AddFaceToPersonAsync(ownerId, personId, novel.FaceId));
        }

        var ids = (await ReferenceRowsAsync(f, personId)).Select(r => r.FaceDetectionId).ToHashSet();
        Assert.Contains(seed.FaceId, ids);
        Assert.Contains(novel.FaceId, ids);
        Assert.DoesNotContain(covered.FaceId, ids);
    }

    // Overlapping similar-face requests both try to bootstrap the same person —
    // a double click or a slider drag is enough. The loser must read the winner's
    // set, never fail the search on a unique-index violation.
    [Fact]
    public async Task Concurrent_Bootstraps_Do_Not_Fail_And_Leave_One_Valid_Set()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);

        var faceIds = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            faceIds.Add((await SeedFaceAsync(f, ownerId, profileId, OneHot(i))).FaceId);
        }
        var personId = await CreatePersonWithFacesAsync(f, ownerId, faceIds.ToArray());

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var scope = f.Services.CreateScope();
            var references = scope.ServiceProvider.GetRequiredService<PersonFaceReferenceService>();
            return await references.EnsureAsync(ownerId, personId, profileId, 0.9);
        }));

        // Every caller got a usable, capped set…
        Assert.All(results, r =>
        {
            Assert.NotEmpty(r);
            Assert.InRange(r.Count, 1, PersonFaceReferenceService.MaxPersonReferenceFaces);
        });

        // …and exactly one set is persisted, with unique faces and ordinals.
        var rows = await ReferenceRowsAsync(f, personId);
        Assert.InRange(rows.Count, 1, PersonFaceReferenceService.MaxPersonReferenceFaces);
        Assert.Equal(rows.Count, rows.Select(r => r.FaceDetectionId).Distinct().Count());
        Assert.Equal(rows.Count, rows.Select(r => r.Ordinal).Distinct().Count());
    }

    // ---- correction rebuilds the WHOLE set --------------------------------
    //
    // A reference set is chosen GLOBALLY: quality, diversity and coverage decide
    // the set as a whole, so which faces are optimal depends on which other faces
    // are in it. When the owner says "#3 is not this person", deleting row #3 and
    // topping the set back up leaves the survivors frozen in an arrangement that
    // was partly chosen BECAUSE of the face that turned out to be somebody else.
    // Every one of these covers the same rule: the set is invalidated whole and
    // reselected from what remains.

    [Fact]
    public async Task Removing_A_Reference_Face_Reselects_The_Whole_Set()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);

        var faces = new List<SeededFace>();
        for (var i = 0; i < 8; i++)
        {
            faces.Add(await SeedFaceAsync(f, ownerId, profileId, OneHot(i), quality: 0.5 + (i * 0.01)));
        }
        var personId = await CreatePersonWithFacesAsync(f, ownerId, faces.Select(x => x.FaceId).ToArray());
        await EnsureAsync(f, ownerId, personId, profileId, coverage: DefaultCoverage);

        var before = await ReferenceRowsAsync(f, personId);
        Assert.Equal(PersonFaceReferenceService.MaxPersonReferenceFaces, before.Count);
        var victim = before[2].FaceDetectionId; // a face that IS a reference

        using (var scope = f.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            Assert.True(await people.RemoveFaceFromPersonAsync(ownerId, personId, victim));
        }

        var after = await ReferenceRowsAsync(f, personId);
        var assigned = (await AssignedFaceIdsAsync(f, personId)).ToHashSet();

        Assert.DoesNotContain(victim, assigned);
        Assert.DoesNotContain(victim, after.Select(r => r.FaceDetectionId));
        Assert.InRange(after.Count, 1, PersonFaceReferenceService.MaxPersonReferenceFaces);
        Assert.All(after, r => Assert.Contains(r.FaceDetectionId, assigned));
        // Reselected, not patched: every row is new, and the slots are a clean
        // 0..n-1 rather than the old ordinals with a hole punched in them.
        Assert.Empty(after.Select(r => r.Id).Intersect(before.Select(r => r.Id)));
        Assert.Equal(Enumerable.Range(0, after.Count), after.Select(r => r.Ordinal));
    }

    // The distinguishing case. The fixture is built so that removing ONE
    // reference changes which of the OTHERS belong in the set:
    //
    //   A  (quality .99) is the best face and anchors the original set;
    //   B  sits at cosine .30 to A — just OUTSIDE coverage, so it earns slot 2;
    //   C  sits at cosine .70 to A — INSIDE coverage, so it is never selected…
    //      …but at cosine .89 to B, and it outranks B on quality.
    //
    // Original set: [A, B]. Take A away and a full reselection picks C first,
    // which then COVERS B — so the surviving reference B drops out entirely.
    // A "fill the empty slot" implementation would keep B and answer [B].
    [Fact]
    public async Task Rebuild_Is_A_Full_Reselection_Not_A_Slot_Fill()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);

        var va = Plane(0, 1, 1.0, 0.0);
        var vb = Plane(0, 1, 0.3, 0.9539392);
        var vc = Plane(0, 1, 0.7, 0.7141428);

        var a = await SeedFaceAsync(f, ownerId, profileId, va, quality: 0.99);
        var b = await SeedFaceAsync(f, ownerId, profileId, vb, quality: 0.30);
        var c = await SeedFaceAsync(f, ownerId, profileId, vc, quality: 0.80);
        var personId = await CreatePersonWithFacesAsync(f, ownerId, a.FaceId, b.FaceId, c.FaceId);

        // Guard the fixture's geometry, so a change to the vectors fails here with
        // an explanation rather than in the assertion below.
        Assert.True(PersonReferenceSelector.CosineSimilarity(va, vb) < DefaultCoverage);
        Assert.True(PersonReferenceSelector.CosineSimilarity(va, vc) > DefaultCoverage);
        Assert.True(PersonReferenceSelector.CosineSimilarity(vb, vc) > DefaultCoverage);

        await EnsureAsync(f, ownerId, personId, profileId, coverage: DefaultCoverage);
        var before = (await ReferenceRowsAsync(f, personId)).Select(r => r.FaceDetectionId).ToList();
        Assert.Equal(new[] { a.FaceId, b.FaceId }, before);

        using (var scope = f.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            Assert.True(await people.RemoveFaceFromPersonAsync(ownerId, personId, a.FaceId));
        }

        var after = (await ReferenceRowsAsync(f, personId)).Select(r => r.FaceDetectionId).ToList();

        // Exactly what a fresh selection over the REMAINING candidates produces.
        var fresh = PersonReferenceSelector.Select(
            new[]
            {
                new PersonReferenceSelector.ReferenceCandidate(b.FaceId, vb, 0.30),
                new PersonReferenceSelector.ReferenceCandidate(c.FaceId, vc, 0.80),
            },
            DefaultCoverage);
        Assert.Equal(fresh, after);

        // …which is NOT the survivors-plus-a-filler a slot fill would give.
        Assert.Equal(new[] { c.FaceId }, after);
        Assert.DoesNotContain(b.FaceId, after);
    }

    [Fact]
    public async Task Moving_A_Reference_Face_Rebuilds_The_Source_And_Leaves_The_Target_Valid()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);

        // Alice: four confirmed faces, one of which is really Maria.
        var alice1 = await SeedFaceAsync(f, ownerId, profileId, OneHot(0), quality: 0.9);
        var alice2 = await SeedFaceAsync(f, ownerId, profileId, OneHot(1), quality: 0.8);
        var alice3 = await SeedFaceAsync(f, ownerId, profileId, OneHot(2), quality: 0.7);
        var reallyMaria = await SeedFaceAsync(f, ownerId, profileId, OneHot(3), quality: 0.95);
        var aliceId = await CreatePersonWithFacesAsync(
            f, ownerId, alice1.FaceId, alice2.FaceId, alice3.FaceId, reallyMaria.FaceId);
        await EnsureAsync(f, ownerId, aliceId, profileId, coverage: DefaultCoverage);
        Assert.Contains(reallyMaria.FaceId, (await ReferenceRowsAsync(f, aliceId)).Select(r => r.FaceDetectionId));

        Guid mariaId;
        using (var scope = f.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            var dto = await people.AssignFaceAsync(ownerId, reallyMaria.FaceId, personId: null, newPersonName: "Maria");
            mariaId = dto!.PersonId;
        }

        var aliceRows = await ReferenceRowsAsync(f, aliceId);
        var aliceAssigned = (await AssignedFaceIdsAsync(f, aliceId)).ToHashSet();
        Assert.DoesNotContain(reallyMaria.FaceId, aliceRows.Select(r => r.FaceDetectionId));
        Assert.DoesNotContain(reallyMaria.FaceId, aliceAssigned);
        // Alice was REBUILT from what is left — not left one short.
        Assert.Equal(3, aliceRows.Count);
        Assert.All(aliceRows, r => Assert.Contains(r.FaceDetectionId, aliceAssigned));
        Assert.Equal(Enumerable.Range(0, aliceRows.Count), aliceRows.Select(r => r.Ordinal));

        // Maria owns the face and bootstraps her own template lazily, unchanged.
        Assert.Contains(reallyMaria.FaceId, await AssignedFaceIdsAsync(f, mariaId));
        var maria = await EnsureAsync(f, ownerId, mariaId, profileId, coverage: DefaultCoverage);
        Assert.Single(maria);
        Assert.Equal(reallyMaria.FaceId, maria[0].FaceDetectionId);
    }

    [Fact]
    public async Task Ignoring_A_Reference_Face_Rebuilds_The_Set_Without_It()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);

        var keep1 = await SeedFaceAsync(f, ownerId, profileId, OneHot(0), quality: 0.9);
        var keep2 = await SeedFaceAsync(f, ownerId, profileId, OneHot(1), quality: 0.8);
        var stranger = await SeedFaceAsync(f, ownerId, profileId, OneHot(2), quality: 0.95);
        var personId = await CreatePersonWithFacesAsync(f, ownerId, keep1.FaceId, keep2.FaceId, stranger.FaceId);
        await EnsureAsync(f, ownerId, personId, profileId, coverage: DefaultCoverage);
        Assert.Equal(3, (await ReferenceRowsAsync(f, personId)).Count);

        using (var scope = f.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            Assert.True(await people.IgnoreFaceAsync(ownerId, stranger.FaceId));
        }

        var rows = await ReferenceRowsAsync(f, personId);
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(stranger.FaceId, rows.Select(r => r.FaceDetectionId));
        Assert.Equal(
            new[] { keep1.FaceId, keep2.FaceId }.OrderBy(x => x),
            rows.Select(r => r.FaceDetectionId).OrderBy(x => x));

        // An ignored face is no longer assigned, so a rebuild cannot pick it back.
        Assert.DoesNotContain(stranger.FaceId, await AssignedFaceIdsAsync(f, personId));
    }

    // The other half of the rule: a face that was NOT a reference costs nothing.
    // Removing it must not churn a healthy set — no invalidation, no reselection.
    [Fact]
    public async Task Removing_A_Non_Reference_Face_Leaves_The_Set_Untouched()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);

        // One appearance repeated: the first face becomes the only reference and
        // the rest are covered, so they are confirmed faces but not references.
        var reference = await SeedFaceAsync(f, ownerId, profileId, OneHot(0), quality: 0.9);
        var alsoCovered = await SeedFaceAsync(f, ownerId, profileId, OneHot(0), quality: 0.5);
        var spare = await SeedFaceAsync(f, ownerId, profileId, OneHot(0), quality: 0.4);
        var personId = await CreatePersonWithFacesAsync(f, ownerId, reference.FaceId, alsoCovered.FaceId, spare.FaceId);
        await EnsureAsync(f, ownerId, personId, profileId, coverage: DefaultCoverage);

        var before = await ReferenceRowsAsync(f, personId);
        Assert.Single(before);
        Assert.Equal(reference.FaceId, before[0].FaceDetectionId);

        using (var scope = f.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            Assert.True(await people.RemoveFaceFromPersonAsync(ownerId, personId, spare.FaceId));
        }

        var after = await ReferenceRowsAsync(f, personId);
        // The SAME rows, not equivalent ones: nothing was rewritten.
        Assert.Equal(before.Select(r => r.Id), after.Select(r => r.Id));
        Assert.Equal(before.Select(r => r.FaceDetectionId), after.Select(r => r.FaceDetectionId));
        Assert.Equal(before.Select(r => r.Ordinal), after.Select(r => r.Ordinal));
    }

    [Fact]
    public async Task Rebuild_Performs_No_Inference_And_Uses_Only_Completed_Embeddings()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);

        var good1 = await SeedFaceAsync(f, ownerId, profileId, OneHot(0), quality: 0.9);
        var good2 = await SeedFaceAsync(f, ownerId, profileId, OneHot(1), quality: 0.8);
        var failed = await SeedFaceAsync(
            f, ownerId, profileId, OneHot(2), quality: 0.99, embeddingStatus: AiArtifactStatuses.Failed);
        var noEmbedding = await SeedFaceAsync(f, ownerId, profileId, vector: null, quality: 0.99);
        var personId = await CreatePersonWithFacesAsync(
            f, ownerId, good1.FaceId, good2.FaceId, failed.FaceId, noEmbedding.FaceId);
        await EnsureAsync(f, ownerId, personId, profileId, coverage: DefaultCoverage);

        int detectionsBefore, embeddingsBefore;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            detectionsBefore = await db.FaceDetections.CountAsync();
            embeddingsBefore = await db.FaceEmbeddings.CountAsync();
        }

        var rebuilt = await RebuildAsync(f, ownerId, personId, profileId);

        Assert.NotNull(rebuilt);
        Assert.DoesNotContain(failed.FaceId, rebuilt!);
        Assert.DoesNotContain(noEmbedding.FaceId, rebuilt);
        Assert.Equal(new[] { good1.FaceId, good2.FaceId }.OrderBy(x => x), rebuilt.OrderBy(x => x));

        // No detection, no embedding, no re-embedding: a rebuild is a read of what
        // already exists plus at most six rows.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(detectionsBefore, await db.FaceDetections.CountAsync());
            Assert.Equal(embeddingsBefore, await db.FaceEmbeddings.CountAsync());
        }
    }

    // Derived state under contention: a rebuild racing the reference reads a
    // similar-face request makes must never turn into a 500, and must never leave
    // duplicate faces or duplicate slots behind.
    [Fact]
    public async Task Concurrent_Rebuild_And_Reference_Reads_Leave_One_Valid_Set()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);

        var faceIds = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            faceIds.Add((await SeedFaceAsync(f, ownerId, profileId, OneHot(i), quality: 0.5 + (i * 0.01))).FaceId);
        }
        var personId = await CreatePersonWithFacesAsync(f, ownerId, faceIds.ToArray());
        await EnsureAsync(f, ownerId, personId, profileId, coverage: DefaultCoverage);

        var exceptions = new List<Exception>();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            try
            {
                using var scope = f.Services.CreateScope();
                var references = scope.ServiceProvider.GetRequiredService<PersonFaceReferenceService>();
                if (i % 2 == 0)
                {
                    await references.RebuildAsync(ownerId, personId, profileId);
                }
                else
                {
                    await references.EnsureAsync(ownerId, personId, profileId, DefaultCoverage);
                }
            }
            catch (Exception ex)
            {
                lock (exceptions) { exceptions.Add(ex); }
            }
        }));

        Assert.Empty(exceptions);

        var rows = await ReferenceRowsAsync(f, personId);
        var assigned = (await AssignedFaceIdsAsync(f, personId)).ToHashSet();
        Assert.InRange(rows.Count, 0, PersonFaceReferenceService.MaxPersonReferenceFaces);
        Assert.Equal(rows.Count, rows.Select(r => r.FaceDetectionId).Distinct().Count());
        Assert.Equal(rows.Count, rows.Select(r => r.Ordinal).Distinct().Count());
        Assert.All(rows, r => Assert.Contains(r.FaceDetectionId, assigned));
        Assert.All(rows, r => Assert.InRange(r.Ordinal, 0, PersonFaceReferenceService.MaxPersonReferenceFaces - 1));
    }

    [Fact]
    public async Task Rebuild_Endpoint_Reselects_The_Set_And_Is_Owner_Scoped()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync("a@example.com");
        var (_, other) = await f.CreateAuthenticatedClientAsync("b@example.com");

        var faces = new List<SeededFace>();
        for (var i = 0; i < 4; i++)
        {
            faces.Add(await SeedFaceAsync(f, ownerId, profileId, OneHot(i), quality: 0.5 + (i * 0.01)));
        }
        var personId = await CreatePersonWithFacesAsync(f, ownerId, faces.Select(x => x.FaceId).ToArray());

        // Nothing bootstrapped yet: the manual rebuild BUILDS the set (unlike the
        // read-only GET, which deliberately does not).
        Assert.Empty(await ReferenceRowsAsync(f, personId));
        var resp = await client.PostAsync($"/api/people/{personId}/reference-faces/rebuild", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        var items = (await resp.Content.ReadFromJsonAsync<List<PersonReferenceFaceDto>>())!;

        Assert.Equal(4, items.Count);
        Assert.Equal(items.Select(r => r.Ordinal).OrderBy(x => x), items.Select(r => r.Ordinal));
        Assert.Equal(Enumerable.Range(0, items.Count), items.Select(r => r.Ordinal));
        var assigned = (await AssignedFaceIdsAsync(f, personId)).ToHashSet();
        Assert.All(items, r => Assert.Contains(r.FaceId, assigned));

        // Same set through the ordinary read.
        var read = (await client.GetFromJsonAsync<List<PersonReferenceFaceDto>>(
            $"/api/people/{personId}/reference-faces"))!;
        Assert.Equal(items.Select(r => r.FaceId), read.Select(r => r.FaceId));

        // No internals in the response.
        foreach (var forbidden in new[]
                 {
                     "EmbeddingBytes", "embeddingBytes", "StorageKey", "storageKey", "BlobObjectId",
                     "blobObjectId", "Sha256", "sha256", "/storage/objects/", "PrivateVaultId",
                     "privateVaultId", "ProfileId", "profileId", "score", "distance", "at NubArca.",
                 })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.Ordinal);
        }

        // The confirmed assignments are untouched — this is derived state only.
        Assert.Equal(4, (await AssignedFaceIdsAsync(f, personId)).Count);

        // Cross-owner, unknown and archived are the same generic 404; anonymous 401.
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.PostAsync($"/api/people/{personId}/reference-faces/rebuild", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync($"/api/people/{Guid.NewGuid()}/reference-faces/rebuild", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await f.CreateClient().PostAsync($"/api/people/{personId}/reference-faces/rebuild", null)).StatusCode);

        using (var scope = f.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            Assert.True(await people.ArchivePersonAsync(ownerId, personId));
        }
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync($"/api/people/{personId}/reference-faces/rebuild", null)).StatusCode);
    }

    // ---- read-only reference-faces surface --------------------------------

    [Fact]
    public async Task Reference_Faces_Are_Returned_In_Ordinal_Order()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);

        var faceIds = new List<Guid>();
        for (var i = 0; i < 9; i++)
        {
            faceIds.Add((await SeedFaceAsync(f, ownerId, profileId, OneHot(i))).FaceId);
        }
        var personId = await CreatePersonWithFacesAsync(f, ownerId, faceIds.ToArray());
        await EnsureAsync(f, ownerId, personId, profileId);

        var stored = await ReferenceRowsAsync(f, personId);
        Assert.Equal(PersonFaceReferenceService.MaxPersonReferenceFaces, stored.Count);

        using var scope = f.Services.CreateScope();
        var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
        var surfaced = await people.GetPersonReferenceFacesAsync(ownerId, personId);

        Assert.NotNull(surfaced);
        // Exactly the persisted rows, in slot order — never a second selection.
        Assert.Equal(stored.Select(r => r.FaceDetectionId), surfaced!.Select(r => r.FaceId));
        Assert.Equal(stored.Select(r => r.Ordinal), surfaced.Select(r => r.Ordinal));
        Assert.Equal(surfaced.Select(r => r.Ordinal).OrderBy(x => x), surfaced.Select(r => r.Ordinal));
        Assert.All(surfaced, r => Assert.InRange(r.Ordinal, 0, PersonFaceReferenceService.MaxPersonReferenceFaces - 1));
    }

    [Fact]
    public async Task Reference_Faces_Get_Does_Not_Bootstrap()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);
        var face = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var personId = await CreatePersonWithFacesAsync(f, ownerId, face.FaceId);

        using (var scope = f.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            // The person HAS an eligible confirmed embedding, so a bootstrapping
            // read would happily invent a set here. It must not.
            var surfaced = await people.GetPersonReferenceFacesAsync(ownerId, personId);
            Assert.NotNull(surfaced);
            Assert.Empty(surfaced!);
        }
        Assert.Empty(await ReferenceRowsAsync(f, personId));

        // Only the search builds it; the same read then reports it.
        await EnsureAsync(f, ownerId, personId, profileId);
        using (var scope = f.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            var surfaced = await people.GetPersonReferenceFacesAsync(ownerId, personId);
            Assert.Single(surfaced!);
            Assert.Equal(face.FaceId, surfaced![0].FaceId);
            Assert.Equal(0, surfaced[0].Ordinal);
        }
    }

    [Fact]
    public async Task Reference_Faces_Foreign_Or_Archived_Person_Is_404()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f, "a@example.com");
        var otherId = await CreateOwnerAsync(f, "b@example.com");
        var face = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var personId = await CreatePersonWithFacesAsync(f, ownerId, face.FaceId);
        await EnsureAsync(f, ownerId, personId, profileId);

        using var scope = f.Services.CreateScope();
        var people = scope.ServiceProvider.GetRequiredService<PeopleService>();

        // Another owner cannot read this person's template at all.
        Assert.Null(await people.GetPersonReferenceFacesAsync(otherId, personId));
        // Nor can anybody read a person that does not exist.
        Assert.Null(await people.GetPersonReferenceFacesAsync(ownerId, Guid.NewGuid()));

        Assert.True(await people.ArchivePersonAsync(ownerId, personId));
        Assert.Null(await people.GetPersonReferenceFacesAsync(ownerId, personId));
    }

    [Fact]
    public async Task Reference_Face_That_Is_No_Longer_Surfaceable_Is_Not_Leaked()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var ownerId = await CreateOwnerAsync(f);
        var visible = await SeedFaceAsync(f, ownerId, profileId, OneHot(0), quality: 0.9);
        var hidden = await SeedFaceAsync(f, ownerId, profileId, OneHot(1), quality: 0.8);
        var personId = await CreatePersonWithFacesAsync(f, ownerId, visible.FaceId, hidden.FaceId);
        await EnsureAsync(f, ownerId, personId, profileId);
        Assert.Equal(2, (await ReferenceRowsAsync(f, personId)).Count);

        // Vault the second reference's photo: the row survives (the search path
        // repairs it) but the read must not surface a face the owner cannot see.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var vault = new PrivateVault
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerId, DisplayName = "Private",
                PasswordHash = "x", EncryptionMode = PrivateVaultEncryptionModes.None, CreatedAt = DateTime.UtcNow,
            };
            db.PrivateVaults.Add(vault);
            var file = await db.FileItems.FirstAsync(x => x.Id == hidden.FileId);
            file.PrivateVaultId = vault.Id;
            await db.SaveChangesAsync();
        }

        using (var scope = f.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            var surfaced = await people.GetPersonReferenceFacesAsync(ownerId, personId);
            Assert.Single(surfaced!);
            Assert.Equal(visible.FaceId, surfaced![0].FaceId);
            Assert.DoesNotContain(hidden.FaceId, surfaced.Select(r => r.FaceId));
        }
    }

    [Fact]
    public async Task Reference_Faces_Endpoint_Is_Owner_Scoped_And_Leaks_Nothing()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync("a@example.com");
        var (_, other) = await f.CreateAuthenticatedClientAsync("b@example.com");
        var face = await SeedFaceAsync(f, ownerId, profileId, OneHot(0));
        var personId = await CreatePersonWithFacesAsync(f, ownerId, face.FaceId);

        // Before any search: 200 with an empty list, and still no rows written.
        var empty = await client.GetAsync($"/api/people/{personId}/reference-faces");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Empty((await empty.Content.ReadFromJsonAsync<List<PersonReferenceFaceDto>>())!);
        Assert.Empty(await ReferenceRowsAsync(f, personId));

        await EnsureAsync(f, ownerId, personId, profileId);
        var resp = await client.GetAsync($"/api/people/{personId}/reference-faces");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        var items = await resp.Content.ReadFromJsonAsync<List<PersonReferenceFaceDto>>();
        Assert.Single(items!);
        Assert.Equal(face.FaceId, items![0].FaceId);
        foreach (var forbidden in new[]
                 {
                     "EmbeddingBytes", "embeddingBytes", "StorageKey", "storageKey", "BlobObjectId",
                     "blobObjectId", "Sha256", "sha256", "/storage/objects/", "PrivateVaultId",
                     "privateVaultId", "ProfileId", "profileId", "score", "distance", "at NubArca.",
                 })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.Ordinal);
        }

        // Cross-owner and unknown person are the same generic 404; anonymous 401.
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/people/{personId}/reference-faces")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/people/{Guid.NewGuid()}/reference-faces")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await f.CreateClient().GetAsync($"/api/people/{personId}/reference-faces")).StatusCode);
    }

    // ---- pure selection ---------------------------------------------------

    [Fact]
    public void Selector_Picks_Highest_Quality_First_And_Breaks_Ties_By_Id()
    {
        var low = new Guid("00000000-0000-0000-0000-0000000000aa");
        var high = new Guid("00000000-0000-0000-0000-0000000000bb");
        var tie = new Guid("00000000-0000-0000-0000-000000000001");

        var byQuality = PersonReferenceSelector.Select(
            new[]
            {
                new PersonReferenceSelector.ReferenceCandidate(low, OneHot(0), 0.2),
                new PersonReferenceSelector.ReferenceCandidate(high, OneHot(0), 0.8),
            },
            coverageThreshold: 0.9);
        Assert.Equal(high, byQuality[0]);

        var byId = PersonReferenceSelector.Select(
            new[]
            {
                new PersonReferenceSelector.ReferenceCandidate(low, OneHot(0), 0.5),
                new PersonReferenceSelector.ReferenceCandidate(tie, OneHot(0), 0.5),
            },
            coverageThreshold: 0.9);
        Assert.Equal(tie, byId[0]); // equal quality → lowest FaceDetectionId
    }

    [Fact]
    public void Selector_Never_Returns_More_Than_The_Cap()
    {
        var candidates = Enumerable.Range(0, 20)
            .Select(i => new PersonReferenceSelector.ReferenceCandidate(Guid.NewGuid(), OneHot(i), 0.5))
            .ToList();

        var selected = PersonReferenceSelector.Select(candidates, coverageThreshold: 0.9);

        Assert.Equal(PersonReferenceSelector.MaxPersonReferenceFaces, selected.Count);
        Assert.Equal(selected.Count, selected.Distinct().Count());
    }

    [Fact]
    public void Selector_Keeps_The_Seed_And_Only_Appends_Uncovered_Candidates()
    {
        var seedId = Guid.NewGuid();
        var coveredId = Guid.NewGuid();
        var novelId = Guid.NewGuid();
        var seed = new[] { new PersonReferenceSelector.ReferenceCandidate(seedId, OneHot(0), 0.5) };

        var selected = PersonReferenceSelector.Select(
            new[]
            {
                new PersonReferenceSelector.ReferenceCandidate(coveredId, Blend(0, 1, 0.05f), 0.9),
                new PersonReferenceSelector.ReferenceCandidate(novelId, OneHot(5), 0.1),
            },
            coverageThreshold: 0.9,
            seed: seed);

        Assert.Equal(seedId, selected[0]);
        Assert.Contains(novelId, selected);
        Assert.DoesNotContain(coveredId, selected);
    }

    [Fact]
    public void Cosine_Similarity_Is_Norm_Aware()
    {
        // Same direction, different magnitude → still 1.
        Assert.Equal(1.0, PersonReferenceSelector.CosineSimilarity(OneHot(0), OneHot(0, 7f)), 6);
        // Orthogonal → 0.
        Assert.Equal(0.0, PersonReferenceSelector.CosineSimilarity(OneHot(0), OneHot(1)), 6);
    }
}
