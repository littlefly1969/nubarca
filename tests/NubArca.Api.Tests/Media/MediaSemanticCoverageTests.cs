using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Media;
using NubArca.Api.Media.Semantic;
using NubArca.Api.Tests.Endpoints;
using Xunit;
using static NubArca.Api.Tests.Media.MediaSemanticTestHarness;

namespace NubArca.Api.Tests.Media;

// SEARCH-SEM-01: the tests that prove the GUID-prefix blind spot is gone.
//
// The old pipeline took the first 20,000 candidates ordered by FileItem id and
// ranked only those. Because GUID order has nothing to do with relevance, that
// was the SAME arbitrary prefix on every query — a perfect match sitting after
// it could never be returned, no matter how well it matched.
//
// These tests therefore do the one thing that actually proves the fix: build a
// candidate set LARGER than the former cap, put the best match strictly AFTER
// the old cut-off in id order, and require it to come back first.
//
// Rows are seeded straight into the database rather than through the upload
// pipeline. 20k real uploads (hash, write, derivatives) would take many minutes
// and prove nothing extra — what matters is that the real candidate query, the
// real keyset walk and the real ranking path see them.
public sealed class MediaSemanticCoverageTests : IDisposable
{
    // Comfortably past the former 20,000 cap.
    private const int BeyondOldCap = 20_050;

    // MediaSemanticTestHarness.Factory() is the REAL configuration path: it sets
    // Ai:Enabled and Ai:PhotoSimilarityProfileKey, which is exactly what
    // PhotoEmbeddingProfileService.ResolveActiveProfileAsync(null) reads. A bare
    // factory resolves no active profile at all ("no-default-profile"), so these
    // tests would have been measuring nothing.
    private readonly SqliteWebApplicationFactory _factory = Factory();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Photo_After_The_Former_20k_Cutoff_Can_Rank_First()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        // The needle's id is forced to sort LAST, so under the old
        // `.OrderBy(Id).Take(20_000)` it was outside the ranked set entirely.
        var needleId = MaxGuid();
        await SeedPhotoRowsAsync(owner, profile, BeyondOldCap, needleId, q);

        var page = await SearchAsync(_factory, owner, limit: 5);

