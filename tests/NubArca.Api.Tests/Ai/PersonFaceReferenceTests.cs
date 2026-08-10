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
