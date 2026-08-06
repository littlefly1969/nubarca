using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Admin;

// Admin jobs console — the catalog + enqueue endpoints. Admin-gated; each
// command is validated against its descriptor and enqueued with the same
// job type + payload + idempotency key as the `jobs enqueue` CLI. The audit
// row records ONLY the command key, never the submitted parameters.
public sealed class AdminJobsEnqueueEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public AdminJobsEnqueueEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> AdminClientAsync(string email = "admin@example.com")
    {
        var userId = await _factory.SeedUserAsync(email);
        await _factory.PromoteToAdminAsync(userId);
        return await _factory.LoginAsync(email);
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await work(db);
    }

    private static HttpContent Body(string command, object? parameters = null)
        => JsonContent.Create(new { command, @params = parameters });

    // ── catalog ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Catalog_Requires_Admin()
    {
        await _factory.SeedUserAsync("user@example.com");
        var user = await _factory.LoginAsync("user@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/jobs/catalog")).StatusCode);

        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/jobs/catalog")).StatusCode);
    }

    [Fact]
    public async Task Catalog_Lists_Commands_With_Param_Specs()
    {
        var admin = await AdminClientAsync();
        var json = await (await admin.GetAsync("/api/admin/jobs/catalog")).Content.ReadFromJsonAsync<JsonElement>();

        var commands = json.GetProperty("commands").EnumerateArray().ToList();
        var keys = commands.Select(c => c.GetProperty("key").GetString()).ToHashSet();
        Assert.Contains("metadata-backfill", keys);
        Assert.Contains("media-video-hls-backfill", keys);
        Assert.Contains("media-gallery-derivatives-regenerate", keys);
        Assert.Contains("ai-faces-cluster-backfill", keys);
        Assert.Contains("storage-reconcile", keys);
        // The removed narrow face endpoint's work is covered by the ai-faces-* commands.
        Assert.Contains("ai-faces-detect-backfill", keys);

        var hls = commands.Single(c => c.GetProperty("key").GetString() == "media-video-hls-backfill");
        Assert.Equal("media", hls.GetProperty("category").GetString());
        var pnames = hls.GetProperty("params").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "limit", "retryFailed", "force", "dryRun" }, pnames);
        var force = hls.GetProperty("params").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "force");
        Assert.True(force.GetProperty("danger").GetBoolean());
    }

    // ── enqueue ───────────────────────────────────────────────────────────────

    // ── dynamic catalog: availability + choice options ─────────────────────

    [Fact]
    public async Task Catalog_Marks_Commands_Whose_Feature_Is_Switched_Off()
    {
        var admin = await AdminClientAsync();
        var json = await (await admin.GetAsync("/api/admin/jobs/catalog")).Content.ReadFromJsonAsync<JsonElement>();
        var commands = json.GetProperty("commands").EnumerateArray().ToList();

        // The test host runs with AI + the ffmpeg/HLS providers off, so those
        // commands must come back disabled with a stable reason code (never a
        // silently-enqueued no-op).
        var tags = commands.Single(c => c.GetProperty("key").GetString() == "ai-tags-generate-backfill");
        Assert.False(tags.GetProperty("available").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(tags.GetProperty("disabledReason").GetString()));

        var hls = commands.Single(c => c.GetProperty("key").GetString() == "media-video-hls-backfill");
        Assert.False(hls.GetProperty("available").GetBoolean());
        Assert.Equal("hls-disabled", hls.GetProperty("disabledReason").GetString());

        // A command with no feature gate stays available.
        var meta = commands.Single(c => c.GetProperty("key").GetString() == "metadata-backfill");
        Assert.True(meta.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Catalog_Offers_The_Profile_As_A_Choice_With_The_Configured_Model_Preselected()
    {
        // Seed the deterministic profiles so the registry has something to
        // offer, then point the config at one of them.
        using (var scope = _factory.Services.CreateScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<NubArca.Api.Ai.IAiProfileRegistry>();
            await registry.SeedDeterministicProfilesAsync();
        }

        var admin = await AdminClientAsync();
        var json = await (await admin.GetAsync("/api/admin/jobs/catalog")).Content.ReadFromJsonAsync<JsonElement>();
        var photos = json.GetProperty("commands").EnumerateArray()
            .Single(c => c.GetProperty("key").GetString() == "ai-photos-embeddings-backfill");

        var profileParam = photos.GetProperty("params").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "profileKey");
        // It is a select, not a free-text box.
        Assert.Equal("choice", profileParam.GetProperty("kind").GetString());

        var options = profileParam.GetProperty("options").EnumerateArray().ToList();
        Assert.NotEmpty(options);
        // Every option is an image-embedding profile key, and exactly one is
        // flagged as the recommended (configured/default) one.
        Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o.GetProperty("value").GetString())));
        Assert.Single(options, o => o.GetProperty("recommended").GetBoolean());
        var recommended = options.Single(o => o.GetProperty("recommended").GetBoolean())
            .GetProperty("value").GetString();
        Assert.Equal(recommended, profileParam.GetProperty("defaultText").GetString());
    }

    [Fact]
    public async Task Enqueue_Rejects_A_Profile_Outside_The_Offered_Options()
    {
        var admin = await AdminClientAsync();
        var resp = await admin.PostAsync("/api/admin/jobs/enqueue",
            Body("ai-photos-embeddings-backfill", new { profileKey = "not-a-registered-profile" }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, await InDbAsync(db => db.BackgroundJobs.CountAsync()));
    }

    [Fact]
    public async Task Pending_Counts_Are_Admin_Only_And_Safe()
    {
        await _factory.SeedUserAsync("user@example.com");
        var user = await _factory.LoginAsync("user@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/jobs/pending")).StatusCode);

        var admin = await AdminClientAsync();
        var resp = await admin.GetAsync("/api/admin/jobs/pending");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        // Counts only — a map of command key → number, no ids/paths/keys.
        var counts = JsonSerializer.Deserialize<Dictionary<string, int>>(body)!;
        Assert.Contains("metadata-backfill", counts.Keys);
        Assert.All(counts.Values, v => Assert.True(v >= 0));
    }

    [Fact]
    public async Task Enqueue_Requires_Admin()
    {
        await _factory.SeedUserAsync("user@example.com");
        var user = await _factory.LoginAsync("user@example.com");
        var resp = await user.PostAsync("/api/admin/jobs/enqueue", Body("metadata-backfill"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(0, await InDbAsync(db => db.BackgroundJobs.CountAsync()));
    }

    [Fact]
    public async Task Enqueue_Unknown_Command_Is_400()
    {
        var admin = await AdminClientAsync();
        var resp = await admin.PostAsync("/api/admin/jobs/enqueue", Body("does-not-exist"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, await InDbAsync(db => db.BackgroundJobs.CountAsync()));
    }

    [Fact]
    public async Task Enqueue_Metadata_Backfill_Creates_Queued_Row_With_Concrete_Payload()
    {
        var admin = await AdminClientAsync();
        var resp = await admin.PostAsync("/api/admin/jobs/enqueue",
            Body("metadata-backfill", new { limit = 25, failedOnly = true, dryRun = false }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JobTypes.MetadataEmbeddedBackfill, dto.GetProperty("jobType").GetString());

        var jobRow = await InDbAsync(db => db.BackgroundJobs.AsNoTracking().SingleAsync());
        Assert.Equal(JobTypes.MetadataEmbeddedBackfill, jobRow.Type);
        Assert.Equal(JobStatuses.Queued, jobRow.Status);
        // Regression guard: the payload (built as `object`) must serialize to
        // its CONCRETE shape, not "{}".
        var payload = JsonDocument.Parse(jobRow.PayloadJson).RootElement;
        Assert.Equal(25, payload.GetProperty("Limit").GetInt32());
        Assert.True(payload.GetProperty("FailedOnly").GetBoolean());
        Assert.False(payload.GetProperty("DryRun").GetBoolean());
    }

    [Fact]
    public async Task Enqueue_Clamps_Int_And_Audits_Only_The_Command_Key()
    {
        var admin = await AdminClientAsync();
        // 999999 exceeds the limit cap (100000) → clamped, not rejected.
        var resp = await admin.PostAsync("/api/admin/jobs/enqueue",
            Body("media-video-hls-backfill", new { limit = 999999, force = true }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var jobRow = await InDbAsync(db => db.BackgroundJobs.AsNoTracking().SingleAsync());
        var payload = JsonDocument.Parse(jobRow.PayloadJson).RootElement;
        Assert.Equal(100000, payload.GetProperty("Limit").GetInt32());
        Assert.True(payload.GetProperty("Force").GetBoolean());

        var audit = await InDbAsync(db => db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == NubArca.Api.Audit.AuditActions.AdminJobEnqueue));
        Assert.Equal(jobRow.Id, audit.EntityId);
        // The audit metadata keeps only the command key — never the params
        // (no "force"/"limit"/"999999" leaks into the audit trail).
        Assert.Contains("media-video-hls-backfill", audit.MetadataJson);
        Assert.DoesNotContain("999999", audit.MetadataJson);
        Assert.DoesNotContain("force", audit.MetadataJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enqueue_Ai_Command_Is_Idempotent_Per_Profile()
    {
        // A registered profile key is required now: the console only offers
        // real options and the endpoint rejects anything else.
        string faceProfileKey;
        using (var scope = _factory.Services.CreateScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<NubArca.Api.Ai.IAiProfileRegistry>();
            await registry.SeedDeterministicProfilesAsync();
            var profiles = await registry.ListProfilesAsync(enabledOnly: true);
            faceProfileKey = profiles.First(p =>
                p.Capability == NubArca.Api.Domain.Ai.AiCapabilities.FaceEmbedding).Key;
        }

        var admin = await AdminClientAsync();

        // No profile selected → the handler's configured default; two calls
        // collapse onto one queued job (idempotency key type:default).
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsync("/api/admin/jobs/enqueue", Body("ai-faces-cluster-backfill"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsync("/api/admin/jobs/enqueue", Body("ai-faces-cluster-backfill"))).StatusCode);
        Assert.Equal(1, await InDbAsync(db => db.BackgroundJobs
            .CountAsync(j => j.Type == JobTypes.AiFacesClusterBackfill)));

        // An explicitly chosen profile is a DISTINCT job (key type:profile).
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/admin/jobs/enqueue",
            Body("ai-faces-cluster-backfill", new { profileKey = faceProfileKey }))).StatusCode);
        Assert.Equal(2, await InDbAsync(db => db.BackgroundJobs
            .CountAsync(j => j.Type == JobTypes.AiFacesClusterBackfill)));

        // ...and repeating that same choice collapses again.
        await admin.PostAsync("/api/admin/jobs/enqueue",
            Body("ai-faces-cluster-backfill", new { profileKey = faceProfileKey }));
        Assert.Equal(2, await InDbAsync(db => db.BackgroundJobs
            .CountAsync(j => j.Type == JobTypes.AiFacesClusterBackfill)));
    }

    // Regression: four identical media.posters.regenerate rows appeared in prod
    // because library-wide backfills had no idempotency key, so every click
    // queued another full run. They must now collapse onto the pending one and
    // say so.
    [Fact]
    public async Task Repeated_Global_Backfill_Collapses_Onto_The_Queued_Run()
    {
        var admin = await AdminClientAsync();

        var first = await admin.PostAsync("/api/admin/jobs/enqueue",
            Body("media-derivatives-backfill", new { dryRun = false }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstDto = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(firstDto.GetProperty("alreadyQueued").GetBoolean());

        for (var i = 0; i < 3; i++)
        {
            var again = await admin.PostAsync("/api/admin/jobs/enqueue",
                Body("media-derivatives-backfill", new { dryRun = false }));
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            var dto = await again.Content.ReadFromJsonAsync<JsonElement>();
            // Same job, and the UI is told it was already waiting.
            Assert.True(dto.GetProperty("alreadyQueued").GetBoolean());
            Assert.Equal(firstDto.GetProperty("jobId").GetString(), dto.GetProperty("jobId").GetString());
        }

        Assert.Equal(1, await InDbAsync(db => db.BackgroundJobs
            .CountAsync(j => j.Type == JobTypes.MediaDerivativesBackfill)));
    }

    [Fact]
    public async Task Enqueue_Single_Hls_Requires_Blob_Guid()
    {
        var admin = await AdminClientAsync();

        var missing = await admin.PostAsync("/api/admin/jobs/enqueue", Body("media-video-hls-generate"));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var bad = await admin.PostAsync("/api/admin/jobs/enqueue",
            Body("media-video-hls-generate", new { blobId = "not-a-guid" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var blobId = Guid.NewGuid();
        var ok = await admin.PostAsync("/api/admin/jobs/enqueue",
            Body("media-video-hls-generate", new { blobId = blobId.ToString() }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var jobRow = await InDbAsync(db => db.BackgroundJobs.AsNoTracking().SingleAsync());
        Assert.Equal(JobTypes.MediaVideoHlsGenerate, jobRow.Type);
        Assert.Equal($"{JobTypes.MediaVideoHlsGenerate}:{blobId:N}", jobRow.IdempotencyKey);
        var payload = JsonDocument.Parse(jobRow.PayloadJson).RootElement;
        Assert.Equal(blobId, payload.GetProperty("BlobObjectId").GetGuid());
    }
}
