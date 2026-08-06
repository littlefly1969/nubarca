using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Storage;
using NubArca.Api.Users;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// REAL pgvector via Testcontainers (pgvector/pgvector:pg17). Proves the Phase 2B
// foundation end-to-end on Postgres: profile-keyed vector-sync (idempotent,
// validating, no cross-profile writes), the ANN read path, owner-private
// filtering, and the exact-scan fallback when a profile is not vector-indexed.
// Embeddings are inserted directly (controlled unit vectors) so cosine ordering
// is assertable. Skipped when Docker / the pgvector image is unavailable.
[Collection(PgVectorIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class PhotoVectorPgIntegrationTests : IAsyncLifetime
{
    private readonly PgVectorContainerFixture _fixture;

    public PhotoVectorPgIntegrationTests(PgVectorContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Full_Vector_Lifecycle_Sync_Search_Fallback_And_OwnerPrivacy()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        var settings = new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ImageEmbeddingsEnabled"] = "true",
        };
        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, settings);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var profileA = $"pgv-a-{suffix}";
        var profileB = $"pgv-b-{suffix}";

        Guid userA, userB, qBlob, a1Blob, a2Blob, b1Blob, qFile, a1File, a2File, b1File, profileAId, profileBId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

            userA = (await users.CreateAsync($"a-{suffix}@example.com", "A")).Id;
            userB = (await users.CreateAsync($"b-{suffix}@example.com", "B")).Id;

            var model = AddModel(db, $"det1152-{suffix}");
            var pA = AddProfile(db, profileA, model.Id);
            var pB = AddProfile(db, profileB, model.Id);
            profileAId = pA.Id;
            profileBId = pB.Id;

            qBlob = AddBlob(db, suffix, "q");
            a1Blob = AddBlob(db, suffix, "a1");
            a2Blob = AddBlob(db, suffix, "a2");
            b1Blob = AddBlob(db, suffix, "b1");
            qFile = AddFile(db, userA, qBlob, "q.png");
            a1File = AddFile(db, userA, a1Blob, "a1.png");
            a2File = AddFile(db, userA, a2Blob, "a2.png");
            b1File = AddFile(db, userB, b1Blob, "b1.png");
            AddImageMetadata(db, a1Blob, width: 32, height: 32);
            AddImageMetadata(db, a2Blob, width: 256, height: 256);

            // profileA canonical embeddings: q=e0, a1=e0 (identical), a2=e1
            // (orthogonal), b1=e0 (B's file).
            AddEmbedding(db, serializer, qBlob, pA.Id, OneHot(0));
            AddEmbedding(db, serializer, a1Blob, pA.Id, OneHot(0));
            AddEmbedding(db, serializer, a2Blob, pA.Id, OneHot(1));
            AddEmbedding(db, serializer, b1Blob, pA.Id, OneHot(0));
            // profileB canonical embeddings for A-owned files (B is NOT vector-synced).
            AddEmbedding(db, serializer, qBlob, pB.Id, OneHot(0));
            AddEmbedding(db, serializer, a1Blob, pB.Id, OneHot(0));
            AddEmbedding(db, serializer, a2Blob, pB.Id, OneHot(1));

            await db.SaveChangesAsync();
        }

        // 1. dry-run: nothing written.
        using (var scope = factory.Services.CreateScope())
        {
            var vectors = scope.ServiceProvider.GetRequiredService<PhotoVectorIndexService>();
            var reg = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
            var pA = await reg.GetProfileByKeyAsync(profileA);
            var dry = await vectors.SyncProfileAsync(pA!, limit: null, dryRun: true);
            Assert.True(dry.Available);
            Assert.True(dry.DryRun);
            Assert.Equal(4, dry.EligibleEmbeddings);
            Assert.Equal(0, dry.Synced);
            Assert.Equal(0, dry.VectorIndexed);
        }

        // 2. real sync indexes all 4 of profileA's embeddings.
        using (var scope = factory.Services.CreateScope())
        {
            var vectors = scope.ServiceProvider.GetRequiredService<PhotoVectorIndexService>();
            var reg = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
            var pA = await reg.GetProfileByKeyAsync(profileA);
            var r = await vectors.SyncProfileAsync(pA!, limit: null, dryRun: false);
            Assert.True(r.Available);
            Assert.Equal(4, r.Synced);
            Assert.Equal(4, r.VectorIndexed);
            Assert.Equal(0, r.Failed);
            Assert.Equal(0, r.SkippedDimensionMismatch);
            Assert.Equal(0, r.MissingVectors);
        }

        // idempotent re-run: nothing new.
        using (var scope = factory.Services.CreateScope())
        {
            var vectors = scope.ServiceProvider.GetRequiredService<PhotoVectorIndexService>();
            var reg = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
            var pA = await reg.GetProfileByKeyAsync(profileA);
            var r = await vectors.SyncProfileAsync(pA!, limit: null, dryRun: false);
            Assert.Equal(0, r.Synced);
            Assert.Equal(4, r.VectorIndexed);
            // profileB was never synced — its vector table partition is empty.
            Assert.Equal(0, await vectors.CountIndexedAsync(profileBId));
        }

        // 3. ANN read path (profileA): owner-private, query excluded, e0 first.
        using (var scope = factory.Services.CreateScope())
        {
            var sim = scope.ServiceProvider.GetRequiredService<PhotoSimilarityService>();
            var res = await sim.FindSimilarAsync(userA, qFile, 10, profileKeyOverride: profileA);
            Assert.NotNull(res);
            Assert.True(res!.QueryIndexed);
            var ids = res.Items.Select(i => i.FileItemId).ToList();
            Assert.Contains(a1File, ids);
            Assert.Contains(a2File, ids);
            Assert.DoesNotContain(b1File, ids); // owner-private: never B's file
            Assert.DoesNotContain(qFile, ids);  // query excluded
            Assert.Equal(a1File, res.Items[0].FileItemId); // identical vector ranks first
            Assert.True(res.Items.First(i => i.FileItemId == a1File).Score > 0.99);
        }

        // 3b. Text-to-image uses the same vectors but its own candidate-quality
        // gate: the 32px a1 sidecar is excluded while image similarity above
        // deliberately kept it. The normal-sized a2 and metadata-less q remain.
        using (var scope = factory.Services.CreateScope())
        {
            var vectors = scope.ServiceProvider.GetRequiredService<PhotoVectorIndexService>();
            var hits = await vectors.SearchByVectorAsync(profileAId, OneHot(0), userA, 10);
            Assert.NotNull(hits);
            Assert.DoesNotContain(hits!, h => h.FileItemId == a1File);
            Assert.Contains(hits!, h => h.FileItemId == a2File);
            Assert.Contains(hits!, h => h.FileItemId == qFile);
            Assert.DoesNotContain(hits!, h => h.FileItemId == b1File);
        }

        // 4. exact-scan fallback (profileB has embeddings but NO vector rows).
        using (var scope = factory.Services.CreateScope())
        {
            var sim = scope.ServiceProvider.GetRequiredService<PhotoSimilarityService>();
            var res = await sim.FindSimilarAsync(userA, qFile, 10, profileKeyOverride: profileB);
            Assert.NotNull(res);
            Assert.True(res!.QueryIndexed);
            Assert.NotEmpty(res.Items);
            Assert.Equal(a1File, res.Items[0].FileItemId);
            Assert.DoesNotContain(res.Items, i => i.FileItemId == b1File); // B not embedded under profileB
        }

        // 5. Discriminator — the vector path is genuinely used, not exact-scan:
        //    mutate profileA's CANONICAL a1 vector to orthogonal (e1). The vector
        //    table still holds the original e0, so the ANN path keeps a1 at ~1.0;
        //    exact-scan would now score a1 ~0. (UPDATE doesn't cascade-delete the
        //    vector row — only DELETE would.)
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            var emb = await db.BlobEmbeddings.SingleAsync(e => e.BlobObjectId == a1Blob && e.ProfileId == profileAId);
            emb.EmbeddingBytes = serializer.Serialize(OneHot(1), 1152);
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var sim = scope.ServiceProvider.GetRequiredService<PhotoSimilarityService>();
            var res = await sim.FindSimilarAsync(userA, qFile, 10, profileKeyOverride: profileA);
            Assert.NotNull(res);
            var a1 = res!.Items.FirstOrDefault(i => i.FileItemId == a1File);
            Assert.NotNull(a1);
            Assert.True(a1!.Score > 0.99,
                $"vector path should keep a1 at ~1.0 (got {a1.Score}); exact-scan over mutated canonical would be ~0.");
        }
    }

    // Regression for the explorer recall bug: the threshold-filtered ANN must
    // return ALL owner photos above the threshold up to the exploration bound —
    // not just the default hnsw.ef_search (40) nearest. Seeds 60 identical
    // (score ~1.0) + 10 orthogonal (score ~0.0) neighbours and proves:
    //   * ANN returns 60 above 0.5 (60 > 40 → recall is not ef_search-capped);
    //   * ANN count == exact full-scan count (pgvector & exact-scan agree);
    //   * lowering the threshold never reduces results and is a superset
    //     (60 @0.80 ⊆ 60 @0.50 ⊆ 70 @0.00), exercised through the keyset cursor.
    [SkippableFact]
    public async Task Explorer_Recall_Above_Threshold_Beyond_Default_EfSearch()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var profileKey = $"pgv-rec-{suffix}";
        var settings = new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ImageEmbeddingsEnabled"] = "true",
            // Make this the ACTIVE profile so the explorer page path uses it.
            ["Ai:PhotoSimilarityProfileKey"] = profileKey,
        };
        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, settings);

        const int near = 60; // identical → ~1.0, well beyond the default ef_search (40)
        const int far = 10;  // orthogonal → ~0.0
        var query = OneHot(0);
        Guid userId, qFile, profileId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

            userId = (await users.CreateAsync($"rec-{suffix}@example.com", "R")).Id;
            var model = AddModel(db, $"det1152-{suffix}");
            var profile = AddProfile(db, profileKey, model.Id);
            profileId = profile.Id;

            var qBlob = AddBlob(db, suffix, "q");
            qFile = AddFile(db, userId, qBlob, "q.png");
            AddEmbedding(db, serializer, qBlob, profile.Id, OneHot(0));

            for (var i = 0; i < near; i++)
            {
                var b = AddBlob(db, suffix, $"n{i}");
                AddFile(db, userId, b, $"n{i}.png");
                AddEmbedding(db, serializer, b, profile.Id, OneHot(0)); // identical
            }
            for (var i = 0; i < far; i++)
            {
                var b = AddBlob(db, suffix, $"f{i}");
                AddFile(db, userId, b, $"f{i}.png");
                AddEmbedding(db, serializer, b, profile.Id, OneHot(1)); // orthogonal
            }
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var vectors = scope.ServiceProvider.GetRequiredService<PhotoVectorIndexService>();
            var reg = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
            var p = await reg.GetProfileByKeyAsync(profileKey);
            var r = await vectors.SyncProfileAsync(p!, limit: null, dryRun: false);
            Assert.Equal(near + far + 1, r.VectorIndexed); // query + near + far
        }

        using (var scope = factory.Services.CreateScope())
        {
            var vectors = scope.ServiceProvider.GetRequiredService<PhotoVectorIndexService>();

            // ANN recall beyond the default ef_search: all `near` above 0.5.
            var ann = await vectors.SearchTopAsync(
                profileId, query, userId, qFile, 0.5, PhotoVectorIndexService.MaxEfSearch);
            Assert.NotNull(ann);
            Assert.Equal(near, ann!.Count);

            // Exact full-scan ground truth agrees with the ANN-returned count.
            Assert.Equal((long)near,
                await vectors.CountAboveThresholdAsync(profileId, query, userId, qFile, 0.5));
            Assert.Equal((long)(near + far),
                await vectors.CountAboveThresholdAsync(profileId, query, userId, qFile, 0.0));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var sim = scope.ServiceProvider.GetRequiredService<PhotoSimilarityService>();

            // Explorer page path (active profile), paged via the keyset cursor.
            var ids80 = await CollectPageIdsAsync(sim, userId, qFile, 0.80);
            var ids50 = await CollectPageIdsAsync(sim, userId, qFile, 0.50);
            var ids00 = await CollectPageIdsAsync(sim, userId, qFile, 0.00);

            Assert.Equal(near, ids50.Count);          // recall not capped at 40
            Assert.Equal(near + far, ids00.Count);    // lower threshold adds the far set
            Assert.True(ids80.Count <= ids50.Count);  // monotonic
            Assert.True(ids80.ToHashSet().IsSubsetOf(ids50.ToHashSet())); // 0.80 ⊆ 0.50
            Assert.True(ids50.ToHashSet().IsSubsetOf(ids00.ToHashSet())); // 0.50 ⊆ 0.00
            Assert.DoesNotContain(qFile, ids00);      // source excluded
            Assert.Equal(ids00.Count, ids00.Distinct().Count()); // no dupes across pages
        }
    }

    // Regression: SearchWithinCandidatesAsync must be an EXACT scan restricted to
    // the candidate id set — never a global HNSW walk post-filtered by it. With
    // the bare `embedding <=> q` ordering the planner could pick the HNSW index,
    // whose default ef_search (40) caps the walked neighbours BEFORE the
    // candidate filter: in the field a selective candidate set collapsed semantic
    // search to a single arbitrary hit. Seeds 3× the default ef_search and proves
    // every candidate is ranked (count == candidates, near before far), and that
    // non-candidates never leak in.
    [SkippableFact]
    public async Task Candidate_Restricted_Ranking_Returns_All_Candidates_Beyond_Default_EfSearch()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var profileKey = $"pgv-cand-{suffix}";
        var settings = new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ImageEmbeddingsEnabled"] = "true",
            ["Ai:PhotoSimilarityProfileKey"] = profileKey,
        };
        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, settings);

        const int near = 100; // ~1.0 vs the query, well beyond the default ef_search (40)
        const int far = 20;   // orthogonal → ~0.0
        var query = OneHot(0);
        Guid userId, profileId;
        var nearFiles = new List<Guid>(near);
        var farFiles = new List<Guid>(far);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

            userId = (await users.CreateAsync($"cand-{suffix}@example.com", "C")).Id;
            var model = AddModel(db, $"det1152c-{suffix}");
            var profile = AddProfile(db, profileKey, model.Id);
            profileId = profile.Id;

            for (var i = 0; i < near; i++)
            {
                var b = AddBlob(db, suffix, $"cn{i}");
                nearFiles.Add(AddFile(db, userId, b, $"cn{i}.png"));
                AddEmbedding(db, serializer, b, profile.Id, OneHot(0)); // identical
            }
            for (var i = 0; i < far; i++)
            {
                var b = AddBlob(db, suffix, $"cf{i}");
                farFiles.Add(AddFile(db, userId, b, $"cf{i}.png"));
                AddEmbedding(db, serializer, b, profile.Id, OneHot(1)); // orthogonal
            }
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var vectors = scope.ServiceProvider.GetRequiredService<PhotoVectorIndexService>();
            var reg = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
            var p = await reg.GetProfileByKeyAsync(profileKey);
            var r = await vectors.SyncProfileAsync(p!, limit: null, dryRun: false);
            Assert.Equal(near + far, r.VectorIndexed);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var vectors = scope.ServiceProvider.GetRequiredService<PhotoVectorIndexService>();

            // Candidate set: 80 near + all far, EXCLUDING 20 near files. Every
            // candidate must come back (100 > default ef_search 40), the excluded
            // near files must never appear, and all near rank before all far.
            var candidates = nearFiles.Take(80).Concat(farFiles).ToArray();
            var excluded = nearFiles.Skip(80).ToHashSet();

            var hits = await vectors.SearchWithinCandidatesAsync(
                profileId, query, userId, candidates, take: 200);

            Assert.NotNull(hits);
            Assert.Equal(candidates.Length, hits!.Count); // 100: NOT capped at ef_search
            Assert.DoesNotContain(hits, h => excluded.Contains(h.FileItemId));
            var ordered = hits.OrderByDescending(h => h.Score).Select(h => h.FileItemId).ToList();
            Assert.Equal(hits.Select(h => h.FileItemId).ToList(), ordered); // returned in score order
            var nearSet = nearFiles.Take(80).ToHashSet();
            Assert.All(hits.Take(80), h => Assert.Contains(h.FileItemId, nearSet));  // near first
            Assert.All(hits.Skip(80), h => Assert.Contains(h.FileItemId, farFiles)); // far last
        }
    }

    private static async Task<List<Guid>> CollectPageIdsAsync(
        PhotoSimilarityService sim, Guid owner, Guid file, double minSim)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 100; guard++)
        {
            var page = await sim.FindSimilarPageAsync(owner, file, minSim, 25, cursor);
            Assert.NotNull(page);
            Assert.True(page!.ProfileAvailable);
            Assert.True(page.QueryIndexed);
            ids.AddRange(page.Items.Select(i => i.FileItemId));
            if (!page.HasMore || page.NextCursor is null)
            {
                break;
            }
            cursor = page.NextCursor;
        }
        return ids;
    }

    [SkippableFact]
    public async Task VectorSync_Rejects_Wrong_Dimension_And_NonFinite()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        var settings = new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ImageEmbeddingsEnabled"] = "true",
        };
        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, settings);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var profileC = $"pgv-c-{suffix}";
        Guid userC, okBlob, badDimBlob, nanBlob;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

            userC = (await users.CreateAsync($"c-{suffix}@example.com", "C")).Id;
            var model = AddModel(db, $"det1152c-{suffix}");
            var pC = AddProfile(db, profileC, model.Id);

            okBlob = AddBlob(db, suffix, "ok");
            badDimBlob = AddBlob(db, suffix, "bad");
            nanBlob = AddBlob(db, suffix, "nan");
            AddFile(db, userC, okBlob, "ok.png");
            AddFile(db, userC, badDimBlob, "bad.png");
            AddFile(db, userC, nanBlob, "nan.png");

            // Good 1152 vector.
            AddEmbedding(db, serializer, okBlob, pC.Id, OneHot(0));
            // Wrong dimension (10) — must be skipped, never truncated/padded.
            db.BlobEmbeddings.Add(new BlobEmbedding
            {
                Id = Guid.NewGuid(),
                BlobObjectId = badDimBlob,
                ProfileId = pC.Id,
                EmbeddingBytes = serializer.Serialize(new float[10] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
                Dimension = 10,
                CreatedAt = DateTime.UtcNow,
            });
            // 1152-dim with a NaN component — must be counted failed (bytes crafted
            // directly because the serializer rejects NaN on the way in).
            db.BlobEmbeddings.Add(new BlobEmbedding
            {
                Id = Guid.NewGuid(),
                BlobObjectId = nanBlob,
                ProfileId = pC.Id,
                EmbeddingBytes = NanBytes(1152),
                Dimension = 1152,
                CreatedAt = DateTime.UtcNow,
            });

            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var vectors = scope.ServiceProvider.GetRequiredService<PhotoVectorIndexService>();
            var reg = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
            var pC = await reg.GetProfileByKeyAsync(profileC);
            var r = await vectors.SyncProfileAsync(pC!, limit: null, dryRun: false);

            Assert.True(r.Available);
            Assert.Equal(3, r.EligibleEmbeddings);
            Assert.Equal(1, r.Synced);                    // only the valid 1152 vector
            Assert.Equal(1, r.SkippedDimensionMismatch);  // the 10-dim row
            Assert.Equal(1, r.Failed);                    // the NaN row
            Assert.Equal(1, r.VectorIndexed);
        }
    }

    [SkippableFact]
    public async Task TryUpsert_Indexes_Idempotently_Validates_And_Is_Profile_Scoped()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        var settings = new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ImageEmbeddingsEnabled"] = "true",
        };
        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, settings);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var profileA = $"upsert-a-{suffix}";
        var profileB = $"upsert-b-{suffix}";
        Guid e1Id, blob1, profileAId, profileBId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();

            var user = (await users.CreateAsync($"u-{suffix}@example.com", "U")).Id;
            var model = AddModel(db, $"det1152u-{suffix}");
            var pA = AddProfile(db, profileA, model.Id);
            var pB = AddProfile(db, profileB, model.Id);
            profileAId = pA.Id;
            profileBId = pB.Id;
            blob1 = AddBlob(db, suffix, "b1");
            AddFile(db, user, blob1, "b1.png");
            var e1 = new BlobEmbedding
            {
                Id = Guid.NewGuid(),
                BlobObjectId = blob1,
                ProfileId = pA.Id,
                EmbeddingBytes = serializer.Serialize(OneHot(0), 1152),
                Dimension = 1152,
                CreatedAt = DateTime.UtcNow,
            };
            db.BlobEmbeddings.Add(e1);
            await db.SaveChangesAsync();
            e1Id = e1.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var vectors = scope.ServiceProvider.GetRequiredService<PhotoVectorIndexService>();

            // Indexed, then idempotent (ON CONFLICT DO NOTHING).
            Assert.Equal(VectorUpsertOutcome.Indexed,
                await vectors.TryUpsertEmbeddingVectorAsync(e1Id, blob1, profileAId, OneHot(0), 1152));
            Assert.Equal(VectorUpsertOutcome.Indexed,
                await vectors.TryUpsertEmbeddingVectorAsync(e1Id, blob1, profileAId, OneHot(0), 1152));
            Assert.Equal(1L, await vectors.CountIndexedAsync(profileAId));

            // Non-finite (zero-norm) rejected — validated before any insert.
            Assert.Equal(VectorUpsertOutcome.Failed,
                await vectors.TryUpsertEmbeddingVectorAsync(Guid.NewGuid(), blob1, profileAId, new float[1152], 1152));
            // Unsupported dimension skipped (no 32-dim table).
            Assert.Equal(VectorUpsertOutcome.SkippedUnsupported,
                await vectors.TryUpsertEmbeddingVectorAsync(Guid.NewGuid(), blob1, profileAId, new float[32], 32));

            // Profile-scoped: profileB was never touched.
            Assert.Equal(0L, await vectors.CountIndexedAsync(profileBId));
        }
    }

    [SkippableFact]
    public async Task Backfill_AutoUpserts_1152_Vectors_While_Writing_Canonical()
    {
        Skip.IfNot(_fixture.Available, "Docker / pgvector image not available.");

        var settings = new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ImageEmbeddingsEnabled"] = "true",
        };
        await using var factory = new PostgresWebApplicationFactory(_fixture.ConnectionString!, settings);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var profileKey = $"bf-{suffix}";
        Guid profileId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var user = (await users.CreateAsync($"bf-{suffix}@example.com", "BF")).Id;
            var model = AddModel(db, $"det1152bf-{suffix}");
            profileId = AddProfile(db, profileKey, model.Id).Id;
            for (var i = 0; i < 3; i++)
            {
                var blob = AddBlob(db, suffix, $"b{i}");
                AddImageMetadata(db, blob);
                AddFile(db, user, blob, $"b{i}.png");
            }
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            var reg = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
            var vectors = new PhotoVectorIndexService(db, serializer, TimeProvider.System);
            var backfill = new PhotoEmbeddingBackfillService(
                db, new StubBlobService(), serializer, vectors, TimeProvider.System);

            var profile = await reg.GetProfileByKeyAsync(profileKey);
            var result = await backfill.RunAsync(
                new DeterministicAiBackend(), profile!, new PhotoEmbeddingBackfillOptions());

            // Canonical rows AND their pgvector rows are written in the same pass.
            Assert.Equal(3, result.Indexed);
            Assert.Equal(3, result.VectorIndexed);
            Assert.Equal(0, result.VectorDeferred);
            Assert.Equal(3, await db.BlobEmbeddings.CountAsync(e => e.ProfileId == profileId));
            Assert.Equal(3L, await vectors.CountIndexedAsync(profileId));
        }
    }

    // ---- direct-insert helpers ----------------------------------------------

    // Minimal IBlobService for the backfill auto-upsert test: returns fixed bytes
    // (the deterministic embedder only hashes them — no real decode). Everything
    // else is unused.
    private sealed class StubBlobService : IBlobService
    {
        public Task<Stream> OpenContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));

        public Task<BlobObject> StoreAsync(Stream content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<BlobStoreResult> StoreMeasuredAsync(Stream content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<BlobObject> StoreDerivedAsync(Stream content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<Stream?> OpenDerivedContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task ReleaseAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task MarkPurgeEligibleIfUnreferencedAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<BlobObject> AcquireExistingAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<bool> TryRestoreDerivedFromOriginalAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static void AddImageMetadata(
        AppDbContext db,
        Guid blobId,
        int? width = null,
        int? height = null)
    {
        db.BlobMetadata.Add(new BlobMetadata
        {
            Id = Guid.NewGuid(),
            BlobObjectId = blobId,
            SizeBytes = 1,
            DetectedContentType = "image/png",
            MediaCategory = MediaCategories.Image,
            Width = width,
            Height = height,
            PixelCount = width is int w && height is int h ? (long)w * h : null,
        });
    }

    private static AiModel AddModel(AppDbContext db, string key)
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = key,
            Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Image,
            Version = 1,
            Dimension = 1152,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        return model;
    }

    private static AiProfile AddProfile(AppDbContext db, string key, Guid modelId)
    {
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = key,
            AiModelId = modelId,
            Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Image,
            Dimension = 1152,
            DistanceMetric = AiDistanceMetrics.Cosine,
            IsDefault = false,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.AiProfiles.Add(profile);
        return profile;
    }

    private static Guid AddBlob(AppDbContext db, string suffix, string tag)
    {
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = $"{suffix}-{tag}-{Guid.NewGuid():N}",
            SizeBytes = 1,
            StorageKey = $"sk/{suffix}/{tag}/{Guid.NewGuid():N}",
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.BlobObjects.Add(blob);
        return blob.Id;
    }

    private static Guid AddFile(AppDbContext db, Guid ownerId, Guid blobId, string name)
    {
        var file = new FileItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerId,
            BlobObjectId = blobId,
            Name = name,
            MimeType = "image/png",
            SizeBytes = 1,
            CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
        };
        db.FileItems.Add(file);
        return file.Id;
    }

    private static void AddEmbedding(
        AppDbContext db, IAiVectorSerializer serializer, Guid blobId, Guid profileId, float[] vector)
    {
        db.BlobEmbeddings.Add(new BlobEmbedding
        {
            Id = Guid.NewGuid(),
            BlobObjectId = blobId,
            ProfileId = profileId,
            EmbeddingBytes = serializer.Serialize(vector, 1152),
            Dimension = 1152,
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static float[] OneHot(int index)
    {
        var v = new float[1152];
        v[index] = 1f;
        return v;
    }

    private static byte[] NanBytes(int dimension)
    {
        var arr = new float[dimension];
        arr[0] = 1f;
        arr[5] = float.NaN;
        var bytes = new byte[dimension * sizeof(float)];
        Buffer.BlockCopy(arr, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
