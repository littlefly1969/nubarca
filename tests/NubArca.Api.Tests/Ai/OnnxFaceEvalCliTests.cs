using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Onnx.Face;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Ai;

// The `ai face …` face-model EVALUATION CLI, driven through the real dispatcher.
// No real ONNX weights are present, so commands report "unavailable" cleanly,
// write nothing, and leak nothing. Face processing is off by default. One test
// uses dummy (invalid) model files to prove the benchmark candidate set EXCLUDES
// Private Vault content while inference fails safely.
public sealed class OnnxFaceEvalCliTests
{
    private static readonly string[] Forbidden =
    {
        "StorageKey", "storageKey", "BlobObjectId", "blobObjectId",
        "Sha256", "sha256", "/storage/objects/", "EmbeddingBytes", "embeddingBytes",
        "PasswordHash", "TokenHash", "PayloadJson", "at NubArca", // stack-trace frame marker
    };

    private static SqliteWebApplicationFactory EnabledFactory(params (string Key, string Value)[] extra)
    {
        var dict = new Dictionary<string, string?> { ["Ai:Enabled"] = "true" };
        foreach (var (k, v) in extra)
        {
            dict[k] = v;
        }

        var f = new SqliteWebApplicationFactory(dict);
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

    private static async Task SeedFaceProfilesAsync(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
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

    private static async Task<int> FaceRowCountsAsync(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FaceDetections.CountAsync() + await db.FaceEmbeddings.CountAsync();
    }

    [Fact]
    public async Task Models_Lists_Candidates_With_License_And_No_Leak()
    {
        using var f = EnabledFactory();
        var (exit, stdout, stderr) = await RunCli(f, "ai", "face", "models");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(OnnxFaceModels.Antelopev2ProfileKey, stdout);
        Assert.Contains(OnnxFaceModels.BuffaloLProfileKey, stdout);
        Assert.Contains("dim=512", stdout);
        Assert.Contains("detector_present=False", stdout); // no weights in CI
        Assert.Contains("recognition_present=False", stdout);
        Assert.Contains("non-commercial", stdout); // license note surfaced
        Assert.Contains("Commercial use NOT assumed", stdout);
        AssertNoLeak(stdout);
    }

    [Fact]
    public async Task Seed_Profiles_Is_Idempotent_And_Not_Default()
    {
        using var f = EnabledFactory();
        var first = await RunCli(f, "ai", "face", "seed-profiles");
        var second = await RunCli(f, "ai", "face", "seed-profiles");

        Assert.Equal(0, first.exit);
        Assert.Equal(0, second.exit);
        Assert.Contains("profiles_created=2", first.stdout);
        Assert.Contains("profiles_created=0", second.stdout); // idempotent

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var faceProfiles = await db.AiProfiles.AsNoTracking()
            .Where(p => p.Key == OnnxFaceModels.Antelopev2ProfileKey || p.Key == OnnxFaceModels.BuffaloLProfileKey)
            .ToListAsync();
        Assert.Equal(2, faceProfiles.Count);
        Assert.All(faceProfiles, p => Assert.False(p.IsDefault));           // never default
        Assert.All(faceProfiles, p => Assert.Equal(512, p.Dimension));
        Assert.True(await db.AiModels.AnyAsync(m => m.Key == OnnxFaceModels.Antelopev2Key && m.Provider == "onnx"));
    }

    [Fact]
    public async Task Detect_Embed_Compare_Benchmark_Unavailable_Without_Model()
    {
        using var f = EnabledFactory();
        await SeedFaceProfilesAsync(f);
        var id = Guid.NewGuid().ToString();
        var profile = OnnxFaceModels.Antelopev2ProfileKey;

        var det = await RunCli(f, "ai", "face", "detect-test", "--profile", profile, "--file", id);
        var emb = await RunCli(f, "ai", "face", "embed-test", "--profile", profile, "--file", id);
        var cmp = await RunCli(f, "ai", "face", "compare", "--profile", profile, "--file-a", id, "--file-b", id);
        var bench = await RunCli(f, "ai", "face", "benchmark", "--profile", profile, "--limit", "5");

        foreach (var r in new[] { det, emb, cmp, bench })
        {
            Assert.Equal(0, r.exit);                       // clean, not a failure
            Assert.Contains("unavailable", r.stdout);
            Assert.Contains("onnx-", r.stdout);            // onnx-modeldir-not-configured / onnx-face-*-not-found
            AssertNoLeak(r.stdout);
        }

        Assert.Equal(0, await FaceRowCountsAsync(f));       // eval writes no face rows
    }

    [Fact]
    public async Task Detect_Test_Requires_File_And_Profile()
    {
        using var f = EnabledFactory();
        var missingProfile = await RunCli(f, "ai", "face", "detect-test", "--file", Guid.NewGuid().ToString());
        Assert.Equal(64, missingProfile.exit);
        Assert.Contains("--profile", missingProfile.stderr);

        var missingFile = await RunCli(f, "ai", "face", "detect-test", "--profile", OnnxFaceModels.Antelopev2ProfileKey);
        Assert.Equal(64, missingFile.exit);
        Assert.Contains("--file", missingFile.stderr);
    }

    [Fact]
    public async Task Benchmark_Requires_Profile_When_No_Config_Default()
    {
        using var f = EnabledFactory();
        var (exit, _, stderr) = await RunCli(f, "ai", "face", "benchmark");
        Assert.Equal(64, exit);
        Assert.Contains("--profile", stderr);
    }

    [Fact]
    public async Task Face_Features_Disabled_By_Default()
    {
        var options = new AiOptions();
        Assert.False(options.FaceDetectionEnabled);
        Assert.False(options.FaceEmbeddingsEnabled);
        Assert.False(options.FaceClusteringEnabled);
        Assert.Null(options.FaceProfileKey);
    }

    [Fact]
    public async Task Uses_Configured_FaceProfileKey_As_Default_Profile()
    {
        // With Ai__FaceProfileKey set, `ai face benchmark` needs no --profile and
        // resolves that profile (still unavailable without weights, but not a
        // usage error).
        using var f = EnabledFactory(("Ai:FaceProfileKey", OnnxFaceModels.BuffaloLProfileKey));
        await SeedFaceProfilesAsync(f);
        var (exit, stdout, stderr) = await RunCli(f, "ai", "face", "benchmark", "--limit", "3");
        // Exit 0 + "unavailable" (not a 64 usage error) proves the configured
        // default profile was resolved without an explicit --profile.
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("unavailable", stdout);
        AssertNoLeak(stdout);
    }

    [Fact]
    public async Task Benchmark_Excludes_Private_Vault_And_Fails_Inference_Safely()
    {
        // Dummy (invalid) model files make the backend "available" so the
        // benchmark actually iterates its candidate set; inference then fails
        // per-image and is counted, never crashing or leaking. This proves the
        // eligible candidate set excludes vaulted content.
        var config = OnnxFaceModels.Catalog[OnnxFaceModels.Antelopev2Key];
        var modelDir = Path.Combine(Path.GetTempPath(), $"face-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(modelDir, config.PackageSubdir));
        File.WriteAllText(Path.Combine(modelDir, config.PackageSubdir, config.DetectorFile), "not-a-real-onnx");
        File.WriteAllText(Path.Combine(modelDir, config.PackageSubdir, config.RecognitionFile), "not-a-real-onnx");

        try
        {
            using var f = EnabledFactory(("Ai:Onnx:ModelDir", modelDir));
            await SeedFaceProfilesAsync(f);
            var (_, client) = await f.CreateAuthenticatedClientAsync();

            // Two distinct images (distinct blobs); move one into the vault.
            var visible = await UploadPngAsync(client, "visible.png", 12);
            var hidden = await UploadPngAsync(client, "hidden.png", 24);

            (await client.PostAsJsonAsync("/api/private-vault/setup", new { password = "vault-pass-123" }))
                .EnsureSuccessStatusCode();
            var unlock = await client.PostAsJsonAsync("/api/private-vault/unlock", new { password = "vault-pass-123" });
            unlock.EnsureSuccessStatusCode();
            var token = System.Text.Json.JsonDocument.Parse(await unlock.Content.ReadAsStringAsync())
                .RootElement.GetProperty("token").GetString();

            using (var move = new HttpRequestMessage(HttpMethod.Post, "/api/private-vault/move-in")
            {
                Content = JsonContent.Create(new { fileIds = new[] { hidden }, folderIds = Array.Empty<Guid>() }),
            })
            {
                move.Headers.Add("X-Vault-Token", token);
                (await client.SendAsync(move)).EnsureSuccessStatusCode();
            }

            var (exit, stdout, stderr) = await RunCli(
                f, "ai", "face", "benchmark", "--profile", OnnxFaceModels.Antelopev2ProfileKey, "--limit", "100");

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr);
            Assert.DoesNotContain("unavailable", stdout);        // model files present → available
            Assert.Contains("images_attempted=1", stdout);       // only the non-vault image
            Assert.Contains("succeeded=0 failed=1", stdout);     // invalid model → safe failure
            Assert.Contains("processing-error", stdout);
            AssertNoLeak(stdout);
            Assert.DoesNotContain("hidden.png", stdout);         // vault file name never appears

            Assert.Equal(0, await FaceRowCountsAsync(f));         // still no persisted face rows
        }
        finally
        {
            Directory.Delete(modelDir, recursive: true);
        }
    }
}
