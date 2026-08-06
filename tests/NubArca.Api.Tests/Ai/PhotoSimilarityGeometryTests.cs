using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Ai;

// Similar-photo results carry the ORIGINAL media's DISPLAY pixel dimensions, so
// a client can lay each result out at its true proportions instead of guessing a
// square.
//
// What these tests pin:
//   * width/height are mapped from the persisted BlobMetadata extracted at
//     ingestion (landscape stays landscape, portrait stays portrait);
//   * EXIF orientation is resolved through the same ImageDisplayDimensions
//     helper the library listing uses, so a quarter-turned photo reports the
//     portrait shape its auto-oriented thumbnail actually has;
//   * a blob with no extracted dimensions reports null/null so the client can
//     fall back, rather than reporting a misleading value;
//   * adding geometry changes NOTHING about ranking: the same ids, the same
//     order and the same scores come back whether dimensions exist or not.
public sealed class PhotoSimilarityGeometryTests
{
    // Mirrors SimilarPhotosPage (web JSON defaults: camelCase, case-insensitive).
    private sealed record Page(
        bool ProfileAvailable,
        bool QueryIndexed,
        List<Item> Items,
        string? NextCursor,
        bool HasMore,
        string? UnavailableReason);

    private sealed record Item(Guid FileItemId, string Name, double Score, int? Width, int? Height);

    // The legacy Top-N shape on the same route (no minSimilarity, no cursor).
    private sealed record LegacyResult(
        bool ProfileAvailable,
        bool QueryIndexed,
        List<Item> Items,
        string? UnavailableReason);

    // A reader that deliberately knows nothing about the new fields, used to
    // prove the JSON addition is backward compatible.
    private sealed record OldItem(Guid FileItemId, string Name, double Score);

    private sealed record OldPage(
        bool ProfileAvailable,
        bool QueryIndexed,
        List<OldItem> Items,
        string? NextCursor,
        bool HasMore,
        string? UnavailableReason);

