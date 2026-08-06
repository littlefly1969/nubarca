using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Admin;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.MediaLibrary;

// Slice 94 — native metadata pipeline V2: imports write only cheap detection
// facts inline and defer full embedded EXIF/GPS extraction to the async
// metadata.embedded.backfill job, which recomputes EffectiveDateTaken and
// maintains the owner-scoped GPS projection; the whole flow stays idempotent
// and never breaks an import.
public sealed class MetadataPipelineV2Tests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string NewTree(Action<string> build)
    {
        var root = Path.Combine(Path.GetTempPath(), $"nc-meta94-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        build(root);
        _tempDirs.Add(root);
        return root;
    }

    private static Dictionary<string, string?> ImportSettings(string root, bool inlineMetadata = false) => new()
    {
        ["AdminImport:Enabled"] = "true",
        ["AdminImport:Roots:0"] = root,
        ["AdminImport:ExtractMetadataInline"] = inlineMetadata ? "true" : "false",
    };

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<(Guid AdminId, Guid TargetId, HttpClient Client)> SetupAsync(
        SqliteWebApplicationFactory factory)
    {
        factory.EnsureDatabaseCreated();
        var adminId = await factory.SeedUserAsync("admin@example.com");
        await factory.PromoteToAdminAsync(adminId);
        var client = await factory.LoginAsync("admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");
        return (adminId, targetId, client);
    }

    private static async Task RunImportAsync(SqliteWebApplicationFactory factory, HttpClient client, Guid targetId)
    {
        var roots = await client.GetFromJsonAsync<AdminImportRootsResponse>("/api/admin/import/roots");
        var run = await (await client.PostAsJsonAsync("/api/admin/import/run", new
        {
            rootId = roots!.Roots[0].RootId,
            relativePath = "",
            targetUserId = targetId,
            destinationFolderId = (Guid?)null,
        })).Content.ReadFromJsonAsync<AdminImportRunResponse>();
        Assert.NotNull(run);

        // Process ONLY the import job; the enqueued backfill jobs stay queued.
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(1);
    }

    private static async Task ProcessAllJobsAsync(SqliteWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(10);
    }

    [Fact]
    public async Task Import_Defers_Extraction_Then_Backfill_Enriches_Dates_And_Gps()
    {
        var root = NewTree(r =>
        {
            File.WriteAllBytes(Path.Combine(r, "photo.jpg"), ImageFixtures.JpegWithExif(includeGps: true));
            // Garbage bytes with an image extension: detection finds no image,
            // the import must still succeed (extraction can never break it).
            File.WriteAllBytes(Path.Combine(r, "garbage.jpg"), new byte[] { 1, 2, 3, 4 });
        });
        using var factory = new SqliteWebApplicationFactory(ImportSettings(root));
        var (_, targetId, client) = await SetupAsync(factory);

        await RunImportAsync(factory, client, targetId);

        Guid photoFileId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Both files imported (garbage included) — the import fast path
            // never depends on embedded extraction.
            Assert.Equal(2, await db.FileItems.CountAsync(f => f.OwnerUserId == targetId && f.DeletedAt == null));

            var photo = await db.FileItems.AsNoTracking().FirstAsync(f => f.Name == "photo.jpg");
            photoFileId = photo.Id;
            var meta = await db.BlobMetadata.AsNoTracking().FirstAsync(m => m.BlobObjectId == photo.BlobObjectId);

            // Inline part: detection facts only; full extraction deferred.
            Assert.Equal("image/jpeg", meta.DetectedContentType);
            Assert.Equal(MetadataStatuses.Pending, meta.ExtractionStatus);
            Assert.Null(meta.DateTaken);
            Assert.Null(meta.CameraMake);
            Assert.Null(meta.GpsLatitude);
            Assert.Equal(EffectiveDateTakenSources.Uploaded, photo.EffectiveDateTakenSource);
            Assert.Equal(0, await db.FileItemLocations.CountAsync());

            // The run handed enrichment to the async pipeline.
            Assert.Equal(1, await db.BackgroundJobs.CountAsync(j =>
                j.Type == JobTypes.MetadataEmbeddedBackfill && j.Status == JobStatuses.Queued));
        }

        await ProcessAllJobsAsync(factory);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var photo = await db.FileItems.AsNoTracking().FirstAsync(f => f.Id == photoFileId);
            var meta = await db.BlobMetadata.AsNoTracking().FirstAsync(m => m.BlobObjectId == photo.BlobObjectId);

            // Full enrichment landed.
            Assert.Equal(MetadataStatuses.Completed, meta.ExtractionStatus);
            Assert.Equal(ImageFixtures.CameraMake, meta.CameraMake);
            Assert.Equal(new DateTime(2023, 6, 15, 14, 30, 0, DateTimeKind.Utc), meta.DateTaken);

            // EffectiveDateTaken was RECOMPUTED from the async extraction.
            Assert.Equal(EffectiveDateTakenSources.Embedded, photo.EffectiveDateTakenSource);
            Assert.Equal(meta.DateTaken, photo.EffectiveDateTaken);

            // The owner-scoped GPS projection was populated (map preparation).
            var location = await db.FileItemLocations.AsNoTracking()
                .SingleAsync(l => l.FileItemId == photoFileId);
            Assert.Equal(targetId, location.OwnerUserId);
            Assert.InRange(location.Latitude, 51.4, 51.6);
            Assert.InRange(location.Longitude, -0.2, -0.05);
            Assert.Equal(photo.EffectiveDateTaken, location.TakenAt);

            // The job reported progress through Jobs v2.
            var job = await db.BackgroundJobs.AsNoTracking()
                .FirstAsync(j => j.Type == JobTypes.MetadataEmbeddedBackfill);
            Assert.Equal(JobStatuses.Succeeded, job.Status);
            Assert.NotNull(job.ProgressCurrent);
        }
    }

    [Fact]
    public async Task Metadata_Backfill_Is_Idempotent()
    {
        var root = NewTree(r =>
            File.WriteAllBytes(Path.Combine(r, "photo.jpg"), ImageFixtures.JpegWithExif()));
        using var factory = new SqliteWebApplicationFactory(ImportSettings(root));
        var (_, targetId, client) = await SetupAsync(factory);
        await RunImportAsync(factory, client, targetId);

        await using var scope = factory.Services.CreateAsyncScope();
        var backfill = scope.ServiceProvider.GetRequiredService<MetadataBackfillService>();

        var first = await backfill.RunAsync(new MetadataBackfillOptions());
        Assert.True(first.Processed >= 1);

        // A finished default run leaves every row current — re-running it
        // finds nothing to do.
        var second = await backfill.RunAsync(new MetadataBackfillOptions());
        Assert.Equal(0, second.Processed);
    }

    [Fact]
    public async Task Inline_Config_Restores_Ingest_Time_Extraction()
    {
        var root = NewTree(r =>
            File.WriteAllBytes(Path.Combine(r, "photo.jpg"), ImageFixtures.JpegWithExif()));
        using var factory = new SqliteWebApplicationFactory(ImportSettings(root, inlineMetadata: true));
        var (_, targetId, client) = await SetupAsync(factory);

        await RunImportAsync(factory, client, targetId);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var photo = await db.FileItems.AsNoTracking().FirstAsync(f => f.Name == "photo.jpg");
        var meta = await db.BlobMetadata.AsNoTracking().FirstAsync(m => m.BlobObjectId == photo.BlobObjectId);

        // Old behaviour: everything extracted at ingest time…
        Assert.Equal(MetadataStatuses.Completed, meta.ExtractionStatus);
        Assert.Equal(EffectiveDateTakenSources.Embedded, photo.EffectiveDateTakenSource);
        // …and no metadata job was enqueued.
        Assert.Equal(0, await db.BackgroundJobs.CountAsync(
            j => j.Type == JobTypes.MetadataEmbeddedBackfill));
    }

    [Fact]
    public async Task Browser_Upload_Stays_Inline_And_Seeds_The_Gps_Projection()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var ownerId = await factory.SeedUserAsync();

        FileItem first, dedup;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var bytes = ImageFixtures.JpegWithExif(includeGps: true);
            // Normal upload: extraction is inline by default.
            first = await files.CreateAsync(ownerId, null, "a.jpg", "image/jpeg", new MemoryStream(bytes));
            // Dedup upload of the SAME bytes: the existing extracted blob's GPS
            // must seed a projection row for the NEW file too.
            dedup = await files.CreateAsync(ownerId, null, "b.jpg", "image/jpeg", new MemoryStream(bytes));
        }

        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(EffectiveDateTakenSources.Embedded,
            (await db.FileItems.AsNoTracking().FirstAsync(f => f.Id == first.Id)).EffectiveDateTakenSource);
        Assert.True(await db.FileItemLocations.AnyAsync(l => l.FileItemId == first.Id));
        Assert.True(await db.FileItemLocations.AnyAsync(l => l.FileItemId == dedup.Id));
    }
}
