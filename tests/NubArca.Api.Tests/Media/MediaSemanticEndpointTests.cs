using System.Net;
using System.Text.Json;
using Xunit;
using static NubArca.Api.Tests.Media.MediaSemanticTestHarness;

namespace NubArca.Api.Tests.Media;

// VSEM-03: the HTTP surface of GET /api/media/semantic — authentication,
// validation, availability mapping and (critically) the response SHAPE: the
// wire never carries similarity scores, vectors, profile/blob identifiers or
// model names.
public sealed class MediaSemanticEndpointTests
{
    [Fact]
    public async Task Unauthenticated_Requests_Are_Rejected()
    {
        using var factory = Factory();
        var resp = await factory.CreateClient().GetAsync("/api/media/semantic?q=cat");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Theory]
    [InlineData("/api/media/semantic")]                          // missing q
    [InlineData("/api/media/semantic?q=")]                       // empty q
    [InlineData("/api/media/semantic?q=%20%20")]                 // whitespace q
    [InlineData("/api/media/semantic?q=cat&kind=nope")]          // invalid kind
    [InlineData("/api/media/semantic?q=cat&minRating=9")]        // rating out of range
    public async Task Invalid_Requests_Are_400(string url)
    {
        using var factory = Factory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task An_Overlong_Query_Is_400()
    {
        using var factory = Factory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var resp = await client.GetAsync("/api/media/semantic?q=" + new string('a', 300));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task A_Mismatched_Cursor_Is_400()
    {
        using var factory = Factory();
        await SeedProfileAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var resp = await client.GetAsync("/api/media/semantic?q=cat&cursor=garbage");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task An_Unavailable_Profile_Is_A_Sanitized_503()
    {
        using var factory = Factory();   // configured profile key has no row
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var resp = await client.GetAsync("/api/media/semantic?q=cat");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("semantic_search_unavailable", body);
        Assert.DoesNotContain("Exception", body);
    }

    [Fact]
    public async Task Response_Shape_Carries_Media_And_Temporal_Evidence_But_No_Internals()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, client) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (photoId, photoBlob) = await UploadPhotoAsync(factory, owner, 40);
        await SeedPhotoEmbeddingAsync(factory, profile, photoBlob, WithSimilarity(q, 0.9));
        var (videoId, videoBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, profile, videoBlob, q,
            [new SeedSample(0, 8000, 4000, 0.8)]);

        var resp = await client.GetAsync($"/api/media/semantic?q={Uri.EscapeDataString(Query)}&kind=all");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(raw);
        var root = json.RootElement;

        Assert.Equal("ok", root.GetProperty("semanticStatus").GetString());
        Assert.Equal(2, root.GetProperty("total").GetInt32());
        Assert.False(root.GetProperty("hasMore").GetBoolean());

        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        var photo = items.Single(i =>
            i.GetProperty("media").GetProperty("id").GetGuid() == photoId);
        Assert.Equal("image", photo.GetProperty("media").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null,
            photo.GetProperty("bestMatch").GetProperty("representativeMilliseconds").ValueKind);

        var video = items.Single(i =>
            i.GetProperty("media").GetProperty("id").GetGuid() == videoId);
        Assert.Equal("video", video.GetProperty("media").GetProperty("kind").GetString());
        Assert.Equal("visual", video.GetProperty("bestMatch").GetProperty("evidenceType").GetString());
        Assert.Equal(0, video.GetProperty("bestMatch").GetProperty("startMilliseconds").GetInt64());
        Assert.Equal(8000, video.GetProperty("bestMatch").GetProperty("endMilliseconds").GetInt64());
        Assert.Equal(4000, video.GetProperty("bestMatch").GetProperty("representativeMilliseconds").GetInt64());

        // NO internals on the wire: no scores, vectors, blob/profile/model
        // identifiers, storage keys or sample/segment ids.
        var lowered = raw.ToLowerInvariant();
        Assert.DoesNotContain("score", lowered);
        Assert.DoesNotContain("embedding", lowered);
        Assert.DoesNotContain("blobobjectid", lowered);
        Assert.DoesNotContain("profileid", lowered);
        Assert.DoesNotContain("profilekey", lowered);
        Assert.DoesNotContain("storagekey", lowered);
        Assert.DoesNotContain("siglip", lowered);
        Assert.DoesNotContain("sampleid", lowered);
        Assert.DoesNotContain("segmentid", lowered);
    }

    [Fact]
    public async Task Kind_Filter_And_Limit_Are_Honoured_On_The_Wire()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, client) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        for (var i = 0; i < 3; i++)
        {
            var (_, blob) = await UploadPhotoAsync(factory, owner, (byte)(40 + i * 20));
            await SeedPhotoEmbeddingAsync(factory, profile, blob, WithSimilarity(q, 0.9 - i * 0.1));
        }
        var (_, videoBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, profile, videoBlob, q,
            [new SeedSample(0, 8000, 4000, 0.85)]);

        var resp = await client.GetAsync(
            $"/api/media/semantic?q={Uri.EscapeDataString(Query)}&kind=image&limit=2");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = json.RootElement;

        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, i =>
            Assert.Equal("image", i.GetProperty("media").GetProperty("kind").GetString()));
        Assert.True(root.GetProperty("hasMore").GetBoolean());
        Assert.NotNull(root.GetProperty("nextCursor").GetString());
    }

    [Fact]
    public async Task Uncovered_Libraries_Report_The_Indexing_Status()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, client) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        // One embedded photo + many unembedded ones → generic indexing notice.
        var (_, blob) = await UploadPhotoAsync(factory, owner, 40);
        await SeedPhotoEmbeddingAsync(factory, profile, blob, WithSimilarity(q, 0.9));
        for (var i = 0; i < 15; i++)
        {
            await UploadPhotoAsync(factory, owner, (byte)(50 + i * 10));
        }

        var resp = await client.GetAsync($"/api/media/semantic?q={Uri.EscapeDataString(Query)}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("indexing", json.RootElement.GetProperty("semanticStatus").GetString());
    }
}
