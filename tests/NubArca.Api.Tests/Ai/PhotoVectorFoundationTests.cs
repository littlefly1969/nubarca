using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Ai;

// Phase 2B foundation — pgvector path behaviour that is provider-independent or
// observable on SQLite (where pgvector is ALWAYS unavailable): validation,
// clean "unavailable" reporting, dry-run, sanitized output, and the exact-scan
// fallback. The real ANN path is covered by the pgvector integration tests.
public sealed class PhotoVectorFoundationTests
{
    private const string DetImageProfile = "det-image-embedding-v1";

    // NOTE: the literal word "vector" is intentionally NOT forbidden here — the
    // lifecycle output legitimately uses field names like `vector_indexed`. The
    // real leak risk (raw vector arrays, storage identifiers) is covered by the
    // identifiers below; vector-sync/coverage only ever emit counts + stable keys.
    private static readonly string[] Forbidden =
    {
        "EmbeddingBytes", "embeddingBytes",
        "StorageKey", "storageKey", "BlobObjectId", "blobObjectId",
        "Sha256", "sha256", "ProfileId", "profileId", "/storage/objects/", "/models/",
    };

    // ---- ClassifyVector (pure, provider-independent) ------------------------

    [Fact]
    public void ClassifyVector_Accepts_Correct_Dimension_Finite()
    {
        var v = new float[1152];
        Array.Fill(v, 0.1f);
        Assert.Equal(VectorRowValidity.Ok, PhotoVectorIndexService.ClassifyVector(v, 1152));
    }

    [Fact]
    public void ClassifyVector_Rejects_Wrong_Dimension()
    {
        Assert.Equal(
            VectorRowValidity.DimensionMismatch,
            PhotoVectorIndexService.ClassifyVector(new float[10], 1152));
        Assert.Equal(
            VectorRowValidity.DimensionMismatch,
            PhotoVectorIndexService.ClassifyVector(new float[769], 1152));
    }

    [Fact]
    public void ClassifyVector_Rejects_NaN_And_Infinity_And_Zero()
    {
        var nan = new float[1152];
        nan[3] = float.NaN;
        Assert.Equal(VectorRowValidity.NonFinite, PhotoVectorIndexService.ClassifyVector(nan, 1152));

        var inf = new float[1152];
        inf[7] = float.PositiveInfinity;
        Assert.Equal(VectorRowValidity.NonFinite, PhotoVectorIndexService.ClassifyVector(inf, 1152));

        // Zero vector cannot be normalized → rejected (NonFinite bucket).
        Assert.Equal(VectorRowValidity.NonFinite, PhotoVectorIndexService.ClassifyVector(new float[1152], 1152));
    }

    // ---- behaviour on SQLite (pgvector always unavailable) ------------------

    [Fact]
    public async Task VectorSync_Unsupported_Dimension_Reports_Reason_And_Writes_Nothing()
    {
        using var f = EnabledFactory();
        await SeedDeterministicAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 10);
        await RunPhotosBackfillAsync(f, DetImageProfile);

        // det-image-embedding-v1 is dim 32 → no vector table for that dimension.
        var (exit, stdout, stderr) = await RunCli(
            f, "ai", "photos", "embeddings", "vector-sync", "--profile", DetImageProfile);

