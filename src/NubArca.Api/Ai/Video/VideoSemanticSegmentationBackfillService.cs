using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Metadata;

namespace NubArca.Api.Ai.Video;

// VSEM-01: the cooperative driver around VideoSemanticSegmentationService.
//
// Keyset-paged by BlobObject.Id (never "load all candidates into memory"),
// checkpointed BETWEEN blobs — never mid-manifest — and cancellable. One blob
// is one committed unit of work, so a yield or a crash costs at most the blob
// in flight.
//
// The candidate query is the single definition of "still needs work at version
// N": a video blob with authoritative metadata and at least one eligible
// reference, whose (blob, version) manifest is absent or failed. A completed or
// permanently-skipped manifest is never a candidate, which is what makes a
// re-run of a finished backfill a no-op.
public sealed class VideoSemanticSegmentationBackfillService
{
    private const int PageSize = 100;

    private readonly AppDbContext _db;
    private readonly VideoSemanticSegmentationService _segmentation;
    private readonly IVideoSemanticEmbeddingScheduler? _embeddingScheduler;
    private readonly Faces.IVideoFaceAnalysisScheduler? _faceScheduler;

    public VideoSemanticSegmentationBackfillService(
        AppDbContext db,
        VideoSemanticSegmentationService segmentation,
        IVideoSemanticEmbeddingScheduler? embeddingScheduler = null,
        Faces.IVideoFaceAnalysisScheduler? faceScheduler = null)
    {
        _db = db;
        _segmentation = segmentation;
        _embeddingScheduler = embeddingScheduler;
        _faceScheduler = faceScheduler;
    }

    public async Task<VideoSemanticBackfillResult> RunAsync(
        VideoSemanticBackfillOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        Func<int, int?, string?, CancellationToken, Task>? progress = null,
        string? checkpointJson = null,
        Func<long, bool>? shouldYield = null,
        Guid? jobId = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var version = options.SegmentationVersion ?? _segmentation.Options.SegmentationVersion;

        if (options.DryRun)
        {
            var count = await CandidateQuery(version, options.FailedOnly, options.TargetBlobObjectId, cursor: null)
                .CountAsync(cancellationToken);
            if (options.Limit is int lim && count > lim)
            {
                count = lim;
            }

            log?.Invoke($"video semantic segmentation (dry-run): {count} video blob(s) would be segmented.");
            return new VideoSemanticBackfillResult(count, 0, 0, 0, 0, DryRun: true);
        }

        var checkpoint = AiBackfillCheckpoint.TryParse(checkpointJson) ?? AiBackfillCheckpoint.Initial;
        var cursor = checkpoint.CursorBlobId;
        var processedTotal = checkpoint.Processed;

        var examined = 0;
        long processedThisSlice = 0;
        var completed = 0;
        var skipped = 0;
        var failed = 0;
        var alreadyTerminal = 0;
        var yielded = false;
        var exhausted = false;

        bool LimitReached() => options.Limit is int limit && examined >= limit;

        async Task ReportAsync()
        {
            if (progress is not null)
            {
                await progress(processedTotal, null,
                    $"segmenting videos ({completed} ok, {skipped} skipped, {failed} failed)",
                    cancellationToken);
            }
        }

        while (!exhausted && !yielded && !LimitReached())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await CandidateQuery(version, options.FailedOnly, options.TargetBlobObjectId, cursor)
                .OrderBy(id => id)
                .Take(PageSize)
                .ToListAsync(cancellationToken);
            if (page.Count == 0)
            {
                exhausted = true;
                break;
            }

            foreach (var blobId in page)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var outcome = await _segmentation.ProcessBlobAsync(blobId, version, cancellationToken, jobId);
                switch (outcome.Kind)
                {
                    case VideoSemanticSegmentationOutcomeKind.Completed:
                        completed++;
                        // VSEM-02 chaining: a FRESHLY completed manifest makes
                        // its samples embeddable. The scheduler itself gates on
                        // the capability flag and the intended profile; already-
                        // terminal or failed manifests never schedule anything,
                        // and a scheduling failure never affects this outcome.
                        if (_embeddingScheduler is not null)
                        {
                            await _embeddingScheduler.TryScheduleForBlobAsync(
                                blobId, version, cancellationToken);
                        }

                        // VFACE-01 chaining, on the SAME trigger but on an
                        // INDEPENDENT axis: face analysis needs the temporal
                        // manifest only, never the visual embeddings. Its own
                        // scheduler gates on its own capability flag and face
                        // profile, and a scheduling failure never affects this
                        // outcome or the embedding scheduling above.
                        if (_faceScheduler is not null)
                        {
                            await _faceScheduler.TryScheduleForBlobAsync(
                                blobId, version, cancellationToken);
                        }

                        break;
                    case VideoSemanticSegmentationOutcomeKind.Skipped:
                        skipped++;
                        break;
                    case VideoSemanticSegmentationOutcomeKind.AlreadyTerminal:
                        alreadyTerminal++;
                        break;
                    default:
                        failed++;
                        break;
                }

                examined++;
                processedTotal++;
                processedThisSlice++;

                // The cursor advances only AFTER the blob's outcome is
                // committed, so a resumed slice never skips unfinished work.
                cursor = blobId;
                await ReportAsync();

                if (LimitReached())
                {
                    break;
                }

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

        var moreWork = !exhausted && !LimitReached();
        var nextCheckpointJson = moreWork
            ? new AiBackfillCheckpoint(AiBackfillCheckpoint.CurrentVersion, cursor, processedTotal).Serialize()
            : null;

        await ReportAsync();
        log?.Invoke(
            $"video semantic segmentation: {(moreWork ? "yielded" : "done")} — processed {examined} "
            + $"(ok {completed}, skipped {skipped}, failed {failed}, already-done {alreadyTerminal}; "
            + $"total {processedTotal}).");

        return new VideoSemanticBackfillResult(
            examined, completed, skipped, failed, alreadyTerminal, DryRun: false,
            MoreWorkRemaining: moreWork, NextCheckpointJson: nextCheckpointJson);
    }

