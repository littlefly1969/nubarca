using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// A party's videos are WATCHED, not just looked at: the album's adaptive HLS
/// ladder, served under the party's own token on the lifecycle the gallery
/// obeys — and the same shared serving path an album shared by link uses.
///
/// What these defend:
///   * a video item carries its playback address, an image does not, and a
///     video still has no download — a party hands out no originals;
///   * the master is 202 while the ladder is prepared and the playlist once it
///     is, and the rendition files resolve under the party's own route;
///   * before the party there is nothing to watch, exactly as there is nothing
///     to see; a picture, a stranger's file or a dead token is a 404;
///   * without an HLS provider there is no playback address at all;
///   * the album shared by link still plays through the same path.
/// </summary>
public sealed class PartyVideoPlaybackTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new(new Dictionary<string, string?>
    {
        ["Media:VideoHlsProvider"] = "ffmpeg",
    });

    public PartyVideoPlaybackTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_Party_Video_Is_Played_From_Its_Ladder_And_A_Picture_Is_Not()
    {
        var (owner, album) = await AlbumAsync(_factory);
        var video = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");
        var photo = await UploadAsync(owner, ImageFixtures.PlainPng(), "photo.png", "image/png");
        await AddAsync(owner, album, video);
        await AddAsync(owner, album, photo);
        var (partyId, token) = await EnablePartyAsync(owner, album);
        var guest = _factory.CreateClient();

        // Before the party: nothing to see, so nothing to watch.
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{token}/items")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{token}/media/{video}/video")).StatusCode);

        await StartLiveAsync(owner, partyId);

        var items = (await guest.GetFromJsonAsync<JsonElement>($"/api/party/{token}/items"))
            .GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid());
        Assert.Equal($"/api/party/{token}/media/{video}/video", items[video].GetProperty("playbackUrl").GetString());
        Assert.Equal(JsonValueKind.Null, items[video].GetProperty("downloadUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[photo].GetProperty("playbackUrl").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, items[photo].GetProperty("downloadUrl").ValueKind);

        // Preparing, then the published ladder.
        var preparing = await guest.GetAsync($"/api/party/{token}/media/{video}/video");
        Assert.Equal(HttpStatusCode.Accepted, preparing.StatusCode);
        Assert.Equal("no-store", preparing.Headers.CacheControl!.ToString());

        await PublishReadyLadderAsync(video);
        var master = await guest.GetAsync($"/api/party/{token}/media/{video}/video");
        Assert.Equal(HttpStatusCode.OK, master.StatusCode);
        Assert.Equal(VideoHlsServingService.MasterContentType, master.Content.Headers.ContentType?.MediaType);
        Assert.Contains("video/high/stream.m3u8", await master.Content.ReadAsStringAsync());

        var rendition = await guest.GetAsync($"/api/party/{token}/media/{video}/video/high/stream.m3u8");
        Assert.Equal(HttpStatusCode.OK, rendition.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", rendition.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK,
            (await guest.GetAsync($"/api/party/{token}/media/{video}/video/low/seg-0.m4s")).StatusCode);

        // A picture is not a video; a file outside the album and a dead token are nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{token}/media/{photo}/video")).StatusCode);
        var stranger = await UploadAsync(owner, BulkyMp4(salt: 7), "other.mp4", "video/mp4");
        await PublishReadyLadderAsync(stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{token}/media/{stranger}/video")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await guest.GetAsync($"/api/party/{token}/media/{stranger}/video/high/stream.m3u8")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/not-a-token/media/{video}/video")).StatusCode);
    }

    [Fact]
    public async Task An_Album_Shared_By_Link_Plays_Through_The_Same_Path()
    {
        var (owner, album) = await AlbumAsync(_factory);
        var video = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");
        await AddAsync(owner, album, video);
        var share = await owner.PostAsync($"/api/albums/{album}/share-link", null);
        share.EnsureSuccessStatusCode();
        var token = (await share.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!["/album/".Length..];
        var visitor = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Accepted, (await visitor.GetAsync($"/api/album-share/{token}/media/{video}/video")).StatusCode);
        await PublishReadyLadderAsync(video);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/api/album-share/{token}/media/{video}/video")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await visitor.GetAsync($"/api/album-share/{token}/media/{video}/video/high/stream.m3u8")).StatusCode);
    }

    [Fact]
    public async Task Without_An_Hls_Provider_A_Party_Video_Has_No_Playback()
    {
        using var plain = new SqliteWebApplicationFactory();
        plain.EnsureDatabaseCreated();
        var (owner, album) = await AlbumAsync(plain);
        var video = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");
        await AddAsync(owner, album, video);
        var (partyId, token) = await EnablePartyAsync(owner, album);
        await StartLiveAsync(owner, partyId);
        var guest = plain.CreateClient();

        var item = (await guest.GetFromJsonAsync<JsonElement>($"/api/party/{token}/items"))
            .GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, item.GetProperty("playbackUrl").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{token}/media/{video}/video")).StatusCode);
    }

    // --- helpers -----------------------------------------------------------

    // `salt` makes a different file: identical bytes would be one deduplicated blob.
    private static byte[] BulkyMp4(byte salt = 0)
    {
        var head = ImageFixtures.MinimalMp4();
        var bytes = new byte[1024];
        Array.Copy(head, bytes, head.Length);
        for (var i = head.Length; i < bytes.Length; i++) bytes[i] = (byte)((i + salt) & 0xFF);
        return bytes;
    }

    private static async Task<(HttpClient Owner, Guid Album)> AlbumAsync(SqliteWebApplicationFactory factory)
    {
        var (_, owner) = await factory.CreateAuthenticatedClientAsync();
        var response = await owner.PostAsJsonAsync("/api/albums", new { name = "Festa" });
        response.EnsureSuccessStatusCode();
        return (owner, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    private static async Task<Guid> UploadAsync(HttpClient owner, byte[] bytes, string name, string mime)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(mime);
        var upload = await owner.PostAsync("/api/files", new MultipartFormDataContent { { part, "file", name } });
        upload.EnsureSuccessStatusCode();
        return (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task AddAsync(HttpClient owner, Guid album, Guid file) =>
        (await owner.PostAsJsonAsync($"/api/albums/{album}/items", new { fileItemId = file }))
            .EnsureSuccessStatusCode();

    private static async Task<(Guid PartyId, string Token)> EnablePartyAsync(HttpClient owner, Guid album)
    {
        var enable = await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings", new { enabled = true });
        enable.EnsureSuccessStatusCode();
        var settings = await enable.Content.ReadFromJsonAsync<JsonElement>();
        return (settings.GetProperty("partyId").GetGuid(),
            settings.GetProperty("partyUrl").GetString()!["/party/".Length..]);
    }

    private static async Task StartLiveAsync(HttpClient owner, Guid partyId)
    {
        var version = (await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}"))
            .GetProperty("version").GetInt32();
        (await owner.PostAsJsonAsync($"/api/parties/{partyId}/start-live", new { version }))
            .EnsureSuccessStatusCode();
    }

    // A fake (but shape-correct) published ladder and its ready row, as a
    // completed generation run leaves them (FileVideoHlsEndpointTests).
    private async Task PublishReadyLadderAsync(Guid fileId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == fileId);
        var blob = await db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == file.BlobObjectId);
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
        hls.Publish(blob.Sha256, staging);
        db.BlobHlsDerivatives.Add(new BlobHlsDerivative
        {
            Id = Guid.NewGuid(),
            BlobObjectId = blob.Id,
            Status = VideoHlsStatuses.Ready,
            Version = FfmpegVideoHlsTranscoder.Version,
            CreatedAt = DateTime.UtcNow,
            ReadyAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
