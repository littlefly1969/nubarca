using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Aesthetics;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Aesthetics;

// Service-level tests for the owner-private Aesthetics Lab: blob-reference
// lifecycle + accounting, and the analysis run/job orchestration (with a strict
// FAKE sidecar). Resolves the real DI graph from the endpoint factory so the
// exact production service wiring is exercised.
public class AestheticsLabServiceTests
{
    private static SqliteWebApplicationFactory NewFactory(bool enabled = true) =>
        new(new Dictionary<string, string?>
        {
            ["HumanAesExpert:Enabled"] = enabled ? "true" : "false",
            ["HumanAesExpert:SidecarBaseUrl"] = "http://fake:8091",
        });

    private static async Task<Guid> UploadLabItemAsync(IServiceProvider sp, Guid owner, byte[] png)
    {
        var lab = sp.GetRequiredService<IAestheticLabService>();
        var dto = await lab.AddFromUploadAsync(owner, "a.png", "image/png", new MemoryStream(png));
        return dto.Id;
    }

    // An enabled janitor with grace 0 (mirrors BlobJanitorTests) so a zero-ref
    // blob is reclaimable immediately.
    private static BlobJanitor EnabledJanitor(SqliteWebApplicationFactory factory) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new BlobJanitorOptions { Enabled = true, IntervalMinutes = 5, GraceMinutes = 0 }),
            TimeProvider.System,
            NullLogger<BlobJanitor>.Instance);

    private static async Task<long> RefCountAsync(AppDbContext db, Guid itemId)
    {
        var blobId = await db.AestheticLabItems.AsNoTracking()
            .Where(i => i.Id == itemId).Select(i => i.BlobObjectId).FirstAsync();
        return await db.BlobObjects.AsNoTracking().Where(b => b.Id == blobId).Select(b => b.ReferenceCount).FirstAsync();
    }

    // ---- lifecycle + blob accounting ---------------------------------------

    [Fact]
    public async Task Direct_upload_creates_lab_item_and_blob_ref_but_no_FileItem()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var id = await UploadLabItemAsync(scope.ServiceProvider, owner, ImageFixtures.PlainPng(64, 64));

        Assert.True(await db.AestheticLabItems.AnyAsync(i => i.Id == id));
        Assert.False(await db.FileItems.AnyAsync()); // never a gallery file
        Assert.Equal(1, await RefCountAsync(db, id));
    }

    [Fact]
    public async Task Duplicate_upload_is_idempotent_for_the_same_blob()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lab = scope.ServiceProvider.GetRequiredService<IAestheticLabService>();
        var png = ImageFixtures.PlainPng(80, 80);

        var a = await lab.AddFromUploadAsync(owner, "a.png", "image/png", new MemoryStream(png));
        var b = await lab.AddFromUploadAsync(owner, "a.png", "image/png", new MemoryStream(png));

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(1, await db.AestheticLabItems.CountAsync());
        // The blob keeps exactly ONE reference despite two upload attempts.
        Assert.Equal(1, await RefCountAsync(db, a.Id));
    }

    [Fact]
    public async Task Remove_releases_item_and_derivative_references_exactly_once()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lab = scope.ServiceProvider.GetRequiredService<IAestheticLabService>();

        var id = await UploadLabItemAsync(scope.ServiceProvider, owner, ImageFixtures.PlainPng(96, 96));
        // Materialize a derived rendition so its blob reference exists.
        var thumb = await lab.RenderDerivativeAsync(owner, id, "small");
        Assert.NotNull(thumb);
        var derivativeBlobId = await db.AestheticLabDerivatives.AsNoTracking()
            .Where(d => d.AestheticLabItemId == id).Select(d => d.BlobObjectId).FirstAsync();
        var itemBlobId = await db.AestheticLabItems.AsNoTracking()
            .Where(i => i.Id == id).Select(i => i.BlobObjectId).FirstAsync();

        var removed = await lab.RemoveAsync(owner, id);
        Assert.True(removed);

        // Both references released; the item is PHYSICALLY deleted; children purged.
        Assert.Equal(0, await db.BlobObjects.Where(b => b.Id == itemBlobId).Select(b => b.ReferenceCount).FirstAsync());
        Assert.Equal(0, await db.BlobObjects.Where(b => b.Id == derivativeBlobId).Select(b => b.ReferenceCount).FirstAsync());
        Assert.False(await db.AestheticLabItems.AnyAsync(i => i.Id == id));
        Assert.False(await db.AestheticLabDerivatives.AnyAsync(d => d.AestheticLabItemId == id));
    }

    [Fact]
    public async Task Remove_hard_deletes_and_reclaims_a_direct_upload_blob_via_janitor()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lab = scope.ServiceProvider.GetRequiredService<IAestheticLabService>();

        var id = await UploadLabItemAsync(scope.ServiceProvider, owner, ImageFixtures.PlainPng(64, 64));
        var blobId = await db.AestheticLabItems.AsNoTracking().Where(i => i.Id == id).Select(i => i.BlobObjectId).FirstAsync();

        Assert.True(await lab.RemoveAsync(owner, id));

        // Row physically gone; its single reference released → refcount 0.
        Assert.False(await db.AestheticLabItems.AnyAsync(i => i.Id == id));
        Assert.Equal(0, await db.BlobObjects.Where(b => b.Id == blobId).Select(b => b.ReferenceCount).FirstAsync());

        // With no remaining owner, the janitor reclaims the blob (enabled, grace 0).
        await EnabledJanitor(factory).RunOnceAsync(default);
        Assert.False(await db.BlobObjects.AnyAsync(b => b.Id == blobId));

        // Audit agrees: no orphaned nonzero refs, no zero-ref-with-owners.
        var report = await scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>().AuditAsync();
        Assert.Equal(0, report.OrphanedNonzeroRefcount);
        Assert.Equal(0, report.ZeroRefWithRealReferences);
    }

    [Fact]
    public async Task Removing_a_gallery_sourced_lab_item_keeps_the_source_file_and_its_blob()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lab = scope.ServiceProvider.GetRequiredService<IAestheticLabService>();

        // Seed a gallery-eligible FileItem (refcount starts at 1).
        var blobId = Guid.NewGuid();
        db.BlobObjects.Add(new BlobObject
        {
            Id = blobId, Sha256 = $"sha-{blobId:N}", SizeBytes = 1,
            StorageKey = $"sk/{blobId:N}", ReferenceCount = 1, CreatedAt = DateTime.UtcNow,
        });
        var fileId = Guid.NewGuid();
        db.FileItems.Add(new FileItem
        {
            Id = fileId, OwnerUserId = owner, BlobObjectId = blobId,
            Name = "photo.png", MimeType = "image/png", SizeBytes = 1,
            CreatedAt = DateTime.UtcNow, EffectiveDateTaken = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var added = await lab.AddFromGalleryAsync(owner, fileId);
        Assert.Equal(2, await db.BlobObjects.Where(b => b.Id == blobId).Select(b => b.ReferenceCount).FirstAsync());

        // Removing the LAB item leaves the source file untouched and the blob
        // alive (the gallery reference still holds it).
        Assert.True(await lab.RemoveAsync(owner, added.Id));
        Assert.True(await db.FileItems.AnyAsync(f => f.Id == fileId && f.DeletedAt == null));
        Assert.Equal(1, await db.BlobObjects.Where(b => b.Id == blobId).Select(b => b.ReferenceCount).FirstAsync());
    }

    [Fact]
    public async Task Remove_cancels_a_live_queued_analysis_job()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lab = scope.ServiceProvider.GetRequiredService<IAestheticLabService>();
        var analysis = scope.ServiceProvider.GetRequiredService<IAestheticAnalysisService>();

        var id = await UploadLabItemAsync(scope.ServiceProvider, owner, ImageFixtures.PlainPng(64, 64));
        var req = await analysis.RequestAnalysisAsync(owner, new[] { id }, null);
        var jobId = await db.AestheticAnalysisRuns.AsNoTracking()
            .Where(r => r.Id == req.Enqueued[0].RunId).Select(r => r.BackgroundJobId!.Value).FirstAsync();

        Assert.True(await lab.RemoveAsync(owner, id));

        // The background job was asked to cancel (queued → cancellation requested
        // or already cancelled) and the run rows were purged with the item.
        var job = await db.BackgroundJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
        Assert.True(job.CancellationRequested || job.Status == JobStatuses.Cancelled);
        Assert.False(await db.AestheticAnalysisRuns.AnyAsync(r => r.AestheticLabItemId == id));
    }

    [Fact]
    public async Task Reference_audit_counts_lab_and_derivative_references()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        using var scope = factory.Services.CreateScope();
        var lab = scope.ServiceProvider.GetRequiredService<IAestheticLabService>();
        var audit = scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>();

        var id = await UploadLabItemAsync(scope.ServiceProvider, owner, ImageFixtures.PlainPng(72, 72));
        await lab.RenderDerivativeAsync(owner, id, "small");

        // The computed truth (lab item + derivative) matches the stored refcount.
        var report = await audit.AuditAsync();
        Assert.Equal(report.TotalBlobs, report.MatchedReferenceCount);
        Assert.Equal(0, report.OrphanedNonzeroRefcount);
        Assert.Equal(0, report.ZeroRefWithRealReferences);
    }

    [Fact]
    public async Task Janitor_cannot_reclaim_a_lab_referenced_blob()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var id = await UploadLabItemAsync(scope.ServiceProvider, owner, ImageFixtures.PlainPng(64, 64));
        var blobId = await db.AestheticLabItems.AsNoTracking().Where(i => i.Id == id).Select(i => i.BlobObjectId).FirstAsync();

        // Even an enabled, zero-grace janitor cannot reclaim it: refcount 1
        // (the live lab item owns it) keeps it out of the reclaim query.
        await EnabledJanitor(factory).RunOnceAsync(default);
        Assert.True(await db.BlobObjects.AnyAsync(b => b.Id == blobId));
    }

    // ---- analysis orchestration --------------------------------------------

    [Fact]
    public async Task Adding_an_item_creates_no_job()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await UploadLabItemAsync(scope.ServiceProvider, owner, ImageFixtures.PlainPng(64, 64));

        Assert.False(await db.BackgroundJobs.AnyAsync());
        Assert.False(await db.AestheticAnalysisRuns.AnyAsync());
    }

    [Fact]
    public async Task Manual_batch_creates_one_run_and_one_job_per_item()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analysis = scope.ServiceProvider.GetRequiredService<IAestheticAnalysisService>();

        var id1 = await UploadLabItemAsync(scope.ServiceProvider, owner, ImageFixtures.PlainPng(64, 64));
        var id2 = await UploadLabItemAsync(scope.ServiceProvider, owner, ImageFixtures.PlainPng(65, 65));

        var result = await analysis.RequestAnalysisAsync(owner, new[] { id1, id2 }, null);

        Assert.Equal(2, result.Enqueued.Count);
        Assert.Empty(result.Skipped);
        Assert.Equal(2, await db.AestheticAnalysisRuns.CountAsync());
        Assert.Equal(2, await db.BackgroundJobs.CountAsync(j => j.Type == JobTypes.AestheticsAnalyze));
    }

    [Fact]
    public async Task Batch_over_the_cap_is_bounded()
    {
        using var factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["HumanAesExpert:Enabled"] = "true",
            ["HumanAesExpert:MaximumBatchItems"] = "2",
        });
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        using var scope = factory.Services.CreateScope();
        var analysis = scope.ServiceProvider.GetRequiredService<IAestheticAnalysisService>();

        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
            ids.Add(await UploadLabItemAsync(scope.ServiceProvider, owner, ImageFixtures.PlainPng(60 + i, 60 + i)));

        var result = await analysis.RequestAnalysisAsync(owner, ids, null);
        Assert.Equal(2, result.Enqueued.Count);
        Assert.Contains(result.Skipped, s => s.Reason == "batch_limit_exceeded");
    }

    [Fact]
    public async Task Duplicate_live_run_collapses_but_completed_rerun_creates_history()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analysis = scope.ServiceProvider.GetRequiredService<IAestheticAnalysisService>();
        var id = await UploadLabItemAsync(scope.ServiceProvider, owner, ImageFixtures.PlainPng(64, 64));

        var r1 = await analysis.RequestAnalysisAsync(owner, new[] { id }, null);
        var r2 = await analysis.RequestAnalysisAsync(owner, new[] { id }, null);
        // Same live run reused; no second run row.
        Assert.Equal(r1.Enqueued[0].RunId, r2.Enqueued[0].RunId);
        Assert.Equal(1, await db.AestheticAnalysisRuns.CountAsync());

        // Drive the queued job to completion, then re-request → a NEW run.
        await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(10);
        var r3 = await analysis.RequestAnalysisAsync(owner, new[] { id }, null);
        Assert.NotEqual(r1.Enqueued[0].RunId, r3.Enqueued[0].RunId);
        Assert.Equal(2, await db.AestheticAnalysisRuns.CountAsync());
    }

    [Fact]
    public async Task Worker_success_persists_all_twelve_expert_metrics()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analysis = scope.ServiceProvider.GetRequiredService<IAestheticAnalysisService>();

        var id = await UploadLabItemAsync(scope.ServiceProvider, owner, ImageFixtures.PlainPng(64, 64));
        var req = await analysis.RequestAnalysisAsync(owner, new[] { id }, null);
        var runId = req.Enqueued[0].RunId;

        await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(10);

        var run = await db.AestheticAnalysisRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        Assert.Equal(AestheticRunStatuses.Succeeded, run.Status);
        Assert.Equal(12, await db.AestheticMetrics.CountAsync(m => m.RunId == runId));
        Assert.True(await db.AestheticMetrics.AnyAsync(m => m.RunId == runId && m.MetricKey == AestheticMetricCatalog.OverallKey));
        // RawOutputJson is internal provenance; metrics are separately queryable.
        Assert.NotNull(run.RawOutputJson);
        Assert.Equal("test-revision", run.ModelRevision);
    }

    [Fact]
    public async Task Malformed_model_output_fails_the_run_safely()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        // Flip the singleton fake to return malformed output.
        factory.Services.GetRequiredService<FakeAestheticModelClient>().Behavior =
            FakeAestheticModelClient.Mode.MissingMetric;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analysis = scope.ServiceProvider.GetRequiredService<IAestheticAnalysisService>();
        var id = await UploadLabItemAsync(scope.ServiceProvider, owner, ImageFixtures.PlainPng(64, 64));
        var req = await analysis.RequestAnalysisAsync(owner, new[] { id }, null);

        await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(10);

        var run = await db.AestheticAnalysisRuns.AsNoTracking().FirstAsync(r => r.Id == req.Enqueued[0].RunId);
        Assert.Equal(AestheticRunStatuses.Failed, run.Status);
        Assert.Equal(AestheticErrorCodes.InvalidModelOutput, run.ErrorCode);
        Assert.Equal(0, await db.AestheticMetrics.CountAsync(m => m.RunId == run.Id));
    }

    [Fact]
    public async Task Feature_disabled_skips_with_controlled_reason_and_no_job()
    {
        using var factory = NewFactory(enabled: false);
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analysis = scope.ServiceProvider.GetRequiredService<IAestheticAnalysisService>();
        var id = await UploadLabItemAsync(scope.ServiceProvider, owner, ImageFixtures.PlainPng(64, 64));

        var result = await analysis.RequestAnalysisAsync(owner, new[] { id }, null);

        Assert.Empty(result.Enqueued);
        Assert.Contains(result.Skipped, s => s.Reason == AestheticErrorCodes.FeatureDisabled);
        Assert.False(await db.BackgroundJobs.AnyAsync());
    }

    [Fact]
    public async Task Disabled_capability_is_rejected_before_enqueue()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var owner = await factory.SeedUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analysis = scope.ServiceProvider.GetRequiredService<IAestheticAnalysisService>();
        var id = await UploadLabItemAsync(scope.ServiceProvider, owner, ImageFixtures.PlainPng(64, 64));

        // text_assessment is off by default → nothing allowed → skip, no job.
        var result = await analysis.RequestAnalysisAsync(owner, new[] { id }, new[] { AestheticCapabilities.TextAssessment });

        Assert.Empty(result.Enqueued);
        Assert.Contains(result.Skipped, s => s.Reason == AestheticErrorCodes.CapabilityDisabled);
        Assert.False(await db.AestheticAnalysisRuns.AnyAsync());
    }
}
