using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Ai;

// Phase 0C: the `ai` operator CLI, wired through the real dispatcher with a
// SQLite-backed service provider (the factory registers the AI substrate).
public sealed class AiCliTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public AiCliTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<(int exit, string stdout, string stderr)> RunCli(params string[] args)
    {
        using var scope = _factory.Services.CreateScope();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliEntryPoint.RunAsync(args, stdout, stderr, () => scope.ServiceProvider);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static readonly string[] ForbiddenNeedles =
    {
        "StorageKey", "storageKey", "storage_key",
        "BlobObjectId", "blobObjectId",
        "Sha256", "sha256",
        "/storage/objects/", "PayloadJson", "TokenHash",
        "EmbeddingBytes", "Vector", "stack trace", "Exception:",
    };

    private static void AssertNoForbidden(string text)
    {
        foreach (var needle in ForbiddenNeedles)
        {
            Assert.DoesNotContain(needle, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Status_Works_When_Ai_Disabled()
    {
        var (exit, stdout, stderr) = await RunCli("ai", "status");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("enabled=False", stdout);
        Assert.Contains("provider=none", stdout);
        AssertNoForbidden(stdout);
    }

    [Fact]
    public async Task Models_And_Profiles_Empty_By_Default_And_Safe()
    {
        var models = await RunCli("ai", "models");
        Assert.Equal(0, models.exit);
        Assert.Contains("(none)", models.stdout);

        var profiles = await RunCli("ai", "profiles");
        Assert.Equal(0, profiles.exit);
        Assert.Contains("(none)", profiles.stdout);
    }

    [Fact]
    public async Task Seed_Then_Models_And_Profiles_List_Only_Safe_Fields()
    {
        var seed = await RunCli("ai", "seed");
        Assert.Equal(0, seed.exit);

        var models = await RunCli("ai", "models");
        Assert.Equal(0, models.exit);
        Assert.Contains("deterministic-v1", models.stdout);
        Assert.Contains("provider=deterministic", models.stdout);
        AssertNoForbidden(models.stdout);
        // No GUIDs leaked (the deterministic model id is a Guid; its 'N' form is
        // 32 hex chars — the stable key is what we print instead).
        Assert.DoesNotContain("AiModelId", models.stdout);

        var profiles = await RunCli("ai", "profiles");
        Assert.Equal(0, profiles.exit);
        Assert.Contains("det-image-embedding-v1", profiles.stdout);
        Assert.Contains("model=deterministic-v1", profiles.stdout);
        AssertNoForbidden(profiles.stdout);
    }

    [Fact]
    public async Task Seed_Is_Idempotent()
    {
        var first = await RunCli("ai", "seed");
        var second = await RunCli("ai", "seed");

        Assert.Equal(0, first.exit);
        Assert.Equal(0, second.exit);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Exactly one deterministic model and one profile per capability — a
        // second seed created nothing new.
        Assert.Equal(1, await db.AiModels.CountAsync());
        var profileCount = await db.AiProfiles.CountAsync();
        Assert.Equal(profileCount, await db.AiProfiles.Select(p => p.Key).Distinct().CountAsync());
        Assert.Contains("profiles_created=0", second.stdout);
    }

    [Fact]
    public async Task Deterministic_Profiles_Are_Not_Created_On_Startup()
    {
        // The factory ran Program.cs startup + EnsureDatabaseCreated. Nothing
        // should have seeded models/profiles automatically.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.AiModels.CountAsync());
        Assert.Equal(0, await db.AiProfiles.CountAsync());
    }

    [Fact]
    public async Task Diagnostics_Is_Aggregate_Only_And_Empty_By_Default()
    {
        var (exit, stdout, stderr) = await RunCli("ai", "diagnostics");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("aggregate-only", stdout);
        Assert.Contains("(no diagnostics)", stdout);
        AssertNoForbidden(stdout);
    }

    [Fact]
    public async Task Jobs_Enqueue_Ai_Backfill_Creates_Queued_Row()
    {
        var (exit, stdout, stderr) = await RunCli(
            "jobs", "enqueue", "ai-photos-embeddings-backfill", "--profile", "det-image-embedding-v1");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(JobTypes.AiPhotosEmbeddingsBackfill, stdout);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobTypes.AiPhotosEmbeddingsBackfill, job.Type);
        Assert.Equal(JobStatuses.Queued, job.Status);
        // Compute band (priority 200).
        Assert.Equal(JobScheduling.Compute, job.Priority);
    }

    [Fact]
    public async Task Jobs_Enqueue_All_Ai_Backfills_Are_Recognized()
    {
        string[] names =
        {
            "ai-photos-embeddings-backfill",
            "ai-documents-extract-backfill",
            "ai-documents-embeddings-backfill",
            "ai-faces-detect-backfill",
            "ai-faces-embeddings-backfill",
            "ai-faces-cluster-backfill",
            "ai-tags-generate-backfill",
        };

        foreach (var name in names)
        {
            var (exit, _, stderr) = await RunCli("jobs", "enqueue", name);
            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(names.Length, await db.BackgroundJobs.CountAsync());
    }

    [Fact]
    public async Task Help_Lists_Ai_Commands()
    {
        var (exit, stdout, _) = await RunCli("--help");
        Assert.Equal(0, exit);
        Assert.Contains("ai status", stdout);
        Assert.Contains("ai seed", stdout);
        Assert.Contains("ai-photos-embeddings-backfill", stdout);
    }
}
