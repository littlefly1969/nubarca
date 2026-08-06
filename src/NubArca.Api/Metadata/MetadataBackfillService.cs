using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;

namespace NubArca.Api.Metadata;

// Operator-driven, idempotent re-extraction of embedded metadata for existing
// blobs (slice 55). Reuses IFileItemService.ReExtractEmbeddedMetadataAsync per
// blob, so it never mutates file bytes and never creates duplicate metadata.
// Not run on startup; invoked only by the `metadata backfill` CLI command.
public sealed class MetadataBackfillService
{
    private readonly AppDbContext _db;
    private readonly IFileItemService _files;

    public MetadataBackfillService(AppDbContext db, IFileItemService files)
    {
        _db = db;
        _files = files;
    }

    // Page fetch chunk. Resolved blobs drop out of the candidate query and
    // failed ids are excluded, so this is only a fetch-batch size — NOT the
    // per-slice budget (the scheduler enforces that via shouldYield). There is
    // no re-processing across pages or slices, so a moderate fixed size is fine.
    private const int PageSize = 100;
    private const int MaxFailedIds = 2000;

    // Slice-aware, keyset-paged re-extraction (scheduler v2).
    //   * `checkpointJson` resumes a previous slice (null = start fresh).
    //   * `shouldYield(processedThisSlice)` is polled at SAFE per-blob
    //     boundaries (after each blob's extraction is committed); when it
    //     returns true the slice checkpoints and stops and the result reports
    //     MoreWorkRemaining = true. Null (CLI) runs the whole backfill to
    //     completion, never loading the full candidate set up front.
    // The operator/global `options.Limit` is honoured CUMULATIVELY across
    // slices (tracked in the checkpoint), distinct from the slice budget.
    public async Task<MetadataBackfillResult> RunAsync(
        MetadataBackfillOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        // Coarse progress sink (processed, total-or-null, message). Wired to
        // JobContext.ReportProgressAsync when run as a background job so the
        // Admin Jobs dashboard shows live extraction counts; null from the CLI.
        Func<int, int?, string?, CancellationToken, Task>? progress = null,
        string? checkpointJson = null,
        Func<long, bool>? shouldYield = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var currentVersion = EmbeddedImageMetadataExtractor.Version;

        if (options.DryRun)
        {
            var count = await CandidateQuery(currentVersion, options.FailedOnly, options.TargetBlobObjectId).CountAsync(cancellationToken);
            if (options.Limit is int lim && count > lim)
            {
                count = lim;
            }
            // Numbers only — never any extracted/raw metadata.
            log?.Invoke($"metadata backfill (dry-run): {count} blob(s) would be re-extracted.");
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
        // Bounds BOTH the checkpoint size AND termination: the failed-id set can
        // never exceed MaxFailedIds, and once it is full this logical job stops
        // (rather than re-fetching un-recordable failures forever). Any remaining
        // items are left for a future enqueue / `--failed-only`.
        var cappedOut = false;
        var logBatch = Math.Clamp(options.BatchSize, 1, 1000);
        var globalLimit = options.Limit;

        bool LimitReached() => globalLimit is int gl && processedTotal >= gl;

        async Task ReportAsync()
        {
            if (progress is not null)
            {
                await progress(processedTotal, null,
                    $"extracting metadata ({completedTotal} ok, {skippedTotal} skipped, {failedTotal} failed)",
                    cancellationToken);
            }
        }

        while (!exhausted && !yielded && !LimitReached())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await FetchPageAsync(currentVersion, options.FailedOnly, options.TargetBlobObjectId, failed, PageSize, cancellationToken);
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
                    default: // Failed — still a candidate; record it so later slices skip it.
                        failedSlice++; failedTotal++;
                        if (failed.Count < MaxFailedIds) failed.Add(blobId);
                        else cappedOut = true; // set is full → stop after this slice (see decl)
                        break;
                }

                if (examinedThisSlice % logBatch == 0)
                {
                    log?.Invoke($"metadata backfill: processed {examinedThisSlice} (ok {completedSlice}, skipped {skippedSlice}, failed {failedSlice}).");
                }
                await ReportAsync();

                if (LimitReached()) break;
                if (shouldYield is not null && shouldYield(processedThisSlice)) { yielded = true; break; }
            }
        }

        // A yield triggered by CANCELLATION must surface as cancellation (the
        // engine then marks the job cancelled), not a clean continuation.
        if (yielded)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (cappedOut)
        {
            log?.Invoke($"metadata backfill: failed-id cap ({MaxFailedIds}) reached; deferring remaining items to a future run.");
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
            $"metadata backfill: {(moreWork ? "yielded" : "done")} — processed {examinedThisSlice} "
            + $"(ok {completedSlice}, skipped {skippedSlice}, failed {failedSlice}; total {processedTotal}).");

        var succeededSlice = completedSlice + skippedSlice;
        return new MetadataBackfillResult(
            examinedThisSlice, examinedThisSlice, succeededSlice, failedSlice, DryRun: false,
            MoreWorkRemaining: moreWork, NextCheckpointJson: nextCheckpointJson,
            Completed: completedSlice, Skipped: skippedSlice);
    }

    // Re-extract one blob and classify the outcome from its resulting status.
    // Completed/Skipped resolve the row (it leaves the candidate set); Failed (or
    // a vanished row) stays a candidate, so the caller records it to skip later.
    private async Task<BlobOutcome> ProcessBlobAsync(Guid blobId, CancellationToken cancellationToken)
    {
        try
        {
            var ok = await _files.ReExtractEmbeddedMetadataAsync(blobId, cancellationToken);
            if (!ok)
            {
                // No metadata row (vanished). Not retryable here.
                return BlobOutcome.Failed;
            }
            var status = await _db.BlobMetadata.AsNoTracking()
                .Where(m => m.BlobObjectId == blobId)
                .Select(m => m.ExtractionStatus)
                .FirstOrDefaultAsync(cancellationToken);
            return status switch
            {
                MetadataStatuses.Completed => BlobOutcome.Completed,
                MetadataStatuses.Skipped => BlobOutcome.Skipped,
                _ => BlobOutcome.Failed, // Failed / Pending / unexpected → still a candidate
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Per-blob failure is non-fatal; the extractor already sanitizes its
            // own error code on the row. We deliberately do NOT log the exception
            // detail, which could echo raw metadata bytes.
            return BlobOutcome.Failed;
        }
    }

    private enum BlobOutcome { Completed, Skipped, Failed }

    // Next page of candidates (oldest blob id first), excluding ids that already
    // failed to resolve this run. Resolved blobs drop out of CandidateQuery on
    // their own, so successive pages return fresh work with no positional cursor.
    private async Task<List<Guid>> FetchPageAsync(
        int currentVersion, bool failedOnly, Guid? targetBlobObjectId, IReadOnlyCollection<Guid> exclude, int pageSize, CancellationToken cancellationToken)
        => await CandidateQuery(currentVersion, failedOnly, targetBlobObjectId)
            .Where(m => !exclude.Contains(m.BlobObjectId))
            .OrderBy(m => m.BlobObjectId)
            .Select(m => m.BlobObjectId)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

    // Repair command: rebuild the denormalized FileItem.EffectiveDateTaken (and
    // its source tag) for EVERY file from the layered sources of truth (user
    // override → embedded blob DateTaken → CreatedAt). Pure set-based SQL — no
    // byte reads, no per-row round-trips, and it never touches the sources. Use
    // after a bulk import or if the column is ever suspected stale. Returns the
    // number of file rows updated. Exposes no storage internals.
    public async Task<int> RecomputeEffectiveDatesAsync(
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var updated = await _db.FileItems.ExecuteUpdateAsync(
            s => s
                .SetProperty(
                    f => f.EffectiveDateTaken,
                    f => _db.FileItemUserMetadata
                            .Where(u => u.FileItemId == f.Id)
                            .Select(u => u.DateTakenOverride)
                            .FirstOrDefault()
                        ?? _db.BlobMetadata
                            .Where(m => m.BlobObjectId == f.BlobObjectId)
                            .Select(m => m.DateTaken)
                            .FirstOrDefault()
                        ?? f.CreatedAt)
                .SetProperty(
                    f => f.EffectiveDateTakenSource,
                    f => _db.FileItemUserMetadata
                            .Any(u => u.FileItemId == f.Id && u.DateTakenOverride != null)
                        ? EffectiveDateTakenSources.User
                        : _db.BlobMetadata
                            .Any(m => m.BlobObjectId == f.BlobObjectId && m.DateTaken != null)
                            ? EffectiveDateTakenSources.Embedded
                            : EffectiveDateTakenSources.Uploaded),
            cancellationToken);

        log?.Invoke($"recompute-effective-dates: updated {updated} file(s).");
        return updated;
    }

    // Default selection targets rows that need (re)extraction: never-extracted
    // (null version, e.g. pre-slice-54 blobs), stale-version, or pending/failed.
    // A finished default run leaves every row at the current version, so a
    // re-run is a no-op (idempotent). `--failed-only` targets only failures.
    private IQueryable<BlobMetadata> CandidateQuery(int currentVersion, bool failedOnly, Guid? targetBlobObjectId = null)
    {
        var query = _db.BlobMetadata.AsNoTracking();

        query = failedOnly
            ? query.Where(m => m.ExtractionStatus == MetadataStatuses.Failed)
            : query.Where(m =>
                m.ExtractionVersion == null
                || m.ExtractionVersion < currentVersion
                || m.ExtractionStatus == MetadataStatuses.Pending
                || m.ExtractionStatus == MetadataStatuses.Failed);

        // Post-ingest single-target scope: restrict to one blob AND re-check
        // normal-library eligibility — the blob must still be referenced by an
        // active FileItem. `_db.FileItems` carries the global Private Vault query
        // filter, so a blob whose only referencing file was moved into the vault
        // (or deleted) between enqueue and run is NOT extracted. The global
        // backfill path (targetBlobObjectId == null) is unchanged.
        if (targetBlobObjectId is Guid target)
        {
            query = query.Where(m => m.BlobObjectId == target
                && _db.FileItems.Any(f => f.BlobObjectId == target && f.DeletedAt == null));
        }

        return query;
    }
}

public sealed record MetadataBackfillOptions
{
    public int? Limit { get; init; }
    public bool FailedOnly { get; init; }
    public bool DryRun { get; init; }
    public int BatchSize { get; init; } = 100;
    // Post-ingest single-target scope: when set, restrict to this one blob.
    public Guid? TargetBlobObjectId { get; init; }
}

// Per-call (per-slice) counts. Succeeded = Completed + Skipped (processed
// without failure), kept for back-compat; Completed/Skipped break it down.
// MoreWorkRemaining + NextCheckpointJson drive cooperative continuation.
public sealed record MetadataBackfillResult(
    int Examined,
    int Processed,
    int Succeeded,
    int Failed,
    bool DryRun,
    bool MoreWorkRemaining = false,
    string? NextCheckpointJson = null,
    int Completed = 0,
    int Skipped = 0);
