using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

// GET /api/videos video-metadata filters (duration/resolution/codec/audio),
// the ffprobe-derived projection, and the /api/videos/codecs facet. Probed
// state is simulated by writing the BlobMetadata video fields directly (no real
// ffprobe binary in tests).
public sealed class ListVideosFilterTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public ListVideosFilterTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> UploadProbedAsync(
        Guid ownerId, string name, string signature,
        double? duration, int? width, int? height, string? codec, bool hasAudio)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f = await files.CreateAsync(
            ownerId, null, name, "video/mp4", new MemoryStream(ImageFixtures.MinimalMp4(signature)));
        var meta = await db.BlobMetadata.SingleAsync(m => m.BlobObjectId == f.BlobObjectId);
        meta.VideoExtractionStatus = MetadataStatuses.Completed;
        meta.DurationSeconds = duration;
        meta.Width = width;
        meta.Height = height;
        meta.VideoCodec = codec;
        meta.HasAudio = hasAudio;
        await db.SaveChangesAsync();
        return f.Id;
    }

    private sealed record CodecsResponse(IReadOnlyList<string> Codecs);

    [Fact]
    public async Task Projects_Probed_Duration_Dimensions_And_Codec()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadProbedAsync(owner, "a.mp4", "aa01", 12.5, 1920, 1080, "h264", hasAudio: true);

        var page = await client.GetFromJsonAsync<VideoListResponse>("/api/videos");
        var item = Assert.Single(page!.Items);
        Assert.Equal(12.5, item.DurationSeconds);
        Assert.Equal(1920, item.Width);
        Assert.Equal(1080, item.Height);
        Assert.Equal("h264", item.VideoCodec);
        Assert.True(item.HasAudio);
    }

    [Fact]
    public async Task Duration_Range_Filters()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var shortV = await UploadProbedAsync(owner, "short.mp4", "sh01", 5, 640, 480, "h264", false);
        var longV = await UploadProbedAsync(owner, "long.mp4", "ln01", 600, 640, 480, "h264", false);

        var min = await client.GetFromJsonAsync<VideoListResponse>("/api/videos?durationMin=60");
        Assert.Equal(longV, Assert.Single(min!.Items).Id);

        var max = await client.GetFromJsonAsync<VideoListResponse>("/api/videos?durationMax=60");
        Assert.Equal(shortV, Assert.Single(max!.Items).Id);
    }

    [Fact]
    public async Task MinResolution_Filter()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadProbedAsync(owner, "sd.mp4", "sd01", 10, 640, 480, "h264", false);
        var hd = await UploadProbedAsync(owner, "hd.mp4", "hd01", 10, 1920, 1080, "h264", false);

        var page = await client.GetFromJsonAsync<VideoListResponse>("/api/videos?minWidth=1280&minHeight=720");
        Assert.Equal(hd, Assert.Single(page!.Items).Id);
    }

    [Fact]
    public async Task Codec_Filter_Is_Case_Insensitive()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var h264 = await UploadProbedAsync(owner, "h264.mp4", "h201", 10, 640, 480, "h264", false);
        await UploadProbedAsync(owner, "hevc.mp4", "he01", 10, 640, 480, "hevc", false);

        var page = await client.GetFromJsonAsync<VideoListResponse>("/api/videos?codec=H264");
        Assert.Equal(h264, Assert.Single(page!.Items).Id);
    }

    [Fact]
    public async Task HasAudio_Filter_Both_Directions()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var withAudio = await UploadProbedAsync(owner, "sound.mp4", "au01", 10, 640, 480, "h264", true);
        var silent = await UploadProbedAsync(owner, "silent.mp4", "si01", 10, 640, 480, "h264", false);

        var yes = await client.GetFromJsonAsync<VideoListResponse>("/api/videos?hasAudio=true");
        Assert.Equal(withAudio, Assert.Single(yes!.Items).Id);

        var no = await client.GetFromJsonAsync<VideoListResponse>("/api/videos?hasAudio=false");
        Assert.Equal(silent, Assert.Single(no!.Items).Id);
    }

    [Fact]
    public async Task Codecs_Facet_Returns_Distinct_Sorted()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadProbedAsync(owner, "a.mp4", "ca01", 10, 640, 480, "hevc", false);
        await UploadProbedAsync(owner, "b.mp4", "cb01", 10, 640, 480, "h264", false);
        await UploadProbedAsync(owner, "c.mp4", "cc01", 10, 640, 480, "h264", false);

        var resp = await client.GetFromJsonAsync<CodecsResponse>("/api/videos/codecs");
        Assert.Equal(new[] { "h264", "hevc" }, resp!.Codecs);
    }

    [Fact]
    public async Task Bad_Duration_Range_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var resp = await client.GetAsync("/api/videos?durationMin=100&durationMax=10");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
