using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Metadata;

// Scheduler v2 — cooperative SLICING of metadata.embedded.backfill at the
// service level: yield via shouldYield at safe per-blob boundaries, durable
// checkpoint, resume to completion, idempotency, and already-current skip.
// (Budget-type differentiation, fairness, and starvation are covered generically
// for all maintenance jobs in SchedulerTests; engine wiring in
// MetadataBackfillJobSlicingTests.)
public sealed class MetadataBackfillSlicingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly FileItemService _files;
    private readonly MetadataBackfillService _backfill;

    public MetadataBackfillSlicingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-meta-slice-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);
        var storage = new LocalFileSystemBlobStorage(Options.Create(new BlobStorageOptions { RootPath = _storageRoot }));
        var blobService = new BlobService(storage, _db, TimeProvider.System);
        var thumbnails = new FileThumbnailService(
            _db, blobService, storage, new SyntheticVideoPosterProvider(),
            TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()));
        _files = new FileItemService(_db, blobService, thumbnails, TimeProvider.System, new EmbeddedImageMetadataExtractor());
        _backfill = new MetadataBackfillService(_db, _files);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); } catch { }
    }

    private static byte[] Png(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    // Seed N images, then mark them legacy-pending so the backfill has work.
    private async Task<List<Guid>> SeedPendingAsync(int n)
    {
        var owner = new User { Id = Guid.NewGuid(), Email = "o@example.com", DisplayName = "O", CreatedAt = DateTime.UtcNow };
        _db.Users.Add(owner);
        await _db.SaveChangesAsync();

        var blobIds = new List<Guid>();
        for (var i = 0; i < n; i++)
        {
            var file = await _files.CreateAsync(owner.Id, null, $"f{i}.png", "image/png", new MemoryStream(Png(10 + i)));
            blobIds.Add(file.BlobObjectId);
        }
        await _db.BlobMetadata
            .Where(m => blobIds.Contains(m.BlobObjectId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.ExtractionStatus, MetadataStatuses.Pending)
                .SetProperty(m => m.ExtractionVersion, (int?)null));
        // ExecuteUpdate bypasses the change tracker; clear it so the backfill
        // (sharing this context) reads the fresh DB state, mirroring how the
        // real engine runs each slice in its own scope/context.
        _db.ChangeTracker.Clear();
        return blobIds;
    }

    [Fact]
    public async Task Yields_After_Item_Budget_With_Durable_Checkpoint()
    {
        await SeedPendingAsync(3);

        // Budget of 2 items per slice (shouldYield trips at the 2nd).
        var result = await _backfill.RunAsync(
            new MetadataBackfillOptions(), shouldYield: processed => processed >= 2);

        Assert.True(result.MoreWorkRemaining);
        Assert.NotNull(result.NextCheckpointJson);
        Assert.Equal(2, result.Processed);

        // Checkpoint is durable + parseable and carries cumulative counts only.
        var cp = MetadataBackfillCheckpoint.TryParse(result.NextCheckpointJson);
        Assert.NotNull(cp);
        Assert.Equal(2, cp!.ProcessedTotal);

        // One blob is still pending (un-touched by the first slice).
        Assert.Equal(1, await _db.BlobMetadata.CountAsync(m => m.ExtractionStatus == MetadataStatuses.Pending));
    }

    [Fact]
    public async Task Resumes_From_Checkpoint_And_Completes_Idempotently()
    {
        var ids = await SeedPendingAsync(5);

        string? checkpoint = null;
        var slices = 0;
        var processedAcrossSlices = 0;
        while (true)
        {
            var r = await _backfill.RunAsync(
                new MetadataBackfillOptions(), checkpointJson: checkpoint,
                shouldYield: processed => processed >= 2);
            slices++;
            processedAcrossSlices += r.Processed;
            if (!r.MoreWorkRemaining) break;
            checkpoint = r.NextCheckpointJson;
            Assert.True(slices < 20, "did not converge");
        }

        // 5 blobs / 2 per slice → 3 slices (2,2,1), each blob processed once.
        Assert.Equal(3, slices);
        Assert.Equal(5, processedAcrossSlices);
        Assert.All(ids, id => Assert.Equal(
            MetadataStatuses.Completed,
            _db.BlobMetadata.AsNoTracking().Single(m => m.BlobObjectId == id).ExtractionStatus));
        Assert.True(_db.BlobMetadata.All(m => m.ExtractionVersion == EmbeddedImageMetadataExtractor.Version));

        // A fresh run (no checkpoint) is a no-op: everything is already current.
        var again = await _backfill.RunAsync(new MetadataBackfillOptions());
        Assert.Equal(0, again.Examined);
        Assert.False(again.MoreWorkRemaining);
    }

    [Fact]
    public async Task Completes_In_One_Slice_When_Budget_Not_Reached()
    {
        await SeedPendingAsync(2);

        var result = await _backfill.RunAsync(
            new MetadataBackfillOptions(), shouldYield: processed => processed >= 100);

        Assert.False(result.MoreWorkRemaining);
        Assert.Null(result.NextCheckpointJson);
        Assert.Equal(2, result.Processed);
        Assert.Equal(2, result.Completed);
    }
}
