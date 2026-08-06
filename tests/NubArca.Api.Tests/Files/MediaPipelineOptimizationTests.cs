using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Admin;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 95 — media pipeline optimization: file-first bundled derivative
// generation (one decode for small+medium), generation-only backfill, poster
// provenance + regeneration, and the import fast path's granular timings /
// reduced DB overhead.
public sealed class MediaPipelineOptimizationTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<Guid> SeedOwnerAsync(SqliteWebApplicationFactory factory)
    {
        factory.EnsureDatabaseCreated();
        return await factory.SeedUserAsync();
    }

    private static async Task<FileItem> UploadAsync(
        SqliteWebApplicationFactory factory, Guid ownerId, string name, byte[] bytes, string mime,
        bool generateSmallThumbnail = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(
            ownerId, null, name, mime, new MemoryStream(bytes),
            generateSmallThumbnail: generateSmallThumbnail);
    }

    private static async Task<MediaDerivativesBackfillResult> RunBackfillAsync(
        SqliteWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var backfill = scope.ServiceProvider.GetRequiredService<MediaDerivativesBackfillService>();
        return await backfill.RunAsync(new MediaDerivativesBackfillOptions());
    }

    // Valid JPEG headers (Identify succeeds) with the entropy-coded scan cut
    // off (full decode reliably throws on the premature EOF).
    private static byte[] TruncatedJpeg()
    {
        var good = ImageFixtures.JpegWithExif();
        return good.Take((int)(good.Length * 0.7)).ToArray();
    }

    // ---- bundled image derivatives -------------------------------------------

    [Fact]
    public async Task Image_Missing_Both_Sizes_Is_Decoded_Once_And_Gets_Small_And_Medium()
    {
        using var factory = new SqliteWebApplicationFactory();
        var ownerId = await SeedOwnerAsync(factory);
        var file = await UploadAsync(factory, ownerId, "a.png", ImageFixtures.PlainPng(), "image/png");

        var result = await RunBackfillAsync(factory);

        Assert.Equal(1, result.Stats.ImagesProcessed);
        Assert.Equal(1, result.Stats.ImagesDecoded); // ONE decode for both sizes
        Assert.Equal(1, result.Stats.SmallGenerated);
        Assert.Equal(1, result.Stats.MediumGenerated);
        Assert.Equal(0, result.Failed);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sizes = await db.FileThumbnails.AsNoTracking()
            .Where(t => t.FileItemId == file.Id)
            .Select(t => t.Size)
            .OrderBy(s => s)
            .ToListAsync();
        Assert.Equal(new[] { "medium", "small" }, sizes);
    }

    [Fact]
    public async Task Existing_Small_Is_Not_Regenerated_Only_Medium_Is()
    {
        using var factory = new SqliteWebApplicationFactory();
        var ownerId = await SeedOwnerAsync(factory);
        // Eager small at upload; medium left missing.
        await UploadAsync(factory, ownerId, "a.png", ImageFixtures.PlainPng(), "image/png",
            generateSmallThumbnail: true);

        var result = await RunBackfillAsync(factory);

        Assert.Equal(1, result.Stats.SmallSkipped);
        Assert.Equal(0, result.Stats.SmallGenerated);
        Assert.Equal(1, result.Stats.MediumGenerated);

        // Idempotent: a second run finds nothing to do.
        var second = await RunBackfillAsync(factory);
        Assert.Equal(0, second.Stats.ImagesProcessed);
        Assert.Equal(0, second.Succeeded);
    }

    [Fact]
    public async Task Corrupt_Image_Fails_Per_File_And_The_Backfill_Continues()
    {
        using var factory = new SqliteWebApplicationFactory();
        var ownerId = await SeedOwnerAsync(factory);
        await UploadAsync(factory, ownerId, "broken.jpg", TruncatedJpeg(), "image/jpeg");
        var good = await UploadAsync(factory, ownerId, "good.png", ImageFixtures.PlainPng(width: 20), "image/png");

        var result = await RunBackfillAsync(factory);

        // The corrupt image failed both sizes; the good one still succeeded.
        Assert.Equal(1, result.Stats.SmallFailed);
        Assert.Equal(1, result.Stats.MediumFailed);
        Assert.Equal(1, result.Stats.SmallGenerated);
        Assert.Equal(1, result.Stats.MediumGenerated);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.FileThumbnails.CountAsync(t => t.FileItemId == good.Id));
    }

    [Fact]
    public async Task Bundled_Generation_Skips_A_Size_That_Raced_Into_Existence()
    {
        using var factory = new SqliteWebApplicationFactory();
        var ownerId = await SeedOwnerAsync(factory);
        var file = await UploadAsync(factory, ownerId, "a.png", ImageFixtures.PlainPng(), "image/png",
            generateSmallThumbnail: true);

        await using var scope = factory.Services.CreateAsyncScope();
        var thumbnails = scope.ServiceProvider.GetRequiredService<IFileThumbnailService>();
        // Request BOTH sizes although small already exists (simulates the
        // lazy endpoint having won a race before our work unit ran).
        var result = await thumbnails.EnsureImageDerivativesAsync(
            file.Id, ownerId, new[] { "small", "medium" });

        Assert.Contains(result.Outcomes,
            o => o.Size == "small" && o.Outcome == DerivativeOutcome.SkippedExisting);
        Assert.Contains(result.Outcomes,
            o => o.Size == "medium" && o.Outcome == DerivativeOutcome.Generated);
    }

    // ---- poster provenance + regeneration -------------------------------------

    [Fact]
    public async Task Synthetic_Poster_Is_Marked_Synthetic_And_Visible_In_Video_List()
    {
        using var factory = new SqliteWebApplicationFactory();
        var ownerId = await SeedOwnerAsync(factory);
        var video = await UploadAsync(factory, ownerId, "clip.mp4", ImageFixtures.MinimalMp4(), "video/mp4");

        var result = await RunBackfillAsync(factory);
        Assert.Equal(1, result.Stats.PosterGenerated);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var poster = await db.FileThumbnails.AsNoTracking()
            .SingleAsync(t => t.FileItemId == video.Id && t.Size == "poster");
        Assert.Equal("synthetic", poster.PosterSource);

        // The owner-facing video list carries the provenance (and nothing else).
        var client = await factory.LoginAsync();
        var body = await (await client.GetAsync("/api/videos?limit=10")).Content.ReadAsStringAsync();
        Assert.Contains("\"posterSource\":\"synthetic\"", body);
    }

    [Fact]
    public async Task Poster_Regeneration_Selects_Only_Synthetic_By_Default()
    {
        using var factory = new SqliteWebApplicationFactory();
        var ownerId = await SeedOwnerAsync(factory);
        var v1 = await UploadAsync(factory, ownerId, "a.mp4", ImageFixtures.MinimalMp4(), "video/mp4");
        var v2 = await UploadAsync(factory, ownerId, "b.mov", ImageFixtures.MinimalMov(), "video/quicktime");
        await RunBackfillAsync(factory);

        // Make one row look like a pre-provenance (legacy) poster.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.FileThumbnails
                .Where(t => t.FileItemId == v2.Id && t.Size == "poster")
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.PosterSource, (string?)null));
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var regen = scope.ServiceProvider.GetRequiredService<PosterRegenerationService>();

            // Dry-run + default scope: only the synthetic row matches.
            var dry = await regen.RunAsync(new PosterRegenerationOptions { DryRun = true });
            Assert.True(dry.DryRun);
            Assert.Equal(1, dry.Examined);

            // Only the synthetic-source row is examined by default. The minimal
            // fixture mp4 has no decodable frame, so the provider falls back to a
            // synthetic poster again — a "still placeholder" outcome, never a
            // real-frame regenerate and never a failure. (What matters here is the
            // SELECTION: exactly one synthetic row is picked up.)
            var run = await regen.RunAsync(new PosterRegenerationOptions());
            Assert.Equal(1, run.Examined);
            Assert.Equal(0, run.Failed);
            Assert.Equal(1, run.Regenerated + run.StillPlaceholder);
            Assert.Equal(1, run.StillPlaceholder);

            // --force covers everything, including the legacy (null-source) row.
            var forced = await regen.RunAsync(new PosterRegenerationOptions { Force = true });
            Assert.Equal(2, forced.Examined);
            Assert.Equal(0, forced.Failed);
            Assert.Equal(2, forced.Regenerated + forced.StillPlaceholder);
        }

        await using (var verify = factory.Services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            // Every poster row survived regeneration and is now provenance-stamped.
            var sources = await db.FileThumbnails.AsNoTracking()
                .Where(t => t.Size == "poster")
                .Select(t => t.PosterSource)
                .ToListAsync();
            Assert.Equal(2, sources.Count);
            Assert.All(sources, s => Assert.Equal("synthetic", s));
            _ = v1;
        }
    }

    // ---- import fast path -------------------------------------------------------

    private string NewTree(Action<string> build)
    {
        var root = Path.Combine(Path.GetTempPath(), $"nc-opt95-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        build(root);
        _tempDirs.Add(root);
        return root;
    }

    [Fact]
    public async Task Import_Dedup_Stays_Correct_With_Single_Commit_Meta_And_File()
    {
        var root = NewTree(r =>
        {
            // Two identical files + one distinct.
            File.WriteAllBytes(Path.Combine(r, "one.png"), ImageFixtures.PlainPng());
            File.WriteAllBytes(Path.Combine(r, "two.png"), ImageFixtures.PlainPng());
            File.WriteAllBytes(Path.Combine(r, "other.png"), ImageFixtures.PlainPng(width: 32));
        });
        using var factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["AdminImport:Enabled"] = "true",
            ["AdminImport:Roots:0"] = root,
        });
        factory.EnsureDatabaseCreated();
        var adminId = await factory.SeedUserAsync("admin@example.com");
        await factory.PromoteToAdminAsync(adminId);
        var client = await factory.LoginAsync("admin@example.com");
        var targetId = await factory.SeedUserAsync("target@example.com");

        var roots = await client.GetFromJsonAsync<AdminImportRootsResponse>("/api/admin/import/roots");
        var run = await (await client.PostAsJsonAsync("/api/admin/import/run", new
        {
            rootId = roots!.Roots[0].RootId,
            relativePath = "",
            targetUserId = targetId,
            destinationFolderId = (Guid?)null,
        })).Content.ReadFromJsonAsync<AdminImportRunResponse>();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAvailableAsync(1);
        }

        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        // 3 FileItems over 2 content-addressed blobs; the duplicate pair
        // shares one blob with ReferenceCount 2 and exactly ONE metadata row.
        Assert.Equal(3, await db.FileItems.CountAsync(f => f.OwnerUserId == targetId && f.DeletedAt == null));
        Assert.Equal(2, await db.BlobObjects.CountAsync());
        var dupBlob = await db.BlobObjects.AsNoTracking().FirstAsync(b => b.ReferenceCount == 2);
        Assert.Equal(1, await db.BlobMetadata.CountAsync(m => m.BlobObjectId == dupBlob.Id));
        Assert.Equal(2, await db.BlobMetadata.CountAsync()); // one row per blob

        // Granular timings landed on the run: detect + item bookkeeping are
        // measured, and Metadata (full extraction) is ZERO on the deferred path.
        var status = await client.GetFromJsonAsync<AdminImportRunStatusResponse>(
            $"/api/admin/import/runs/{run!.ImportRunId}");
        Assert.Equal("succeeded", status!.Status);
        Assert.NotNull(status.Timings.DetectMillis);
        Assert.NotNull(status.Timings.ItemDbMillis);
        Assert.Equal(0, status.Timings.MetadataMillis);
    }
}