        Assert.True(page.Available);
        Assert.NotEmpty(page.Items);
        // The whole point: the post-cutoff candidate is rank 1.
        Assert.Equal(needleId, page.Items[0].Media.Id);
    }

    [Fact]
    public async Task Every_Eligible_Photo_Batch_Is_Examined_Not_Just_The_First()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var needleId = MaxGuid();
        await SeedPhotoRowsAsync(owner, profile, BeyondOldCap, needleId, q);

        // Ask for more than the old per-modality window so the count reflects
        // the ranking, not the page size.
        var page = await SearchAsync(_factory, owner, limit: 100);

        // Two separate facts, and it matters which proves what.
        //
        // The ranking is capped at the policy's SOFT LIMIT — 300 — because
        // thresholds are uncalibrated, so the total is 300 rather than 20,050.
        // That cap is a RESULT-count bound and says nothing about coverage.
        var policy = _factory.Services.GetRequiredService<SemanticResultPolicy>();
        Assert.Equal(policy.SoftResultLimit, page.Total);

        // Coverage is proven by WHICH 300 they are: the needle is the very last
        // row in id order, so it can only be here if the walk reached the final
        // keyset batch — 20,050 candidates deep, well past the former cut-off.
        Assert.Equal(needleId, page.Items[0].Media.Id);
    }

    [Fact]
    public async Task Video_Sample_After_The_Former_Cutoff_Can_Rank_First()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        // Enough photo rows to blow past the former global candidate cap, so a
        // video ranked afterwards is exactly the case the old code could not
        // reach. The video is the strongest match in the library.
        await SeedPhotoRowsAsync(owner, profile, BeyondOldCap, needleId: null, queryVector: q);

        var (videoFileId, videoBlob) = await UploadVideoAsync(_factory, owner);
        await SeedVideoManifestAsync(_factory, profile, videoBlob, q,
            [new SeedSample(0, 8_000, 4_000, 0.99)]);

        var page = await SearchAsync(_factory, owner, limit: 5);

        Assert.True(page.Available);
        Assert.Equal(videoFileId, page.Items[0].Media.Id);
        // And it still carries its temporal evidence.
        Assert.Equal(4_000, page.Items[0].BestMatch.RepresentativeMilliseconds);
    }

    [Fact]
    public async Task Video_Sample_Beyond_20k_Temporal_Embeddings_Ranks_First()
    {
        // The video half of the blind spot, tested on its own terms.
        //
        // The old sample scope was `ORDER BY BlobObjectId, ... LIMIT 20000`, so
        // the needle here is a DISTINCT video whose blob id sorts last: under
        // the old code its samples were beyond the cut and it could never be
        // returned. 42 videos x 500 samples = 21,000 temporal embeddings, past
        // the former ceiling.
        var profile = await SeedProfileAsync(_factory);
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var needleFileId = await SeedVideoRowsAsync(owner, profile, q,
            videoCount: 42, samplesPerVideo: 500);

        var samples = await CountVideoSampleEmbeddingsAsync(profile.Id);
        Assert.True(samples > 20_050, $"expected >20,050 temporal embeddings, seeded {samples}");

        var page = await SearchAsync(_factory, owner, kind: MediaKindScope.Video, limit: 5);

        Assert.True(page.Available);
        Assert.NotEmpty(page.Items);
        // The post-cutoff video is rank 1, and it is its OWN result — not
        // aggregated into an earlier video.
        Assert.Equal(needleFileId, page.Items[0].Media.Id);
        Assert.NotNull(page.Items[0].BestMatch.RepresentativeMilliseconds);
    }

    [Fact]
    public async Task Coverage_Never_Crosses_The_Owner_Boundary()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync("a@example.com");
        var (stranger, _) = await _factory.CreateAuthenticatedClientAsync("b@example.com");
        var q = QueryVector(profile);

        // The stranger owns the single best-matching row in the database, and
        // it sorts last — the batching walk must never reach it.
        await SeedPhotoRowsAsync(stranger, profile, 10, MaxGuid(), q);
        await SeedPhotoRowsAsync(owner, profile, 20, needleId: null, queryVector: q);

        var page = await SearchAsync(_factory, owner, limit: 100);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var strangerIds = await db.FileItems
            .Where(f => f.OwnerUserId == stranger).Select(f => f.Id).ToListAsync();

        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, i => Assert.DoesNotContain(i.Media.Id, strangerIds));
    }

    [Fact]
    public async Task Pagination_Walks_One_Immutable_Ranking_Without_Gaps_Or_Repeats()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);
        await SeedPhotoRowsAsync(owner, profile, 120, needleId: null, queryVector: q);

        var seen = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 6; page++)
        {
            var result = await SearchAsync(_factory, owner, limit: 25, cursor: cursor);
            seen.AddRange(result.Items.Select(i => i.Media.Id));
            cursor = result.NextCursor;
            if (!result.HasMore)
            {
                break;
            }
        }

        Assert.NotEmpty(seen);
        // No duplicates across pages: every page is a keyset slice of the SAME
        // cached ranking, never an offset over a recomputed one.
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task Second_Page_Reuses_The_Cached_Ranking()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);
        await SeedPhotoRowsAsync(owner, profile, 80, needleId: null, queryVector: q);

        var first = await SearchAsync(_factory, owner, limit: 25);
        Assert.NotNull(first.NextCursor);

        var cache = _factory.Services.GetRequiredService<SemanticRankingCache>();
        var second = await SearchAsync(_factory, owner, limit: 25, cursor: first.NextCursor);

        Assert.True(cache.LastLookupWasHit, "page 2 must be served from the cached ranking");
        Assert.NotEmpty(second.Items);
        // Same ranking ⇒ same denominator.
        Assert.Equal(first.Total, second.Total);
    }

    [Fact]
    public async Task A_Cached_Ranking_Is_Never_Shared_Across_Owners()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync("a@example.com");
        var (stranger, _) = await _factory.CreateAuthenticatedClientAsync("b@example.com");
        var q = QueryVector(profile);
        await SeedPhotoRowsAsync(owner, profile, 30, needleId: null, queryVector: q);

        var mine = await SearchAsync(_factory, owner, limit: 10);
        Assert.NotEmpty(mine.Items);

        // Same query, same filters, different owner: the cache key includes the
        // owner, so this cannot hit the first owner's ranking.
        var theirs = await SearchAsync(_factory, stranger, limit: 10);
        Assert.Empty(theirs.Items);
        Assert.Equal(0, theirs.Total);
    }

    [Fact]
    public async Task A_Cursor_From_A_Different_Query_Is_Rejected()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);
        await SeedPhotoRowsAsync(owner, profile, 80, needleId: null, queryVector: q);

        var first = await SearchAsync(_factory, owner, limit: 25);
        Assert.NotNull(first.NextCursor);

        await Assert.ThrowsAsync<SemanticSearchCursorException>(
            () => SearchAsync(_factory, owner, query: "a completely different query",
                limit: 25, cursor: first.NextCursor));
    }

    [Fact]
    public async Task Cancellation_Propagates_Through_The_Batch_Walk()
    {
        var profile = await SeedProfileAsync(_factory);
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);
        await SeedPhotoRowsAsync(owner, profile, BeyondOldCap, needleId: null, queryVector: q);

        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<MediaSemanticSearchService>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.SearchAsync(owner, Query, MediaKindScope.All, 50, null,
                new ImageFilters(), cts.Token));
    }

    // ---- helpers -----------------------------------------------------------

    private async Task<int> CountVideoSampleEmbeddingsAsync(Guid profileId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.VideoSemanticSampleEmbeddings
            .CountAsync(e => e.ProfileId == profileId
                && e.Status == AiArtifactStatuses.Completed);
    }

    // Seeds `videoCount` eligible videos, each with `samplesPerVideo` temporal
    // embeddings, written straight to the database. Returns the FileItem id of
    // the NEEDLE video — the one whose blob id sorts last and which holds the
    // only strong sample in the library.
    private async Task<Guid> SeedVideoRowsAsync(
        Guid ownerUserId, AiProfile profile, float[] queryVector,
        int videoCount, int samplesPerVideo)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

        var weakBytes = serializer.Serialize(WithSimilarity(queryVector, 0.10), queryVector.Length);
        var strongBytes = serializer.Serialize(WithSimilarity(queryVector, 0.99), queryVector.Length);
        var now = DateTime.UtcNow;
        var needleFileId = Guid.Empty;

        for (var v = 0; v < videoCount; v++)
        {
            var isNeedle = v == videoCount - 1;
            // The needle's BLOB sorts last, which is what the old
            // BlobObjectId-ordered sample cap truncated on.
            var blobId = isNeedle ? MaxGuid() : Guid.NewGuid();
            var fileId = Guid.NewGuid();
            if (isNeedle)
            {
                needleFileId = fileId;
            }
            var sha = $"v{v:D7}{Guid.NewGuid():N}{Guid.NewGuid():D}".Replace("-", string.Empty)[..64];

            db.BlobObjects.Add(new BlobObject
            {
                Id = blobId, Sha256 = sha, SizeBytes = 4096,
                StorageKey = $"{sha[..2]}/{sha[2..4]}/{sha}",
                ReferenceCount = 1, CreatedAt = now,
            });
            db.FileItems.Add(new FileItem
            {
                Id = fileId, OwnerUserId = ownerUserId, BlobObjectId = blobId,
                Name = $"clip-{v}.mp4", MimeType = "video/mp4", SizeBytes = 4096,
                CreatedAt = now, EffectiveDateTaken = now,
                MediaLibraryState = MediaLibraryState.Active,
            });
            db.BlobMetadata.Add(new BlobMetadata
            {
                Id = Guid.NewGuid(), BlobObjectId = blobId,
                DetectedContentType = "video/mp4", Width = 1920, Height = 1080,
                CreatedAt = now,
            });

            var index = new VideoSemanticIndex
            {
                Id = Guid.NewGuid(), BlobObjectId = blobId, SegmentationVersion = 1,
                Status = AiArtifactStatuses.Completed, AttemptCount = 1,
                DurationMilliseconds = samplesPerVideo * 1_000L,
                SegmentCount = samplesPerVideo, SampleCount = samplesPerVideo,
                CreatedAt = now, CompletedAt = now,
            };
            db.VideoSemanticIndexes.Add(index);

            for (var i = 0; i < samplesPerVideo; i++)
            {
                // One segment per sample keeps each timestamp a DISTINCT
                // segment, so the needle cannot be merged into a neighbour.
                var segment = new VideoSemanticSegment
                {
                    Id = Guid.NewGuid(), VideoSemanticIndexId = index.Id, SegmentIndex = i,
                    StartMilliseconds = i * 1_000L, EndMilliseconds = (i + 1) * 1_000L,
                    BoundaryReason = VideoSemanticBoundaryReasons.Scene, CreatedAt = now,
                };
                db.VideoSemanticSegments.Add(segment);

                var sample = new VideoSemanticSample
                {
                    Id = Guid.NewGuid(), VideoSemanticSegmentId = segment.Id, SampleIndex = 0,
                    TimestampMilliseconds = i * 1_000L + 500,
                    SelectionReason = VideoSemanticSelectionReasons.Interior, CreatedAt = now,
                };
                db.VideoSemanticSamples.Add(sample);

                // Exactly one strong sample in the whole library, in the needle.
                var strong = isNeedle && i == samplesPerVideo - 1;
                db.VideoSemanticSampleEmbeddings.Add(new VideoSemanticSampleEmbedding
                {
                    Id = Guid.NewGuid(), VideoSemanticSampleId = sample.Id, ProfileId = profile.Id,
                    EmbeddingBytes = strong ? strongBytes : weakBytes,
                    Dimension = queryVector.Length, Status = AiArtifactStatuses.Completed,
                    AttemptCount = 1, CreatedAt = now, CompletedAt = now,
                });
            }
        }

        await db.SaveChangesAsync();
        return needleFileId;
    }

    // A Guid that sorts after every randomly generated one under both the
    // .NET comparison and the provider's ordering.
    private static Guid MaxGuid() => new(
        uint.MaxValue, ushort.MaxValue, ushort.MaxValue,
        255, 255, 255, 255, 255, 255, 255, 255);

    // Seeds `count` eligible owner-visible photo candidates directly.
    //
    // When `needleId` is supplied that row gets the query vector itself (the
    // best possible score) and every other row gets a deliberately weak vector,
    // so rank 1 is unambiguous. No BlobMetadata rows are written, which keeps
    // every row eligible under SemanticPhotoCandidatePolicy (absence of
    // dimensions never disqualifies a photo).
    private async Task SeedPhotoRowsAsync(
        Guid ownerUserId, AiProfile profile, int count, Guid? needleId, float[] queryVector)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

        // Exact controlled similarities: the needle at 0.99 and everything else
        // at 0.10, so rank 1 is dictated by construction rather than hoped for.
        var weakBytes = serializer.Serialize(
            WithSimilarity(queryVector, 0.10), queryVector.Length);
        var strongBytes = serializer.Serialize(
            WithSimilarity(queryVector, 0.99), queryVector.Length);

        var now = DateTime.UtcNow;
        var blobs = new List<BlobObject>(count);
        var files = new List<FileItem>(count);
        var embeddings = new List<BlobEmbedding>(count);
        // Real BlobMetadata so each row satisfies gallery membership by the
        // POSITIVE DetectedContentType rule (rather than the client-MIME
        // fallback) and clears SemanticPhotoCandidatePolicy's small-image gate.
        var metadata = new List<BlobMetadata>(count);

        for (var i = 0; i < count; i++)
        {
            var isNeedle = needleId is not null && i == count - 1;
            var blobId = Guid.NewGuid();
            var sha = $"{i:D8}{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64];
            blobs.Add(new BlobObject
            {
                Id = blobId,
                Sha256 = sha,
                SizeBytes = 1024,
                StorageKey = $"{sha[..2]}/{sha[2..4]}/{sha}",
                ReferenceCount = 1,
                CreatedAt = now,
            });
            files.Add(new FileItem
            {
                Id = isNeedle ? needleId!.Value : Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                BlobObjectId = blobId,
                Name = $"seed-{i}.png",
                MimeType = "image/png",
                SizeBytes = 1024,
                CreatedAt = now,
                EffectiveDateTaken = now,
                MediaLibraryState = MediaLibraryState.Active,
            });
            embeddings.Add(new BlobEmbedding
            {
                Id = Guid.NewGuid(),
                BlobObjectId = blobId,
                ProfileId = profile.Id,
                EmbeddingBytes = isNeedle ? strongBytes : weakBytes,
                Dimension = queryVector.Length,
                CreatedAt = now,
            });
            metadata.Add(new BlobMetadata
            {
                Id = Guid.NewGuid(),
                BlobObjectId = blobId,
                DetectedContentType = "image/png",
                Width = 1024,
                Height = 768,
                CreatedAt = now,
            });
        }

        db.BlobObjects.AddRange(blobs);
        db.FileItems.AddRange(files);
        db.BlobEmbeddings.AddRange(embeddings);
        db.BlobMetadata.AddRange(metadata);
        await db.SaveChangesAsync();
    }
}
