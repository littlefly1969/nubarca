using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 63 — video poster derivative endpoint.
public sealed class FilePosterEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FilePosterEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<FileItem> UploadAsync(Guid ownerId, byte[] bytes, string name, string mime)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, null, name, mime, new MemoryStream(bytes));
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await work(db);
    }

    [Fact]
    public async Task Poster_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var resp = await anonymous.GetAsync($"/api/files/{Guid.NewGuid()}/poster");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Poster_Cross_User_Returns_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFile = await UploadAsync(alice, ImageFixtures.MinimalMp4(), "a.mp4", "video/mp4");
        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var resp = await bobClient.GetAsync($"/api/files/{aliceFile.Id}/poster");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Poster_NonVideo_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "photo.jpg", "image/jpeg");

        var resp = await client.GetAsync($"/api/files/{file.Id}/poster");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Poster_Spoofed_Mp4_Mime_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, "this is not a video"u8.ToArray(), "spoof.mp4", "video/mp4");

        var resp = await client.GetAsync($"/api/files/{file.Id}/poster");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Poster_Video_Returns_Image_Jpeg_With_Nosniff_And_Cache_Header()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "clip.mp4", "video/mp4");

        var resp = await client.GetAsync($"/api/files/{file.Id}/poster");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(FileThumbnailService.ThumbnailMimeType,
            resp.Content.Headers.ContentType?.ToString());
        Assert.Contains("nosniff",
            string.Join(",", resp.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains("private", resp.Headers.CacheControl?.ToString() ?? "");
    }

    [Fact]
    public async Task Poster_Persisted_FileThumbnail_Row_Is_Reused_On_Second_Call()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "clip.mp4", "video/mp4");

        var first = await client.GetAsync($"/api/files/{file.Id}/poster");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var afterFirst = await InDbAsync(db => db.FileThumbnails.AsNoTracking()
            .CountAsync(t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Poster));

        var second = await client.GetAsync($"/api/files/{file.Id}/poster");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var afterSecond = await InDbAsync(db => db.FileThumbnails.AsNoTracking()
            .CountAsync(t => t.FileItemId == file.Id && t.Size == ThumbnailSizes.Poster));

        Assert.Equal(1, afterFirst);
        Assert.Equal(1, afterSecond);
    }

    [Fact]
    public async Task Poster_Response_Does_Not_Leak_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "clip.mp4", "video/mp4");

        var resp = await client.GetAsync($"/api/files/{file.Id}/poster");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var headers = string.Join("\n",
            resp.Headers.Concat(resp.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));

        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Preview_Strip_Failure_Is_Recorded_And_Lazy_Does_Not_Retry()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "clip.mp4", "video/mp4");

        // The endpoint test host intentionally uses the synthetic provider,
        // which cannot decode real frames. First call records the failure;
        // second call must be gated instead of launching the provider again.
        var first = await client.GetAsync($"/api/files/{file.Id}/video-preview-strip");
        var second = await client.GetAsync($"/api/files/{file.Id}/video-preview-strip");

        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        var diagnostic = await InDbAsync(db => db.DerivativeDiagnostics.AsNoTracking()
            .SingleAsync(d => d.FileItemId == file.Id
                && d.Size == ThumbnailSizes.VideoPreviewStrip));
        Assert.Equal(DerivativeStatuses.FailedPermanent, diagnostic.Status);
        Assert.Equal(1, diagnostic.AttemptCount);
    }
}
