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

    // The multi-reference template in the real ANN path: a person confirmed with
    // two very different appearances gets TWO references, and a candidate that
    // matches only the SECOND one still comes back — which the previous
    // single-arbitrary-source query could not do. Also proves the merge is a MAX
    // over references, that a neighbour seen by both references appears once, that
    // the person's own faces stay excluded, and that a candidate already on
    // another person is RETAINED and named.
    [SkippableFact]
    public async Task Multi_Reference_Search_Merges_By_Max_Score_And_Names_Assigned_Candidates()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var profileKey = $"face-multiref-{suffix}";
        var settings = new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:FaceProfileKey"] = profileKey,
        };
        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, settings);

        Guid owner, personId, otherPersonId, profileId;
        Guid youngFace, oldFace, matchesYoung, matchesOld, seenByBoth, onOtherPerson;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            var vectors = scope.ServiceProvider.GetRequiredService<FaceVectorIndexService>();

            owner = (await users.CreateAsync($"mr-{suffix}@example.com", "M")).Id;
            var model = AddModel(db, $"m-{suffix}");
            profileId = AddProfile(db, profileKey, model.Id).Id;

            // Two ORTHOGONAL confirmed appearances ⇒ cosine 0 between them, so both
            // are selected as references (neither covers the other).
            youngFace = (await AddFaceAsync(db, ser, vectors, owner, profileId, OneHot(0), null)).FaceId;
            oldFace = (await AddFaceAsync(db, ser, vectors, owner, profileId, OneHot(1), null)).FaceId;

            // Candidates: one on each reference's axis, plus one at 45° that BOTH
            // reference searches return (cosine ~0.707 to each).
            matchesYoung = (await AddFaceAsync(db, ser, vectors, owner, profileId, OneHot(0), null)).FaceId;
            matchesOld = (await AddFaceAsync(db, ser, vectors, owner, profileId, OneHot(1), null)).FaceId;
            seenByBoth = (await AddFaceAsync(db, ser, vectors, owner, profileId, Diagonal(), null)).FaceId;
            onOtherPerson = (await AddFaceAsync(db, ser, vectors, owner, profileId, OneHot(1), null)).FaceId;

            var person = new Person { Id = Guid.NewGuid(), OwnerUserId = owner, DisplayName = "Alice", CreatedAt = DateTime.UtcNow };
            var other = new Person { Id = Guid.NewGuid(), OwnerUserId = owner, DisplayName = "Maria", CreatedAt = DateTime.UtcNow };
            db.People.AddRange(person, other);
            Assign(db, owner, person.Id, youngFace);
            Assign(db, owner, person.Id, oldFace);
            Assign(db, owner, other.Id, onOtherPerson);
            personId = person.Id;
            otherPersonId = other.Id;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            var page = await people.FindSimilarFacesAsync(owner, personId, 0.5, 50, null);
            Assert.NotNull(page);
            Assert.True(page!.ProfileAvailable);

            var byFace = page.Items.ToDictionary(i => i.FaceId);

            // A candidate on EITHER reference's axis is returned — with one
            // arbitrary source, only one of these two could have been.
            Assert.Contains(matchesYoung, byFace.Keys);
            Assert.Contains(matchesOld, byFace.Keys);

            // Score is the BEST similarity across references, not the last one.
            Assert.Equal(1.0, byFace[matchesYoung].Score, 3);
            Assert.Equal(1.0, byFace[matchesOld].Score, 3);
            Assert.Equal(Math.Sqrt(0.5), byFace[seenByBoth].Score, 3);

            // Seen by both reference searches, listed once.
            Assert.Single(page.Items, i => i.FaceId == seenByBoth);
            Assert.Equal(page.Items.Count, page.Items.Select(i => i.FaceId).Distinct().Count());

            // The person's own confirmed faces stay excluded, exactly as before.
            Assert.DoesNotContain(youngFace, byFace.Keys);
            Assert.DoesNotContain(oldFace, byFace.Keys);

            // A face on ANOTHER person is kept — it is how a past mistake is
            // corrected — and says so.
            Assert.Contains(onOtherPerson, byFace.Keys);
            Assert.Equal(otherPersonId, byFace[onOtherPerson].AssignedPersonId);
            Assert.Equal("Maria", byFace[onOtherPerson].AssignedPersonName);

            // A free candidate carries no assignment.
            Assert.Null(byFace[matchesYoung].AssignedPersonId);
            Assert.Null(byFace[matchesYoung].AssignedPersonName);
        }

        // The reference set was persisted, is at most 6, and is reused unchanged.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.PersonFaceReferences.AsNoTracking()
                .Where(r => r.PersonId == personId).OrderBy(r => r.Ordinal).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.True(rows.Count <= PersonFaceReferenceService.MaxPersonReferenceFaces);
            Assert.All(rows, r => Assert.Equal(profileId, r.ProfileId));
            Assert.Equal(new[] { youngFace, oldFace }.OrderBy(x => x), rows.Select(r => r.FaceDetectionId).OrderBy(x => x));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            var again = await people.FindSimilarFacesAsync(owner, personId, 0.5, 50, null);
            Assert.NotNull(again);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.PersonFaceReferences.AsNoTracking().Where(r => r.PersonId == personId).ToListAsync();
            Assert.Equal(2, rows.Count); // reused, not re-derived into duplicates
        }
    }

    // An IGNORED face is not a candidate — including in "Cerca volti simili",
    // which was the one surface that kept proposing faces the owner had already
    // dismissed. Also pins the two things that must NOT change with it: a face on
    // ANOTHER person stays proposed (that is how a past mistake is corrected), and
    // the filter runs BEFORE paging, so a page is never short and a cursor never
    // walks past a candidate.
    [SkippableFact]
    public async Task Similar_Faces_Exclude_Ignored_Candidates_Before_Paging()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var profileKey = $"face-ignored-{suffix}";
        var settings = new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:FaceProfileKey"] = profileKey,
        };
        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, settings);

        const int freeCount = 6;
        const int ignoredCount = 4;
        Guid owner, personId, otherPersonId;
        Guid onOtherPerson, ignoredOnOtherPerson;
        var freeIds = new List<Guid>();
        var ignoredIds = new List<Guid>();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            var vectors = scope.ServiceProvider.GetRequiredService<FaceVectorIndexService>();

            owner = (await users.CreateAsync($"ig-{suffix}@example.com", "I")).Id;
            var model = AddModel(db, $"m-{suffix}");
            var profileId = AddProfile(db, profileKey, model.Id).Id;

            // One confirmed face is the whole template; every candidate below is
            // identical to it, so all of them clear any threshold and the only
            // thing that can remove one is the ignore filter.
            var query = (await AddFaceAsync(db, ser, vectors, owner, profileId, OneHot(0), null)).FaceId;
            for (var i = 0; i < freeCount; i++)
            {
                freeIds.Add((await AddFaceAsync(db, ser, vectors, owner, profileId, OneHot(0), null)).FaceId);
            }
            for (var i = 0; i < ignoredCount; i++)
            {
                ignoredIds.Add((await AddFaceAsync(db, ser, vectors, owner, profileId, OneHot(0), null)).FaceId);
            }
            onOtherPerson = (await AddFaceAsync(db, ser, vectors, owner, profileId, OneHot(0), null)).FaceId;
            ignoredOnOtherPerson = (await AddFaceAsync(db, ser, vectors, owner, profileId, OneHot(0), null)).FaceId;

            var person = new Person { Id = Guid.NewGuid(), OwnerUserId = owner, DisplayName = "Alice", CreatedAt = DateTime.UtcNow };
            var other = new Person { Id = Guid.NewGuid(), OwnerUserId = owner, DisplayName = "Maria", CreatedAt = DateTime.UtcNow };
            db.People.AddRange(person, other);
            Assign(db, owner, person.Id, query);
            Assign(db, owner, other.Id, onOtherPerson);
            Assign(db, owner, other.Id, ignoredOnOtherPerson);
            personId = person.Id;
            otherPersonId = other.Id;

            foreach (var faceId in ignoredIds.Append(ignoredOnOtherPerson))
            {
                db.IgnoredFaces.Add(new IgnoredFace
                {
                    Id = Guid.NewGuid(), OwnerUserId = owner, FaceDetectionId = faceId, CreatedAt = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();

            // Paged four at a time, so a filter applied to an already-cut window
            // would show up as a short page or a skipped candidate.
            var all = await CollectAsync(people, owner, personId, 0.5);

            Assert.All(ignoredIds, id => Assert.DoesNotContain(id, all));
            Assert.DoesNotContain(ignoredOnOtherPerson, all);
            Assert.All(freeIds, id => Assert.Contains(id, all));
            // A candidate on ANOTHER person is retained and named — unchanged.
            Assert.Contains(onOtherPerson, all);
            Assert.Equal(freeCount + 1, all.Count);
            Assert.Equal(all.Count, all.Distinct().Count());

            // Every full page really is full: the ignored rows were gone before
            // the window was cut, not removed from it afterwards.
            string? cursor = null;
            var pages = new List<int>();
            for (var guard = 0; guard < 20; guard++)
            {
                var page = await people.FindSimilarFacesAsync(owner, personId, 0.5, 4, cursor);
                Assert.NotNull(page);
                pages.Add(page!.Items.Count);
                if (!page.HasMore || page.NextCursor is null) break;
                Assert.Equal(4, page.Items.Count);
                cursor = page.NextCursor;
            }
            Assert.Equal(freeCount + 1, pages.Sum());

            var named = Assert.Single(
                (await people.FindSimilarFacesAsync(owner, personId, 0.5, 50, null))!.Items,
                i => i.FaceId == onOtherPerson);
            Assert.Equal(otherPersonId, named.AssignedPersonId);
            Assert.Equal("Maria", named.AssignedPersonName);
        }

        // Restoring an ignored face makes it a candidate again — ignore is a
        // reversible owner decision, not a deletion.
        using (var scope = factory.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            Assert.True(await people.UnignoreFaceAsync(owner, ignoredIds[0]));
            var all = await CollectAsync(people, owner, personId, 0.5);
            Assert.Contains(ignoredIds[0], all);
            Assert.Equal(freeCount + 2, all.Count);
        }
    }

    private static void Assign(AppDbContext db, Guid owner, Guid personId, Guid faceId) =>
        db.PersonFaceAssignments.Add(new PersonFaceAssignment
        {
            Id = Guid.NewGuid(), OwnerUserId = owner, PersonId = personId, FaceDetectionId = faceId,
            Source = PersonFaceAssignmentSources.UserConfirmed, CreatedAt = DateTime.UtcNow,
        });

    // 45° between OneHot(0) and OneHot(1): cosine sqrt(0.5) to each, so BOTH
    // reference searches return it above a 0.5 threshold.
    [SkippableFact]
    public async Task Unassigned_Candidates_Come_First_Even_When_Assigned_Ones_Score_Higher()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var profileKey = $"face-order-{suffix}";
        var settings = new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:FaceProfileKey"] = profileKey,
        };
        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, settings);

        // The scores are deliberately the WRONG way round for the priority: the
        // faces already on somebody else are identical to the template (score 1.0)
        // and the free ones are only ~0.707. Ordering by score alone would put
        // every assigned face first — and there are more of them than fit on a
        // page, so the free candidates would not appear on the first page at all.
        const int assignedCount = 5;
        const int freeCount = 3;
        const int pageSize = 4;

        Guid owner, personId;
        var freeIds = new List<Guid>();
        var assignedElsewhereIds = new List<Guid>();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var ser = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            var vectors = scope.ServiceProvider.GetRequiredService<FaceVectorIndexService>();

            owner = (await users.CreateAsync($"order-{suffix}@example.com", "O")).Id;
            var model = AddModel(db, $"m-{suffix}");
            var profileId = AddProfile(db, profileKey, model.Id).Id;

            var query = (await AddFaceAsync(db, ser, vectors, owner, profileId, OneHot(0), null)).FaceId;
            for (var i = 0; i < assignedCount; i++)
            {
                assignedElsewhereIds.Add(
                    (await AddFaceAsync(db, ser, vectors, owner, profileId, OneHot(0), null)).FaceId);
            }
            for (var i = 0; i < freeCount; i++)
            {
                freeIds.Add((await AddFaceAsync(db, ser, vectors, owner, profileId, NearVec(), null)).FaceId);
            }

            var person = new Person { Id = Guid.NewGuid(), OwnerUserId = owner, DisplayName = "Alice", CreatedAt = DateTime.UtcNow };
            var other = new Person { Id = Guid.NewGuid(), OwnerUserId = owner, DisplayName = "Maria", CreatedAt = DateTime.UtcNow };
            db.People.AddRange(person, other);
            Assign(db, owner, person.Id, query);
            foreach (var faceId in assignedElsewhereIds)
            {
                Assign(db, owner, other.Id, faceId);
            }
            personId = person.Id;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();

            // THE LIMIT MUST NOT DEFEAT THE PRIORITY. The first page is smaller
            // than the number of higher-scoring assigned faces, so ordering by
            // score would fill it entirely with them.
            var first = await people.FindSimilarFacesAsync(owner, personId, 0.5, pageSize, null);
            Assert.NotNull(first);
            var firstIds = first!.Items.Select(i => i.FaceId).ToList();
            Assert.All(freeIds, id => Assert.Contains(id, firstIds));

            // Every free candidate precedes every assigned one, across the WHOLE
            // list rather than within a page.
            var all = await CollectAsync(people, owner, personId, 0.5);
            var lastFree = all.FindLastIndex(id => freeIds.Contains(id));
            var firstAssigned = all.FindIndex(id => assignedElsewhereIds.Contains(id));
            Assert.True(lastFree >= 0 && firstAssigned >= 0, "both groups must be present");
            Assert.True(
                lastFree < firstAssigned,
                $"an assigned candidate appeared at {firstAssigned}, before a free one at {lastFree}");

            // …and the scores really were inverted, so this is not passing because
            // the free faces happened to rank higher anyway.
            var byId = new Dictionary<Guid, double>();
            string? cursor = null;
            for (var guard = 0; guard < 20; guard++)
            {
                var page = await people.FindSimilarFacesAsync(owner, personId, 0.5, pageSize, cursor);
                Assert.NotNull(page);
                foreach (var item in page!.Items) byId[item.FaceId] = item.Score;
                if (!page.HasMore || page.NextCursor is null) break;
                cursor = page.NextCursor;
            }
            Assert.True(
                byId[assignedElsewhereIds[0]] > byId[freeIds[0]],
                "the fixture must keep the assigned candidate scoring HIGHER");

            // Paging across the boundary between the two blocks neither repeats
            // nor drops a candidate — the cursor carries the group.
            Assert.Equal(assignedCount + freeCount, all.Count);
            Assert.Equal(all.Count, all.Distinct().Count());

            // Within each block, still descending by score.
            var freeOrder = all.Where(freeIds.Contains).Select(id => byId[id]).ToList();
            Assert.Equal(freeOrder.OrderByDescending(x => x).ToList(), freeOrder);
        }
    }

    private static float[] Diagonal()
    {
        var v = new float[Dim];
        v[0] = 1f;
        v[1] = 1f;
        return v;
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

    // ~0.707 cosine to OneHot(0): [1, 1, 0, …] (normalized at insert time). Above
    // a 0.5 threshold, and deliberately BELOW an identical match, so a test can
    // make the free candidates score worse than the assigned ones.
    private static float[] NearVec()
    {
        var v = new float[Dim];
        v[0] = 1f;
        v[1] = 1f;
        return v;
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
