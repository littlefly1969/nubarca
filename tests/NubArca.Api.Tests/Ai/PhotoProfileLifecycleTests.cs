using System.Net;
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

// Photo-embedding profile LIFECYCLE: explicit active-profile selection
// (Ai__PhotoSimilarityProfileKey + --profile override), aggregate coverage, and
// the profile-keyed backfill safety contract. Exercised through the real
// HTTP/upload + queue/processor + CLI stack. Deterministic backend only; no ONNX
// weights in CI.
public sealed class PhotoProfileLifecycleTests
{
    private const string DetImageProfile = "det-image-embedding-v1";
    private const string DetDocProfile = "det-document-embedding-v1";
    private const string DetImageProfileV2 = "det-image-embedding-v2";

    // "vector" is NOT forbidden: coverage now emits field names like
    // `vector_supported`/`vector_indexed`. Raw-vector/identifier leakage is still
    // covered by EmbeddingBytes + the storage identifiers below.
    private static readonly string[] Forbidden =
    {
        "EmbeddingBytes", "embeddingBytes",
        "StorageKey", "storageKey", "storage_key",
        "BlobObjectId", "blobObjectId",
        "Sha256", "sha256", "ProfileId", "profileId",
        "/storage/objects/", "/models/",
    };

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

    // A second, NON-default deterministic image profile bound to the same
    // deterministic model — used to prove profile-keyed coexistence.
    private static async Task SeedSecondDeterministicImageProfileAsync(SqliteWebApplicationFactory f, string key)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var modelId = await db.AiModels.Where(m => m.Key == "deterministic-v1").Select(m => m.Id).SingleAsync();
        db.AiProfiles.Add(new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = key,
            AiModelId = modelId,
            Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Image,
            Dimension = 32,
            DistanceMetric = AiDistanceMetrics.Cosine,
            IsDefault = false,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
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
            // No idempotency key: each call runs a fresh job (data-level
            // idempotency comes from the candidate query dropping indexed blobs).
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

    private static async Task<int> EmbeddingCountAsync(SqliteWebApplicationFactory f, string? profileKey = null)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (profileKey is null)
        {
            return await db.BlobEmbeddings.CountAsync();
        }

        var profileId = await db.AiProfiles.Where(p => p.Key == profileKey).Select(p => p.Id).SingleAsync();
        return await db.BlobEmbeddings.CountAsync(e => e.ProfileId == profileId);
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

    // ---- active-profile selection (read path) -------------------------------

    [Fact]
    public async Task Similarity_Uses_Configured_Active_Profile_And_Does_Not_Mix()
    {
        // Active profile = the ONNX eval profile (no embeddings); the DEFAULT
        // deterministic profile DOES have embeddings. Similarity must read only
        // the configured profile and ignore the default's embeddings.
        using var f = EnabledFactory(("Ai:PhotoSimilarityProfileKey", OnnxImageModels.SiglipSo400mProfileKey));
        await SeedDeterministicAsync(f);
        await SeedOnnxEvalAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();

        var query = await UploadPngAsync(client, "q.png", 10);
        await UploadPngAsync(client, "b.png", 12);
        await UploadPngAsync(client, "c.png", 14);

        // Embeddings ONLY under the deterministic (default) profile.
        await RunPhotosBackfillAsync(f, DetImageProfile);
        Assert.Equal(3, await EmbeddingCountAsync(f, DetImageProfile));
        Assert.Equal(0, await EmbeddingCountAsync(f, OnnxImageModels.SiglipSo400mProfileKey));

        // API uses the configured (siglip) profile → profile available but query
        // not indexed FOR THAT PROFILE; deterministic rows are never mixed in.
        var result = await client.GetFromJsonAsync<SimilarPhotosResult>($"/api/files/{query}/similar?limit=10");
        Assert.NotNull(result);
        Assert.True(result!.ProfileAvailable);
        Assert.False(result.QueryIndexed);
        Assert.Empty(result.Items);

        // The operator --profile override CAN read the deterministic profile's
        // embeddings, proving they exist and are comparable (so the empty API
        // result above is genuinely "configured profile only", not "no data").
        var cli = await RunCli(f, "ai", "photos", "similar", "--file", query.ToString(), "--profile", DetImageProfile);
        Assert.Equal(0, cli.exit);
        Assert.Contains("ai photos similar: top", cli.stdout);
        AssertNoLeak(cli.stdout);
    }

    [Fact]
    public async Task Similarity_Missing_Configured_Profile_Returns_Clean_Empty()
    {
        using var f = EnabledFactory(("Ai:PhotoSimilarityProfileKey", "no-such-profile"));
        await SeedDeterministicAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        var query = await UploadPngAsync(client, "q.png", 10);

        var response = await client.GetAsync($"/api/files/{query}/similar");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        AssertNoLeak(raw);

        var result = await response.Content.ReadFromJsonAsync<SimilarPhotosResult>();
        Assert.NotNull(result);
        Assert.False(result!.ProfileAvailable);
        Assert.False(result.QueryIndexed);
        Assert.Empty(result.Items);
        Assert.Equal("profile-not-found", result.UnavailableReason);
    }

    [Fact]
    public async Task Similarity_Wrong_Capability_Profile_Is_Rejected()
    {
        // Configured profile exists but is a DOCUMENT-embedding profile.
        using var f = EnabledFactory(("Ai:PhotoSimilarityProfileKey", DetDocProfile));
        await SeedDeterministicAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        var query = await UploadPngAsync(client, "q.png", 10);

        var result = await client.GetFromJsonAsync<SimilarPhotosResult>($"/api/files/{query}/similar");
        Assert.NotNull(result);
        Assert.False(result!.ProfileAvailable);
        Assert.Equal("capability-mismatch", result.UnavailableReason);
    }

    [Fact]
    public async Task Cli_Similar_Bad_Override_Profile_Is_Operator_Error()
    {
        using var f = EnabledFactory();
        await SeedDeterministicAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        var query = await UploadPngAsync(client, "q.png", 10);

        var (exit, _, stderr) = await RunCli(
            f, "ai", "photos", "similar", "--file", query.ToString(), "--profile", "no-such-profile");

        Assert.Equal(64, exit);
        Assert.Contains("not usable", stderr);
        Assert.Contains("profile-not-found", stderr);
    }

    // ---- coverage -----------------------------------------------------------

    [Fact]
    public async Task Cli_Coverage_Is_Aggregate_And_Sanitized()
    {
        using var f = EnabledFactory();
        await SeedDeterministicAsync(f);
        await SeedOnnxEvalAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 10);
        await UploadPngAsync(client, "b.png", 12);
        await UploadPngAsync(client, "c.png", 14);
        await RunPhotosBackfillAsync(f, DetImageProfile);

        // Fully covered deterministic profile.
        var det = await RunCli(f, "ai", "photos", "embeddings", "coverage", "--profile", DetImageProfile);
        Assert.Equal(0, det.exit);
        Assert.Contains($"profile={DetImageProfile}", det.stdout);
        Assert.Contains("eligible_images=3", det.stdout);
        Assert.Contains("embedded=3", det.stdout);
        Assert.Contains("missing_embeddings=0", det.stdout);
        Assert.Contains("coverage_percent=100", det.stdout);
        Assert.Contains("dimension=32", det.stdout);
        Assert.Contains("distance_metric=cosine", det.stdout);
        AssertNoLeak(det.stdout);

        // ONNX profile: eligible but nothing embedded (no weights / no backfill).
        var onnx = await RunCli(f, "ai", "photos", "embeddings", "coverage", "--profile", OnnxImageModels.SiglipSo400mProfileKey);
        Assert.Equal(0, onnx.exit);
        Assert.Contains("eligible_images=3", onnx.stdout);
        Assert.Contains("embedded=0", onnx.stdout);
        Assert.Contains("missing_embeddings=3", onnx.stdout);
        Assert.Contains("coverage_percent=0", onnx.stdout);
        Assert.Contains("dimension=1152", onnx.stdout);
        AssertNoLeak(onnx.stdout);
    }

