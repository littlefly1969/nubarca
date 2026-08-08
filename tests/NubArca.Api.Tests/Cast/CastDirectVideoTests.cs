using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Cast;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Cast;

// NUBARCA-GOOGLE-CAST-01 — the PROGRESSIVE contract, active when the
// installation runs the direct/original video mode (Media:VideoHlsProvider
// unset, the default). A receiver that pulls the original bytes needs real HTTP
// Range semantics or it cannot seek at all, so those are the assertions here.
public sealed class CastDirectVideoTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public CastDirectVideoTests()
    {
        // No Media:VideoHlsProvider — the legacy/progressive contract.
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static byte[] BulkyMp4()
    {
        var head = ImageFixtures.MinimalMp4();
        var bytes = new byte[4096];
        Array.Copy(head, bytes, head.Length);
        for (var i = head.Length; i < bytes.Length; i++) bytes[i] = (byte)(i & 0xFF);
        return bytes;
    }

    private async Task<(HttpClient Client, string ContentPath, string ContentType, string Mode)>
        CastableAsync()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        FileItem file;
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            file = await files.CreateAsync(
                owner, null, "clip.mp4", "video/mp4", new MemoryStream(BulkyMp4()));
        }

        var response = await client.PostAsync($"/api/cast/videos/{file.Id}/grant", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (
            client,
            json.GetProperty("contentPath").GetString()!,
            json.GetProperty("contentType").GetString()!,
            json.GetProperty("mode").GetString()!);
    }

    [Fact]
    public async Task Progressive_Mode_Is_Announced_With_The_Detected_Video_Type()
    {
        var (_, _, contentType, mode) = await CastableAsync();

        Assert.Equal(CastPlaybackModes.Direct, mode);
        Assert.Equal("video/mp4", contentType);
    }

    [Fact]
    public async Task Progressive_Playback_Serves_The_Whole_Stream()
    {
        var (_, contentPath, _, _) = await CastableAsync();
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(contentPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(4096, response.Content.Headers.ContentLength);
        // A playback URL, never a download: no attachment disposition.
        Assert.Null(response.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task Progressive_Playback_Supports_Range_Requests()
    {
        var (_, contentPath, _, _) = await CastableAsync();
        var anonymous = _factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, contentPath);
        request.Headers.Range = new RangeHeaderValue(100, 199);
        var response = await anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes", response.Headers.AcceptRanges.ToString());
        Assert.NotNull(response.Content.Headers.ContentRange);
        Assert.Equal(100, response.Content.Headers.ContentRange!.From);
        Assert.Equal(199, response.Content.Headers.ContentRange.To);
        Assert.Equal(4096, response.Content.Headers.ContentRange.Length);
        Assert.Equal(100, response.Content.Headers.ContentLength);

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(100, body.Length);
        Assert.Equal((byte)(100 & 0xFF), body[0]);
    }

    [Fact]
    public async Task Progressive_Playback_Still_Requires_A_Valid_Token()
    {
        var (_, contentPath, _, _) = await CastableAsync();
        var anonymous = _factory.CreateClient();
        var withoutToken = contentPath[..contentPath.IndexOf("?token=", StringComparison.Ordinal)];

        Assert.Equal(
            HttpStatusCode.NotFound, (await anonymous.GetAsync(withoutToken)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound, (await anonymous.GetAsync($"{withoutToken}?token=x")).StatusCode);
    }

    // With the HLS provider off there is no ladder to serve, so the child route
    // is a 404 rather than a second way to reach bytes.
    [Fact]
    public async Task The_Hls_Child_Route_Is_Absent_In_Progressive_Mode()
    {
        var (_, contentPath, _, _) = await CastableAsync();
        var anonymous = _factory.CreateClient();
        var basePath = contentPath[..contentPath.IndexOf("/video", StringComparison.Ordinal)];
        var token = contentPath[(contentPath.IndexOf("?token=", StringComparison.Ordinal) + 7)..];

        var response = await anonymous.GetAsync($"{basePath}/hls/high/seg-0.m4s?token={token}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
