using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Ai.NaturalGallery;

// Proves the NON-NEGOTIABLE physical-filter-first contract: the physical filter
// builds the candidate set FIRST, then semantic ranking runs only inside it. In
// particular, a valid match that lies OUTSIDE the global semantic prefix is still
// discoverable once a selective physical filter is applied — which a global
// top-N-then-filter approach would lose. Runs on SQLite (no pgvector) so it
// exercises the in-process exact-scan ranker; the pgvector SQL path is proven
// equivalent because both rank the SAME candidate id set exactly.
public sealed class GallerySemanticQueryTests
{
    private const string ProfileKey = "test-multimodal-1152";
    private const string Query = "cane nero sulla neve";

    private static SqliteWebApplicationFactory Factory()
    {
        var f = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ImageEmbeddingsEnabled"] = "true",
            ["Ai:PhotoSimilarityProfileKey"] = ProfileKey,
        }, poolHost: true);
        f.EnsureDatabaseCreated();
        return f;
    }

    private static byte[] Png(byte color, int edge = SemanticPhotoCandidatePolicy.MinEdgePixels)
    {
        using var image = new Image<Rgba32>(edge, edge, new Rgba32(color, color, color));
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    private static async Task<Guid> UploadAsync(HttpClient client, string name, byte color, int? edge = null)
    {
        var part = new ByteArrayContent(Png(color, edge ?? SemanticPhotoCandidatePolicy.MinEdgePixels));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var form = new MultipartFormDataContent { { part, "file", name } };
        var response = await client.PostAsync("/api/files", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FileSummary>())!.Id;
    }

    private static async Task<AiProfile> SeedProfileAsync(SqliteWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = "test-multimodal-model-1152",
            Provider = AiProviders.Deterministic, Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Image, Dimension = 1152, DistanceMetric = AiDistanceMetrics.Cosine,
            Version = 1, Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = ProfileKey, AiModelId = model.Id,
            Capability = AiCapabilities.ImageEmbedding, Modality = AiModalities.Image,
            Dimension = 1152, DistanceMetric = AiDistanceMetrics.Cosine,
            IsDefault = false, Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        db.AiProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }

    // Deterministic text-tower embedding for the fixed query (the vector all
    // controlled image embeddings are built from).
    private static float[] QueryVector(AiProfile profile)
        => new DeterministicAiBackend().EmbedTextAsync(Query, profile).GetAwaiter().GetResult().Vector;

    private static async Task SeedEmbeddingAsync(
        SqliteWebApplicationFactory factory, AiProfile profile, Guid fileId, float[] vector)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
        var blobId = await db.FileItems.Where(f => f.Id == fileId).Select(f => f.BlobObjectId).FirstAsync();
        db.BlobEmbeddings.Add(new BlobEmbedding
        {
            Id = Guid.NewGuid(), BlobObjectId = blobId, ProfileId = profile.Id,
            EmbeddingBytes = serializer.Serialize(vector, 1152), Dimension = 1152, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SetFavoriteAsync(SqliteWebApplicationFactory factory, Guid fileId, bool favorite)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.FileItemUserMetadata.FirstOrDefaultAsync(u => u.FileItemId == fileId);
        if (row is null)
        {
            db.FileItemUserMetadata.Add(new FileItemUserMetadata
            {
                Id = Guid.NewGuid(), FileItemId = fileId, IsFavorite = favorite, CreatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            row.IsFavorite = favorite;
        }
        await db.SaveChangesAsync();
    }

    private static async Task<GallerySemanticPage> SearchAsync(
        SqliteWebApplicationFactory factory, Guid owner, ImageFilters filters, int limit = 50, string? cursor = null)
    {
        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<GallerySemanticQueryService>();
        return await svc.SearchAsync(owner, limit, cursor, filters);
    }

    private static float[] Perturb(float[] v, int flip)
    {
        var copy = (float[])v.Clone();
        for (var i = 0; i < flip && i < copy.Length; i++) copy[i] = -copy[i];
        return copy;
    }

    [Fact]
    public async Task Physical_Filter_First_Finds_Match_Outside_Global_Semantic_Prefix()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, client) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        // 20 NON-favorite decoys that are the GLOBAL nearest neighbours (== query).
        for (var i = 0; i < 20; i++)
        {
            var id = await UploadAsync(client, $"decoy{i}.png", (byte)(10 + i));
            await SeedEmbeddingAsync(factory, profile, id, q);
        }
        // 1 FAVORITE target that is globally FAR less similar (ranks ~21st).
        var targetId = await UploadAsync(client, "target.png", 200);
        await SeedEmbeddingAsync(factory, profile, targetId, Perturb(q, 400));
        await SetFavoriteAsync(factory, targetId, true);

        // Global semantic search with a small Top-K would NOT surface the target.
        var global = await SearchAsync(factory, owner,
            new ImageFilters { SemanticQuery = Query, SemanticTopK = 3 });
        Assert.DoesNotContain(global.Items, x => x.Id == targetId);

        // Physical filter FIRST (favorites) → candidate set {target} → target found.
        var filtered = await SearchAsync(factory, owner,
            new ImageFilters { Favorite = true, SemanticQuery = Query, SemanticTopK = 3 });
        Assert.Single(filtered.Items);
        Assert.Equal(targetId, filtered.Items[0].Id);
        Assert.Equal(1, filtered.TotalCount); // reduced semantic total, not the physical count
        Assert.True(filtered.Available);
    }

    [Fact]
    public async Task TopK_Reduces_Total_And_Candidate_Smaller_Than_K_Returns_All()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, client) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        for (var i = 0; i < 5; i++)
        {
            var id = await UploadAsync(client, $"fav{i}.png", (byte)(30 + i));
            await SeedEmbeddingAsync(factory, profile, id, Perturb(q, i)); // distinct scores
            await SetFavoriteAsync(factory, id, true);
        }

        var topk3 = await SearchAsync(factory, owner,
            new ImageFilters { Favorite = true, SemanticQuery = Query, SemanticTopK = 3 });
        Assert.Equal(3, topk3.TotalCount);

        var topk300 = await SearchAsync(factory, owner,
            new ImageFilters { Favorite = true, SemanticQuery = Query, SemanticTopK = 300 });
        Assert.Equal(5, topk300.TotalCount); // candidate set (5) < K → all indexed candidates
    }

    [Fact]
    public async Task Media_Without_Embedding_Is_Excluded_From_Semantic_Total()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, client) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var embedded = await UploadAsync(client, "embedded.png", 40);
        await SeedEmbeddingAsync(factory, profile, embedded, q);
        await SetFavoriteAsync(factory, embedded, true);

        var missing = await UploadAsync(client, "missing.png", 80); // favorite but NO embedding
        await SetFavoriteAsync(factory, missing, true);

        var page = await SearchAsync(factory, owner,
            new ImageFilters { Favorite = true, SemanticQuery = Query, SemanticTopK = 300 });
        Assert.Equal(1, page.TotalCount);
        Assert.Contains(page.Items, x => x.Id == embedded);
        Assert.DoesNotContain(page.Items, x => x.Id == missing);
    }

    [Fact]
    public async Task Tiny_Technical_Images_Are_Excluded_Only_From_Semantic_Candidates()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, client) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var photo = await UploadAsync(client, "photo.png", 40);
        var sidecar = await UploadAsync(client, "camera-sidecar.thm", 80, edge: 32);
        await SeedEmbeddingAsync(factory, profile, photo, Perturb(q, 100));
        await SeedEmbeddingAsync(factory, profile, sidecar, q);

        var semantic = await SearchAsync(factory, owner,
            new ImageFilters { SemanticQuery = Query, SemanticTopK = 10 });
        Assert.Single(semantic.Items);
        Assert.Equal(photo, semantic.Items[0].Id);
        Assert.Equal(1, semantic.PhysicalCandidateCount);

        // The quality gate is semantic-only: the normal gallery still lists the
        // small image and no source/blob state is changed.
        var gallery = await client.GetFromJsonAsync<ImageListResponse>("/api/images?limit=10");
        Assert.NotNull(gallery);
        Assert.Contains(gallery!.Items, x => x.Id == sidecar);
    }

    [Fact]
    public async Task Pagination_Is_Stable_No_Duplicates_No_Gaps_Stable_Denominator()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, client) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        for (var i = 0; i < 5; i++)
        {
            var id = await UploadAsync(client, $"p{i}.png", (byte)(50 + i));
            await SeedEmbeddingAsync(factory, profile, id, Perturb(q, i));
            await SetFavoriteAsync(factory, id, true);
        }

        var filters = new ImageFilters { Favorite = true, SemanticQuery = Query, SemanticTopK = 5 };
        var seen = new List<Guid>();
        string? cursor = null;
        var denominators = new List<int>();
        for (var page = 0; page < 10; page++)
        {
            var p = await SearchAsync(factory, owner, filters, limit: 2, cursor: cursor);
            denominators.Add(p.TotalCount);
            seen.AddRange(p.Items.Select(i => i.Id));
            cursor = p.NextCursor;
            if (cursor is null) break;
        }

        Assert.All(denominators, d => Assert.Equal(5, d)); // stable denominator across pages
        Assert.Equal(5, seen.Count);                        // no gaps
        Assert.Equal(5, seen.Distinct().Count());           // no duplicates
    }

    [Fact]
    public async Task Cursor_From_A_Different_Query_Is_Rejected()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, client) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);
        for (var i = 0; i < 4; i++)
        {
            var id = await UploadAsync(client, $"c{i}.png", (byte)(60 + i));
            await SeedEmbeddingAsync(factory, profile, id, Perturb(q, i));
            await SetFavoriteAsync(factory, id, true);
        }

        var first = await SearchAsync(factory, owner,
            new ImageFilters { Favorite = true, SemanticQuery = Query, SemanticTopK = 5 }, limit: 2);
        Assert.NotNull(first.NextCursor);

        // Reuse the cursor under a CHANGED semantic query → rejected.
        await Assert.ThrowsAsync<SemanticSearchCursorException>(() => SearchAsync(
            factory, owner,
            new ImageFilters { Favorite = true, SemanticQuery = "gatto bianco", SemanticTopK = 5 },
            limit: 2, cursor: first.NextCursor));
    }

    [Fact]
    public async Task Unfavorite_Reconciles_Semantic_Total()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, client) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var id = await UploadAsync(client, "only.png", 90);
        await SeedEmbeddingAsync(factory, profile, id, q);
        await SetFavoriteAsync(factory, id, true);

        var before = await SearchAsync(factory, owner,
            new ImageFilters { Favorite = true, SemanticQuery = Query, SemanticTopK = 300 });
        Assert.Equal(1, before.TotalCount);

        await SetFavoriteAsync(factory, id, false);
        var after = await SearchAsync(factory, owner,
            new ImageFilters { Favorite = true, SemanticQuery = Query, SemanticTopK = 300 });
        Assert.Equal(0, after.TotalCount);
        Assert.Empty(after.Items);
    }
}
