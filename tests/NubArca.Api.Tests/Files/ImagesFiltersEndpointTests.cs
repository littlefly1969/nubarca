using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 61 — gallery discovery: extended q search across name + user
// title / description / tags, and compact filters (favorite, minRating,
// hasGps, dateTakenFrom/to). All filters owner-scoped.
public sealed class ImagesFiltersEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public ImagesFiltersEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static byte[] PngBytes(int w = 16, int h = 16)
    {
        using var img = new Image<Rgba32>(w, h);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static MultipartFormDataContent Multipart(byte[] bytes, string name, string mime = "image/png")
    {
        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(mime);
        multipart.Add(part, "file", name);
        return multipart;
    }

    private async Task<Guid> UploadAsync(HttpClient client, string name, byte[]? bytes = null, string mime = "image/png")
    {
        var resp = await client.PostAsync("/api/files",
            Multipart(bytes ?? PngBytes(), name, mime));
        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();
        return summary!.Id;
    }

    private async Task PatchMetaAsync(HttpClient client, Guid fileId, object body)
    {
        var resp = await client.PatchAsJsonAsync($"/api/files/{fileId}/metadata", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static async Task<ImageListResponse> GetAsync(HttpClient client, string path)
    {
        var resp = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ImageListResponse>())!;
    }

    // -- q expansion -------------------------------------------------------

    [Fact]
    public async Task Q_Matches_File_Name()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var a = await UploadAsync(client, "sunset.png");
        var b = await UploadAsync(client, "forest.png");

        var page = await GetAsync(client, "/api/images?q=sun");
        Assert.Contains(page.Items, i => i.Id == a);
        Assert.DoesNotContain(page.Items, i => i.Id == b);
    }

    [Fact]
    public async Task Q_Matches_User_Title()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var a = await UploadAsync(client, "a.png");
        var b = await UploadAsync(client, "b.png");
        await PatchMetaAsync(client, a, new { title = "Vacation in Spain" });

        var page = await GetAsync(client, "/api/images?q=vacation");
        Assert.Contains(page.Items, i => i.Id == a);
        Assert.DoesNotContain(page.Items, i => i.Id == b);
    }

    [Fact]
    public async Task Q_Matches_User_Description()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var a = await UploadAsync(client, "a.png");
        await PatchMetaAsync(client, a, new { description = "Mountains and forest at dawn." });

        var page = await GetAsync(client, "/api/images?q=mountain");
        Assert.Contains(page.Items, i => i.Id == a);
    }

    [Fact]
    public async Task Q_Matches_User_Tags()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var a = await UploadAsync(client, "a.png");
        var b = await UploadAsync(client, "b.png");
        await PatchMetaAsync(client, a, new { tags = new[] { "park", "summer" } });

        var page = await GetAsync(client, "/api/images?q=park");
        Assert.Contains(page.Items, i => i.Id == a);
        Assert.DoesNotContain(page.Items, i => i.Id == b);
    }

    [Fact]
    public async Task Q_Does_Not_Match_Other_Users_Metadata()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceClient = await _factory.LoginAsync("alice@example.com");
        var aliceFile = await UploadAsync(aliceClient, "a.png");
        await PatchMetaAsync(aliceClient, aliceFile, new { title = "secret-marker" });

        var bob = await _factory.SeedUserAsync("bob@example.com");
        var bobClient = await _factory.LoginAsync("bob@example.com");
        await UploadAsync(bobClient, "b.png");

        var bobPage = await GetAsync(bobClient, "/api/images?q=secret-marker");
        Assert.Empty(bobPage.Items);
    }

    [Fact]
    public async Task Q_Does_Not_Search_Raw_Embedded_Metadata()
    {
        // Slice 54 stores camera make / model / serials internally on
        // BlobMetadata, but q must NOT match against them — only the user-
        // supplied title / description / tags + file name.
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        // Use the EXIF fixture so the BlobMetadata row carries CameraMake =
        // "NanoCam" + BodySerial = "BODY-SN-SECRET-XYZ".
        await client.PostAsync("/api/files", Multipart(
            ImageFixtures.JpegWithExif(includeGps: true), "leak.jpg", "image/jpeg"));

        var byCamera = await GetAsync(client, $"/api/images?q={ImageFixtures.CameraMake}");
        Assert.Empty(byCamera.Items);
        var bySerial = await GetAsync(client, "/api/images?q=BODY-SN-SECRET");
        Assert.Empty(bySerial.Items);
    }

    // -- compact filters ---------------------------------------------------

    [Fact]
    public async Task Favorite_Filter_Owner_Scoped()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var a = await UploadAsync(client, "a.png");
        var b = await UploadAsync(client, "b.png");
        await PatchMetaAsync(client, a, new { favorite = true });

        var favPage = await GetAsync(client, "/api/images?favorite=true");
        Assert.Contains(favPage.Items, i => i.Id == a);
        Assert.DoesNotContain(favPage.Items, i => i.Id == b);

        var notFavPage = await GetAsync(client, "/api/images?favorite=false");
        Assert.DoesNotContain(notFavPage.Items, i => i.Id == a);
        Assert.Contains(notFavPage.Items, i => i.Id == b);
    }

    [Fact]
    public async Task MinRating_Filter_Validates_Range()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/images?minRating=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/images?minRating=6")).StatusCode);
    }

    [Fact]
    public async Task MinRating_Filter_Works()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var a = await UploadAsync(client, "a.png");
        var b = await UploadAsync(client, "b.png");
        var c = await UploadAsync(client, "c.png");
        await PatchMetaAsync(client, a, new { rating = 5 });
        await PatchMetaAsync(client, b, new { rating = 3 });

        var page = await GetAsync(client, "/api/images?minRating=4");
        Assert.Contains(page.Items, i => i.Id == a);
        Assert.DoesNotContain(page.Items, i => i.Id == b);
        Assert.DoesNotContain(page.Items, i => i.Id == c);
    }

    [Fact]
    public async Task HasGps_True_Selects_Only_Gps_Images_Without_Exposing_Coords()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        // Two uploads: one with GPS EXIF, one without.
        await client.PostAsync("/api/files",
            Multipart(ImageFixtures.JpegWithExif(includeGps: true), "geo.jpg", "image/jpeg"));
        await client.PostAsync("/api/files",
            Multipart(ImageFixtures.JpegWithExif(includeGps: false), "plain.jpg", "image/jpeg"));

        var withGps = await GetAsync(client, "/api/images?hasGps=true");
        var withoutGps = await GetAsync(client, "/api/images?hasGps=false");

        Assert.Single(withGps.Items);
        Assert.Single(withoutGps.Items);
        Assert.NotEqual(withGps.Items[0].Id, withoutGps.Items[0].Id);

        // No-leak: coordinates / sensitive embedded names never appear.
        var raw = await (await client.GetAsync("/api/images?hasGps=true")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Latitude", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Longitude", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ImageFixtures.BodySerial, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DateTaken_From_To_Uses_Effective_Date()
    {
        // Effective date precedence: user override → embedded DateTaken →
        // CreatedAt. This test exercises each branch.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();

        var uploadedOnly = await UploadAsync(client, "uploaded.png"); // CreatedAt fallback
        var embedded = await UploadAsync(client, "embedded.jpg",
            ImageFixtures.JpegWithExif(), "image/jpeg"); // embedded DateTaken
        var overriden = await UploadAsync(client, "override.png");
        await PatchMetaAsync(client, overriden,
            new { dateTakenOverride = "2024-07-01T10:00:00Z" });

        // Backdate the uploadedOnly file's CreatedAt to a known instant. It has
        // no embedded date or override, so its effective date IS CreatedAt —
        // keep the denormalized EffectiveDateTaken column in sync (slice 88).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var backdated = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            await db.FileItems.Where(f => f.Id == uploadedOnly)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.CreatedAt, _ => backdated)
                    .SetProperty(f => f.EffectiveDateTaken, _ => backdated));
        }

        // The embedded fixture's DateTaken is 2023-06-15 14:30:00 UTC.
        // Range [2024-01-01, 2024-07-01]: includes uploadedOnly + overriden,
        // excludes embedded (older than from).
        var page = await GetAsync(client,
            "/api/images?dateTakenFrom=2024-01-01T00:00:00Z&dateTakenTo=2024-07-01T23:59:59Z");
        Assert.Contains(page.Items, i => i.Id == uploadedOnly);
        Assert.Contains(page.Items, i => i.Id == overriden);
        Assert.DoesNotContain(page.Items, i => i.Id == embedded);
    }

    [Fact]
    public async Task DateTaken_From_After_To_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var resp = await client.GetAsync(
            "/api/images?dateTakenFrom=2024-12-31T00:00:00Z&dateTakenTo=2024-01-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Invalid_Date_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var resp = await client.GetAsync("/api/images?dateTakenFrom=not-a-date");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // -- cursor + filter binding ------------------------------------------

    [Fact]
    public async Task Cursor_With_Filter_Walks_All_Matches_Without_Overlap()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await UploadAsync(client, $"x-{i}.png"));
        }
        // Mark the first 3 as favorites.
        foreach (var id in ids.Take(3))
        {
            await PatchMetaAsync(client, id, new { favorite = true });
        }

        var first = await GetAsync(client, "/api/images?favorite=true&limit=2");
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextCursor);

        var second = await GetAsync(client,
            $"/api/images?favorite=true&limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Single(second.Items);
        Assert.False(second.HasMore);

        var visited = first.Items.Select(i => i.Id).Concat(second.Items.Select(i => i.Id)).ToHashSet();
        Assert.Equal(3, visited.Count);
        Assert.All(visited, id => Assert.Contains(id, ids.Take(3)));
    }

    [Fact]
    public async Task Cursor_Issued_Under_Filters_Rejected_When_Filters_Change()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        for (var i = 0; i < 5; i++) await UploadAsync(client, $"x-{i}.png");

        var first = await GetAsync(client, "/api/images?q=x&limit=2");
        Assert.NotNull(first.NextCursor);

        // Drop the q. Cursor was bound to (q="x"); request now has no
        // filter at all → fingerprint mismatch → 400.
        var resp = await client.GetAsync(
            $"/api/images?limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // Same q but additional filter → also a mismatch.
        var resp2 = await client.GetAsync(
            $"/api/images?q=x&favorite=true&limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, resp2.StatusCode);
    }

    [Fact]
    public async Task Unfiltered_Cursor_Still_Works_For_Unfiltered_Request()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        for (var i = 0; i < 5; i++) await UploadAsync(client, $"x-{i}.png");

        var first = await GetAsync(client, "/api/images?limit=2");
        Assert.NotNull(first.NextCursor);

        var second = await GetAsync(client,
            $"/api/images?limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(2, second.Items.Count);
    }

    [Fact]
    public async Task Filters_Response_Does_Not_Leak_Internals()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await client.PostAsync("/api/files",
            Multipart(ImageFixtures.JpegWithExif(includeGps: true), "leak.jpg", "image/jpeg"));

        var resp = await client.GetAsync(
            "/api/images?favorite=false&hasGps=true&minRating=0&limit=10");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var headers = string.Join("\n",
            resp.Headers.Concat(resp.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));

        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
        }
    }
}
