using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 60 — cursor pagination for GET /api/images.
public sealed class ImagesCursorEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public ImagesCursorEndpointTests()
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

    private static MultipartFormDataContent Multipart(byte[] bytes, string name)
    {
        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(part, "file", name);
        return multipart;
    }

    // Seeds N image FileItems with CreatedAt spaced 1 minute apart so the
    // default sort (created desc) produces a deterministic order. Returns the
    // ids in upload order (i = 0 was uploaded FIRST = oldest).
    private async Task<List<Guid>> SeedImagesAsync(HttpClient client, int n)
    {
        var ids = new List<Guid>(n);
        for (var i = 0; i < n; i++)
        {
            var resp = await client.PostAsync("/api/files", Multipart(PngBytes(), $"img-{i:D3}.png"));
            resp.EnsureSuccessStatusCode();
            var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();
            ids.Add(summary!.Id);
        }
        // Backdate CreatedAt deterministically so created-desc returns the
        // last uploaded file first and the cursor's primary value (CreatedAt)
        // is unique per row.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var i = 0; i < ids.Count; i++)
        {
            var ts = new DateTime(2026, 5, 1, 0, i, 0, DateTimeKind.Utc);
            // These PNGs carry no embedded date or override, so the effective
            // date equals CreatedAt — backdate both so the datetaken cursor walk
            // exercises distinct date keyset values (slice 88), not just the Id
            // tie-break.
            await db.FileItems
                .Where(f => f.Id == ids[i])
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.CreatedAt, _ => ts)
                    .SetProperty(f => f.EffectiveDateTaken, _ => ts));
        }
        return ids;
    }

    private static async Task<ImageListResponse> GetImagesAsync(HttpClient client, string path)
    {
        var resp = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ImageListResponse>())!;
    }

    [Fact]
    public async Task Cursor_First_Page_Includes_NextCursor_When_More_Exists()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedImagesAsync(client, 7);

        var page = await GetImagesAsync(client, "/api/images?limit=3");

        Assert.Equal(3, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task Cursor_Last_Page_Has_No_NextCursor()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedImagesAsync(client, 3);

        var page = await GetImagesAsync(client, "/api/images?limit=10");

        Assert.Equal(3, page.Items.Count);
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Cursor_Second_Page_Does_Not_Overlap_First()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedImagesAsync(client, 6);

        var first = await GetImagesAsync(client, "/api/images?limit=3");
        var second = await GetImagesAsync(client,
            $"/api/images?limit=3&cursor={Uri.EscapeDataString(first.NextCursor!)}");

        Assert.Equal(3, second.Items.Count);
        var firstIds = first.Items.Select(i => i.Id).ToHashSet();
        Assert.All(second.Items, item => Assert.DoesNotContain(item.Id, firstIds));
    }

    [Fact]
    public async Task Cursor_Walk_Visits_All_Items_Exactly_Once()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var seeded = await SeedImagesAsync(client, 10);

        var visited = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var qs = cursor is null
                ? "/api/images?limit=3"
                : $"/api/images?limit=3&cursor={Uri.EscapeDataString(cursor)}";
            var resp = await GetImagesAsync(client, qs);
            visited.AddRange(resp.Items.Select(i => i.Id));
            cursor = resp.NextCursor;
            if (cursor is null) break;
        }

        Assert.Equal(seeded.Count, visited.Count);
        Assert.Equal(seeded.OrderBy(x => x).ToList(),
            visited.OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task Malformed_Cursor_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var resp = await client.GetAsync("/api/images?cursor=not-a-valid-cursor");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Cursor_With_Mismatched_Sort_Returns_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedImagesAsync(client, 4);

        // Issue a cursor under default sort=created…
        var first = await GetImagesAsync(client, "/api/images?limit=2");
        Assert.NotNull(first.NextCursor);

        // …then try to use it under sort=name. The cursor's stored sort
        // doesn't match the request's sort → 400.
        var resp = await client.GetAsync(
            $"/api/images?limit=2&sort=name&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Cursor_And_Offset_Cannot_Be_Combined()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedImagesAsync(client, 4);
        var first = await GetImagesAsync(client, "/api/images?limit=2");

        var resp = await client.GetAsync(
            $"/api/images?limit=2&offset=1&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Cursor_Is_Owner_Scoped()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceClient = await _factory.LoginAsync("alice@example.com");
        await SeedImagesAsync(aliceClient, 4);
        var alicePage = await GetImagesAsync(aliceClient, "/api/images?limit=2");
        Assert.NotNull(alicePage.NextCursor);

        // Bob's gallery is empty. Even using Alice's cursor he gets an empty
        // page (cursor only restricts the seek, owner-scoping is still
        // enforced by the outer query).
        var bob = await _factory.SeedUserAsync("bob@example.com");
        var bobClient = await _factory.LoginAsync("bob@example.com");
        var bobPage = await GetImagesAsync(bobClient,
            $"/api/images?limit=10&cursor={Uri.EscapeDataString(alicePage.NextCursor!)}");

        Assert.Empty(bobPage.Items);
        Assert.False(bobPage.HasMore);
    }

    [Theory]
    [InlineData("created", "asc")]
    [InlineData("created", "desc")]
    [InlineData("name", "asc")]
    [InlineData("name", "desc")]
    [InlineData("size", "asc")]
    [InlineData("size", "desc")]
    [InlineData("datetaken", "asc")]
    [InlineData("datetaken", "desc")]
    public async Task Cursor_Walk_Produces_Same_Order_As_Single_Page_For_Every_Sort(
        string sort, string direction)
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedImagesAsync(client, 8);

        var full = await GetImagesAsync(client,
            $"/api/images?limit=20&sort={sort}&direction={direction}");
        var fullOrder = full.Items.Select(i => i.Id).ToList();

        var walked = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var qs = cursor is null
                ? $"/api/images?limit=3&sort={sort}&direction={direction}"
                : $"/api/images?limit=3&sort={sort}&direction={direction}&cursor={Uri.EscapeDataString(cursor)}";
            var resp = await GetImagesAsync(client, qs);
            walked.AddRange(resp.Items.Select(i => i.Id));
            cursor = resp.NextCursor;
            if (cursor is null) break;
        }

        Assert.Equal(fullOrder, walked);
    }

    [Fact]
    public async Task Cursor_Respects_Q_Filter()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedImagesAsync(client, 5);

        var firstQ = await GetImagesAsync(client, "/api/images?limit=2&q=img-00");
        // q=img-00 matches img-000..img-004 (5 items by name). Cursor walk
        // must return them all without overlap.
        var visited = firstQ.Items.Select(i => i.Id).ToList();
        var cursor = firstQ.NextCursor;
        for (var safety = 0; safety < 10 && cursor is not null; safety++)
        {
            var p = await GetImagesAsync(client,
                $"/api/images?limit=2&q=img-00&cursor={Uri.EscapeDataString(cursor)}");
            visited.AddRange(p.Items.Select(i => i.Id));
            cursor = p.NextCursor;
        }
        Assert.Equal(5, visited.Distinct().Count());
    }

    [Fact]
    public async Task Limit_Cap_Holds_In_Cursor_Mode()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedImagesAsync(client, 3);

        // limit=500 is over the 200 cap; the response should still cap to ≤200.
        var page = await GetImagesAsync(client, "/api/images?limit=500");
        Assert.True(page.Limit <= 200);
        Assert.Equal(3, page.Items.Count);
    }

    [Fact]
    public async Task Legacy_Offset_Mode_Still_Works()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedImagesAsync(client, 5);

        var page = await GetImagesAsync(client, "/api/images?limit=2&offset=2");
        Assert.Equal(2, page.Offset);
        Assert.Equal(2, page.Items.Count);
        // Legacy path returns no cursor — clients that opted in via offset
        // continue to drive pagination by offset.
        Assert.Null(page.NextCursor);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Cursor_Response_Does_Not_Leak_Internals()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedImagesAsync(client, 4);

        var resp = await client.GetAsync("/api/images?limit=2");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var headers = string.Join("\n",
            resp.Headers.Concat(resp.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));

        var forbidden = new[]
        {
            "StorageKey", "storageKey",
            "BlobObjectId", "blobObjectId",
            "OwnerUserId", "ownerUserId",
            "Sha256", "sha256",
            "TokenHash", "tokenHash",
            "RawMetadataJson", "rawMetadataJson",
            "objects/",
        };
        foreach (var needle in forbidden)
        {
            Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
        }
    }
}
