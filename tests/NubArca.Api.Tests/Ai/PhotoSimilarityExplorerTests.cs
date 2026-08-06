using System.Net;
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

namespace NubArca.Api.Tests.Ai;

// Similar Photos Explorer: threshold-filtered, keyset-paginated similarity over
// the deterministic dev embeddings + exact-scan path (SQLite has no pgvector;
// the pgvector ANN path is covered by the Testcontainers integration suite).
public sealed class PhotoSimilarityExplorerTests
{
    private static readonly string[] ForbiddenNeedles =
    {
        "EmbeddingBytes", "embeddingBytes", "StorageKey", "storageKey", "storage_key",
        "BlobObjectId", "blobObjectId", "Sha256", "sha256", "ProfileId", "profileId",
        "/storage/objects/", "EmbeddingId", "embeddingId", "Distance", "distance",
    };

    // Mirrors SimilarPhotosPage (web JSON defaults: camelCase, case-insensitive).
    private sealed record Page(
        bool ProfileAvailable,
        bool QueryIndexed,
        List<Item> Items,
        string? NextCursor,
        bool HasMore,
        string? UnavailableReason);

    private sealed record Item(Guid FileItemId, string Name, double Score);

    private static SqliteWebApplicationFactory EnabledFactory(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ImageEmbeddingsEnabled"] = "true",
        };
        foreach (var (k, v) in extra) settings[k] = v;
        var factory = new SqliteWebApplicationFactory(settings, poolHost: true);
        factory.EnsureDatabaseCreated();
        return factory;
    }

    private static async Task SeedProfilesAsync(SqliteWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
        await registry.SeedDeterministicProfilesAsync();
    }

    private static byte[] Png(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static async Task<Guid> UploadPngAsync(HttpClient client, string name, int dim)
    {
        var part = new ByteArrayContent(Png(dim));
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

    // Seed a query image + N others, all indexed. Returns the query file id.
    private static async Task<Guid> SeedIndexedSetAsync(HttpClient client, SqliteWebApplicationFactory factory, int others)
    {
        var query = await UploadPngAsync(client, "query.png", 10);
        for (var i = 0; i < others; i++)
        {
            await UploadPngAsync(client, $"img-{i}.png", 12 + i * 2);
        }
        await RunBackfillAsync(factory);
        return query;
    }

    // Page through every result at a threshold via the keyset cursor.
    private static async Task<List<Guid>> CollectAllIdsAsync(
        HttpClient client, Guid query, double minSim)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 100; guard++)
        {
            var url = $"/api/files/{query}/similar?minSimilarity={minSim.ToString(System.Globalization.CultureInfo.InvariantCulture)}&limit=2"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await client.GetFromJsonAsync<Page>(url);
            if (page is null) break;
            ids.AddRange(page.Items.Select(i => i.FileItemId));
            if (!page.HasMore || page.NextCursor is null) break;
            cursor = page.NextCursor;
        }
        return ids;
    }

    [Fact]
    public async Task Lowering_Threshold_Never_Reduces_Results_And_Is_A_Superset()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var query = await SeedIndexedSetAsync(client, factory, others: 6);

        // Descending thresholds → non-decreasing result counts, and each higher
        // threshold's set is a subset of every lower threshold's set.
        var thresholds = new[] { 0.95, 0.85, 0.75, 0.65, 0.50, 0.30, 0.0 };
        var sets = new List<(double T, List<Guid> Ids)>();
        foreach (var t in thresholds)
        {
            sets.Add((t, await CollectAllIdsAsync(client, query, t)));
        }

        for (var i = 1; i < sets.Count; i++)
        {
            var higher = sets[i - 1];
            var lower = sets[i];
            Assert.True(
                lower.Ids.Count >= higher.Ids.Count,
                $"count at {lower.T} ({lower.Ids.Count}) < count at {higher.T} ({higher.Ids.Count})");
            Assert.True(
                higher.Ids.ToHashSet().IsSubsetOf(lower.Ids.ToHashSet()),
                $"results at {higher.T} are not a subset of results at {lower.T}");
        }

        // The loosest threshold returns every other indexed image (source excluded).
        Assert.Equal(6, sets[^1].Ids.Count);
        Assert.DoesNotContain(query, sets[^1].Ids);
    }

    [Fact]
    public async Task Threshold_080_Results_Are_All_Present_At_050()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var query = await SeedIndexedSetAsync(client, factory, others: 6);

        var at80 = await CollectAllIdsAsync(client, query, 0.80);
        var at50 = await CollectAllIdsAsync(client, query, 0.50);

        Assert.True(at80.ToHashSet().IsSubsetOf(at50.ToHashSet()));
        Assert.True(at50.Count >= at80.Count);
    }

    [Fact]
    public async Task Histogram_Cli_Prints_Counts_And_No_Storage_Identifiers()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var query = await SeedIndexedSetAsync(client, factory, others: 4);

        using var scope = factory.Services.CreateScope();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await NubArca.Api.Cli.CliEntryPoint.RunAsync(
            new[] { "ai", "photos", "similar", "histogram", "--file", query.ToString() },
            stdout, stderr, () => scope.ServiceProvider);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr.ToString());
        var output = stdout.ToString();
        Assert.Contains("histogram", output);
        Assert.Contains("threshold", output);
        // SQLite has no pgvector → exact-scan only; pg columns read "n/a".
        Assert.Contains("exact-scan only", output);
        foreach (var needle in ForbiddenNeedles)
        {
            Assert.DoesNotContain(needle, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Source_File_Is_Excluded_From_Results()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var query = await SeedIndexedSetAsync(client, factory, others: 4);

        var page = await client.GetFromJsonAsync<Page>(
            $"/api/files/{query}/similar?minSimilarity=0&limit=100");

        Assert.NotNull(page);
        Assert.True(page!.QueryIndexed);
        Assert.NotEmpty(page.Items);
        Assert.DoesNotContain(page.Items, i => i.FileItemId == query);
    }

    [Fact]
    public async Task Results_Are_Sorted_Most_Similar_First()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var query = await SeedIndexedSetAsync(client, factory, others: 5);

        var page = await client.GetFromJsonAsync<Page>(
            $"/api/files/{query}/similar?minSimilarity=0&limit=100");

        Assert.NotNull(page);
        var scores = page!.Items.Select(i => i.Score).ToList();
        Assert.Equal(scores.OrderByDescending(s => s).ToList(), scores);
    }

    [Fact]
    public async Task MinSimilarity_Filters_Results()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var query = await SeedIndexedSetAsync(client, factory, others: 5);

        var all = await client.GetFromJsonAsync<Page>(
            $"/api/files/{query}/similar?minSimilarity=0&limit=100");
        Assert.NotNull(all);
        Assert.NotEmpty(all!.Items);

        // Threshold above the lowest score must drop at least that item, and
        // every returned item must satisfy the threshold.
        var maxScore = all.Items.Max(i => i.Score);
        var minScore = all.Items.Min(i => i.Score);
        var threshold = maxScore > minScore ? (maxScore + minScore) / 2.0 : maxScore;

        var filtered = await client.GetFromJsonAsync<Page>(
            $"/api/files/{query}/similar?minSimilarity={threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}&limit=100");

        Assert.NotNull(filtered);
        Assert.All(filtered!.Items, i => Assert.True(i.Score >= threshold));
        Assert.True(filtered.Items.Count <= all.Items.Count);

        // A threshold of ~1.0 returns no more results than a threshold of 0.
        var strict = await client.GetFromJsonAsync<Page>(
            $"/api/files/{query}/similar?minSimilarity=0.999999&limit=100");
        Assert.NotNull(strict);
        Assert.True(strict!.Items.Count <= all.Items.Count);
    }

    [Fact]
    public async Task Cursor_Pagination_Is_Stable_And_Complete()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var query = await SeedIndexedSetAsync(client, factory, others: 5);

        // Full ordered set in one shot.
        var full = await client.GetFromJsonAsync<Page>(
            $"/api/files/{query}/similar?minSimilarity=0&limit=100");
        Assert.NotNull(full);
        var expected = full!.Items.Select(i => i.FileItemId).ToList();
        Assert.True(expected.Count >= 4);

        // Page through 2 at a time following the cursor; concatenation must equal
        // the full ordered list exactly (no gaps, no duplicates, same order).
        var collected = new List<Guid>();
        string? cursor = null;
        var guard = 0;
        do
        {
            var url = $"/api/files/{query}/similar?minSimilarity=0&limit=2"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await client.GetFromJsonAsync<Page>(url);
            Assert.NotNull(page);
            Assert.True(page!.Items.Count <= 2);
            collected.AddRange(page.Items.Select(i => i.FileItemId));
            cursor = page.NextCursor;
            Assert.True(++guard < 20, "pagination did not terminate");
        } while (cursor is not null);

        Assert.Equal(expected, collected);
        Assert.Equal(expected.Count, collected.Distinct().Count());
    }

    [Fact]
    public async Task Limit_Is_Capped_Server_Side()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var query = await SeedIndexedSetAsync(client, factory, others: 3);

        // A limit far above the server cap must not error and must return a
        // bounded page (<= MaxPageSize = 100).
        var page = await client.GetFromJsonAsync<Page>(
            $"/api/files/{query}/similar?minSimilarity=0&limit=99999");

        Assert.NotNull(page);
        Assert.True(page!.Items.Count <= 100);
        Assert.False(page.HasMore); // only 3 neighbours → all fit, no further pages
    }

    [Fact]
    public async Task MinSimilarity_Out_Of_Bounds_Is_BadRequest()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var query = await SeedIndexedSetAsync(client, factory, others: 1);

        var tooHigh = await client.GetAsync($"/api/files/{query}/similar?minSimilarity=1.5");
        Assert.Equal(HttpStatusCode.BadRequest, tooHigh.StatusCode);

        var tooLow = await client.GetAsync($"/api/files/{query}/similar?minSimilarity=-0.1");
        Assert.Equal(HttpStatusCode.BadRequest, tooLow.StatusCode);
    }

    [Fact]
    public async Task Owner_A_Cannot_Explore_Owner_B_Source_File()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);

        await factory.SeedUserAsync("a@example.com");
        var clientA = await factory.LoginAsync("a@example.com");
        await factory.SeedUserAsync("b@example.com");
        var clientB = await factory.LoginAsync("b@example.com");

        await UploadPngAsync(clientA, "a.png", 10);
        var bOwn = await UploadPngAsync(clientB, "b-own.png", 14);
        await RunBackfillAsync(factory);

        // A explores B's file → 404 (not owned), even with explorer params.
        var crossOwner = await clientA.GetAsync(
            $"/api/files/{bOwn}/similar?minSimilarity=0.5&limit=60");
        Assert.Equal(HttpStatusCode.NotFound, crossOwner.StatusCode);
    }

    [Fact]
    public async Task Response_Has_No_Storage_Internals()
    {
        using var factory = EnabledFactory();
        await SeedProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var query = await SeedIndexedSetAsync(client, factory, others: 4);

        var response = await client.GetAsync(
            $"/api/files/{query}/similar?minSimilarity=0&limit=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();

        // The cursor itself must not leak internals either (it encodes only an
        // already-exposed score + FileItem id).
        foreach (var needle in ForbiddenNeedles)
        {
            Assert.DoesNotContain(needle, raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Unusable_Profile_Returns_Graceful_Result_Not_Error()
    {
        // AI + capability enabled but NO profile seeded → profile unavailable.
        using var factory = EnabledFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var query = await UploadPngAsync(client, "q.png", 10);

        var response = await client.GetAsync(
            $"/api/files/{query}/similar?minSimilarity=0.75&limit=60");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<Page>();
        Assert.NotNull(page);
        Assert.False(page!.ProfileAvailable);
        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }
}
