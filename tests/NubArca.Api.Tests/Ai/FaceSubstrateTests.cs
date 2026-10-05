using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Ai;

// Face Substrate v0 — blob-level detection + embedding persistence, backfill
// gating/idempotency, Private-Vault exclusion, coverage/diagnostics, thresholds,
// and no-leak. Driven end-to-end through the real HTTP upload + job queue stack
// with the DETERMINISTIC backend (which now emits stable landmarked faces), so
// the whole pipeline is exercised without ONNX weights. pgvector-specific
// assertions (512-dim table, HNSW index, vector upsert) live in the Postgres
// integration suite (FaceVectorPgIntegrationTests).
public sealed class FaceSubstrateTests
{
    private static readonly string[] Forbidden =
    {
        "EmbeddingBytes", "embeddingBytes", "StorageKey", "storageKey", "storage_key",
        "BlobObjectId", "blobObjectId", "Sha256", "sha256", "/storage/objects/",
        "PasswordHash", "TokenHash", "PayloadJson", "at NubArca.",
    };

    private static void AssertNoLeak(string text)
    {
        foreach (var n in Forbidden)
        {
            Assert.DoesNotContain(n, text, StringComparison.Ordinal);
        }
    }

    private static SqliteWebApplicationFactory Factory(params (string Key, string Value)[] settings)
    {
        var dict = settings.ToDictionary(s => s.Key, s => (string?)s.Value);
        var f = new SqliteWebApplicationFactory(dict, poolHost: true);
        f.EnsureDatabaseCreated();
        return f;
    }

    // Face processing fully enabled (AI + detection + embeddings).
    private static SqliteWebApplicationFactory FacesEnabledFactory() => Factory(
        ("Ai:Enabled", "true"),
        ("Ai:FaceDetectionEnabled", "true"),
        ("Ai:FaceEmbeddingsEnabled", "true"));

    private static async Task SeedProfilesAsync(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>().SeedDeterministicProfilesAsync();
        await scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>().SeedOnnxFaceEvalProfilesAsync();
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
        return (await resp.Content.ReadFromJsonAsync<FileSummary>())!.Id;
    }

    // Move a FileItem into a (test-created) Private Vault by DB, so the global
    // vault query filter excludes it from all normal flows.
    private static async Task MoveToVaultAsync(SqliteWebApplicationFactory f, Guid ownerUserId, Guid fileId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vault = await db.PrivateVaults.FirstOrDefaultAsync(v => v.OwnerUserId == ownerUserId);
        if (vault is null)
        {
            vault = new PrivateVault
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                DisplayName = "Private",
                PasswordHash = "x",
                EncryptionMode = PrivateVaultEncryptionModes.None,
                CreatedAt = DateTime.UtcNow,
            };
            db.PrivateVaults.Add(vault);
            await db.SaveChangesAsync();
        }