    [Fact]
    public async Task Cli_Coverage_Unknown_Profile_Errors()
    {
        using var f = EnabledFactory();
        await SeedDeterministicAsync(f);

        var (exit, _, stderr) = await RunCli(f, "ai", "photos", "embeddings", "coverage", "--profile", "no-such-profile");
        Assert.Equal(64, exit);
        Assert.Contains("not found", stderr);
    }

    [Fact]
    public async Task Cli_Coverage_Requires_Profile()
    {
        using var f = EnabledFactory();
        await SeedDeterministicAsync(f);
        var (exit, _, stderr) = await RunCli(f, "ai", "photos", "embeddings", "coverage");
        Assert.Equal(64, exit);
        Assert.Contains("--profile", stderr);
    }

    // ---- active-profile -----------------------------------------------------

    [Fact]
    public async Task Cli_Active_Profile_Reports_Config_Source()
    {
        using var f = EnabledFactory(("Ai:PhotoSimilarityProfileKey", DetImageProfile));
        await SeedDeterministicAsync(f);

        var (exit, stdout, stderr) = await RunCli(f, "ai", "photos", "embeddings", "active-profile");
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("source=config", stdout);
        Assert.Contains($"config_key={DetImageProfile}", stdout);
        Assert.Contains($"profile={DetImageProfile}", stdout);
        Assert.Contains("usable=True", stdout);
        Assert.Contains("capability=image-embedding", stdout);
        Assert.Contains("dimension=32", stdout);
        Assert.Contains("distance_metric=cosine", stdout);
        AssertNoLeak(stdout);
    }

