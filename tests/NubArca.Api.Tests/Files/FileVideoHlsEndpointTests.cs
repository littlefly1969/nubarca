using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Video-hls slice 2 — the ADAPTIVE /video contract, active only when
// Media:VideoHlsProvider=ffmpeg: master playlist when the ladder is published,
// 202 + idempotent lazy enqueue while preparing, and the whitelisted
// per-rendition child route. The LEGACY direct-stream contract (provider
// disabled, the default) keeps its own coverage in FileVideoEndpointTests —
// the flag fully gates the breaking change.
public sealed class FileVideoHlsEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FileVideoHlsEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Media:VideoHlsProvider"] = "ffmpeg",
        }, poolHost: true);
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static byte[] BulkyMp4()
    {
        var head = ImageFixtures.MinimalMp4();
        var bytes = new byte[1024];
        Array.Copy(head, bytes, head.Length);
        for (var i = head.Length; i < bytes.Length; i++) bytes[i] = (byte)(i & 0xFF);
        return bytes;
    }

    private async Task<FileItem> UploadAsync(Guid ownerId, byte[] bytes, string name, string mime)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, null, name, mime, new MemoryStream(bytes));
    }

    private async Task<(Guid BlobId, string Sha)> BlobOfAsync(FileItem file)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = await db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == file.BlobObjectId);
        return (blob.Id, blob.Sha256);
    }

    // Publish a fake (but shape-correct) ladder + a ready row, as a completed
    // generation run would have.
    private async Task PublishReadyLadderAsync(FileItem file)
    {
        var (blobId, sha) = await BlobOfAsync(file);
        var hls = _factory.Services.GetRequiredService<HlsDerivativeStorage>();
        var staging = hls.CreateStagingDirectory();
        File.WriteAllText(
            Path.Combine(staging, "master.m3u8"),
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080\nhigh/stream.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=854x480\nlow/stream.m3u8\n");
        foreach (var name in new[] { "high", "low" })
        {
            var dir = Path.Combine(staging, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "stream.m3u8"), "#EXTM3U\n#EXT-X-MAP:URI=\"init_0.mp4\"\n#EXTINF:4.0,\nseg-0.m4s\n");
            File.WriteAllBytes(Path.Combine(dir, "init_0.mp4"), [0x00, 0x01]);
            File.WriteAllBytes(Path.Combine(dir, "seg-0.m4s"), [0x02, 0x03]);
        }
        hls.Publish(sha, staging);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.BlobHlsDerivatives.Add(new BlobHlsDerivative
        {
            Id = Guid.NewGuid(),
            BlobObjectId = blobId,
            Status = VideoHlsStatuses.Ready,
            Version = FfmpegVideoHlsTranscoder.Version,
            CreatedAt = DateTime.UtcNow,
            ReadyAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<BackgroundJob>> HlsJobsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Type == JobTypes.MediaVideoHlsGenerate)
            .ToListAsync();
    }

    // ---- preparing: 202 + idempotent lazy enqueue ---------------------------

    [Fact]
    public async Task Unprepared_Video_Returns_202_And_Enqueues_Once()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");

        var first = await client.GetAsync($"/api/files/{file.Id}/video");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await client.GetAsync($"/api/files/{file.Id}/video");
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

        // The per-blob idempotency key collapses the repeat request.
        Assert.Single(await HlsJobsAsync());
    }

    // UX-02: the 202 carries a Retry-After floor so a client does not have to
    // guess a poll interval. Additive only — the status code and the (empty)
    // body are the existing contract.
    [Fact]
    public async Task Preparing_202_Advertises_A_RetryAfter_Floor()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");

        var resp = await client.GetAsync($"/api/files/{file.Id}/video");

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.True(resp.Headers.TryGetValues("Retry-After", out var values));
        var value = Assert.Single(values!);
        Assert.Equal(
            VideoHlsServingService.PreparingRetryAfterSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            value);
        // Delta-seconds, not an HTTP-date: the client parses the numeric form.
        Assert.True(int.TryParse(value, out var seconds));
        Assert.InRange(seconds, 1, 30);
    }

    [Fact]
    public async Task Ready_200_Carries_No_RetryAfter()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");
        await PublishReadyLadderAsync(file);

        var resp = await client.GetAsync($"/api/files/{file.Id}/video");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(resp.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Ready_Row_With_Wiped_Bytes_Returns_202_And_Reenqueues()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");
        await PublishReadyLadderAsync(file);
        var (_, sha) = await BlobOfAsync(file);
        _factory.Services.GetRequiredService<HlsDerivativeStorage>().Delete(sha);

        var resp = await client.GetAsync($"/api/files/{file.Id}/video");

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Single(await HlsJobsAsync());
    }

    // ---- ready: master playlist --------------------------------------------

    [Fact]
    public async Task Ready_Ladder_Serves_Master_With_Rewritten_Rendition_Uris()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");
        await PublishReadyLadderAsync(file);

        var resp = await client.GetAsync($"/api/files/{file.Id}/video");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(
            VideoHlsServingService.MasterContentType,
            resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadAsStringAsync();
        // Rendition URIs are prefixed so they resolve under .../video/.
        Assert.Contains("video/high/stream.m3u8", body);
        Assert.Contains("video/low/stream.m3u8", body);
        // Tag lines untouched.
        Assert.Contains("#EXT-X-STREAM-INF", body);
        // No generation job for an already-published ladder.
        Assert.Empty(await HlsJobsAsync());
    }

    // ---- child route: playlists / init / segments ---------------------------

    [Theory]
    [InlineData("high/stream.m3u8", "application/vnd.apple.mpegurl")]
    [InlineData("high/init_0.mp4", "video/mp4")]
    [InlineData("low/seg-0.m4s", "video/iso.segment")]
    public async Task Child_Route_Serves_Ladder_Files_With_Correct_Types(
        string relative, string expectedType)
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");
        await PublishReadyLadderAsync(file);

        var resp = await client.GetAsync($"/api/files/{file.Id}/video/{relative}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(expectedType, resp.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await resp.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("high/evil.sh")]
    [InlineData("high/seg-x.m4s")]
    [InlineData("medium/stream.m3u8")]
    [InlineData("high/master.m3u8")]
    public async Task Child_Route_Rejects_NonWhitelisted_Names(string relative)
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");
        await PublishReadyLadderAsync(file);

        var resp = await client.GetAsync($"/api/files/{file.Id}/video/{relative}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- authorization / no-leak gates (parity with the legacy contract) ----

    [Fact]
    public async Task Video_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var master = await anonymous.GetAsync($"/api/files/{Guid.NewGuid()}/video");
        Assert.Equal(HttpStatusCode.Unauthorized, master.StatusCode);
        var seg = await anonymous.GetAsync($"/api/files/{Guid.NewGuid()}/video/high/seg-0.m4s");
        Assert.Equal(HttpStatusCode.Unauthorized, seg.StatusCode);
    }

    [Fact]
    public async Task Foreign_File_Returns_404_On_Master_And_Segments()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFile = await UploadAsync(alice, BulkyMp4(), "a.mp4", "video/mp4");
        await PublishReadyLadderAsync(aliceFile);

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var master = await bobClient.GetAsync($"/api/files/{aliceFile.Id}/video");
        Assert.Equal(HttpStatusCode.NotFound, master.StatusCode);
        var seg = await bobClient.GetAsync($"/api/files/{aliceFile.Id}/video/high/seg-0.m4s");
        Assert.Equal(HttpStatusCode.NotFound, seg.StatusCode);
        Assert.Empty(await HlsJobsAsync()); // a foreign request never enqueues
    }

    [Fact]
    public async Task SoftDeleted_File_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");
        await PublishReadyLadderAsync(file);
        var del = await client.DeleteAsync($"/api/files/{file.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var resp = await client.GetAsync($"/api/files/{file.Id}/video");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task NonVideo_File_Returns_404_And_Never_Enqueues()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "photo.jpg", "image/jpeg");

        var resp = await client.GetAsync($"/api/files/{file.Id}/video");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Empty(await HlsJobsAsync());
    }

    [Fact]
    public async Task Failed_Row_Returns_404_And_Never_Reenqueues()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");
        var (blobId, _) = await BlobOfAsync(file);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BlobHlsDerivatives.Add(new BlobHlsDerivative
            {
                Id = Guid.NewGuid(),
                BlobObjectId = blobId,
                Status = VideoHlsStatuses.Failed,
                ErrorCode = VideoHlsErrorCodes.TranscodeFailed,
                Version = FfmpegVideoHlsTranscoder.Version,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync($"/api/files/{file.Id}/video");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Empty(await HlsJobsAsync());
    }

    [Fact]
    public async Task Responses_Do_Not_Leak_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, BulkyMp4(), "leak.mp4", "video/mp4");
        await PublishReadyLadderAsync(file);

        var resp = await client.GetAsync($"/api/files/{file.Id}/video");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var headers = string.Join("\n",
            resp.Headers.Concat(resp.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));
        var body = await resp.Content.ReadAsStringAsync();

        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
            Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
        }
        // The master body must not contain the blob hash or storage paths.
        var (_, sha) = await BlobOfAsync(file);
        Assert.DoesNotContain(sha, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objects/", body, StringComparison.Ordinal);
    }
}
