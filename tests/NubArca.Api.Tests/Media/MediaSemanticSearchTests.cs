using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Ai.Video;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Media.Semantic;
using Xunit;
using static NubArca.Api.Tests.Media.MediaSemanticTestHarness;

namespace NubArca.Api.Tests.Media;

// VSEM-03: ranking, temporal evidence and pagination of the unified semantic
// search. SQLite host with the deterministic backend → both vector layers run
// their exact fallbacks; scores are controlled cosine similarities against the
// deterministic text-tower vector, so every ordering assertion is exact.
public sealed class MediaSemanticSearchTests
{
    // ---- modality selection ------------------------------------------------

    [Fact]
    public async Task Image_Kind_Returns_Photos_Only()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (photoId, photoBlob) = await UploadPhotoAsync(factory, owner, 40);
        await SeedPhotoEmbeddingAsync(factory, profile, photoBlob, WithSimilarity(q, 0.6));
        var (videoId, videoBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, profile, videoBlob, q,
            [new SeedSample(0, 8000, 4000, 0.9)]);

        var page = await SearchAsync(factory, owner, kind: MediaKindScope.Image);

        var item = Assert.Single(page.Items);
        Assert.Equal(photoId, item.Media.Id);
        Assert.Equal("image", item.Media.Kind);
        Assert.DoesNotContain(page.Items, i => i.Media.Id == videoId);
    }

    [Fact]
    public async Task Video_Kind_Returns_Videos_Only()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (_, photoBlob) = await UploadPhotoAsync(factory, owner, 40);
        await SeedPhotoEmbeddingAsync(factory, profile, photoBlob, WithSimilarity(q, 0.9));
        var (videoId, videoBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, profile, videoBlob, q,
            [new SeedSample(0, 8000, 4000, 0.6)]);

        var page = await SearchAsync(factory, owner, kind: MediaKindScope.Video);

        var item = Assert.Single(page.Items);
        Assert.Equal(videoId, item.Media.Id);
        Assert.Equal("video", item.Media.Kind);
    }

    // ---- mixed ranking -----------------------------------------------------

    [Fact]
    public async Task Mixed_Results_Merge_By_Comparable_Score_With_No_Modality_Boost()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (photoHigh, blobHigh) = await UploadPhotoAsync(factory, owner, 40);
        await SeedPhotoEmbeddingAsync(factory, profile, blobHigh, WithSimilarity(q, 0.95));
        var (videoMid, videoBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, profile, videoBlob, q,
            [new SeedSample(0, 8000, 4000, 0.85)]);
        var (photoLow, blobLow) = await UploadPhotoAsync(factory, owner, 80);
        await SeedPhotoEmbeddingAsync(factory, profile, blobLow, WithSimilarity(q, 0.55));

        var page = await SearchAsync(factory, owner);

        Assert.Equal(
            new[] { photoHigh, videoMid, photoLow },
            page.Items.Select(i => i.Media.Id).ToArray());
        Assert.Equal(new[] { "image", "video", "image" }, page.Items.Select(i => i.Media.Kind));
        Assert.Equal(3, page.Total);
    }

    [Fact]
    public async Task The_Query_Embedding_Is_Calculated_Exactly_Once()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (_, photoBlob) = await UploadPhotoAsync(factory, owner, 40);
        await SeedPhotoEmbeddingAsync(factory, profile, photoBlob, WithSimilarity(q, 0.9));
        var (_, videoBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, profile, videoBlob, q,
            [new SeedSample(0, 8000, 4000, 0.8)]);

        using var scope = factory.Services.CreateScope();
        var counting = new CountingTextEmbedder(new DeterministicAiBackend());
        var service = new MediaSemanticSearchService(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<IFileItemService>(),
            scope.ServiceProvider.GetRequiredService<SemanticMediaCandidateService>(),
            scope.ServiceProvider.GetRequiredService<PhotoEmbeddingProfileService>(),
            new SingleBackendResolver(counting),
            scope.ServiceProvider.GetRequiredService<PhotoVectorIndexService>(),
            scope.ServiceProvider.GetRequiredService<VideoSemanticSampleVectorIndexService>(),
            scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>(),
            scope.ServiceProvider.GetRequiredService<IOptions<VideoSemanticSegmentationOptions>>(),
            scope.ServiceProvider.GetRequiredService<SemanticResultPolicy>(),
            scope.ServiceProvider.GetRequiredService<SemanticRankingCache>(),
            NullLogger<MediaSemanticSearchService>.Instance);

        var page = await service.SearchAsync(
            owner, Query, MediaKindScope.All, 50, null, new ImageFilters());

        Assert.Equal(2, page.Items.Count);   // both modalities were ranked…
        Assert.Equal(1, counting.Calls);     // …from ONE text embedding
    }

    [Fact]
    public async Task Embeddings_Of_A_Different_Profile_Never_Participate()
    {
        using var factory = Factory();
        var active = await SeedProfileAsync(factory);
        var other = await SeedProfileAsync(factory, key: "other-profile-1152");
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(active);

        var (_, photoBlob) = await UploadPhotoAsync(factory, owner, 40);
        await SeedPhotoEmbeddingAsync(factory, other, photoBlob, WithSimilarity(q, 0.95));
        var (_, videoBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, other, videoBlob, q,
            [new SeedSample(0, 8000, 4000, 0.95)]);

        var page = await SearchAsync(factory, owner);

        Assert.Empty(page.Items);   // profile isolation across BOTH modalities
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task Equal_Scores_Order_Deterministically_By_Id()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (photoId, photoBlob) = await UploadPhotoAsync(factory, owner, 40);
        await SeedPhotoEmbeddingAsync(factory, profile, photoBlob, WithSimilarity(q, 0.8));
        var (videoId, videoBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, profile, videoBlob, q,
            [new SeedSample(0, 8000, 4000, 0.8)]);

        var first = await SearchAsync(factory, owner);
        var second = await SearchAsync(factory, owner);

        var expected = new[] { photoId, videoId }.OrderBy(id => id).ToArray();
        Assert.Equal(expected, first.Items.Select(i => i.Media.Id).ToArray());
        Assert.Equal(expected, second.Items.Select(i => i.Media.Id).ToArray());
    }

    // ---- temporal evidence -------------------------------------------------

    [Fact]
    public async Task Photo_Results_Carry_Null_Temporal_Fields()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (_, photoBlob) = await UploadPhotoAsync(factory, owner, 40);
        await SeedPhotoEmbeddingAsync(factory, profile, photoBlob, WithSimilarity(q, 0.9));

        var page = await SearchAsync(factory, owner);

        var item = Assert.Single(page.Items);
        Assert.Equal("visual", item.BestMatch.EvidenceType);
        Assert.Null(item.BestMatch.StartMilliseconds);
        Assert.Null(item.BestMatch.EndMilliseconds);
        Assert.Null(item.BestMatch.RepresentativeMilliseconds);
        Assert.Empty(item.AdditionalMatches);
    }

    [Fact]
    public async Task Video_Score_Is_The_Best_Sample_And_Representative_Is_Its_Timestamp()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        // Two samples in ONE segment: the weaker one earlier, the stronger later.
        var (videoId, videoBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, profile, videoBlob, q,
        [
            new SeedSample(0, 10_000, 2_000, 0.3),
            new SeedSample(0, 10_000, 7_000, 0.8),
        ]);
        // A photo between the two sample scores pins the video's EFFECTIVE score.
        var (photoId, photoBlob) = await UploadPhotoAsync(factory, owner, 40);
        await SeedPhotoEmbeddingAsync(factory, profile, photoBlob, WithSimilarity(q, 0.5));

        var page = await SearchAsync(factory, owner);

        // Video (0.8, the BEST sample — not 0.3) outranks the 0.5 photo.
        Assert.Equal(new[] { videoId, photoId }, page.Items.Select(i => i.Media.Id).ToArray());
        var video = page.Items[0];
        Assert.Equal(0, video.BestMatch.StartMilliseconds);
        Assert.Equal(10_000, video.BestMatch.EndMilliseconds);
        Assert.Equal(7_000, video.BestMatch.RepresentativeMilliseconds);
        Assert.Empty(video.AdditionalMatches);   // one segment → no extra intervals
    }

    [Fact]
    public async Task Segments_Deduplicate_Intervals_And_Additional_Matches_Are_Capped_At_Three()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        // Five distinct non-overlapping segments, every sample matching.
        var (_, videoBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, profile, videoBlob, q,
        [
            new SeedSample(0, 10_000, 5_000, 0.9),
            new SeedSample(10_000, 20_000, 15_000, 0.8),
            new SeedSample(20_000, 30_000, 25_000, 0.7),
            new SeedSample(30_000, 40_000, 35_000, 0.6),
            new SeedSample(40_000, 50_000, 45_000, 0.5),
        ]);

        var page = await SearchAsync(factory, owner);

        var item = Assert.Single(page.Items);
        Assert.Equal(0, item.BestMatch.StartMilliseconds);
        Assert.Equal(5_000, item.BestMatch.RepresentativeMilliseconds);

        // Best-first, capped at three, all DISTINCT non-overlapping intervals,
        // none equal to the best interval.
        Assert.Equal(3, item.AdditionalMatches.Count);
        Assert.Equal(
            new long?[] { 10_000, 20_000, 30_000 },
            item.AdditionalMatches.Select(m => m.StartMilliseconds).ToArray());
        var intervals = item.AdditionalMatches
            .Prepend(item.BestMatch)
            .Select(m => (m.StartMilliseconds, m.EndMilliseconds))
            .ToList();
        Assert.Equal(intervals.Count, intervals.Distinct().Count());
    }

    [Fact]
    public async Task Multiple_Logical_Files_On_One_Blob_Each_Keep_A_Result_With_The_Same_Evidence()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        // Identical bytes → the SAME blob under two logical FileItems.
        var bytes = NubArca.Api.Tests.Metadata.ImageFixtures.MinimalMp4();
        var (firstId, blobId) = await UploadVideoAsync(factory, owner, bytes, "a.mp4");
        var (secondId, secondBlob) = await UploadVideoAsync(factory, owner, bytes, "b.mp4");
        Assert.Equal(blobId, secondBlob);
        await SeedVideoManifestAsync(factory, profile, blobId, q,
            [new SeedSample(0, 8000, 4000, 0.7)]);

        var page = await SearchAsync(factory, owner);

        Assert.Equal(2, page.Items.Count);   // distinct logical files are NOT collapsed
        Assert.Contains(page.Items, i => i.Media.Id == firstId);
        Assert.Contains(page.Items, i => i.Media.Id == secondId);
        Assert.All(page.Items, i =>
        {
            Assert.Equal(4000, i.BestMatch.RepresentativeMilliseconds);
            Assert.Empty(i.AdditionalMatches);   // evidence deduplicated per item
        });
    }

    // ---- coverage edge cases -----------------------------------------------

    [Fact]
    public async Task A_Video_With_Partial_Sample_Embeddings_Ranks_On_Its_Completed_Samples()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (videoId, videoBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, profile, videoBlob, q,
        [
            new SeedSample(0, 10_000, 5_000, null),        // failed — no embedding
            new SeedSample(10_000, 20_000, 15_000, 0.7),   // completed
        ]);

        var page = await SearchAsync(factory, owner);

        var item = Assert.Single(page.Items);
        Assert.Equal(videoId, item.Media.Id);
        Assert.Equal(15_000, item.BestMatch.RepresentativeMilliseconds);
    }

    [Fact]
    public async Task A_Video_Without_Completed_Embeddings_Is_Absent_But_Photos_Still_Return()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (photoId, photoBlob) = await UploadPhotoAsync(factory, owner, 40);
        await SeedPhotoEmbeddingAsync(factory, profile, photoBlob, WithSimilarity(q, 0.6));
        var (_, videoBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, profile, videoBlob, q,
            [new SeedSample(0, 8000, 4000, null)]);

        var page = await SearchAsync(factory, owner);

        var item = Assert.Single(page.Items);
        Assert.Equal(photoId, item.Media.Id);
    }

    [Fact]
    public async Task Unavailable_Profile_Or_Text_Tower_Reports_Unavailable()
    {
        using var factory = Factory();   // configured key exists but NO profile row
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();

        var page = await SearchAsync(factory, owner);

        Assert.False(page.Available);
        Assert.NotNull(page.UnavailableReason);
        Assert.Empty(page.Items);
    }

    // ---- pagination --------------------------------------------------------

    [Fact]
    public async Task Cursor_Pagination_Is_Stable_Across_Mixed_Results()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var expected = new List<Guid>();
        var sims = new[] { 0.95, 0.85, 0.75, 0.65, 0.55 };
        for (var i = 0; i < sims.Length; i++)
        {
            if (i % 2 == 0)
            {
                var (photoId, blob) = await UploadPhotoAsync(factory, owner, (byte)(40 + i * 20));
                await SeedPhotoEmbeddingAsync(factory, profile, blob, WithSimilarity(q, sims[i]));
                expected.Add(photoId);
            }
            else
            {
                var (videoId, blob) = await UploadVideoAsync(factory, owner);
                await SeedVideoManifestAsync(factory, profile, blob, q,
                    [new SeedSample(0, 8000, 4000, sims[i])]);
                expected.Add(videoId);
            }
        }

        var collected = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await SearchAsync(factory, owner, limit: 2, cursor: cursor);
            collected.AddRange(page.Items.Select(i => i.Media.Id));
            Assert.Equal(5, page.Total);   // stable denominator on every page
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(expected, collected);   // no duplicates, no gaps, exact order
    }

    [Fact]
    public async Task Equal_Scores_At_A_Page_Boundary_Do_Not_Duplicate_Or_Skip()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        // Three photos with the SAME score straddling the page boundary.
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var (id, blob) = await UploadPhotoAsync(factory, owner, (byte)(40 + i * 30));
            await SeedPhotoEmbeddingAsync(factory, profile, blob, WithSimilarity(q, 0.8));
            ids.Add(id);
        }

        var first = await SearchAsync(factory, owner, limit: 2);
        Assert.True(first.HasMore);
        var second = await SearchAsync(factory, owner, limit: 2, cursor: first.NextCursor);

        var collected = first.Items.Concat(second.Items).Select(i => i.Media.Id).ToList();
        Assert.Equal(ids.OrderBy(id => id).ToList(), collected);
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task A_Modality_Exhausting_Mid_Page_Continues_With_The_Other()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (photoId, photoBlob) = await UploadPhotoAsync(factory, owner, 40);
        await SeedPhotoEmbeddingAsync(factory, profile, photoBlob, WithSimilarity(q, 0.9));
        var videoIds = new List<Guid>();
        foreach (var sim in new[] { 0.8, 0.7, 0.6 })
        {
            var (videoId, blob) = await UploadVideoAsync(factory, owner);
            await SeedVideoManifestAsync(factory, profile, blob, q,
                [new SeedSample(0, 8000, 4000, sim)]);
            videoIds.Add(videoId);
        }

        var first = await SearchAsync(factory, owner, limit: 2);
        Assert.Equal(new[] { photoId, videoIds[0] }, first.Items.Select(i => i.Media.Id).ToArray());

        // Photos are exhausted; the second page is videos only.
        var second = await SearchAsync(factory, owner, limit: 2, cursor: first.NextCursor);
        Assert.Equal(new[] { videoIds[1], videoIds[2] }, second.Items.Select(i => i.Media.Id).ToArray());
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task A_Cursor_From_A_Different_Query_Kind_Or_Filter_Set_Is_Rejected()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        for (var i = 0; i < 3; i++)
        {
            var (_, blob) = await UploadPhotoAsync(factory, owner, (byte)(40 + i * 20));
            await SeedPhotoEmbeddingAsync(factory, profile, blob, WithSimilarity(q, 0.9 - i * 0.1));
        }

        var first = await SearchAsync(factory, owner, limit: 1);
        Assert.NotNull(first.NextCursor);

        await Assert.ThrowsAsync<SemanticSearchCursorException>(() =>
            SearchAsync(factory, owner, query: "another query", limit: 1, cursor: first.NextCursor));
        await Assert.ThrowsAsync<SemanticSearchCursorException>(() =>
            SearchAsync(factory, owner, kind: MediaKindScope.Video, limit: 1, cursor: first.NextCursor));
        await Assert.ThrowsAsync<SemanticSearchCursorException>(() =>
            SearchAsync(factory, owner, limit: 1, cursor: first.NextCursor,
                filters: new ImageFilters { Favorite = true }));
        await Assert.ThrowsAsync<SemanticSearchCursorException>(() =>
            SearchAsync(factory, owner, limit: 1, cursor: "garbage"));
    }

    // ---- filters before ranking --------------------------------------------

    [Fact]
    public async Task Physical_Filters_Restrict_Candidates_Before_Vector_Ranking()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        // A globally better NON-favorite decoy and a weaker FAVORITE target,
        // in both modalities.
        var (_, decoyPhotoBlob) = await UploadPhotoAsync(factory, owner, 40);
        await SeedPhotoEmbeddingAsync(factory, profile, decoyPhotoBlob, WithSimilarity(q, 0.99));
        var (favPhoto, favPhotoBlob) = await UploadPhotoAsync(factory, owner, 80);
        await SeedPhotoEmbeddingAsync(factory, profile, favPhotoBlob, WithSimilarity(q, 0.4));
        await SetFavoriteAsync(factory, favPhoto, true);

        var (_, decoyVideoBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, profile, decoyVideoBlob, q,
            [new SeedSample(0, 8000, 4000, 0.98)]);
        var (favVideo, favVideoBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, profile, favVideoBlob, q,
            [new SeedSample(0, 8000, 4000, 0.3)]);
        await SetFavoriteAsync(factory, favVideo, true);

        var page = await SearchAsync(factory, owner, filters: new ImageFilters { Favorite = true });

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(new[] { favPhoto, favVideo }, page.Items.Select(i => i.Media.Id).ToArray());
    }

    // ---- fakes -------------------------------------------------------------

    private sealed class CountingTextEmbedder : ITextEmbedder
    {
        private readonly ITextEmbedder _inner;

        public CountingTextEmbedder(ITextEmbedder inner) => _inner = inner;

        public int Calls { get; private set; }

        public string Provider => _inner.Provider;

        public bool Supports(string capability) => _inner.Supports(capability);

        public Task<AiEmbeddingResult> EmbedTextAsync(
            string text, AiProfile profile, CancellationToken cancellationToken = default)
        {
            Calls++;
            return _inner.EmbedTextAsync(text, profile, cancellationToken);
        }
    }

    private sealed class SingleBackendResolver : IAiBackendResolver
    {
        private readonly ITextEmbedder _embedder;

        public SingleBackendResolver(ITextEmbedder embedder) => _embedder = embedder;

        public Task<AiBackendResolution<T>> ResolveForCapabilityAsync<T>(
            string capability, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => throw new NotSupportedException();

        public Task<AiBackendResolution<T>> ResolveForProfileKeyAsync<T>(
            string profileKey, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => Task.FromResult(_embedder is T typed
                ? AiBackendResolution<T>.Available(typed, new AiResolution
                {
                    IsAvailable = true,
                    Capability = AiCapabilities.ImageEmbedding,
                    Provider = AiProviders.Deterministic,
                    ProfileKey = profileKey,
                })
                : AiBackendResolution<T>.Unavailable(AiResolution.Unavailable(
                    AiCapabilities.ImageEmbedding, AiUnavailableReasons.ProviderUnavailable)));

        public Task<AiResolution> GetCapabilityAvailabilityAsync(
            string capability, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