    [Fact]
    public async Task Cli_Active_Profile_Reports_Default_Fallback_When_Unset()
    {
        using var f = EnabledFactory();
        await SeedDeterministicAsync(f);

        var (exit, stdout, _) = await RunCli(f, "ai", "photos", "embeddings", "active-profile");
        Assert.Equal(0, exit);
        Assert.Contains("source=default-fallback", stdout);
        Assert.Contains("config_key=(unset)", stdout);
        Assert.Contains($"profile={DetImageProfile}", stdout); // capability default
        Assert.Contains("usable=True", stdout);
        AssertNoLeak(stdout);
    }

    // ---- profile-keyed backfill safety --------------------------------------

    [Fact]
    public async Task Backfill_Without_Profile_Honors_Configured_Active_Profile()
    {
        using var f = EnabledFactory(("Ai:PhotoSimilarityProfileKey", DetImageProfileV2));
        await SeedDeterministicAsync(f);
        await SeedSecondDeterministicImageProfileAsync(f, DetImageProfileV2);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 10);
        await UploadPngAsync(client, "b.png", 12);

        // No --profile → must follow the configured active profile (v2), NOT the
        // capability default (v1).
        await RunPhotosBackfillAsync(f, profileKey: null);

        Assert.Equal(2, await EmbeddingCountAsync(f, DetImageProfileV2));
        Assert.Equal(0, await EmbeddingCountAsync(f, DetImageProfile));
    }

    [Fact]
    public async Task Profile_Keyed_Backfill_Does_Not_Overwrite_Other_Profiles_And_Is_Idempotent()
    {
        using var f = EnabledFactory();
        await SeedDeterministicAsync(f);
        await SeedSecondDeterministicImageProfileAsync(f, DetImageProfileV2);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 10);
        await UploadPngAsync(client, "b.png", 12);
        await UploadPngAsync(client, "c.png", 14);

        await RunPhotosBackfillAsync(f, DetImageProfile);
        Assert.Equal(3, await EmbeddingCountAsync(f, DetImageProfile));

        // Backfilling a DIFFERENT profile adds its own rows and leaves the first
        // profile's rows untouched (coexisting, not overwritten).
        await RunPhotosBackfillAsync(f, DetImageProfileV2);
        Assert.Equal(3, await EmbeddingCountAsync(f, DetImageProfileV2));
        Assert.Equal(3, await EmbeddingCountAsync(f, DetImageProfile));
        Assert.Equal(6, await EmbeddingCountAsync(f));

        // Re-running the first profile is idempotent (no duplicate rows).
        await RunPhotosBackfillAsync(f, DetImageProfile);
        Assert.Equal(3, await EmbeddingCountAsync(f, DetImageProfile));
        Assert.Equal(6, await EmbeddingCountAsync(f));
    }
}
