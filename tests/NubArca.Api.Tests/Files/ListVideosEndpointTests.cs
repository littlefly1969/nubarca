using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 86 — GET /api/videos: owner-scoped, video-only, cursor-paginated,
// poster URL, no storage internals.
public sealed class ListVideosEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public ListVideosEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> UploadAsync(Guid ownerId, byte[] bytes, string name, string mime)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        var f = await files.CreateAsync(ownerId, null, name, mime, new MemoryStream(bytes));
        return f.Id;
    }

    [Fact]
    public async Task ListVideos_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/videos");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ListVideos_ReturnsOnlyVideos_WithPosterUrl_NotImages()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var v1 = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "a.mp4", "video/mp4");
        var v2 = await UploadAsync(owner, ImageFixtures.MinimalWebm(), "b.webm", "video/webm");
        await UploadAsync(owner, ImageFixtures.JpegWithExif(), "photo.jpg", "image/jpeg");

        var page = await client.GetFromJsonAsync<VideoListResponse>("/api/videos");
        Assert.NotNull(page);
        var ids = page!.Items.Select(i => i.Id).ToHashSet();
        Assert.Contains(v1, ids);
        Assert.Contains(v2, ids);
        Assert.Equal(2, page.Items.Count); // the JPEG is NOT listed

        foreach (var item in page.Items)
        {
            Assert.Equal($"/api/files/{item.Id}/poster", item.PosterUrl);
            Assert.Equal($"/api/files/{item.Id}/video-preview-strip", item.PreviewStripUrl);
            Assert.Null(item.DurationSeconds);
        }
    }

    [Fact]
    public async Task ListVideos_IsOwnerScoped()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync("owner@example.com");
        var mine = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "mine.mp4", "video/mp4");

        var other = await _factory.SeedUserAsync("other@example.com");
        await UploadAsync(other, ImageFixtures.MinimalMp4(), "theirs.mp4", "video/mp4");

        var page = await client.GetFromJsonAsync<VideoListResponse>("/api/videos");
        Assert.Single(page!.Items);
        Assert.Equal(mine, page.Items[0].Id);
    }

    [Fact]
    public async Task ListVideos_CursorPagination_WalksAllWithoutOverlap()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        for (var i = 0; i < 3; i++)
        {
            await UploadAsync(owner, ImageFixtures.MinimalMp4($"is{i:00}"), $"v{i}.mp4", "video/mp4");
        }

        var seen = new List<Guid>();
        var first = await client.GetFromJsonAsync<VideoListResponse>("/api/videos?limit=2");
        Assert.Equal(2, first!.Items.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);
        seen.AddRange(first.Items.Select(i => i.Id));

        var second = await client.GetFromJsonAsync<VideoListResponse>(
            $"/api/videos?limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Single(second!.Items);
        Assert.False(second.HasMore);
        seen.AddRange(second.Items.Select(i => i.Id));

        Assert.Equal(3, seen.Distinct().Count()); // every video once, no overlap
    }

    [Fact]
    public async Task ListVideos_DoesNotLeakStorageInternals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(owner, ImageFixtures.MinimalMp4(), "clip.mp4", "video/mp4");

        var body = await (await client.GetAsync("/api/videos")).Content.ReadAsStringAsync();
        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
