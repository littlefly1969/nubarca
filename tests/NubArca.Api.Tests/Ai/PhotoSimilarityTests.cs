using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Ai;

// Phase 1 — photo similarity v0. Drives the REAL ai.photos.embeddings.backfill
// (deterministic dev/test embeddings, no pgvector) and the owner-private
// similarity lookup, through the genuine HTTP/upload + queue/processor stack.
public sealed class PhotoSimilarityTests
{
    private static readonly string[] ForbiddenNeedles =
    {
        "EmbeddingBytes", "embeddingBytes", "Vector", "vector",
        "StorageKey", "storageKey", "storage_key",
        "BlobObjectId", "blobObjectId",
        "Sha256", "sha256", "ProfileId", "profileId",
        "/storage/objects/",
    };

    private static SqliteWebApplicationFactory NewFactory(params (string Key, string Value)[] settings)
    {
        var dict = settings.ToDictionary(s => s.Key, s => (string?)s.Value);
        var factory = new SqliteWebApplicationFactory(dict, poolHost: true);
        factory.EnsureDatabaseCreated();
        return factory;
    }

    private static SqliteWebApplicationFactory EnabledFactory(params (string Key, string Value)[] extra)
    {
        var settings = new List<(string, string)>
        {
            ("Ai:Enabled", "true"),
            ("Ai:ImageEmbeddingsEnabled", "true"),
        };
        settings.AddRange(extra);
        return NewFactory(settings.ToArray());
    }

    private static async Task SeedDeterministicProfilesAsync(SqliteWebApplicationFactory factory)
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

    private static async Task<string> RunPhotosBackfillAsync(
        SqliteWebApplicationFactory factory, string? profileKey = null)
    {
        Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var job = await queue.EnqueueAsync(
                JobTypes.AiPhotosEmbeddingsBackfill, new AiBackfillJobPayload(ProfileKey: profileKey));
            jobId = job.Id;
        }

