using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Ai;

public sealed class PhotoSemanticSearchTests
{
    private const string ProfileKey = "test-multimodal-1152";

    private static SqliteWebApplicationFactory Factory()
    {
        var f = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ImageEmbeddingsEnabled"] = "true",
            ["Ai:PhotoSimilarityProfileKey"] = ProfileKey,
        });
        f.EnsureDatabaseCreated();
        return f;
    }

    private static byte[] Png(byte color, int edge = SemanticPhotoCandidatePolicy.MinEdgePixels)
    {
        using var image = new Image<Rgba32>(edge, edge, new Rgba32(color, 0, 0));
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
            Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Image,
            Dimension = 1152, DistanceMetric = AiDistanceMetrics.Cosine,
            Version = 1, Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = ProfileKey, AiModelId = model.Id,
            Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Image,
            Dimension = 1152, DistanceMetric = AiDistanceMetrics.Cosine,
            IsDefault = false, Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        db.AiProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }

    [Fact]
    public async Task Semantic_Search_Is_Authenticated_And_Relevance_Ordered()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var bestId = await UploadAsync(client, "best.png", 20);
        var worstId = await UploadAsync(client, "worst.png", 40);
        var tinyTechnicalId = await UploadAsync(client, "camera-sidecar.thm", 50, edge: 32);
        await factory.SeedUserAsync("other-semantic@example.com");
        var otherClient = await factory.LoginAsync("other-semantic@example.com");
        var foreignId = await UploadAsync(otherClient, "foreign.png", 60);

        const string query = "cane nero sulla neve";
        var textVector = (await new DeterministicAiBackend().EmbedTextAsync(query, profile)).Vector;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
            var blobs = await db.FileItems
                .Where(f => f.Id == bestId || f.Id == worstId || f.Id == tinyTechnicalId || f.Id == foreignId)
                .ToDictionaryAsync(f => f.Id, f => f.BlobObjectId);
            db.BlobEmbeddings.AddRange(
                new BlobEmbedding
                {
                    Id = Guid.NewGuid(), BlobObjectId = blobs[bestId], ProfileId = profile.Id,
                    EmbeddingBytes = serializer.Serialize(textVector, 1152), Dimension = 1152,
                    CreatedAt = DateTime.UtcNow,
                },
                new BlobEmbedding
                {
                    Id = Guid.NewGuid(), BlobObjectId = blobs[worstId], ProfileId = profile.Id,
                    EmbeddingBytes = serializer.Serialize(textVector.Select(x => -x).ToArray(), 1152),
                    Dimension = 1152, CreatedAt = DateTime.UtcNow,
                },
                new BlobEmbedding
                {
                    Id = Guid.NewGuid(), BlobObjectId = blobs[tinyTechnicalId], ProfileId = profile.Id,
                    EmbeddingBytes = serializer.Serialize(textVector, 1152), Dimension = 1152,
                    CreatedAt = DateTime.UtcNow,
                },
                new BlobEmbedding
                {
                    Id = Guid.NewGuid(), BlobObjectId = blobs[foreignId], ProfileId = profile.Id,
                    EmbeddingBytes = serializer.Serialize(textVector, 1152), Dimension = 1152,
                    CreatedAt = DateTime.UtcNow,
                });
            await db.SaveChangesAsync();
        }

        var result = await client.GetFromJsonAsync<ImageListResponse>(
            "/api/images/semantic?q=cane%20nero%20sulla%20neve&limit=10");
        Assert.NotNull(result);
        Assert.Equal(new[] { bestId, worstId }, result!.Items.Select(x => x.Id));
        Assert.DoesNotContain(result.Items, x => x.Id == tinyTechnicalId);
        Assert.DoesNotContain(result.Items, x => x.Id == foreignId);

        using var anonymous = factory.CreateClient();
        var unauthorized = await anonymous.GetAsync("/api/images/semantic?q=cane");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Semantic_Search_Rejects_Empty_Query(string query)
    {
        using var factory = Factory();
        await SeedProfileAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/images/semantic?q={Uri.EscapeDataString(query)}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Semantic_Search_Rejects_Malformed_Cursor()
    {
        using var factory = Factory();
        await SeedProfileAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/images/semantic?q=cane&cursor=not-a-cursor");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