    // Eligible video blobs still needing a manifest at this version.
    //
    // Vault safety comes for free: `_db.FileItems` carries the global
    // PrivateVaultId == null filter, so a blob referenced only from the Private
    // Vault has no eligible reference and is never a candidate.
    private IQueryable<Guid> CandidateQuery(int version, bool failedOnly, Guid? targetBlobObjectId, Guid? cursor)
    {
        return _db.BlobObjects.AsNoTracking()
            .Where(b =>
                (targetBlobObjectId == null || b.Id == targetBlobObjectId)
                && (cursor == null || b.Id > cursor)
                && _db.BlobMetadata.Any(m =>
                    m.BlobObjectId == b.Id
                    && m.MediaCategory == MediaCategories.Video
                    && m.VideoExtractionStatus == MetadataStatuses.Completed)
                && _db.FileItems.Any(f =>
                    f.BlobObjectId == b.Id
                    && f.DeletedAt == null
                    && f.MediaLibraryState == MediaLibraryState.Active)
                && (failedOnly
                    ? _db.VideoSemanticIndexes.Any(i =>
                        i.BlobObjectId == b.Id
                        && i.SegmentationVersion == version
                        && i.Status == AiArtifactStatuses.Failed)
                    : !_db.VideoSemanticIndexes.Any(i =>
                        i.BlobObjectId == b.Id
                        && i.SegmentationVersion == version
                        && (i.Status == AiArtifactStatuses.Completed
                            || (i.Status == AiArtifactStatuses.Skipped && i.IsPermanentFailure)))))
            .Select(b => b.Id);
    }
}

public sealed record VideoSemanticBackfillOptions
{
    public int? Limit { get; init; }
    public bool FailedOnly { get; init; }
    public bool DryRun { get; init; }
    public Guid? TargetBlobObjectId { get; init; }

    // Explicit reindex target. When null the currently configured version is
    // used, so an ordinary run never silently rewrites another version.
    public int? SegmentationVersion { get; init; }
}

public sealed record VideoSemanticBackfillResult(
    int Examined,
    int Completed,
    int Skipped,
    int Failed,
    int AlreadyTerminal,
    bool DryRun,
    bool MoreWorkRemaining = false,
    string? NextCheckpointJson = null);
