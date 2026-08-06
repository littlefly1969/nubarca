using System.Net;
using System.Net.Http.Headers;
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

// Slice 62 — authorized video playback with HTTP Range support.
// Video-hls slice 2: this class now covers the LEGACY direct-stream contract,
// which stays active while Media:VideoHlsProvider is unset/none (the default).
// The adaptive-HLS contract behind the flag is covered by
// FileVideoHlsEndpointTests.
public sealed class FileVideoEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FileVideoEndpointTests()
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

    // Build a 1024-byte MP4-ish blob: real ftyp header + filler bytes so the
    // /video endpoint has enough content to exercise range requests.
    private static byte[] BulkyMp4()
    {
        var head = ImageFixtures.MinimalMp4();
        var bytes = new byte[1024];
        Array.Copy(head, bytes, head.Length);
        for (var i = head.Length; i < bytes.Length; i++) bytes[i] = (byte)(i & 0xFF);
        return bytes;
    }

    // -- Detection results in the upload pipeline ------------------------

    [Fact]
    public async Task Upload_Of_Mp4_Bytes_Records_Video_MediaCategory()
    {
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "clip.mp4", "video/mp4");

        var meta = await InDbAsync(db => db.BlobMetadata.AsNoTracking()
            .SingleAsync(m => m.BlobObjectId == file.BlobObjectId));
        Assert.Equal(MediaCategories.Video, meta.MediaCategory);
        Assert.Equal("video/mp4", meta.DetectedContentType);
    }

    [Fact]
    public async Task Upload_Of_Webm_Bytes_Records_Video_MediaCategory()
    {
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.MinimalWebm(), "clip.webm", "video/webm");

        var meta = await InDbAsync(db => db.BlobMetadata.AsNoTracking()
            .SingleAsync(m => m.BlobObjectId == file.BlobObjectId));
        Assert.Equal(MediaCategories.Video, meta.MediaCategory);
        Assert.Equal("video/webm", meta.DetectedContentType);
    }

    [Fact]
    public async Task Spoofed_Mp4_Mime_With_Text_Bytes_Is_Not_Detected_As_Video()
    {
        // Client claims video/mp4 but uploads "this is plain text" bytes.
        // The server-side detector must NOT classify it as video; the /video
        // endpoint then returns 404.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(
            owner, "this is not a video"u8.ToArray(), "spoof.mp4", "video/mp4");

        var meta = await InDbAsync(db => db.BlobMetadata.AsNoTracking()
            .SingleAsync(m => m.BlobObjectId == file.BlobObjectId));
        // MediaCategory falls back from MIME (so it is "video"), but the
        // DetectedContentType is NULL → endpoint must reject.
        Assert.Null(meta.DetectedContentType);

        var resp = await client.GetAsync($"/api/files/{file.Id}/video");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // -- Endpoint authorization + behaviour --------------------------------

    [Fact]
    public async Task Video_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var resp = await anonymous.GetAsync($"/api/files/{Guid.NewGuid()}/video");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Video_Missing_File_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var resp = await client.GetAsync($"/api/files/{Guid.NewGuid()}/video");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Video_Foreign_File_Returns_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFile = await UploadAsync(alice, BulkyMp4(), "a.mp4", "video/mp4");

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var resp = await bobClient.GetAsync($"/api/files/{aliceFile.Id}/video");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Video_SoftDeleted_File_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");
        var del = await client.DeleteAsync($"/api/files/{file.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var resp = await client.GetAsync($"/api/files/{file.Id}/video");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Video_Endpoint_Returns_Detected_Video_ContentType_And_Nosniff()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(
            (await _factory.SeedUserAsync("u2@example.com")), // dummy seeded user (ignored)
            BulkyMp4(), "dummy.mp4", "video/mp4");
        // Upload as the authed owner.
        var (owner, _) = (await CreateAuthedOwnerAsync("owner@example.com"), client);
        var file = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");

        var resp = await client.GetAsync($"/api/files/{file.Id}/video");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("video/mp4", resp.Content.Headers.ContentType?.ToString());
        Assert.Contains("nosniff",
            string.Join(",", resp.Headers.GetValues("X-Content-Type-Options")));
    }

    // The factory's default authed client is "owner@example.com"; this helper
    // returns its userId without re-logging in (the client cookie stays valid).
    private async Task<Guid> CreateAuthedOwnerAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var u = await db.Users.AsNoTracking().SingleAsync(x => x.Email == email);
        return u.Id;
    }

    [Fact]
    public async Task Video_Endpoint_Honours_Range_Header_With_206_And_ContentRange()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{file.Id}/video");
        req.Headers.Range = new RangeHeaderValue(0, 99);
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        Assert.NotNull(resp.Content.Headers.ContentRange);
        Assert.Equal(0L, resp.Content.Headers.ContentRange!.From);
        Assert.Equal(99L, resp.Content.Headers.ContentRange.To);
        Assert.Equal(1024L, resp.Content.Headers.ContentRange.Length);

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(100, bytes.Length);
    }

    [Fact]
    public async Task Video_Endpoint_NonImage_NonVideo_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, "plain notes"u8.ToArray(), "notes.txt", "text/plain");

        var resp = await client.GetAsync($"/api/files/{file.Id}/video");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Video_Endpoint_Image_File_Returns_404()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "photo.jpg", "image/jpeg");

        var resp = await client.GetAsync($"/api/files/{file.Id}/video");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Original_Download_Endpoint_Still_Works_For_Video_Files()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, BulkyMp4(), "clip.mp4", "video/mp4");

        // The /content endpoint serves the original bytes as octet-stream
        // (slice 54.2). It is the explicit-download path and must keep
        // working independently of the new /video stream.
        var resp = await client.GetAsync($"/api/files/{file.Id}/content");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(1024, bytes.Length);
    }

    [Fact]
    public async Task Video_Endpoint_Response_Does_Not_Leak_Internals()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, BulkyMp4(), "leak.mp4", "video/mp4");

        var resp = await client.GetAsync($"/api/files/{file.Id}/video");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var headers = string.Join("\n",
            resp.Headers.Concat(resp.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));

        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
        }
    }
}
