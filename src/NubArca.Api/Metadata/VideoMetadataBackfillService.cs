using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Video;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;

namespace NubArca.Api.Metadata;

// Operator-driven, idempotent video-metadata probing (ffprobe) for existing
// video blobs, mirroring MetadataBackfillService for images. Reuses
// IFileItemService.ReExtractVideoMetadataAsync per blob, so it never mutates
// file bytes. Not run on startup; invoked by the `metadata video-backfill` CLI
// command and the MetadataVideoBackfill job. Reuses the image backfill's
// checkpoint / options / result shapes (generic counts + failed-id set).
public sealed class VideoMetadataBackfillService
{
    private readonly AppDbContext _db;
    private readonly IFileItemService _files;
    // VSEM-01: the explicit "video metadata completed" seam. Optional so the
    // service stays constructible without the AI substrate; when the capability
    // is disabled the scheduler itself no-ops.
    private readonly IVideoSemanticSegmentationScheduler? _segmentationScheduler;

    public VideoMetadataBackfillService(
        AppDbContext db,
        IFileItemService files,
        IVideoSemanticSegmentationScheduler? segmentationScheduler = null)
    {
        _db = db;
        _files = files;
        _segmentationScheduler = segmentationScheduler;
    }

    private const int PageSize = 100;
    private const int MaxFailedIds = 2000;

    public async Task<MetadataBackfillResult> RunAsync(
        MetadataBackfillOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        Func<int, int?, string?, CancellationToken, Task>? progress = null,
        string? checkpointJson = null,
        Func<long, bool>? shouldYield = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var currentVersion = FfprobeVideoMetadataExtractor.Version;

        if (options.DryRun)
        {
            var count = await CandidateQuery(currentVersion, options.FailedOnly, options.TargetBlobObjectId)
                .CountAsync(cancellationToken);
            if (options.Limit is int lim && count > lim)
            {
                count = lim;
            }
            log?.Invoke($"video metadata backfill (dry-run): {count} video blob(s) would be probed.");
            return new MetadataBackfillResult(count, 0, 0, 0, DryRun: true);
        }

        var checkpoint = MetadataBackfillCheckpoint.TryParse(checkpointJson) ?? new MetadataBackfillCheckpoint();
        var failed = new HashSet<Guid>(checkpoint.FailedIds);
        var processedTotal = checkpoint.ProcessedTotal;
        var completedTotal = checkpoint.CompletedTotal;
        var skippedTotal = checkpoint.SkippedTotal;
        var failedTotal = checkpoint.FailedTotal;

        var examinedThisSlice = 0;
        long processedThisSlice = 0;
        var completedSlice = 0;
        var skippedSlice = 0;
        var failedSlice = 0;
        var yielded = false;
        var exhausted = false;
        var cappedOut = false;
        var logBatch = Math.Clamp(options.BatchSize, 1, 1000);
        var globalLimit = options.Limit;

        bool LimitReached() => globalLimit is int gl && processedTotal >= gl;

        async Task ReportAsync()
        {
            if (progress is not null)
            {
                await progress(processedTotal, null,
                    $"probing video metadata ({completedTotal} ok, {skippedTotal} skipped, {failedTotal} failed)",
                    cancellationToken);
            }
        }

        while (!exhausted && !yielded && !LimitReached())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await FetchPageAsync(
                currentVersion, options.FailedOnly, options.TargetBlobObjectId, failed, PageSize, cancellationToken);
            if (page.Count == 0)
            {
                exhausted = true;
                break;
            }

            foreach (var blobId in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                examinedThisSlice++;
                processedTotal++;
                processedThisSlice++;

                switch (await ProcessBlobAsync(blobId, cancellationToken))
                {
                    case BlobOutcome.Completed:
                        completedSlice++; completedTotal++;
                        break;
                    case BlobOutcome.Skipped:
                        skippedSlice++; skippedTotal++;
                        break;
                    default:
                        failedSlice++; failedTotal++;
                        if (failed.Count < MaxFailedIds) failed.Add(blobId);
                        else cappedOut = true;
                        break;
                }

                if (examinedThisSlice % logBatch == 0)
                {
                    log?.Invoke($"video metadata backfill: processed {examinedThisSlice} "
                        + $"(ok {completedSlice}, skipped {skippedSlice}, failed {failedSlice}).");
                }
                await ReportAsync();

                if (LimitReached()) break;
                if (shouldYield is not null && shouldYield(processedThisSlice)) { yielded = true; break; }
            }
        }