        Assert.Equal(0, exit);
        Assert.Contains("vector_backend=unsupported-dimension", stdout);
        Assert.Contains("synced=0", stdout);
        Assert.Contains("dry_run=false", stdout);
        AssertNoLeak(stdout);
        AssertNoLeak(stderr);
    }

    [Fact]
    public async Task VectorSync_1152_Profile_On_Sqlite_Reports_Pgvector_Unavailable()
    {
        using var f = EnabledFactory();
        await SeedDeterministicAsync(f);
        await SeedOnnxEvalAsync(f); // seeds the 1152-dim SigLIP eval profile

        var (exit, stdout, _) = await RunCli(
            f, "ai", "photos", "embeddings", "vector-sync", "--profile", OnnxImageModels.SiglipSo400mProfileKey);

        Assert.Equal(0, exit);
        Assert.Contains("dimension=1152", stdout);
        Assert.Contains("vector_backend=pgvector-unavailable", stdout);
        Assert.Contains("synced=0", stdout);
        AssertNoLeak(stdout);
    }

    [Fact]
    public async Task VectorSync_DryRun_Reports_DryRun_True()
    {
        using var f = EnabledFactory();
        await SeedOnnxEvalAsync(f);

        var (exit, stdout, _) = await RunCli(
            f, "ai", "photos", "embeddings", "vector-sync",
            "--profile", OnnxImageModels.SiglipSo400mProfileKey, "--dry-run");

        Assert.Equal(0, exit);
        Assert.Contains("dry_run=true", stdout);
        Assert.Contains("synced=0", stdout);
    }

    [Fact]
    public async Task VectorSync_Unknown_Profile_Errors()
    {
        using var f = EnabledFactory();
        await SeedDeterministicAsync(f);
        var (exit, _, stderr) = await RunCli(
            f, "ai", "photos", "embeddings", "vector-sync", "--profile", "no-such-profile");
        Assert.Equal(64, exit);
        Assert.Contains("not found", stderr);
    }

    [Fact]
    public async Task VectorSync_Requires_Profile()
    {
        using var f = EnabledFactory();
        var (exit, _, stderr) = await RunCli(f, "ai", "photos", "embeddings", "vector-sync");
        Assert.Equal(64, exit);
        Assert.Contains("--profile", stderr);
    }

    [Fact]
    public async Task Coverage_Includes_Vector_Lines_And_Is_Sanitized()
    {
        using var f = EnabledFactory();
        await SeedDeterministicAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 10);
        await UploadPngAsync(client, "b.png", 12);
        await RunPhotosBackfillAsync(f, DetImageProfile);

        var (exit, stdout, _) = await RunCli(
            f, "ai", "photos", "embeddings", "coverage", "--profile", DetImageProfile);

        Assert.Equal(0, exit);
        Assert.Contains("eligible_images=2", stdout);
        Assert.Contains("embedded=2", stdout);
        Assert.Contains("missing_embeddings=0", stdout);
        // pgvector unavailable on SQLite + dim 32 unsupported → not vector-indexed.
        Assert.Contains("vector_supported=False", stdout);
        Assert.Contains("vector_indexed=0", stdout);
        Assert.Contains("missing_vectors=0", stdout);
        Assert.Contains("vector_coverage_percent=0", stdout);
        AssertNoLeak(stdout);
    }

    [Fact]
    public async Task Similarity_Falls_Back_To_ExactScan_When_No_Vectors()
    {
        // No pgvector on SQLite → CountIndexed=0 → exact-scan path still returns
        // owner-private deterministic results (existing behaviour preserved).
        using var f = EnabledFactory();
        await SeedDeterministicAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        var query = await UploadPngAsync(client, "q.png", 10);
        await UploadPngAsync(client, "b.png", 12);
        await UploadPngAsync(client, "c.png", 14);
        await RunPhotosBackfillAsync(f, DetImageProfile);

        var result = await client.GetFromJsonAsync<SimilarPhotosResult>($"/api/files/{query}/similar?limit=10");
        Assert.NotNull(result);
        Assert.True(result!.ProfileAvailable);
        Assert.True(result.QueryIndexed);
        Assert.NotEmpty(result.Items);
        Assert.DoesNotContain(result.Items, i => i.FileItemId == query);
    }

    [Fact]
    public async Task Backfill_1152_Profile_On_Sqlite_Writes_Canonical_And_No_Vectors()
    {
        // The auto-upsert path must never break the backfill when pgvector is
        // unavailable (SQLite): canonical 1152-dim BlobEmbedding rows are written,
        // and no vector rows exist (the read path then uses exact-scan).
        using var f = EnabledFactory();
        await SeedDeterministic1152ProfileAsync(f, "det-1152", "det-image-1152");
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 10);
        await UploadPngAsync(client, "b.png", 12);

        await RunPhotosBackfillAsync(f, "det-image-1152");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pid = await db.AiProfiles.Where(p => p.Key == "det-image-1152").Select(p => p.Id).SingleAsync();
        var rows = await db.BlobEmbeddings.AsNoTracking().Where(e => e.ProfileId == pid).ToListAsync();
        Assert.Equal(2, rows.Count);                         // canonical rows written
        Assert.All(rows, r => Assert.Equal(1152, r.Dimension));

        var coverage = await scope.ServiceProvider
            .GetRequiredService<PhotoEmbeddingProfileService>().GetCoverageAsync("det-image-1152");
        Assert.NotNull(coverage);
        Assert.Equal(2, coverage!.Embedded);
        Assert.False(coverage.VectorSupported);              // pgvector unavailable on SQLite
        Assert.Equal(0, coverage.VectorIndexed);
    }

    [Fact]
    public async Task Legacy_Retirement_Fails_Closed_Without_Complete_1152_Vector_Coverage()
    {
        using var f = EnabledFactory(
            ("Ai:PhotoSimilarityProfileKey", OnnxImageModels.SiglipSo400mProfileKey));
        await SeedOnnxEvalAsync(f);

        var result = await RunCli(f, "ai", "photos", "embeddings", "retire-legacy-768");

        Assert.Equal(75, result.exit);
        Assert.Contains("ready=False", result.stdout);
        Assert.Contains("executed=False", result.stdout);
        Assert.Contains("reason=1152-coverage-incomplete", result.stdout);
    }

    // A non-default deterministic image profile at dim 1152 (the supported vector
    // dimension), bound to its own deterministic model.
    private static async Task SeedDeterministic1152ProfileAsync(
        SqliteWebApplicationFactory f, string modelKey, string profileKey)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = modelKey,
            Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Image,
            Version = 1,
            Dimension = 1152,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        db.AiProfiles.Add(new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = profileKey,
            AiModelId = model.Id,
            Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Image,
            Dimension = 1152,
            DistanceMetric = AiDistanceMetrics.Cosine,
            IsDefault = false,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // ---- helpers ------------------------------------------------------------

    private static SqliteWebApplicationFactory EnabledFactory(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ImageEmbeddingsEnabled"] = "true",
        };
        foreach (var (k, v) in extra)
        {
            settings[k] = v;
        }

        var factory = new SqliteWebApplicationFactory(settings, poolHost: true);
        factory.EnsureDatabaseCreated();
        return factory;
    }

    private static async Task SeedDeterministicAsync(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>().SeedDeterministicProfilesAsync();
    }

    private static async Task SeedOnnxEvalAsync(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>().SeedOnnxImageEvalProfilesAsync();
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

    private static async Task RunPhotosBackfillAsync(SqliteWebApplicationFactory f, string? profileKey = null)
    {
        using (var scope = f.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            await queue.EnqueueAsync(
                JobTypes.AiPhotosEmbeddingsBackfill, new AiBackfillJobPayload(ProfileKey: profileKey));
        }

        for (var i = 0; i < 50; i++)
        {
            using var scope = f.Services.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            if (await processor.ProcessAvailableAsync(10) == 0)
            {
                break;
            }
        }
    }

    private static async Task<(int exit, string stdout, string stderr)> RunCli(
        SqliteWebApplicationFactory f, params string[] args)
    {
        using var scope = f.Services.CreateScope();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliEntryPoint.RunAsync(args, stdout, stderr, () => scope.ServiceProvider);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static void AssertNoLeak(string text)
    {
        foreach (var n in Forbidden)
        {
            Assert.DoesNotContain(n, text, StringComparison.Ordinal);
        }
    }
}
