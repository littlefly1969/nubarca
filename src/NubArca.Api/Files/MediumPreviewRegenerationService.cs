using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.MediaLibrary;
using NubArca.Api.Storage;

namespace NubArca.Api.Files;

// Admin-triggered rebuild of image medium previews only. Originals, small
// thumbnails, video posters, and face previews are outside this service.
public sealed class MediumPreviewRegenerationService
{
    private const int PageSize = 100;
    private const int MaxFailedIds = 2000;

    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly IFileThumbnailService _thumbnails;
    private readonly IMediaLibraryService? _mediaLibrary;
    private readonly DerivativeDiagnosticsService? _diagnostics;
    private readonly MediaDerivativesOptions _options;

    public MediumPreviewRegenerationService(
        AppDbContext db,
        IBlobService blobs,
        IFileThumbnailService thumbnails,
        IOptions<MediaDerivativesOptions> options,
        IMediaLibraryService? mediaLibrary = null,
        DerivativeDiagnosticsService? diagnostics = null)
    {
        _db = db;
        _blobs = blobs;
        _thumbnails = thumbnails;
        _options = options.Value;
        _mediaLibrary = mediaLibrary;
        _diagnostics = diagnostics;
    }

    public async Task<MediumPreviewRegenerationResult> RunAsync(
        MediumPreviewRegenerationOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        Func<int, int?, string?, CancellationToken, Task>? progress = null,
        string? checkpointJson = null,
        Func<long, bool>? shouldYield = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var checkpoint = MediumPreviewRegenerationCheckpoint.TryParse(checkpointJson)
            ?? new MediumPreviewRegenerationCheckpoint();
        var failed = new HashSet<Guid>(checkpoint.FailedIds ?? Array.Empty<Guid>());
        var stats = new MediumPreviewRegenerationStats();
        var processedTotal = checkpoint.ProcessedTotal;
        var failedTotal = checkpoint.FailedTotal;
        var processedThisSlice = 0L;
        var yielded = false;
        var maxEdge = _options.EdgeFor(ThumbnailSizes.Medium);
        var started = Stopwatch.GetTimestamp();

        async Task ReportAsync()
        {
            if (progress is not null)
            {
                await progress(processedTotal, null,
                    $"regenerating medium previews ({processedTotal} examined, {stats.Regenerated} regenerated)",
                    cancellationToken);
            }
        }

        while (!yielded && !LimitReached(options.Limit, processedTotal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await FetchPageAsync(processedTotal, PageSize, cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var image in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processedTotal++;
                processedThisSlice++;
                stats.Examined++;

                var ok = await ProcessImageAsync(image, stats, cancellationToken);
                if (!ok && failed.Add(image.FileItemId))
                {
                    failedTotal++;
                }

                await ReportAsync();
                if (LimitReached(options.Limit, processedTotal)) break;
                if (shouldYield is not null && shouldYield(processedThisSlice))
                {
                    yielded = true;
                    break;
                }
            }
        }

        if (yielded)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        var moreWork = yielded || (!LimitReached(options.Limit, processedTotal)
            && await HasMoreAsync(processedTotal, cancellationToken));
        var nextCheckpointJson = moreWork
            ? new MediumPreviewRegenerationCheckpoint(
                processedTotal, failedTotal, failed.Take(MaxFailedIds).ToArray()).Serialize()
            : null;

        await ReportAsync();
        var elapsed = Stopwatch.GetElapsedTime(started);
        log?.Invoke(
            $"media medium previews regenerate: {(moreWork ? "yielded" : "done")} "
            + $"examined={stats.Examined} cleared={stats.Cleared} regenerated={stats.Regenerated} "
            + $"skipped={stats.Skipped} failed={stats.Failed} elapsed_ms={(long)elapsed.TotalMilliseconds} "
            + $"max_edge={maxEdge}");

        return new MediumPreviewRegenerationResult
        {
            Stats = stats,
            MoreWorkRemaining = moreWork,
            NextCheckpointJson = nextCheckpointJson,
            ProcessedTotal = processedTotal,
            FailedTotal = failedTotal,
            MaxEdge = maxEdge,
        };
    }

    private async Task<bool> ProcessImageAsync(
        ImageCandidate image, MediumPreviewRegenerationStats stats, CancellationToken ct)
    {
        try
        {
            var rows = await _db.FileThumbnails
                .Where(t => t.FileItemId == image.FileItemId && t.Size == ThumbnailSizes.Medium)
                .ToListAsync(ct);
            if (rows.Count > 0)
            {
                var blobIds = rows.Select(r => r.BlobObjectId).Distinct().ToList();
                _db.FileThumbnails.RemoveRange(rows);
                await _db.SaveChangesAsync(ct);
                foreach (var blobId in blobIds)
                {
                    await _blobs.ReleaseAsync(blobId, ct);
                }
                stats.Cleared += rows.Count;
            }

            var result = await _thumbnails.EnsureImageDerivativesAsync(
                image.FileItemId, image.OwnerUserId, new[] { ThumbnailSizes.Medium }, ct);
            var outcome = result.Outcomes.FirstOrDefault(o => o.Size == ThumbnailSizes.Medium);
            if (outcome is null)
            {
                stats.Failed++;
                return false;
            }

            if (_diagnostics is not null)
            {
                await _diagnostics.ApplyImageOutcomeAsync(
                    image.FileItemId, outcome,
                    image.DetectedContentType, image.DetectedFormat, ct);
            }

            if (outcome.Outcome == DerivativeOutcome.Generated)
            {
                stats.Regenerated++;
                return true;
            }
            if (outcome.Outcome == DerivativeOutcome.SkippedExisting)
            {
                stats.Skipped++;
                return true;
            }

            stats.Failed++;
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            stats.Failed++;
            return false;
        }
    }

    // Slice 3: the batch regeneration also skips per-file Excluded files (no new
    // work); existing previews are preserved and still served in the Excluded tab.
    private IQueryable<FileItem> Eligible(IQueryable<FileItem> query)
    {
        var scoped = MediaLibrary.MediaLibraryScopePolicy.ApplyScope(
            query, MediaLibrary.MediaLibraryScope.Active);
        return _mediaLibrary is null ? scoped : _mediaLibrary.ApplyMediaLibraryVisibility(scoped, MediaKind.Photo);
    }

    private async Task<List<ImageCandidate>> FetchPageAsync(
        int skip, int pageSize, CancellationToken ct)
    {
        var query = _db.FileItems.AsNoTracking()
            .Where(f => f.DeletedAt == null
                && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                    && m.MediaCategory == MediaCategories.Image
                    && m.DetectedContentType != null));

        return await Eligible(query)
            .OrderBy(f => f.Id)
            .Skip(skip)
            .Select(f => new ImageCandidate(
                f.Id,
                f.OwnerUserId,
                _db.BlobMetadata.Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.DetectedContentType).FirstOrDefault(),
                _db.BlobMetadata.Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.DetectedFormat).FirstOrDefault()))
            .Take(pageSize)
            .ToListAsync(ct);
    }

    private async Task<bool> HasMoreAsync(
        int skip, CancellationToken ct)
        => await Eligible(_db.FileItems.AsNoTracking()
            .Where(f => f.DeletedAt == null
                && _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                    && m.MediaCategory == MediaCategories.Image
                    && m.DetectedContentType != null)))
            .OrderBy(f => f.Id)
            .Skip(skip)
            .AnyAsync(ct);

    private static bool LimitReached(int? limit, int processedTotal)
        => limit is int n && processedTotal >= n;

    private readonly record struct ImageCandidate(
        Guid FileItemId, Guid OwnerUserId, string? DetectedContentType, string? DetectedFormat);
}

public sealed record MediumPreviewRegenerationOptions(int? Limit = null);

public sealed class MediumPreviewRegenerationStats
{
    public int Examined { get; set; }
    public int Cleared { get; set; }
    public int Regenerated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
}

public sealed class MediumPreviewRegenerationResult
{
    public MediumPreviewRegenerationStats Stats { get; init; } = new();
    public bool MoreWorkRemaining { get; init; }
    public string? NextCheckpointJson { get; init; }
    public int ProcessedTotal { get; init; }
    public int FailedTotal { get; init; }
    public int MaxEdge { get; init; }
}

public sealed record MediumPreviewRegenerationCheckpoint(
    int ProcessedTotal = 0,
    int FailedTotal = 0,
    Guid[]? FailedIds = null)
{
    public string Serialize() => JsonSerializer.Serialize(this);

    public static MediumPreviewRegenerationCheckpoint? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<MediumPreviewRegenerationCheckpoint>(json);
        }
        catch
        {
            return null;
        }
    }
}
