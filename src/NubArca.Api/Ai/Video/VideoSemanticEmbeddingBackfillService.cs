using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Video;

// VSEM-02: the cooperative driver around VideoSemanticEmbeddingService.
//
// Keyset-paged by BlobObject.Id, checkpointed BETWEEN blobs — never
// mid-batch — and cancellable. One blob is one unit of work in the checkpoint
// sense; inside a blob each sample commits independently, so even an
// interrupted blob resumes at its unfinished samples.
//
// The candidate query is the single definition of "still needs embedding work
// at version N for this profile": a blob with a COMPLETED manifest at that
// version, at least one eligible reference, and no terminal aggregate status
// for (manifest, profile). Duplicate FileItem references collapse by
// construction — candidates are blobs, not files.
public sealed class VideoSemanticEmbeddingBackfillService
{
    private const int PageSize = 100;

    private readonly AppDbContext _db;
    private readonly VideoSemanticEmbeddingService _embedding;
    private readonly IOptions<VideoSemanticSegmentationOptions> _segmentationOptions;

    public VideoSemanticEmbeddingBackfillService(
        AppDbContext db,
        VideoSemanticEmbeddingService embedding,
        IOptions<VideoSemanticSegmentationOptions> segmentationOptions)
    {
        _db = db;
        _embedding = embedding;
        _segmentationOptions = segmentationOptions;
    }

    // `embedder` may be null ONLY when `options.DryRun` is true: a dry-run
    // preview is a pure count query and never reaches the embedding call
    // below, so operator tooling (the CLI) can preview scope without resolving
    // a live inference backend first.
    public async Task<VideoSemanticEmbeddingBackfillResult> RunAsync(
        IImageEmbedder? embedder,
        AiProfile profile,
        VideoSemanticEmbeddingBackfillOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        Func<int, int?, string?, CancellationToken, Task>? progress = null,
        string? checkpointJson = null,
        Func<long, bool>? shouldYield = null,
        Guid? jobId = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.DryRun)
        {
            ArgumentNullException.ThrowIfNull(embedder);
        }

        var version = options.SegmentationVersion
            ?? _segmentationOptions.Value.SegmentationVersion;

        if (options.DryRun)
        {
            var count = await CandidateQuery(profile.Id, version, options.FailedOnly, options.TargetBlobObjectId, cursor: null)
                .CountAsync(cancellationToken);
            if (options.Limit is int lim && count > lim)
            {
                count = lim;
            }

            log?.Invoke($"video visual embeddings (dry-run): {count} video blob(s) would be embedded.");
            return new VideoSemanticEmbeddingBackfillResult(count, 0, 0, 0, 0, 0, DryRun: true);
        }

        var checkpoint = AiBackfillCheckpoint.TryParse(checkpointJson) ?? AiBackfillCheckpoint.Initial;
        var cursor = checkpoint.CursorBlobId;
        var processedTotal = checkpoint.Processed;

        var examined = 0;
        long processedThisSlice = 0;
        var completed = 0;
        var partial = 0;
        var failed = 0;
        var skipped = 0;
        var alreadyTerminal = 0;
        var yielded = false;
        var exhausted = false;

        bool LimitReached() => options.Limit is int limit && examined >= limit;

        async Task ReportAsync()
        {
            if (progress is not null)
            {
                await progress(processedTotal, null,
                    $"embedding video samples ({completed} ok, {partial} partial, {failed} failed)",
                    cancellationToken);
            }
        }

        while (!exhausted && !yielded && !LimitReached())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await CandidateQuery(profile.Id, version, options.FailedOnly, options.TargetBlobObjectId, cursor)
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

