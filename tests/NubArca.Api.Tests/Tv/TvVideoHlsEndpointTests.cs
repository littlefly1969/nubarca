using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using NubArca.Api.Tv;
using Xunit;

namespace NubArca.Api.Tests.Tv;

// Video-hls slice 2 — the adaptive /api/tv/media/{fileId}/video contract under
// the TV pairing-cookie model (master | 202-preparing | 404), plus the child
// ladder route. Mirrors FileVideoHlsEndpointTests; the TV-specific concern is
// the allowlist visibility gate composed IN FRONT of the shared serving gate.
public sealed class TvVideoHlsEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public TvVideoHlsEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Media:VideoHlsProvider"] = "ffmpeg",
        });
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

    // Uploads a detected-video file, adds it to an album, and returns both ids.
    private static async Task<Guid> UploadMp4Async(HttpClient owner, string name)
    {
        var part = new ByteArrayContent(BulkyMp4());
        part.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var resp = await owner.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return summary.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateTvAlbumWithAsync(HttpClient owner, Guid fileId)
    {
        var created = await owner.PostAsJsonAsync("/api/albums", new { name = "On TV" });
        created.EnsureSuccessStatusCode();
        var albumId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = true }))
            .EnsureSuccessStatusCode();
        return albumId;
    }

    private async Task PublishReadyLadderAsync(Guid fileId)
    {
        Guid blobId;
        string sha;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pair = await db.FileItems.AsNoTracking()
                .Where(f => f.Id == fileId)
                .Join(db.BlobObjects.AsNoTracking(), f => f.BlobObjectId, b => b.Id,
                    (f, b) => new { b.Id, b.Sha256 })
                .SingleAsync();
            (blobId, sha) = (pair.Id, pair.Sha256);
        }

        var hls = _factory.Services.GetRequiredService<HlsDerivativeStorage>();
        var staging = hls.CreateStagingDirectory();
        File.WriteAllText(
            Path.Combine(staging, "master.m3u8"),
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=5000000\nhigh/stream.m3u8\n");
        var dir = Path.Combine(staging, "high");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stream.m3u8"), "#EXTM3U\nseg-0.m4s\n");
        File.WriteAllBytes(Path.Combine(dir, "init_0.mp4"), [0x00]);
        File.WriteAllBytes(Path.Combine(dir, "seg-0.m4s"), [0x01]);
        hls.Publish(sha, staging);

        using (var scope = _factory.Services.CreateScope())
        {
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
    }

    private async Task<int> HlsJobCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BackgroundJobs.AsNoTracking()
            .CountAsync(j => j.Type == JobTypes.MediaVideoHlsGenerate);
    }

    [Fact]
    public async Task Allowlisted_Unprepared_Video_Returns_202_And_Enqueues()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadMp4Async(owner, "clip.mp4");
        await CreateTvAlbumWithAsync(owner, fileId);
        var cookie = await PairTvAsync(owner);

        var resp = await TvSendAsync(cookie, $"/api/tv/media/{fileId}/video");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal(1, await HlsJobCountAsync());
    }

    [Fact]
    public async Task Allowlisted_Ready_Video_Serves_Rewritten_Master_And_Segments()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadMp4Async(owner, "clip.mp4");
        await CreateTvAlbumWithAsync(owner, fileId);
        await PublishReadyLadderAsync(fileId);
        var cookie = await PairTvAsync(owner);

        var master = await TvSendAsync(cookie, $"/api/tv/media/{fileId}/video");
        Assert.Equal(HttpStatusCode.OK, master.StatusCode);
        Assert.Equal(
            VideoHlsServingService.MasterContentType,
            master.Content.Headers.ContentType?.MediaType);
        Assert.Contains("video/high/stream.m3u8", await master.Content.ReadAsStringAsync());

        var seg = await TvSendAsync(cookie, $"/api/tv/media/{fileId}/video/high/seg-0.m4s");
        Assert.Equal(HttpStatusCode.OK, seg.StatusCode);
        Assert.Equal("video/iso.segment", seg.Content.Headers.ContentType?.MediaType);

        var bad = await TvSendAsync(cookie, $"/api/tv/media/{fileId}/video/high/evil.sh");
        Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
    }

    [Fact]
    public async Task Hidden_Video_Is_404_Even_With_Ready_Ladder_And_Never_Enqueues()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var fileId = await UploadMp4Async(owner, "hidden.mp4"); // in NO allowlisted album
        await PublishReadyLadderAsync(fileId);
        var cookie = await PairTvAsync(owner);

        var master = await TvSendAsync(cookie, $"/api/tv/media/{fileId}/video");
        Assert.Equal(HttpStatusCode.NotFound, master.StatusCode);
        var seg = await TvSendAsync(cookie, $"/api/tv/media/{fileId}/video/high/seg-0.m4s");
        Assert.Equal(HttpStatusCode.NotFound, seg.StatusCode);
        Assert.Equal(0, await HlsJobCountAsync());
    }

    [Fact]
    public async Task Tv_Video_Routes_Require_A_Tv_Session()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"/api/tv/media/{Guid.NewGuid()}/video")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"/api/tv/media/{Guid.NewGuid()}/video/high/seg-0.m4s")).StatusCode);
    }

    // --- pairing/session helpers (same flow as TvMediaBrowsingTests) ---

    private async Task<string> PairTvAsync(HttpClient owner)
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
                personalPin = "123456",
                personalPinConfirmation = "123456",
            })).EnsureSuccessStatusCode();

        var pollRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        pollRequest.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var poll = await tvClient.SendAsync(pollRequest);
        poll.EnsureSuccessStatusCode();
        var setCookie = poll.Headers.GetValues("Set-Cookie").Single();
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }

    private Task<HttpResponseMessage> TvSendAsync(string cookieValue, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={cookieValue}");
        return _factory.CreateClient().SendAsync(request);
    }
}