        // IgnoreQueryFilters so we can load the row we are about to hide.
        var file = await db.FileItems.IgnoreQueryFilters().SingleAsync(x => x.Id == fileId);
        file.PrivateVaultId = vault.Id;
        await db.SaveChangesAsync();
    }

    private static async Task<string> RunJobAsync(SqliteWebApplicationFactory f, string jobType)
    {
        Guid jobId;
        using (var scope = f.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var job = await queue.EnqueueAsync(jobType, new AiBackfillJobPayload());
            jobId = job.Id;
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

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.BackgroundJobs.Where(j => j.Id == jobId).Select(j => j.Status).SingleAsync();
        }
    }

    [Fact]
    public async Task A_Whole_Library_Detection_Run_Chains_Nothing()
    {
        // Catching up the whole library is an explicit operation, detection
        // and recognition each asked for: a global run queues no second one.
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync("library@example.com");
        await UploadPngAsync(client, "a.png", 48);

        Assert.Equal(JobStatuses.Succeeded, await RunDetectWithoutFollowingEnqueuedJobsAsync(f));

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.FaceDetections.AnyAsync());
        Assert.Equal(0, await db.BackgroundJobs.CountAsync(j => j.Type == JobTypes.AiFacesEmbeddingsBackfill));
    }

    [Fact]
    public async Task A_Set_Of_Blobs_Is_Detected_Alone_And_Chains_Recognition_For_That_Set()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync("set@example.com");
        var inSet = new[] { await UploadPngAsync(client, "a.png", 48), await UploadPngAsync(client, "b.png", 49) };
        var outside = await UploadPngAsync(client, "c.png", 50);

        Guid jobId;
        List<Guid> set;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BackgroundJobs.ExecuteDeleteAsync();
            set = await db.FileItems.Where(x => inSet.Contains(x.Id)).Select(x => x.BlobObjectId).ToListAsync();
            jobId = (await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(
                JobTypes.AiFacesDetectBackfill, new AiBackfillJobPayload(BlobObjectIds: set))).Id;
        }
        for (var i = 0; i < 20; i++)
        {
            using var scope = f.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(10) == 0) break;
        }

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var outsideBlob = await db.FileItems.Where(x => x.Id == outside).Select(x => x.BlobObjectId).SingleAsync();
            Assert.Equal(set.OrderBy(x => x), (await db.FaceDetections.Select(d => d.BlobObjectId).Distinct().ToListAsync()).OrderBy(x => x));
            Assert.False(await db.FaceDetections.AnyAsync(d => d.BlobObjectId == outsideBlob));
            var chained = await db.BackgroundJobs.SingleAsync(j => j.Type == JobTypes.AiFacesEmbeddingsBackfill);
            Assert.Equal($"faces:embed:after:{jobId:N}", chained.IdempotencyKey);
            Assert.Equal(
                set.OrderBy(x => x),
                System.Text.Json.JsonSerializer.Deserialize<AiBackfillJobPayload>(chained.PayloadJson)!.BlobObjectIds!.OrderBy(x => x));
            Assert.Equal(
                await db.FaceDetections.CountAsync(),
                await db.FaceEmbeddings.CountAsync(e => e.EmbeddingStatus == NubArca.Api.Domain.Ai.AiArtifactStatuses.Completed));
        }
    }

    private static Task<string> RunDetectAsync(SqliteWebApplicationFactory f) => RunJobAsync(f, JobTypes.AiFacesDetectBackfill);
    private static Task<string> RunEmbedAsync(SqliteWebApplicationFactory f) => RunJobAsync(f, JobTypes.AiFacesEmbeddingsBackfill);

    private static async Task<string> RunDetectWithoutFollowingEnqueuedJobsAsync(
        SqliteWebApplicationFactory f)
    {
        Guid jobId;
        using (var scope = f.Services.CreateScope())
        {
            // Uploads enqueue their own targeted post-ingest face jobs. Remove
            // those pending jobs so this helper controls the exact boundary:
            // detection completes, then the caller creates the vault race,
            // then embedding is allowed to run.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.BackgroundJobs.ExecuteDeleteAsync();

            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            jobId = (await queue.EnqueueAsync(
                JobTypes.AiFacesDetectBackfill,
                new AiBackfillJobPayload())).Id;
        }

        for (var i = 0; i < 50; i++)
        {
            using var scope = f.Services.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            await processor.ProcessAvailableAsync(1);

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var status = await db.BackgroundJobs
                .Where(j => j.Id == jobId)
                .Select(j => j.Status)
                .SingleAsync();
            if (status is JobStatuses.Succeeded or JobStatuses.Failed or JobStatuses.Cancelled)
            {
                return status;
            }
        }

        throw new TimeoutException("Face detection job did not reach a terminal state.");
    }

    private static async Task<(int detections, int embeddings, int statuses)> CountsAsync(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (
            await db.FaceDetections.CountAsync(),
            await db.FaceEmbeddings.CountAsync(),
            await db.BlobAiArtifactStatuses.CountAsync(s => s.Capability == "face-detection"));
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

    // ---- disabled-by-default gating --------------------------------------

    [Fact]
    public void Face_Features_Are_Disabled_By_Default()
    {
        var o = new AiOptions();
        Assert.False(o.FaceDetectionEnabled);
        Assert.False(o.FaceEmbeddingsEnabled);
        Assert.False(o.FaceClusteringEnabled);
        // Provisional thresholds default from the antelopev2 evaluation.
        Assert.Equal(0.40, o.Face.ClusterSimilarityThreshold);
        Assert.Equal(0.30, o.Face.CandidateSimilarityThreshold);
        Assert.Equal(0.35, o.Face.SearchDefaultSimilarityThreshold);
        Assert.Equal(0.20, o.Face.SearchMinSimilarity);
        Assert.Equal(0.95, o.Face.SearchMaxSimilarity);
    }

    [Fact]
    public async Task Detection_Backfill_Does_Not_Run_When_Disabled()
    {
        // AI on, but face detection flag OFF (the default).
        using var f = Factory(("Ai:Enabled", "true"));
        await SeedProfilesAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 16);

        var status = await RunDetectAsync(f);

        Assert.Equal(JobStatuses.Succeeded, status);
        var (detections, _, statuses) = await CountsAsync(f);
        Assert.Equal(0, detections);
        Assert.Equal(0, statuses);
    }

    [Fact]
    public async Task Embedding_Backfill_Does_Not_Run_When_Disabled()
    {
        using var f = Factory(("Ai:Enabled", "true"), ("Ai:FaceDetectionEnabled", "true"));
        await SeedProfilesAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 16);
        await RunDetectAsync(f); // detections exist

        var status = await RunEmbedAsync(f); // embeddings flag OFF

        Assert.Equal(JobStatuses.Succeeded, status);
        var (detections, embeddings, _) = await CountsAsync(f);
        Assert.True(detections > 0);
        Assert.Equal(0, embeddings);
    }

    // ---- persistence + idempotency ---------------------------------------

    [Fact]
    public async Task Detection_Persists_Faces_And_Is_Idempotent()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 16);
        await UploadPngAsync(client, "b.png", 20);

        Assert.Equal(JobStatuses.Succeeded, await RunDetectAsync(f));
        var (d1, _, s1) = await CountsAsync(f);
        Assert.True(d1 > 0);           // deterministic backend emits 1–2 faces/image
        Assert.Equal(2, s1);           // one completion status per eligible blob

        // Re-running detection is a no-op: completed blobs drop out of candidates.
        Assert.Equal(JobStatuses.Succeeded, await RunDetectAsync(f));
        var (d2, _, s2) = await CountsAsync(f);
        Assert.Equal(d1, d2);
        Assert.Equal(s1, s2);

        // Every detection carries landmarks + a face index; none leak owner/file.
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.All(await db.FaceDetections.AsNoTracking().ToListAsync(), d =>
        {
            Assert.NotNull(d.LandmarksJson);
            Assert.True(d.FaceIndex >= 0);
            Assert.NotEqual(Guid.Empty, d.BlobObjectId);
        });
    }

    [Fact]
    public async Task Embedding_Persists_And_Is_Idempotent()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 16);
        await UploadPngAsync(client, "b.png", 20);
        await RunDetectAsync(f);

        Assert.Equal(JobStatuses.Succeeded, await RunEmbedAsync(f));
        var (detections, e1, _) = await CountsAsync(f);
        Assert.Equal(detections, e1); // one embedding per landmarked detection

        // Re-running embedding is a no-op.
        Assert.Equal(JobStatuses.Succeeded, await RunEmbedAsync(f));
        var (_, e2, _) = await CountsAsync(f);
        Assert.Equal(e1, e2);
    }

    // ---- Private-Vault exclusion -----------------------------------------

    [Fact]
    public async Task Detection_Excludes_Private_Vault_And_VaultOnly_Blob()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (userId, client) = await f.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "normal.png", 16);
        var vaultFile = await UploadPngAsync(client, "secret.png", 24);
        await MoveToVaultAsync(f, userId, vaultFile); // its blob is now vault-only

        Assert.Equal(JobStatuses.Succeeded, await RunDetectAsync(f));

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Exactly one blob (the normal one) got a detection completion status.
        Assert.Equal(1, await db.BlobAiArtifactStatuses.CountAsync(s => s.Capability == "face-detection"));

        var vaultBlobId = await db.FileItems.IgnoreQueryFilters()
            .Where(x => x.Id == vaultFile).Select(x => x.BlobObjectId).SingleAsync();
        Assert.False(await db.FaceDetections.AnyAsync(d => d.BlobObjectId == vaultBlobId));
    }

    [Fact]
    public async Task Embedding_Excludes_Blob_Moved_To_Vault_After_Detection()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (userId, client) = await f.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "normal.png", 16);
        var vaultFile = await UploadPngAsync(client, "secret.png", 24);

        // Stop after the detection job itself. The production handler queues an
        // embedding follow-up; the generic helper drains that follow-up too,
        // which would embed both blobs before this test creates the vault race.
        Assert.Equal(
            JobStatuses.Succeeded,
            await RunDetectWithoutFollowingEnqueuedJobsAsync(f));
        await MoveToVaultAsync(f, userId, vaultFile); // now hide one
        await RunEmbedAsync(f);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vaultBlobId = await db.FileItems.IgnoreQueryFilters()
            .Where(x => x.Id == vaultFile).Select(x => x.BlobObjectId).SingleAsync();

        // The vaulted blob's detections got NO embeddings; the normal blob's did.
        var vaultDetectionIds = await db.FaceDetections
            .Where(d => d.BlobObjectId == vaultBlobId).Select(d => d.Id).ToListAsync();
        Assert.NotEmpty(vaultDetectionIds);
        Assert.False(await db.FaceEmbeddings.AnyAsync(e => vaultDetectionIds.Contains(e.FaceDetectionId)));
        Assert.True(await db.FaceEmbeddings.AnyAsync());
    }

    // ---- coverage + diagnostics + thresholds -----------------------------

    [Fact]
    public async Task Coverage_Reports_Completed_And_Missing_Counts()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 16);
        await UploadPngAsync(client, "b.png", 20);
        await RunDetectAsync(f);
        await RunEmbedAsync(f);

        using var scope = f.Services.CreateScope();
        var coverage = scope.ServiceProvider.GetRequiredService<FaceCoverageService>();
        var c = await coverage.GetCoverageAsync("det-face-embedding-v1");
        Assert.NotNull(c);
        Assert.Equal(2, c!.EligibleImages);
        Assert.Equal(2, c.DetectionCompletedBlobs);
        Assert.Equal(0, c.DetectionMissingBlobs);
        Assert.True(c.FacesDetected > 0);
        Assert.Equal(c.FacesDetected, c.EmbeddingsCompleted);
        Assert.Equal(0, c.EmbeddingsMissing);
        // Deterministic profile is dim 32 → no 512 vector table support.
        Assert.False(c.VectorSupported);
    }

    [Fact]
    public async Task Diagnostics_Reports_Active_Threshold_Settings()
    {
        using var f = Factory(
            ("Ai:Enabled", "true"),
            ("Ai:FaceProfileKey", "face-insightface-antelopev2-v1"),
            ("Ai:Face:ClusterSimilarityThreshold", "0.5"));
        using var scope = f.Services.CreateScope();
        var diag = await scope.ServiceProvider.GetRequiredService<FaceDiagnosticsService>().GetAsync();

        Assert.Equal("face-insightface-antelopev2-v1", diag.ActiveFaceProfileKey);
        Assert.Equal(0.5, diag.Thresholds.ClusterSimilarityThreshold);
        Assert.Equal(0.35, diag.Thresholds.SearchDefaultSimilarityThreshold);
        // Both eval packages surface with dim 512 and (in CI) no weights present.
        Assert.Contains(diag.Models, m => m.ProfileKey == "face-insightface-antelopev2-v1" && m.Dimension == 512);
        Assert.Contains(diag.Models, m => m.ProfileKey == "face-insightface-buffalo-l-v1" && m.Dimension == 512);
        Assert.All(diag.Models, m => Assert.False(m.RecognitionPresent)); // no weights in CI
    }

    [Fact]
    public void Face_Profile_Registry_Includes_Antelopev2_And_BuffaloL()
    {
        using var f = Factory(("Ai:Enabled", "true"));
        SeedProfilesAsync(f).GetAwaiter().GetResult();
        using var scope = f.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
        Assert.NotNull(registry.GetProfileByKeyAsync("face-insightface-antelopev2-v1").GetAwaiter().GetResult());
        var buffalo = registry.GetProfileByKeyAsync("face-insightface-buffalo-l-v1").GetAwaiter().GetResult();
        Assert.NotNull(buffalo);
        Assert.Equal(512, buffalo!.Dimension);
    }

    [Theory]
    // valid defaults
    [InlineData(0.40, 0.35, 0.20, 0.95, true)]
    // threshold out of [0,1]
    [InlineData(1.40, 0.35, 0.20, 0.95, false)]
    // min >= max
    [InlineData(0.40, 0.35, 0.80, 0.50, false)]
    // default outside [min,max]
    [InlineData(0.40, 0.10, 0.20, 0.95, false)]
    public void Threshold_Config_Validates_Range(
        double cluster, double searchDefault, double min, double max, bool expectValid)
    {
        var options = new AiOptions
        {
            Face = new AiFaceOptions
            {
                ClusterSimilarityThreshold = cluster,
                SearchDefaultSimilarityThreshold = searchDefault,
                SearchMinSimilarity = min,
                SearchMaxSimilarity = max,
            },
        };
        var result = new AiFaceOptionsValidator().Validate(null, options);
        Assert.Equal(expectValid, result.Succeeded);
    }

    // ---- CLI + no-leak ---------------------------------------------------

    [Fact]
    public async Task Cli_Coverage_And_Diagnostics_Report_Safely()
    {
        using var f = FacesEnabledFactory();
        await SeedProfilesAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        await UploadPngAsync(client, "a.png", 16);
        await RunDetectAsync(f);
        await RunEmbedAsync(f);

        var cov = await RunCli(f, "ai", "faces", "coverage", "--profile", "det-face-embedding-v1");
        Assert.Equal(0, cov.exit);
        Assert.Contains("eligible_images=1", cov.stdout);
        Assert.Contains("faces_detected=", cov.stdout);
        AssertNoLeak(cov.stdout);

        var diag = await RunCli(f, "ai", "faces", "diagnostics");
        Assert.Equal(0, diag.exit);
        Assert.Contains("cluster_similarity_threshold=0.4", diag.stdout);
        Assert.Contains("face_detection_enabled=True", diag.stdout);
        AssertNoLeak(diag.stdout);
    }

    [Fact]
    public async Task Cli_Backfill_Refuses_When_Capability_Disabled()
    {
        using var f = Factory(("Ai:Enabled", "true")); // detection flag OFF
        await SeedProfilesAsync(f);
        var r = await RunCli(f, "ai", "faces", "backfill", "detection", "--profile", "det-face-embedding-v1");
        Assert.NotEqual(0, r.exit);
        Assert.Contains("Ai__FaceDetectionEnabled", r.stderr);
    }
}