        if (yielded)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (cappedOut)
        {
            log?.Invoke($"video metadata backfill: failed-id cap ({MaxFailedIds}) reached; deferring remaining items.");
        }

        var moreWork = !exhausted && !LimitReached() && !cappedOut;
        var nextCheckpointJson = moreWork
            ? new MetadataBackfillCheckpoint
            {
                ProcessedTotal = processedTotal,
                CompletedTotal = completedTotal,
                SkippedTotal = skippedTotal,
                FailedTotal = failedTotal,
                FailedIds = failed.Take(MaxFailedIds).ToArray(),
            }.Serialize()
            : null;

        await ReportAsync();
        log?.Invoke(
            $"video metadata backfill: {(moreWork ? "yielded" : "done")} — processed {examinedThisSlice} "
            + $"(ok {completedSlice}, skipped {skippedSlice}, failed {failedSlice}; total {processedTotal}).");

        var succeededSlice = completedSlice + skippedSlice;
        return new MetadataBackfillResult(
            examinedThisSlice, examinedThisSlice, succeededSlice, failedSlice, DryRun: false,
            MoreWorkRemaining: moreWork, NextCheckpointJson: nextCheckpointJson,
            Completed: completedSlice, Skipped: skippedSlice);
    }

    private async Task<BlobOutcome> ProcessBlobAsync(Guid blobId, CancellationToken cancellationToken)
    {
        try
        {
            var ok = await _files.ReExtractVideoMetadataAsync(blobId, cancellationToken);
            if (!ok)
            {
                return BlobOutcome.Failed; // metadata row vanished
            }
            var status = await _db.BlobMetadata.AsNoTracking()
                .Where(m => m.BlobObjectId == blobId)
                .Select(m => m.VideoExtractionStatus)
                .FirstOrDefaultAsync(cancellationToken);

            // VSEM-01: chain semantic segmentation ONLY after the probe result
            // is committed. Duration and video-stream fields are authoritative
            // from this point on, so segmentation never runs against unknown
            // metadata — and there is nothing to poll.
            if (status == MetadataStatuses.Completed && _segmentationScheduler is not null)
            {
                await _segmentationScheduler.TryScheduleForBlobAsync(blobId, cancellationToken);
            }

            return status switch
            {
                MetadataStatuses.Completed => BlobOutcome.Completed,
                MetadataStatuses.Skipped => BlobOutcome.Skipped,
                _ => BlobOutcome.Failed,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return BlobOutcome.Failed;
        }
    }

    private enum BlobOutcome { Completed, Skipped, Failed }

    private async Task<List<Guid>> FetchPageAsync(
        int currentVersion, bool failedOnly, Guid? targetBlobObjectId,
        IReadOnlyCollection<Guid> exclude, int pageSize, CancellationToken cancellationToken)
        => await CandidateQuery(currentVersion, failedOnly, targetBlobObjectId)
            .Where(m => !exclude.Contains(m.BlobObjectId))
            .OrderBy(m => m.BlobObjectId)
            .Select(m => m.BlobObjectId)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

    // Video blobs that need (re)probing: never-probed (null version), stale
    // version, or pending/failed. A finished default run leaves every video row
    // at the current version, so a re-run is a no-op. `--failed-only` targets
    // only failures. Non-video blobs are never candidates.
    private IQueryable<BlobMetadata> CandidateQuery(int currentVersion, bool failedOnly, Guid? targetBlobObjectId = null)
    {
        var query = _db.BlobMetadata.AsNoTracking()
            .Where(m => m.MediaCategory == MediaCategories.Video);

        query = failedOnly
            ? query.Where(m => m.VideoExtractionStatus == MetadataStatuses.Failed)
            : query.Where(m =>
                m.VideoExtractionVersion == null
                || m.VideoExtractionVersion < currentVersion
                || m.VideoExtractionStatus == MetadataStatuses.Pending
                || m.VideoExtractionStatus == MetadataStatuses.Failed);

        if (targetBlobObjectId is Guid target)
        {
            query = query.Where(m => m.BlobObjectId == target
                && _db.FileItems.Any(f => f.BlobObjectId == target && f.DeletedAt == null));
        }

        return query;
    }
}
