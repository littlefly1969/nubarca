using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Ai;

// Phase 2A: the `ai onnx image …` evaluation CLI + the ONNX-profile backfill
// safety contract, driven through the real dispatcher. No ONNX weights are
// present, so commands report "unavailable" cleanly, write nothing, and leak
// nothing.
public sealed class OnnxImageEvalCliTests
{
    private static readonly string[] Forbidden =
    {
        "StorageKey", "storageKey", "BlobObjectId", "blobObjectId",
        "Sha256", "sha256", "/storage/objects/", "/models/",
        "EmbeddingBytes", "embeddingBytes", "Vector", "vector",
    };

    private static SqliteWebApplicationFactory EnabledFactory()
    {
        var f = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ImageEmbeddingsEnabled"] = "true",
        });
        f.EnsureDatabaseCreated();
        return f;
    }

    private static async Task<(int exit, string stdout, string stderr)> RunCli(
        SqliteWebApplicationFactory factory, params string[] args)
    {
        using var scope = factory.Services.CreateScope();
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

    private static async Task<int> EmbeddingCountAsync(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BlobEmbeddings.CountAsync();
    }

    [Fact]
    public async Task Models_Lists_Candidates_Without_Leaks()
    {
        using var f = EnabledFactory();
        var (exit, stdout, stderr) = await RunCli(f, "ai", "onnx", "image", "models");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("photo-siglip2-so400m-patch14-384-v2", stdout);
        Assert.DoesNotContain("photo-siglip2-base", stdout);
        Assert.DoesNotContain("photo-dinov2", stdout);
        Assert.Contains("model_present=False", stdout); // no weights in CI
        Assert.Contains("text_model_present=False", stdout);
        Assert.Contains("tokenizer_present=False", stdout);
        AssertNoLeak(stdout);
    }

    [Fact]
    public async Task Seed_Profiles_Is_Idempotent_And_Not_Default()
    {
        using var f = EnabledFactory();
        var first = await RunCli(f, "ai", "onnx", "image", "seed-profiles");
        var second = await RunCli(f, "ai", "onnx", "image", "seed-profiles");

        Assert.Equal(0, first.exit);
        Assert.Equal(0, second.exit);
        Assert.Contains("profiles_created=1", first.stdout);
        Assert.Contains("profiles_created=0", second.stdout); // idempotent

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // ONNX eval profiles exist, provider onnx, and are NOT default.
        var onnxProfiles = await db.AiProfiles.AsNoTracking()
            .Where(p => p.Key == OnnxImageModels.SiglipSo400mProfileKey)
            .ToListAsync();
        Assert.Single(onnxProfiles);
        Assert.All(onnxProfiles, p => Assert.False(p.IsDefault));
        Assert.All(onnxProfiles, p => Assert.Equal(AiModalities.Multimodal, p.Modality));
        Assert.True(await db.AiModels.AnyAsync(m => m.Provider == "onnx"));
    }

    [Fact]
    public async Task Benchmark_Unavailable_Without_Model_Writes_Nothing_And_No_Leak()
    {
        using var f = EnabledFactory();
        await RunCli(f, "ai", "onnx", "image", "seed-profiles");

        var (exit, stdout, stderr) = await RunCli(
            f, "ai", "onnx", "image", "benchmark", "--profile", OnnxImageModels.SiglipSo400mProfileKey, "--limit", "5");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("unavailable", stdout);
        Assert.Contains("onnx-", stdout); // onnx-modeldir-not-configured or onnx-model-not-found
        Assert.Equal(0, await EmbeddingCountAsync(f)); // dry-run never writes
        AssertNoLeak(stdout);
    }

    [Fact]
    public async Task EmbedTest_And_Compare_Unavailable_Without_Model()
    {
        using var f = EnabledFactory();
        await RunCli(f, "ai", "onnx", "image", "seed-profiles");
        var someId = Guid.NewGuid().ToString();

        var et = await RunCli(f, "ai", "onnx", "image", "embed-test",
            "--profile", OnnxImageModels.SiglipSo400mProfileKey, "--file", someId);
        Assert.Equal(0, et.exit);
        Assert.Contains("unavailable", et.stdout);
        AssertNoLeak(et.stdout);

        var cmp = await RunCli(f, "ai", "onnx", "image", "compare",
            "--profile", OnnxImageModels.SiglipSo400mProfileKey, "--file", someId);
        Assert.Equal(0, cmp.exit);
        Assert.Contains("unavailable", cmp.stdout);
        AssertNoLeak(cmp.stdout);

        Assert.Equal(0, await EmbeddingCountAsync(f));
    }

    [Fact]
    public async Task Benchmark_Requires_Profile()
    {
        using var f = EnabledFactory();
        var (exit, _, stderr) = await RunCli(f, "ai", "onnx", "image", "benchmark");
        Assert.Equal(64, exit);
        Assert.Contains("--profile", stderr);
    }

    [Fact]
    public async Task Photos_Backfill_With_Missing_Onnx_Model_Writes_No_Per_Blob_Rows()
    {
        using var f = EnabledFactory();
        using (var scope = f.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>()
                .SeedOnnxImageEvalProfilesAsync();
        }

        // Backfill targeting the ONNX profile whose model file is absent.
        using (var scope = f.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            await queue.EnqueueAsync(
                JobTypes.AiPhotosEmbeddingsBackfill,
                new AiBackfillJobPayload(ProfileKey: OnnxImageModels.SiglipSo400mProfileKey));
        }
        using (var scope = f.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(10);
        }

        using var verify = f.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking()
            .SingleAsync(j => j.Type == JobTypes.AiPhotosEmbeddingsBackfill);
        Assert.Equal(JobStatuses.Succeeded, job.Status);            // unavailable model = clean no-op
        Assert.Equal(0, await db.BlobEmbeddings.CountAsync());      // no embeddings
        Assert.Equal(0, await db.BlobAiArtifactStatuses.CountAsync()); // no per-blob skipped/failed rows
        var diagnostics = await db.AiIndexDiagnostics.AsNoTracking().ToListAsync();
        Assert.True(diagnostics.Count <= 1);                        // at most one aggregate diagnostic
        Assert.All(diagnostics, d => Assert.False(d.IsPermanent));  // transient, never permanent
    }
}
