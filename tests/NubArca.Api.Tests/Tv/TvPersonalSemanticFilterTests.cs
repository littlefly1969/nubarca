using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using NubArca.Api.Tv;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Tv;

// Do the TV's semantic filters actually REACH the backend filter?
//
// TvPersonalSemanticTests covers the route's own contract — authorization,
// owner derivation, validation — by asserting a request is not rejected. That
// is deliberately not enough here: a parameter can be accepted, parsed, and
// then dropped on the floor, and every status-code assertion still passes. That
// is exactly how `albumMembership` came to be shown as APPLIED on the TV while
// the endpoint declared no such parameter at all.
//
// So these tests run a REAL semantic search — deterministic backend, seeded
// embeddings — and assert on which items come back. An ignored filter cannot
// survive that. Both routes are covered, because they build candidates
// differently: kind=image goes to the photo pipeline, kind=all|video to the
// unified one, and both must honour the same filter.
public sealed class TvPersonalSemanticFilterTests : IDisposable
{
    private const string Code = "URDLSUDLR";
    private const string Route = "/api/tv/personal/media/semantic";
    private const string ProfileKey = "test-multimodal-1152";
    private const string Query = "cane nero sulla neve";
    private const int Dimension = 1152;

    private readonly SqliteWebApplicationFactory _factory = new(new Dictionary<string, string?>
    {
        ["Ai:Enabled"] = "true",
        ["Ai:ImageEmbeddingsEnabled"] = "true",
        ["Ai:PhotoSimilarityProfileKey"] = ProfileKey,
    });

    public TvPersonalSemanticFilterTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("image")]   // photo pipeline  (GallerySemanticQueryService)
    [InlineData("all")]     // unified pipeline (MediaSemanticSearchService)
    public async Task AlbumMembership_Reaches_The_Filter_On_Both_Semantic_Routes(string kind)
    {
        var profile = await SeedProfileAsync();
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        var loose = await UploadPhotoAsync(owner, "loose.png", 20);
        var filed = await UploadPhotoAsync(owner, "filed.png", 40);
        await FileIntoAnAlbumAsync(owner, filed);
        // Identical vectors: nothing but the FILTER can separate these two.
        await SeedEmbeddingsAsync(profile, loose, filed);

        var unassigned = await SearchAsync(cookie, grant, kind, "unassigned");
        Assert.Contains(loose, unassigned);
        Assert.DoesNotContain(filed, unassigned);

        var assigned = await SearchAsync(cookie, grant, kind, "assigned");
        Assert.Contains(filed, assigned);
        Assert.DoesNotContain(loose, assigned);

        // …and the filter is genuinely OPTIONAL rather than always-on.
        var any = await SearchAsync(cookie, grant, kind, null);
        Assert.Contains(loose, any);
        Assert.Contains(filed, any);
    }

    [Fact]
    public async Task AlbumMembership_Reaches_The_Video_Candidate_Query()
    {
        // kind=video shares SearchMediaAsync with kind=all, and its candidates
        // come from ListPhysicalVideoCandidatesAsync. Ranking a video needs
        // segmentation this test has no reason to stage, so the filter is
        // asserted where it is actually applied: the candidate query that the
        // unified route feeds on. Same ImageFilters, same BuildGalleryQuery.
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var loose = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "loose.mp4", "video/mp4");
        var filed = await UploadAsync(owner, ImageFixtures.MinimalMp4("mp42"), "filed.mp4", "video/mp4");
        await FileIntoAnAlbumAsync(owner, filed);

        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();

        var unassigned = await files.ListPhysicalVideoCandidatesAsync(
            ownerId, new ImageFilters { AlbumMembership = AlbumMembershipFilter.Unassigned }, 50);
        Assert.Contains(unassigned, c => c.Id == loose);
        Assert.DoesNotContain(unassigned, c => c.Id == filed);

        var assigned = await files.ListPhysicalVideoCandidatesAsync(
            ownerId, new ImageFilters { AlbumMembership = AlbumMembershipFilter.Assigned }, 50);
        Assert.Contains(assigned, c => c.Id == filed);
        Assert.DoesNotContain(assigned, c => c.Id == loose);
    }

    [Fact]
    public async Task A_Malformed_AlbumMembership_Is_Refused_Rather_Than_Ignored()
    {
        // The failure mode this whole file exists for: a value the endpoint
        // cannot honour must not be silently downgraded to "any".
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        var response = await TvSendAsync(
            cookie, $"{Route}?q=cane&kind=image&albumMembership=sometimes", grant);
        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden,
            $"unexpected {response.StatusCode}");
    }

    // ── search ──────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<Guid>> SearchAsync(
        string cookie, string grant, string kind, string? albumMembership)
    {
        var url = $"{Route}?q={Uri.EscapeDataString(Query)}&kind={kind}&limit=20"
            + (albumMembership is null ? string.Empty : $"&albumMembership={albumMembership}");
        var response = await TvSendAsync(cookie, url, grant);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return root.GetProperty("items").EnumerateArray()
            .Select(item => Guid.Parse(item.GetProperty("id").GetString()!))
            .ToList();
    }

    // ── fixtures ────────────────────────────────────────────────────────────

    private static byte[] Png(byte color)
    {
        using var image = new Image<Rgba32>(
            SemanticPhotoCandidatePolicy.MinEdgePixels,
            SemanticPhotoCandidatePolicy.MinEdgePixels,
            new Rgba32(color, 0, 0));
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    private static Task<Guid> UploadPhotoAsync(HttpClient client, string name, byte color)
        => UploadAsync(client, Png(color), name, "image/png");

    private static async Task<Guid> UploadAsync(
        HttpClient client, byte[] bytes, string name, string contentType)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var form = new MultipartFormDataContent { { part, "file", name } };
        var response = await client.PostAsync("/api/files", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FileSummary>())!.Id;
    }

    private static async Task FileIntoAnAlbumAsync(HttpClient owner, Guid fileItemId)
    {
        var created = await owner.PostAsJsonAsync("/api/albums", new { name = $"A{Guid.NewGuid():N}" });
        created.EnsureSuccessStatusCode();
        var albumId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString();
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId }))
            .EnsureSuccessStatusCode();
    }

    private async Task<AiProfile> SeedProfileAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = "test-multimodal-model-1152",
            Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.ImageEmbedding, Modality = AiModalities.Image,
            Dimension = Dimension, DistanceMetric = AiDistanceMetrics.Cosine,
            Version = 1, Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = ProfileKey, AiModelId = model.Id,
            Capability = AiCapabilities.ImageEmbedding, Modality = AiModalities.Image,
            Dimension = Dimension, DistanceMetric = AiDistanceMetrics.Cosine,
            IsDefault = false, Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        db.AiProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }

    private async Task SeedEmbeddingsAsync(AiProfile profile, params Guid[] fileItemIds)
    {
        var vector = (await new DeterministicAiBackend().EmbedTextAsync(Query, profile)).Vector;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
        var blobs = await db.FileItems
            .Where(f => fileItemIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.BlobObjectId);
        foreach (var id in fileItemIds)
        {
            db.BlobEmbeddings.Add(new BlobEmbedding
            {
                Id = Guid.NewGuid(), BlobObjectId = blobs[id], ProfileId = profile.Id,
                EmbeddingBytes = serializer.Serialize(vector, Dimension),
                Dimension = Dimension, CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    // ── TV session helpers (same shape as TvPersonalSemanticTests) ──────────

    private async Task<string> PairAsync(HttpClient owner)
    {
        var tvClient = _factory.CreateClient();
        var start = await tvClient.PostAsync("/api/tv/pairing/start", null);
        start.EnsureSuccessStatusCode();
        var started = (await start.Content.ReadFromJsonAsync<TvPairingStartedDto>())!;

        (await owner.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                personalCode = Code,
                personalCodeConfirmation = Code,
            })).EnsureSuccessStatusCode();

        var poll = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        poll.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var response = await tvClient.SendAsync(poll);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single();
    }

    private async Task<string> UnlockTokenAsync(string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/tv/personal/unlock");
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(cookie)}");
        request.Content = JsonContent.Create(new { code = Code });
        var response = await _factory.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("unlockToken").GetString()!;
    }

    private Task<HttpResponseMessage> TvSendAsync(string cookie, string url, string? grant = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(cookie)}");
        if (grant is not null) request.Headers.Add(TvPersonalAreaService.UnlockHeader, grant);
        return _factory.CreateClient().SendAsync(request);
    }

    private static string CookieValue(string setCookie) => setCookie.Split(';')[0].Split('=', 2)[1];
}