        // Loop so a sliced/continued job runs to a terminal state.
        for (var i = 0; i < 50; i++)
        {
            using var scope = factory.Services.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            if (await processor.ProcessAvailableAsync(10) == 0)
            {
                break;
            }
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.BackgroundJobs.Where(j => j.Id == jobId).Select(j => j.Status).SingleAsync();
        }
    }

    private static async Task<int> EmbeddingCountAsync(SqliteWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BlobEmbeddings.CountAsync();
    }

    [Fact]
    public async Task Backfill_Indexes_Eligible_Image_Blobs()
    {
        using var factory = EnabledFactory();
        await SeedDeterministicProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();

        await UploadPngAsync(client, "a.png", 10);
        await UploadPngAsync(client, "b.png", 12);
        await UploadPngAsync(client, "c.png", 14);

        var status = await RunPhotosBackfillAsync(factory);

        Assert.Equal(JobStatuses.Succeeded, status);
        // Three distinct images → three distinct blobs → three embedding rows.
        Assert.Equal(3, await EmbeddingCountAsync(factory));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.All(await db.BlobEmbeddings.AsNoTracking().ToListAsync(), e =>
        {
            Assert.Equal(32, e.Dimension);
            Assert.Equal(32 * sizeof(float), e.EmbeddingBytes.Length);
        });
    }

    [Fact]
    public async Task Backfill_Skips_Already_Indexed()
    {
        using var factory = EnabledFactory();
        await SeedDeterministicProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 10);
        await UploadPngAsync(client, "b.png", 12);

        await RunPhotosBackfillAsync(factory);
        var afterFirst = await EmbeddingCountAsync(factory);

        // Second run is a no-op: already-indexed (BlobObjectId, ProfileId) drop
        // out of the candidate query.
        await RunPhotosBackfillAsync(factory);
        var afterSecond = await EmbeddingCountAsync(factory);

        Assert.Equal(2, afterFirst);
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public async Task Backfill_Indexes_Across_Slices_With_Keyset_Cursor()
    {
        // Force one item per slice so the job must continue across slices and the
        // keyset cursor advances correctly.
        using var factory = EnabledFactory(("Jobs:MaintenanceSliceItemBudget", "1"));
        await SeedDeterministicProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 10);
        await UploadPngAsync(client, "b.png", 12);
        await UploadPngAsync(client, "c.png", 14);

        var status = await RunPhotosBackfillAsync(factory);

        Assert.Equal(JobStatuses.Succeeded, status);
        Assert.Equal(3, await EmbeddingCountAsync(factory));
    }

    [Fact]
    public async Task Similarity_Returns_Deterministic_Ordering()
    {
        using var factory = EnabledFactory();
        await SeedDeterministicProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var query = await UploadPngAsync(client, "q.png", 10);
        await UploadPngAsync(client, "b.png", 12);
        await UploadPngAsync(client, "c.png", 14);
        await UploadPngAsync(client, "d.png", 16);
        await RunPhotosBackfillAsync(factory);

        var first = await client.GetFromJsonAsync<SimilarPhotosResult>($"/api/files/{query}/similar?limit=10");
        var second = await client.GetFromJsonAsync<SimilarPhotosResult>($"/api/files/{query}/similar?limit=10");

        Assert.NotNull(first);
        Assert.True(first!.QueryIndexed);
        Assert.NotEmpty(first.Items);
        Assert.DoesNotContain(first.Items, i => i.FileItemId == query); // query excluded

        // Deterministic: identical ordering across calls.
        Assert.Equal(
            first.Items.Select(i => i.FileItemId),
            second!.Items.Select(i => i.FileItemId));
        // Scores are non-increasing.
        var scores = first.Items.Select(i => i.Score).ToList();
        Assert.Equal(scores.OrderByDescending(s => s).ToList(), scores);
    }

    [Fact]
    public async Task Owner_A_Cannot_Retrieve_Owner_B_Similar_Photos()
    {
        using var factory = EnabledFactory();
        await SeedDeterministicProfilesAsync(factory);

        var ownerA = await factory.SeedUserAsync("a@example.com");
        var clientA = await factory.LoginAsync("a@example.com");
        await factory.SeedUserAsync("b@example.com");
        var clientB = await factory.LoginAsync("b@example.com");

        // A and B both upload the SAME image (shared blob via dedup) plus a
        // distinct one each.
        var aShared = await UploadPngAsync(clientA, "shared.png", 10);
        var aOwn = await UploadPngAsync(clientA, "a-own.png", 12);
        await UploadPngAsync(clientB, "shared.png", 10); // same bytes → same blob as A's
        var bOwn = await UploadPngAsync(clientB, "b-own.png", 14);

        await RunPhotosBackfillAsync(factory);

        // A's results must be A's files only — never B's, even though the blob is
        // shared.
        var aResult = await clientA.GetFromJsonAsync<SimilarPhotosResult>($"/api/files/{aShared}/similar?limit=50");
        Assert.NotNull(aResult);
        var aFileIds = new[] { aShared, aOwn };
        Assert.All(aResult!.Items, i => Assert.Contains(i.FileItemId, aFileIds));
        Assert.DoesNotContain(aResult.Items, i => i.FileItemId == bOwn);

        // A asking for B's file → 404 (not owned).
        var crossOwner = await clientA.GetAsync($"/api/files/{bOwn}/similar");
        Assert.Equal(HttpStatusCode.NotFound, crossOwner.StatusCode);
    }

    [Fact]
    public async Task Disabled_Ai_NoOps_Backfill()
    {
        using var factory = NewFactory(); // AI disabled (default)
        await SeedDeterministicProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 10);

        var status = await RunPhotosBackfillAsync(factory);

        Assert.Equal(JobStatuses.Succeeded, status);
        Assert.Equal(0, await EmbeddingCountAsync(factory));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.BlobAiArtifactStatuses.CountAsync());
        Assert.Equal(0, await db.AiIndexDiagnostics.CountAsync());
    }

    [Fact]
    public async Task Provider_Unavailable_Writes_No_Per_Blob_Status_Or_Embeddings()
    {
        // AI + capability enabled, but NO profile seeded → provider unavailable.
        using var factory = EnabledFactory();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 10);

        var status = await RunPhotosBackfillAsync(factory);

        Assert.Equal(JobStatuses.Succeeded, status);
        Assert.Equal(0, await EmbeddingCountAsync(factory));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.BlobAiArtifactStatuses.CountAsync()); // no skipped/failed rows
        var diagnostics = await db.AiIndexDiagnostics.AsNoTracking().ToListAsync();
        Assert.True(diagnostics.Count <= 1);
        Assert.All(diagnostics, d => Assert.False(d.IsPermanent));
    }

    [Fact]
    public async Task Cancellation_Does_Not_Record_Permanent_Failure()
    {
        using var factory = EnabledFactory();
        await SeedDeterministicProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 10);

        Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var job = await queue.EnqueueAsync(
                JobTypes.AiPhotosEmbeddingsBackfill, new AiBackfillJobPayload());
            jobId = job.Id;
            await queue.RequestCancellationAsync(jobId);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            await processor.ProcessAvailableAsync(10);
        }

        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var jobStatus = await db.BackgroundJobs.Where(j => j.Id == jobId).Select(j => j.Status).SingleAsync();
        Assert.Equal(JobStatuses.Cancelled, jobStatus);
        Assert.Equal(0, await db.AiIndexDiagnostics.CountAsync(d => d.IsPermanent));
        Assert.Equal(0, await db.BlobAiArtifactStatuses.CountAsync());
    }

    [Fact]
    public async Task Similar_Api_Response_Has_No_Raw_Vectors_Or_Storage_Identifiers()
    {
        using var factory = EnabledFactory();
        await SeedDeterministicProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var query = await UploadPngAsync(client, "q.png", 10);
        await UploadPngAsync(client, "b.png", 12);
        await RunPhotosBackfillAsync(factory);

        var response = await client.GetAsync($"/api/files/{query}/similar");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();

        foreach (var needle in ForbiddenNeedles)
        {
            Assert.DoesNotContain(needle, raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Cli_Similar_Prints_Scores_And_No_Storage_Identifiers()
    {
        using var factory = EnabledFactory();
        await SeedDeterministicProfilesAsync(factory);
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var query = await UploadPngAsync(client, "q.png", 10);
        await UploadPngAsync(client, "b.png", 12);
        await RunPhotosBackfillAsync(factory);

        using var scope = factory.Services.CreateScope();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliEntryPoint.RunAsync(
            new[] { "ai", "photos", "similar", "--file", query.ToString(), "--limit", "5" },
            stdout, stderr, () => scope.ServiceProvider);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr.ToString());
        var output = stdout.ToString();
        Assert.Contains("ai photos similar", output);
        foreach (var needle in ForbiddenNeedles)
        {
            Assert.DoesNotContain(needle, output, StringComparison.Ordinal);
        }
    }
}