                // Guaranteed non-null here: the DryRun branch above always
                // returns before this loop, and the constructor guard throws
                // when embedder is null and DryRun is false.
                var outcome = await _embedding.ProcessBlobAsync(
                    embedder!, profile, blobId, version, cancellationToken, jobId);
                switch (outcome.Kind)
                {
                    case VideoSemanticEmbeddingOutcomeKind.Completed:
                        completed++;
                        break;
                    case VideoSemanticEmbeddingOutcomeKind.Partial:
                        partial++;
                        break;
                    case VideoSemanticEmbeddingOutcomeKind.Skipped:
                    case VideoSemanticEmbeddingOutcomeKind.NotEligible:
                        skipped++;
                        break;
                    case VideoSemanticEmbeddingOutcomeKind.AlreadyTerminal:
                        alreadyTerminal++;
                        break;
                    default:
                        failed++;
                        break;
                }

                examined++;
                processedTotal++;
                processedThisSlice++;
                cursor = blobId;   // advance only AFTER the blob's outcome committed
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
            $"video visual embeddings: {(moreWork ? "yielded" : "done")} — processed {examined} "
            + $"(ok {completed}, partial {partial}, failed {failed}, skipped {skipped}, "
            + $"already-done {alreadyTerminal}; total {processedTotal}).");

        return new VideoSemanticEmbeddingBackfillResult(
            examined, completed, partial, failed, skipped, alreadyTerminal, DryRun: false,
            MoreWorkRemaining: moreWork, NextCheckpointJson: nextCheckpointJson);
    }

    // Blobs with a COMPLETED manifest at this version that still need
    // embedding work for this profile. Vault safety comes for free through the
    // global PrivateVaultId == null filter on FileItems.
    private IQueryable<Guid> CandidateQuery(
        Guid profileId, int version, bool failedOnly, Guid? targetBlobObjectId, Guid? cursor)
    {
        return _db.BlobObjects.AsNoTracking()
            .Where(b =>
                (targetBlobObjectId == null || b.Id == targetBlobObjectId)
                && (cursor == null || b.Id > cursor)
                && _db.VideoSemanticIndexes.Any(i =>
                    i.BlobObjectId == b.Id
                    && i.SegmentationVersion == version
                    && i.Status == AiArtifactStatuses.Completed)
                && _db.FileItems.Any(f =>
                    f.BlobObjectId == b.Id
                    && f.DeletedAt == null
                    && f.MediaLibraryState == MediaLibraryState.Active)
                && (failedOnly
                    ? _db.VideoSemanticEmbeddingStatuses.Any(s =>
                        s.ProfileId == profileId
                        && (s.Status == VideoSemanticEmbeddingStatuses.Failed
                            || s.Status == VideoSemanticEmbeddingStatuses.Partial)
                        && _db.VideoSemanticIndexes.Any(i =>
                            i.Id == s.VideoSemanticIndexId
                            && i.BlobObjectId == b.Id
                            && i.SegmentationVersion == version))
                    : !_db.VideoSemanticEmbeddingStatuses.Any(s =>
                        s.ProfileId == profileId
                        && (s.Status == VideoSemanticEmbeddingStatuses.Completed
                            || s.Status == VideoSemanticEmbeddingStatuses.Skipped)
                        && _db.VideoSemanticIndexes.Any(i =>
                            i.Id == s.VideoSemanticIndexId
                            && i.BlobObjectId == b.Id
                            && i.SegmentationVersion == version))))
            .Select(b => b.Id);
    }
}

public sealed record VideoSemanticEmbeddingBackfillOptions
{
    public int? Limit { get; init; }
    public bool FailedOnly { get; init; }
    public bool DryRun { get; init; }
    public Guid? TargetBlobObjectId { get; init; }

    // Explicit reindex target. When null the currently configured segmentation
    // version is used.
    public int? SegmentationVersion { get; init; }
}

public sealed record VideoSemanticEmbeddingBackfillResult(
    int Examined,
    int Completed,
    int Partial,
    int Failed,
    int Skipped,
    int AlreadyTerminal,
    bool DryRun,
    bool MoreWorkRemaining = false,
    string? NextCheckpointJson = null);
