using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Storage;

// Slice 96 — derived-bytes placement verify/repair. The union-based integrity
// scan treats bytes in either root as present, but serving reads derived
// bytes ONLY from the derived root; these tests cover the displaced state
// (row consistent, bytes only in the original root), its detection
// (verify-bytes + derived-readiness diagnostics), its cheap repair
// (repair-bytes streaming copy, endpoint copy-before-regenerate), and the
// no-leak guarantees of every new output path.
public sealed class DerivedBytesVerifyRepairTests : IDisposable
{
    private readonly string _derivedRoot;
    private readonly SqliteWebApplicationFactory _factory;

    public DerivedBytesVerifyRepairTests()
    {
        _derivedRoot = Path.Combine(Path.GetTempPath(), $"nubarca-derived96-{Guid.NewGuid():N}");
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Storage:DerivedRootPath"] = _derivedRoot,
        });
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { if (Directory.Exists(_derivedRoot)) Directory.Delete(_derivedRoot, recursive: true); }
        catch { /* best effort */ }
    }

    // ---- helpers -----------------------------------------------------------

    // Distinct SOLID colours per file: a blank fixture resized to the small
    // box produces identical bytes for near-identical sources, so two files'
    // small thumbnails would dedup onto ONE blob and break per-file placement
    // counts. A solid colour keeps every derivative content-distinct.
    private static byte[] SolidPng(int edge, byte r, byte g, byte b)
    {
        using var image = new Image<Rgba32>(edge, edge);
        var color = new Rgba32(r, g, b, 255);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                accessor.GetRowSpan(y).Fill(color);
            }
        });
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private async Task<FileItem> UploadAsync(Guid ownerId, byte[] bytes, string name, string mime)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, null, name, mime, new MemoryStream(bytes));
    }

    private async Task BackfillAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var backfill = scope.ServiceProvider.GetRequiredService<MediaDerivativesBackfillService>();
        await backfill.RunAsync(new MediaDerivativesBackfillOptions { MissingOnly = true });
    }

    private async Task<(Guid ThumbnailId, Guid BlobObjectId, string StorageKey, long SizeBytes, DateTime CreatedAt)>
        ThumbRowAsync(Guid fileItemId, string size)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await (
            from t in db.FileThumbnails.AsNoTracking()
            join b in db.BlobObjects.AsNoTracking() on t.BlobObjectId equals b.Id
            where t.FileItemId == fileItemId && t.Size == size
            select new { t.Id, t.BlobObjectId, b.StorageKey, b.SizeBytes, t.CreatedAt })
            .SingleAsync();
        return (row.Id, row.BlobObjectId, row.StorageKey, row.SizeBytes, row.CreatedAt);
    }

    private static string KeyPath(string root, string storageKey)
        => Path.Combine(root, storageKey.Replace('/', Path.DirectorySeparatorChar));

    // Simulates the field issue: the artifact's bytes sit in the ORIGINAL
    // root under the same content-addressed key, not in the derived root.
    private void DisplaceToOriginalRoot(string storageKey)
    {
        var from = KeyPath(_derivedRoot, storageKey);
        var to = KeyPath(_factory.StorageRoot, storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to, overwrite: true);
    }

    private void DeleteFromBothRoots(string storageKey)
    {
        foreach (var root in new[] { _derivedRoot, _factory.StorageRoot })
        {
            var path = KeyPath(root, storageKey);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private async Task<T> WithServiceAsync<T>(Func<MediaDerivativeBytesService, Task<T>> run)
    {
        using var scope = _factory.Services.CreateScope();
        return await run(scope.ServiceProvider.GetRequiredService<MediaDerivativeBytesService>());
    }

    // A poster ROW with real derived bytes, attached to an existing file.
    // Placement classification is content-agnostic, so the synthetic-video
    // machinery is not needed to exercise the poster bucket.
    private async Task<string> SeedPosterRowAsync(Guid fileItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = await blobs.StoreDerivedAsync(
            new MemoryStream(new byte[] { 0xFF, 0xD8, 0x42, 0x96, 0x42, 0x96 }));
        db.FileThumbnails.Add(new FileThumbnail
        {
            Id = Guid.NewGuid(),
            FileItemId = fileItemId,
            BlobObjectId = blob.Id,
            Size = ThumbnailSizes.Poster,
            Width = 1280,
            Height = 720,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return blob.StorageKey;
    }

    // ---- verify-bytes ------------------------------------------------------

    [Fact]
    public async Task Verify_Reports_All_Present_After_Clean_Split_Root_Generation()
    {
        var owner = await _factory.SeedUserAsync();
        await UploadAsync(owner, SolidPng(900, 200, 40, 40), "a.png", "image/png");
        await BackfillAsync();

        var result = await WithServiceAsync(s => s.VerifyAsync(new MediaDerivativeBytesOptions()));

        // Clean split-storage generation: every artifact is where serving reads it.
        Assert.Equal(2, result.Checked); // small + medium
        Assert.Equal(2, result.PresentInDerivedRoot);
        Assert.Equal(0, result.OnlyInOriginalRoot);
        Assert.Equal(0, result.MissingFromBoth);
        Assert.Equal(1, result.Small.Checked);
        Assert.Equal(1, result.Small.PresentInDerivedRoot);
        Assert.Equal(1, result.Medium.Checked);
        Assert.Equal(0, result.Poster.Checked);
    }

    [Fact]
    public async Task Verify_Classifies_Displaced_And_Missing_Bytes_By_Size()
    {
        var owner = await _factory.SeedUserAsync();
        var a = await UploadAsync(owner, SolidPng(900, 200, 40, 40), "a.png", "image/png");
        var b = await UploadAsync(owner, SolidPng(901, 40, 40, 200), "b.png", "image/png");
        await BackfillAsync();
        var posterKey = await SeedPosterRowAsync(a.Id);

        var smallA = await ThumbRowAsync(a.Id, ThumbnailSizes.Small);
        var mediumB = await ThumbRowAsync(b.Id, ThumbnailSizes.Medium);
        DisplaceToOriginalRoot(smallA.StorageKey);
        DisplaceToOriginalRoot(posterKey);
        DeleteFromBothRoots(mediumB.StorageKey);

        var result = await WithServiceAsync(s => s.VerifyAsync(new MediaDerivativeBytesOptions()));

        Assert.Equal(5, result.Checked); // 2 small + 2 medium + 1 poster
        Assert.Equal(2, result.PresentInDerivedRoot);
        Assert.Equal(2, result.OnlyInOriginalRoot);
        Assert.Equal(1, result.MissingFromBoth);
        Assert.Equal(smallA.SizeBytes + 6, result.BytesCopyable); // poster fixture is 6 bytes

        Assert.Equal(new DerivativeBytesSizeCounts(2, 1, 1, 0), result.Small);
        Assert.Equal(new DerivativeBytesSizeCounts(2, 1, 0, 1), result.Medium);
        Assert.Equal(new DerivativeBytesSizeCounts(1, 0, 1, 0), result.Poster);
    }

    [Fact]
    public async Task Verify_Honours_Size_Filter_And_Limit()
    {
        var owner = await _factory.SeedUserAsync();
        await UploadAsync(owner, SolidPng(900, 200, 40, 40), "a.png", "image/png");
        await UploadAsync(owner, SolidPng(901, 40, 40, 200), "b.png", "image/png");
        await BackfillAsync();

        var onlySmall = await WithServiceAsync(s => s.VerifyAsync(
            new MediaDerivativeBytesOptions { Size = ThumbnailSizes.Small }));
        Assert.Equal(2, onlySmall.Checked);
        Assert.Equal(2, onlySmall.Small.Checked);
        Assert.Equal(0, onlySmall.Medium.Checked);

        var limited = await WithServiceAsync(s => s.VerifyAsync(
            new MediaDerivativeBytesOptions { Limit = 1 }));
        Assert.Equal(1, limited.Checked);
    }

    // ---- repair-bytes ------------------------------------------------------

    [Fact]
    public async Task Repair_Copies_Displaced_Bytes_Without_Decoding_Or_Db_Mutation()
    {
        var owner = await _factory.SeedUserAsync();
        var file = await UploadAsync(owner, SolidPng(900, 200, 40, 40), "a.png", "image/png");
        await BackfillAsync();

        var small = await ThumbRowAsync(file.Id, ThumbnailSizes.Small);
        DisplaceToOriginalRoot(small.StorageKey);
        var sourceBytes = await File.ReadAllBytesAsync(KeyPath(_factory.StorageRoot, small.StorageKey));

        var result = await WithServiceAsync(s => s.RepairAsync(new MediaDerivativeBytesOptions()));

        Assert.Equal(1, result.CopiedFromOriginalRoot);
        Assert.Equal(1, result.SkippedPresentInDerivedRoot); // medium untouched
        Assert.Equal(0, result.MissingFromBoth);
        Assert.Equal(0, result.Regenerated);
        Assert.Equal(0, result.Failed);
        Assert.Equal(small.SizeBytes, result.BytesCopied);

        // Bytes are back where serving reads them, identical to the source...
        var restored = await File.ReadAllBytesAsync(KeyPath(_derivedRoot, small.StorageKey));
        Assert.Equal(sourceBytes, restored);
        // ...the original-root copy was NOT deleted...
        Assert.True(File.Exists(KeyPath(_factory.StorageRoot, small.StorageKey)));
        // ...and the row was not touched (copy-only repair never mutates the DB).
        var after = await ThumbRowAsync(file.Id, ThumbnailSizes.Small);
        Assert.Equal(small.ThumbnailId, after.ThumbnailId);
        Assert.Equal(small.BlobObjectId, after.BlobObjectId);
        Assert.Equal(small.CreatedAt, after.CreatedAt);

        // A follow-up verify is clean.
        var verify = await WithServiceAsync(s => s.VerifyAsync(new MediaDerivativeBytesOptions()));
        Assert.Equal(0, verify.OnlyInOriginalRoot);
        Assert.Equal(0, verify.MissingFromBoth);
    }

    [Fact]
    public async Task Repair_DryRun_Reports_But_Copies_Nothing()
    {
        var owner = await _factory.SeedUserAsync();
        var file = await UploadAsync(owner, SolidPng(900, 200, 40, 40), "a.png", "image/png");
        await BackfillAsync();

        var small = await ThumbRowAsync(file.Id, ThumbnailSizes.Small);
        DisplaceToOriginalRoot(small.StorageKey);

        var result = await WithServiceAsync(s => s.RepairAsync(
            new MediaDerivativeBytesOptions { DryRun = true }));

        Assert.True(result.DryRun);
        Assert.Equal(1, result.CopiedFromOriginalRoot); // would copy
        Assert.False(File.Exists(KeyPath(_derivedRoot, small.StorageKey)));

        var verify = await WithServiceAsync(s => s.VerifyAsync(new MediaDerivativeBytesOptions()));
        Assert.Equal(1, verify.OnlyInOriginalRoot);
    }

    [Fact]
    public async Task Repair_Leaves_Missing_From_Both_Alone_By_Default()
    {
        var owner = await _factory.SeedUserAsync();
        var file = await UploadAsync(owner, SolidPng(900, 200, 40, 40), "a.png", "image/png");
        await BackfillAsync();

        var small = await ThumbRowAsync(file.Id, ThumbnailSizes.Small);
        DeleteFromBothRoots(small.StorageKey);

        var result = await WithServiceAsync(s => s.RepairAsync(new MediaDerivativeBytesOptions()));

        Assert.Equal(1, result.MissingFromBoth);
        Assert.Equal(0, result.Regenerated);
        Assert.Equal(0, result.CopiedFromOriginalRoot);

        // Row untouched; bytes still absent (no silent CPU-heavy regeneration).
        var after = await ThumbRowAsync(file.Id, ThumbnailSizes.Small);
        Assert.Equal(small.ThumbnailId, after.ThumbnailId);
        Assert.False(File.Exists(KeyPath(_derivedRoot, small.StorageKey)));
    }

    [Fact]
    public async Task Repair_RegenerateMissing_Rebuilds_Through_The_Generation_Path()
    {
        var owner = await _factory.SeedUserAsync();
        var file = await UploadAsync(owner, SolidPng(900, 200, 40, 40), "a.png", "image/png");
        await BackfillAsync();

        var small = await ThumbRowAsync(file.Id, ThumbnailSizes.Small);
        DeleteFromBothRoots(small.StorageKey);

        var result = await WithServiceAsync(s => s.RepairAsync(
            new MediaDerivativeBytesOptions { RegenerateMissing = true }));

        Assert.Equal(1, result.Regenerated);
        Assert.Equal(0, result.Failed);

        // The size exists again, with bytes in the derived root.
        var after = await ThumbRowAsync(file.Id, ThumbnailSizes.Small);
        Assert.True(File.Exists(KeyPath(_derivedRoot, after.StorageKey)));

        var verify = await WithServiceAsync(s => s.VerifyAsync(new MediaDerivativeBytesOptions()));
        Assert.Equal(0, verify.MissingFromBoth);
    }

    [Fact]
    public async Task Concurrent_Restores_Of_The_Same_Artifact_Are_Race_Safe()
    {
        var owner = await _factory.SeedUserAsync();
        var file = await UploadAsync(owner, SolidPng(900, 200, 40, 40), "a.png", "image/png");
        await BackfillAsync();

        var small = await ThumbRowAsync(file.Id, ThumbnailSizes.Small);
        DisplaceToOriginalRoot(small.StorageKey);
        var sourceBytes = await File.ReadAllBytesAsync(KeyPath(_factory.StorageRoot, small.StorageKey));

        // Four concurrent lazy repairs of the same artifact: every one must
        // succeed (losing the temp-file/rename race counts as success) and
        // the result must be intact.
        var tasks = Enumerable.Range(0, 4).Select(async _ =>
        {
            using var scope = _factory.Services.CreateScope();
            var blobs = scope.ServiceProvider.GetRequiredService<IBlobService>();
            return await blobs.TryRestoreDerivedFromOriginalAsync(small.BlobObjectId);
        });
        var outcomes = await Task.WhenAll(tasks);

        Assert.All(outcomes, Assert.True);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(KeyPath(_derivedRoot, small.StorageKey)));

        // Re-running when the destination already exists is also a success.
        using var verifyScope = _factory.Services.CreateScope();
        Assert.True(await verifyScope.ServiceProvider.GetRequiredService<IBlobService>()
            .TryRestoreDerivedFromOriginalAsync(small.BlobObjectId));
    }

    // ---- lazy endpoint -----------------------------------------------------

    [Fact]
    public async Task Endpoint_Serves_Displaced_Thumbnail_By_Copy_Even_When_Regeneration_Is_Impossible()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, SolidPng(900, 200, 40, 40), "a.png", "image/png");

        var small = await ThumbRowAsync(file.Id, ThumbnailSizes.Small);
        DisplaceToOriginalRoot(small.StorageKey);

        // Delete the SOURCE image bytes: regeneration cannot succeed, so a 200
        // can only come from the copy-before-regenerate path.
        string sourceKey;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            sourceKey = await (
                from f in db.FileItems.AsNoTracking()
                join blob in db.BlobObjects.AsNoTracking() on f.BlobObjectId equals blob.Id
                where f.Id == file.Id
                select blob.StorageKey).SingleAsync();
        }
        File.Delete(KeyPath(_factory.StorageRoot, sourceKey));

        var resp = await client.GetAsync($"/api/files/{file.Id}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Bytes were restored into the derived root; row untouched.
        Assert.True(File.Exists(KeyPath(_derivedRoot, small.StorageKey)));
        var after = await ThumbRowAsync(file.Id, ThumbnailSizes.Small);
        Assert.Equal(small.ThumbnailId, after.ThumbnailId);
        Assert.Equal(small.CreatedAt, after.CreatedAt);
    }

    [Fact]
    public async Task Lazy_Miss_Logs_Are_Safe_And_Name_The_Action()
    {
        var owner = await _factory.SeedUserAsync();
        var file = await UploadAsync(owner, SolidPng(900, 200, 40, 40), "a.png", "image/png");
        var small = await ThumbRowAsync(file.Id, ThumbnailSizes.Small);

        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var logger = new CapturingLogger<FileThumbnailService>();
        var service = new FileThumbnailService(
            sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<IBlobService>(),
            sp.GetRequiredService<IBlobStorage>(),
            sp.GetRequiredService<IVideoPosterProvider>(),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            sp.GetRequiredService<IOptions<ImageProcessingOptions>>());

        // Case 1: displaced → copy-repair, served.
        DisplaceToOriginalRoot(small.StorageKey);
        var served = await service.OpenAsync(file.Id, owner, ThumbnailSizes.Small);
        Assert.NotNull(served);
        await served!.Content.DisposeAsync();
        var copyLine = Assert.Single(logger.Messages, m => m.Contains("action=copy-repair"));
        Assert.Contains("size=small", copyLine);

        // Case 2: missing from both → regenerate handed to the caller.
        DeleteFromBothRoots(small.StorageKey);
        Assert.Null(await service.OpenAsync(file.Id, owner, ThumbnailSizes.Small));
        var regenLine = Assert.Single(logger.Messages, m => m.Contains("action=regenerate"));
        Assert.Contains("size=small", regenLine);

        // No identifiers in any line: keys, SHAs, GUIDs, names, or paths.
        foreach (var line in logger.Messages)
        {
            Assert.DoesNotContain(small.StorageKey, line);
            Assert.DoesNotContain("objects/", line);
            Assert.DoesNotContain("a.png", line);
            Assert.DoesNotMatch(
                new Regex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"),
                line);
            Assert.DoesNotMatch(new Regex("[0-9a-f]{64}"), line);
        }
    }

    // ---- CLI ---------------------------------------------------------------

    private async Task<(int Exit, string Stdout, string Stderr)> RunCliAsync(params string[] args)
    {
        using var scope = _factory.Services.CreateScope();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliEntryPoint.RunAsync(args, stdout, stderr, () => scope.ServiceProvider);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Cli_VerifyBytes_Prints_Counts_And_No_Identifiers()
    {
        var owner = await _factory.SeedUserAsync();
        var file = await UploadAsync(owner, SolidPng(900, 200, 40, 40), "secret-name.png", "image/png");
        await BackfillAsync();
        var small = await ThumbRowAsync(file.Id, ThumbnailSizes.Small);
        DisplaceToOriginalRoot(small.StorageKey);

        var (exit, stdout, stderr) = await RunCliAsync("media", "derivatives", "verify-bytes", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("checked=2", stdout);
        Assert.Contains("present_in_derived_root=1", stdout);
        Assert.Contains("only_in_original_root=1", stdout);
        Assert.Contains("missing_from_both=0", stdout);
        Assert.Contains("small:", stdout);
        Assert.Contains("medium:", stdout);
        Assert.Contains("repair-bytes", stdout); // operator hint when displaced

        Assert.DoesNotContain(small.StorageKey, stdout);
        Assert.DoesNotContain("secret-name", stdout);
        Assert.DoesNotContain(_factory.StorageRoot, stdout);
        Assert.DoesNotContain(_derivedRoot, stdout);
        Assert.DoesNotMatch(
            new Regex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"),
            stdout);
        Assert.DoesNotMatch(new Regex("[0-9a-f]{64}"), stdout);

        // Verify is read-only: the displaced file did not move.
        Assert.False(File.Exists(KeyPath(_derivedRoot, small.StorageKey)));
    }

    [Fact]
    public async Task Cli_RepairBytes_Copies_And_Then_Verify_Is_Clean()
    {
        var owner = await _factory.SeedUserAsync();
        var file = await UploadAsync(owner, SolidPng(900, 200, 40, 40), "a.png", "image/png");
        await BackfillAsync();
        var small = await ThumbRowAsync(file.Id, ThumbnailSizes.Small);
        DisplaceToOriginalRoot(small.StorageKey);

        var dry = await RunCliAsync("media", "derivatives", "repair-bytes", "--dry-run");
        Assert.Equal(0, dry.Exit);
        Assert.Contains("dry-run", dry.Stdout);
        Assert.False(File.Exists(KeyPath(_derivedRoot, small.StorageKey)));

        var run = await RunCliAsync("media", "derivatives", "repair-bytes");
        Assert.Equal(0, run.Exit);
        Assert.Contains("copied_from_original_root=1", run.Stdout);
        Assert.True(File.Exists(KeyPath(_derivedRoot, small.StorageKey)));

        var verify = await RunCliAsync("media", "derivatives", "verify-bytes");
        Assert.Contains("only_in_original_root=0", verify.Stdout);
    }

    [Fact]
    public async Task Cli_Rejects_Invalid_Size_And_Limit()
    {
        var bad = await RunCliAsync("media", "derivatives", "verify-bytes", "--size", "huge");
        Assert.Equal(64, bad.Exit);
        Assert.Contains("--size", bad.Stderr);

        var badLimit = await RunCliAsync("media", "derivatives", "repair-bytes", "--limit", "0");
        Assert.Equal(64, badLimit.Exit);
        Assert.Contains("--limit", badLimit.Stderr);
    }

    // ---- diagnostics -------------------------------------------------------

    [Fact]
    public async Task Union_Integrity_Stays_Clean_While_Derived_Readiness_Reports_Displacement()
    {
        var adminId = await _factory.SeedUserAsync("admin@example.com");
        await _factory.PromoteToAdminAsync(adminId);
        var client = await _factory.LoginAsync("admin@example.com");

        var file = await UploadAsync(adminId, SolidPng(900, 200, 40, 40), "a.png", "image/png");
        await BackfillAsync();

        // Clean state first: a split-root generation is fully ready.
        var clean = await ReadStatsAsync(client);
        Assert.Equal(0, clean.GetProperty("blobs").GetProperty("missingPhysicalBlobCount").GetInt32());
        var readiness = clean.GetProperty("derivedReadiness");
        Assert.True(readiness.GetProperty("splitRoots").GetBoolean());
        Assert.Equal(2, readiness.GetProperty("thumbnailRowsTotal").GetInt32());
        Assert.Equal(2, readiness.GetProperty("presentInDerivedRoot").GetInt32());
        Assert.Equal(0, readiness.GetProperty("onlyInOriginalRoot").GetInt32());

        // Displace the small thumbnail: union integrity must STAY clean (the
        // bytes exist, just in the wrong root) while derived readiness flags it.
        var small = await ThumbRowAsync(file.Id, ThumbnailSizes.Small);
        DisplaceToOriginalRoot(small.StorageKey);

        var displaced = await ReadStatsAsync(client);
        Assert.Equal(0, displaced.GetProperty("blobs").GetProperty("missingPhysicalBlobCount").GetInt32());
        Assert.Equal(0, displaced.GetProperty("blobs").GetProperty("unreferencedPhysicalBlobCount").GetInt32());
        readiness = displaced.GetProperty("derivedReadiness");
        Assert.Equal(1, readiness.GetProperty("onlyInOriginalRoot").GetInt32());
        Assert.Equal(1, readiness.GetProperty("presentInDerivedRoot").GetInt32());
        Assert.Equal(0, readiness.GetProperty("missingFromBoth").GetInt32());
        Assert.Equal(1, readiness.GetProperty("small").GetProperty("onlyInOriginalRoot").GetInt32());
        Assert.Equal(0, readiness.GetProperty("medium").GetProperty("onlyInOriginalRoot").GetInt32());

        // The fast (scan-less) load reports no readiness section at all.
        var fast = await ReadStatsAsync(client, physical: false);
        Assert.True(fast.GetProperty("derivedReadiness").ValueKind is JsonValueKind.Null);
    }

    private static async Task<JsonElement> ReadStatsAsync(HttpClient client, bool physical = true)
    {
        var resp = await client.GetAsync(
            $"/api/admin/storage-stats?refresh=true&physical={(physical ? "true" : "false")}");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}

// Slice 96 — single-root deployments (Storage:DerivedRootPath unset) must
// report trivially-ready placement: the roots coincide, so only_in_original
// is structurally zero and existing artifacts read as present.
public sealed class DerivedBytesSingleRootTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public DerivedBytesSingleRootTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Verify_Reports_Present_With_No_Displacement_Bucket()
    {
        var owner = await _factory.SeedUserAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            await files.CreateAsync(owner, null, "a.png", "image/png",
                new MemoryStream(ImageFixtures.PlainPng(900, 900)));
        }
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MediaDerivativesBackfillService>()
                .RunAsync(new MediaDerivativesBackfillOptions { MissingOnly = true });
        }

        using var verifyScope = _factory.Services.CreateScope();
        var service = verifyScope.ServiceProvider.GetRequiredService<MediaDerivativeBytesService>();
        var result = await service.VerifyAsync(new MediaDerivativeBytesOptions());

        Assert.Equal(2, result.Checked);
        Assert.Equal(2, result.PresentInDerivedRoot);
        Assert.Equal(0, result.OnlyInOriginalRoot);
        Assert.Equal(0, result.MissingFromBoth);
    }
}