    private static SqliteWebApplicationFactory EnabledFactory()
    {
        var factory = new SqliteWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["Ai:Enabled"] = "true",
                ["Ai:ImageEmbeddingsEnabled"] = "true",
            },
            poolHost: true);
        factory.EnsureDatabaseCreated();
        return factory;
    }

    private static async Task SeedProfilesAsync(SqliteWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
        await registry.SeedDeterministicProfilesAsync();
    }

    private static byte[] Png(int width, int height)
    {
        using var img = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static async Task<Guid> UploadPngAsync(HttpClient client, string name, int width, int height)
    {
        var part = new ByteArrayContent(Png(width, height));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var resp = await client.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();
        return summary!.Id;
    }

    private static async Task RunBackfillAsync(SqliteWebApplicationFactory factory)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            await queue.EnqueueAsync(JobTypes.AiPhotosEmbeddingsBackfill, new AiBackfillJobPayload());
        }
        for (var i = 0; i < 50; i++)
        {
            using var scope = factory.Services.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            if (await processor.ProcessAvailableAsync(10) == 0) break;
        }
    }

    private static Task<Page?> GetPageAsync(HttpClient client, Guid query, double minSim = 0.0)
        => client.GetFromJsonAsync<Page>(
            $"/api/files/{query}/similar?minSimilarity={minSim.ToString(System.Globalization.CultureInfo.InvariantCulture)}&limit=50");

    // Blank every extracted dimension, simulating a pre-metadata import or a
    // failed extraction, WITHOUT touching any embedding.
    private static async Task ClearAllDimensionsAsync(SqliteWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var meta in await db.BlobMetadata.ToListAsync())
        {
            meta.Width = null;
            meta.Height = null;
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Results_Carry_The_Original_Landscape_And_Portrait_Dimensions()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var query = await UploadPngAsync(client, "query.png", 40, 40);
        var landscape = await UploadPngAsync(client, "landscape.png", 64, 24);
        var portrait = await UploadPngAsync(client, "portrait.png", 24, 64);
        var square = await UploadPngAsync(client, "square.png", 48, 48);
        await RunBackfillAsync(factory);

        // The Top-N shape, so every indexed neighbour is returned regardless of
        // its score: the explorer's threshold is clamped to [0,1] and cosine
        // similarity can legitimately be negative, which would otherwise hide a
        // shape from this mapping assertion for reasons unrelated to geometry.
        var result = await client.GetFromJsonAsync<LegacyResult>($"/api/files/{query}/similar?limit=20");
        Assert.NotNull(result);

        var byId = result!.Items.ToDictionary(i => i.FileItemId);
        Assert.Equal(3, byId.Count);

        // Each result keeps the ORIGINAL's real geometry — not a derivative size
        // and not a square guess.
        Assert.Equal((64, 24), (byId[landscape].Width, byId[landscape].Height));
        Assert.Equal((24, 64), (byId[portrait].Width, byId[portrait].Height));
        Assert.Equal((48, 48), (byId[square].Width, byId[square].Height));

        // …which is what makes a proportional layout possible at all.
        Assert.True(byId[landscape].Width > byId[landscape].Height, "landscape must stay landscape");
        Assert.True(byId[portrait].Height > byId[portrait].Width, "portrait must stay portrait");
        Assert.Equal(byId[square].Width, byId[square].Height);
    }

    [Fact]
    public async Task Explorer_Page_Also_Carries_Landscape_And_Portrait_Geometry()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var query = await UploadPngAsync(client, "query.png", 40, 40);
        await UploadPngAsync(client, "landscape.png", 64, 24);
        await UploadPngAsync(client, "portrait.png", 24, 64);
        await UploadPngAsync(client, "square.png", 48, 48);
        await RunBackfillAsync(factory);

        var page = await GetPageAsync(client, query);
        Assert.NotNull(page);
        Assert.NotEmpty(page!.Items);

        // Whatever clears the threshold carries its true, non-square-guessed
        // geometry, and the shapes are preserved rather than normalized.
        Assert.All(page.Items, item =>
        {
            Assert.NotNull(item.Width);
            Assert.NotNull(item.Height);
            var expected = item.Name switch
            {
                "landscape.png" => (64, 24),
                "portrait.png" => (24, 64),
                "square.png" => (48, 48),
                _ => throw new InvalidOperationException($"unexpected result '{item.Name}'"),
            };
            Assert.Equal(expected, (item.Width!.Value, item.Height!.Value));
        });
    }

    [Fact]
    public async Task Legacy_TopN_Shape_On_The_Same_Route_Also_Carries_Dimensions()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var query = await UploadPngAsync(client, "query.png", 40, 40);
        var portrait = await UploadPngAsync(client, "portrait.png", 24, 64);
        await RunBackfillAsync(factory);

        var result = await client.GetFromJsonAsync<LegacyResult>($"/api/files/{query}/similar?limit=20");

        Assert.NotNull(result);
        var item = Assert.Single(result!.Items, i => i.FileItemId == portrait);
        Assert.Equal(24, item.Width);
        Assert.Equal(64, item.Height);
    }

    [Fact]
    public async Task Missing_Dimensions_Are_Reported_As_Null_Rather_Than_Guessed()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var query = await UploadPngAsync(client, "query.png", 40, 40);
        await UploadPngAsync(client, "other.png", 64, 24);
        await RunBackfillAsync(factory);
        await ClearAllDimensionsAsync(factory);

        var page = await GetPageAsync(client, query);

        Assert.NotNull(page);
        var item = Assert.Single(page!.Items);
        // The client must be able to tell "unknown" from a real ratio, so it can
        // apply its own fallback instead of rendering a lie.
        Assert.Null(item.Width);
        Assert.Null(item.Height);
    }

    [Fact]
    public async Task A_Half_Known_Dimension_Pair_Is_Reported_As_Unknown()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var query = await UploadPngAsync(client, "query.png", 40, 40);
        var other = await UploadPngAsync(client, "other.png", 64, 24);
        await RunBackfillAsync(factory);

        // Width survives, height is lost — an aspect ratio is not derivable.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blobId = await db.FileItems.Where(f => f.Id == other)
                .Select(f => f.BlobObjectId).FirstAsync();
            var meta = await db.BlobMetadata.FirstAsync(m => m.BlobObjectId == blobId);
            meta.Height = null;
            await db.SaveChangesAsync();
        }

        var page = await GetPageAsync(client, query);

        var item = Assert.Single(page!.Items, i => i.FileItemId == other);
        Assert.Null(item.Width);
        Assert.Null(item.Height);
    }

    [Fact]
    public async Task Geometry_Does_Not_Change_Ordering_Or_Scores()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var query = await UploadPngAsync(client, "query.png", 40, 40);
        for (var i = 0; i < 6; i++)
        {
            // Deliberately mixed shapes, so any geometry-driven reordering shows.
            await UploadPngAsync(client, $"img-{i}.png", 20 + i * 4, 60 - i * 4);
        }
        await RunBackfillAsync(factory);

        var withGeometry = await GetPageAsync(client, query);
        Assert.NotNull(withGeometry);
        Assert.All(withGeometry!.Items, i => Assert.NotNull(i.Width));

        // Same query, dimensions now absent everywhere.
        await ClearAllDimensionsAsync(factory);
        var withoutGeometry = await GetPageAsync(client, query);
        Assert.NotNull(withoutGeometry);
        Assert.All(withoutGeometry!.Items, i => Assert.Null(i.Width));

        // Ids, order and scores are identical — geometry is presentation only.
        Assert.Equal(
            withGeometry.Items.Select(i => i.FileItemId).ToList(),
            withoutGeometry.Items.Select(i => i.FileItemId).ToList());
        Assert.Equal(
            withGeometry.Items.Select(i => i.Score).ToList(),
            withoutGeometry.Items.Select(i => i.Score).ToList());

        // …and the ranking contract itself still holds.
        var scores = withGeometry.Items.Select(i => i.Score).ToList();
        Assert.Equal(scores.OrderByDescending(s => s).ToList(), scores);
    }

    [Fact]
    public async Task Cursor_Pagination_Still_Yields_Each_Result_Once_With_Its_Geometry()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var query = await UploadPngAsync(client, "query.png", 40, 40);
        for (var i = 0; i < 5; i++)
        {
            await UploadPngAsync(client, $"img-{i}.png", 30 + i * 6, 50);
        }
        await RunBackfillAsync(factory);

        // The expected set is whatever actually clears the 0.0 threshold in ONE
        // unpaginated request; the paged walk must reproduce exactly that.
        var single = await GetPageAsync(client, query);
        Assert.NotNull(single);
        var expected = single!.Items.Select(i => i.FileItemId).ToList();
        Assert.True(expected.Count >= 2, "need at least two results to exercise paging");

        var seen = new List<Item>();
        string? cursor = null;
        var pages = 0;
        for (var guard = 0; guard < 20; guard++)
        {
            var url = $"/api/files/{query}/similar?minSimilarity=0.0&limit=1"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await client.GetFromJsonAsync<Page>(url);
            if (page is null) break;
            pages++;
            seen.AddRange(page.Items);
            if (!page.HasMore || page.NextCursor is null) break;
            cursor = page.NextCursor;
        }

        Assert.True(pages > 1, "the walk must actually span multiple pages");
        Assert.Equal(expected, seen.Select(i => i.FileItemId).ToList());
        Assert.Equal(seen.Count, seen.Select(i => i.FileItemId).Distinct().Count());

        // Geometry is attached on every page, not only the first.
        Assert.All(seen, i =>
        {
            Assert.NotNull(i.Width);
            Assert.NotNull(i.Height);
            Assert.Equal(50, i.Height);
        });
    }

    [Fact]
    public async Task Adding_Dimensions_Keeps_The_Json_Backward_Compatible()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var query = await UploadPngAsync(client, "query.png", 40, 40);
        await UploadPngAsync(client, "other.png", 64, 24);
        await RunBackfillAsync(factory);

        // A consumer written before this change — one that has never heard of
        // width/height — still parses the response unchanged.
        var oldPage = await client.GetFromJsonAsync<OldPage>(
            $"/api/files/{query}/similar?minSimilarity=0.0&limit=50");

        Assert.NotNull(oldPage);
        var item = Assert.Single(oldPage!.Items);
        Assert.False(string.IsNullOrEmpty(item.Name));
        Assert.InRange(item.Score, 0.0, 1.0);
    }

    // Force an EXIF orientation onto an already-ingested blob. Uploading a real
    // rotated JPEG is unnecessary: the coded dimensions and the orientation flag
    // are stored independently, and this reproduces exactly that state.
    private static async Task SetOrientationAsync(
        SqliteWebApplicationFactory factory, Guid fileItemId, int orientation)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blobId = await db.FileItems.Where(f => f.Id == fileItemId)
            .Select(f => f.BlobObjectId).FirstAsync();
        var meta = await db.BlobMetadata.FirstAsync(m => m.BlobObjectId == blobId);
        meta.Orientation = orientation;
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task Quarter_Turn_Orientation_Reports_The_Displayed_Portrait_Shape(int orientation)
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var query = await UploadPngAsync(client, "query.png", 40, 40);
        // Coded landscape, but flagged as a quarter turn — a phone portrait shot.
        var rotated = await UploadPngAsync(client, "rotated.png", 64, 24);
        await RunBackfillAsync(factory);
        await SetOrientationAsync(factory, rotated, orientation);

        var result = await client.GetFromJsonAsync<LegacyResult>($"/api/files/{query}/similar?limit=20");

        var item = Assert.Single(result!.Items, i => i.FileItemId == rotated);
        // Every derivative renderer auto-orients, so the thumbnail this result
        // points at IS portrait. Reporting the coded landscape pair made the
        // shared wall reserve a landscape tile for a portrait image — the blurred
        // lateral bands. The displayed pair is the only correct answer.
        Assert.Equal((24, 64), (item.Width, item.Height));
        Assert.True(item.Height > item.Width, "a quarter-turned photo must report as portrait");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Upright_And_Mirrored_Orientations_Keep_The_Coded_Shape(int? orientation)
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var query = await UploadPngAsync(client, "query.png", 40, 40);
        var landscape = await UploadPngAsync(client, "landscape.png", 64, 24);
        await RunBackfillAsync(factory);
        if (orientation is not null)
        {
            await SetOrientationAsync(factory, landscape, orientation.Value);
        }

        var result = await client.GetFromJsonAsync<LegacyResult>($"/api/files/{query}/similar?limit=20");

        var item = Assert.Single(result!.Items, i => i.FileItemId == landscape);
        // 1..4 are upright/mirrored — no quarter turn, so no swap.
        Assert.Equal((64, 24), (item.Width, item.Height));
    }

    [Fact]
    public async Task Explorer_Page_Resolves_Orientation_Exactly_As_The_Library_Listing_Does()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var query = await UploadPngAsync(client, "query.png", 40, 40);
        var rotated = await UploadPngAsync(client, "rotated.png", 64, 24);
        await RunBackfillAsync(factory);
        await SetOrientationAsync(factory, rotated, 6);

        var page = await GetPageAsync(client, query);
        var similarItem = Assert.Single(page!.Items, i => i.FileItemId == rotated);

        // The same file, through the library's own listing endpoint.
        var library = await client.GetFromJsonAsync<MediaList>("/api/media?kind=image&limit=50");
        var libraryItem = Assert.Single(library!.Items, i => i.Id == rotated);

        // One geometry contract across both surfaces — that is what lets the two
        // walls reserve identical tiles for the same photo.
        Assert.Equal(
            (libraryItem.Width, libraryItem.Height),
            (similarItem.Width, similarItem.Height));
        Assert.Equal((24, 64), (similarItem.Width, similarItem.Height));
    }

    private sealed record MediaList(List<MediaListItem> Items);

    private sealed record MediaListItem(Guid Id, string Name, int? Width, int? Height);

    [Fact]
    public async Task Dimensions_Do_Not_Leak_Storage_Internals()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        var query = await UploadPngAsync(client, "query.png", 40, 40);
        await UploadPngAsync(client, "other.png", 64, 24);
        await RunBackfillAsync(factory);

        var body = await client.GetStringAsync($"/api/files/{query}/similar?minSimilarity=0.0&limit=50");

        foreach (var needle in new[]
                 {
                     "blobObjectId", "storageKey", "sha256", "profileId",
                     "embeddingBytes", "/storage/objects/", "pixelCount",
                 })
        {
            Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
        }

        // Only the two geometry fields were added.
        Assert.Contains("\"width\":", body, StringComparison.Ordinal);
        Assert.Contains("\"height\":", body, StringComparison.Ordinal);
    }
}
