using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Video.Faces;

// VFACE-01: the cooperative driver around VideoFaceAnalysisService.
//
// Keyset-paged by BlobObject.Id, checkpointed BETWEEN blobs — never mid-video —
// and cancellable. One blob is one unit of work; a video interrupted mid-sweep
// simply re-runs from its first frame on the next slice (nothing partial is
// persisted, so no inconsistent analysis can survive).
//
// The candidate query is the single definition of "still needs face analysis at
// segmentation version N and analysis version M for this profile pair": a blob
// with a COMPLETED manifest at that segmentation version, at least one eligible
// reference, and no terminal analysis row. It deliberately does NOT look at
// VSEM-02 visual embeddings — face analysis and semantic embedding are
// independent axes and neither waits for the other.
//
// Duplicate FileItem references collapse by construction: candidates are blobs.
public sealed class VideoFaceAnalysisBackfillService
{
    private const int PageSize = 100;

    private readonly AppDbContext _db;
    private readonly VideoFaceAnalysisService _analysis;
    private readonly IOptions<VideoSemanticSegmentationOptions> _segmentationOptions;
    private readonly IOptions<VideoFaceAnalysisOptions> _faceOptions;

    public VideoFaceAnalysisBackfillService(
        AppDbContext db,
        VideoFaceAnalysisService analysis,
        IOptions<VideoSemanticSegmentationOptions> segmentationOptions,
        IOptions<VideoFaceAnalysisOptions> faceOptions)
    {
        _db = db;
        _analysis = analysis;
        _segmentationOptions = segmentationOptions;
        _faceOptions = faceOptions;
    }

    // `detector`/`embedder` may be null ONLY when `options.DryRun` is true: a
    // dry-run preview is a pure count query that never reaches inference, so
    // operator tooling can preview scope without resolving a live backend.
    public async Task<VideoFaceAnalysisBackfillResult> RunAsync(
        IFaceDetector? detector,
        IFaceEmbedder? embedder,
        AiProfile profile,
        VideoFaceAnalysisBackfillOptions options,
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
            ArgumentNullException.ThrowIfNull(detector);
            ArgumentNullException.ThrowIfNull(embedder);
        }

        var segmentationVersion = options.SegmentationVersion
            ?? _segmentationOptions.Value.SegmentationVersion;
        var analysisVersion = options.AnalysisVersion
            ?? _faceOptions.Value.AnalysisVersion;

        if (options.DryRun)
        {
            var count = await CandidateQuery(
                    profile.Id, segmentationVersion, analysisVersion, options.FailedOnly,
                    options.TargetBlobObjectId, cursor: null)
                .CountAsync(cancellationToken);
            if (options.Limit is int lim && count > lim)
            {
                count = lim;
            }

            log?.Invoke($"video face tracks (dry-run): {count} video blob(s) would be analysed.");
            return new VideoFaceAnalysisBackfillResult(count, 0, 0, 0, 0, 0, 0, DryRun: true);
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
        var tracks = 0;
        var yielded = false;
        var exhausted = false;

        bool LimitReached() => options.Limit is int limit && examined >= limit;

        async Task ReportAsync()
        {
            if (progress is not null)
            {
                await progress(processedTotal, null,
                    $"analysing video faces ({completed} ok, {partial} partial, {failed} failed, {tracks} tracks)",
                    cancellationToken);
            }
        }

        while (!exhausted && !yielded && !LimitReached())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await CandidateQuery(
                    profile.Id, segmentationVersion, analysisVersion, options.FailedOnly,
                    options.TargetBlobObjectId, cursor)
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

                // Guaranteed non-null here: the DryRun branch returns before this
                // loop and the guard above throws when they are null.
                var outcome = await _analysis.ProcessBlobAsync(
                    detector!, embedder!, profile, blobId, segmentationVersion, analysisVersion,
                    cancellationToken, jobId);
                switch (outcome.Kind)
                {
                    case VideoFaceAnalysisOutcomeKind.Completed:
                        completed++;
                        tracks += outcome.TrackCount;
                        break;
                    case VideoFaceAnalysisOutcomeKind.Partial:
                        partial++;
                        tracks += outcome.TrackCount;
                        break;
                    case VideoFaceAnalysisOutcomeKind.Skipped:
                    case VideoFaceAnalysisOutcomeKind.NotEligible:
                        skipped++;
                        break;
                    case VideoFaceAnalysisOutcomeKind.AlreadyTerminal:
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
            $"video face tracks: {(moreWork ? "yielded" : "done")} — processed {examined} "
            + $"(ok {completed}, partial {partial}, failed {failed}, skipped {skipped}, "
            + $"already-done {alreadyTerminal}, tracks {tracks}; total {processedTotal}).");

        return new VideoFaceAnalysisBackfillResult(
            examined, completed, partial, failed, skipped, alreadyTerminal, tracks, DryRun: false,
            MoreWorkRemaining: moreWork, NextCheckpointJson: nextCheckpointJson);
    }

    // Blobs with a COMPLETED manifest at this segmentation version that still
    // need face analysis at this analysis version for this profile pair. Vault
    // safety comes for free through the global PrivateVaultId == null filter on
    // FileItems.
    private IQueryable<Guid> CandidateQuery(
        Guid profileId, int segmentationVersion, int analysisVersion, bool failedOnly,
        Guid? targetBlobObjectId, Guid? cursor)
    {
        return _db.BlobObjects.AsNoTracking()
            .Where(b =>
                (targetBlobObjectId == null || b.Id == targetBlobObjectId)
                && (cursor == null || b.Id > cursor)
                && _db.VideoSemanticIndexes.Any(i =>
                    i.BlobObjectId == b.Id
                    && i.SegmentationVersion == segmentationVersion
                    && i.Status == AiArtifactStatuses.Completed)
                && _db.FileItems.Any(f =>
                    f.BlobObjectId == b.Id
                    && f.DeletedAt == null
                    && f.MediaLibraryState == MediaLibraryState.Active)
                && (failedOnly
                    ? _db.VideoFaceAnalysisStatuses.Any(s =>
                        s.AnalysisVersion == analysisVersion
                        && s.DetectionProfileId == profileId
                        && s.EmbeddingProfileId == profileId
                        && (s.Status == VideoFaceAnalysisStatuses.Failed
                            || s.Status == VideoFaceAnalysisStatuses.Partial)
                        && _db.VideoSemanticIndexes.Any(i =>
                            i.Id == s.VideoSemanticIndexId
                            && i.BlobObjectId == b.Id
                            && i.SegmentationVersion == segmentationVersion))
                    : !_db.VideoFaceAnalysisStatuses.Any(s =>
                        s.AnalysisVersion == analysisVersion
                        && s.DetectionProfileId == profileId
                        && s.EmbeddingProfileId == profileId
                        && (s.Status == VideoFaceAnalysisStatuses.Completed
                            || s.Status == VideoFaceAnalysisStatuses.Skipped)
                        && _db.VideoSemanticIndexes.Any(i =>
                            i.Id == s.VideoSemanticIndexId
                            && i.BlobObjectId == b.Id
                            && i.SegmentationVersion == segmentationVersion))))
            .Select(b => b.Id);
    }
}

public sealed record VideoFaceAnalysisBackfillOptions
{
    public int? Limit { get; init; }
    public bool FailedOnly { get; init; }
    public bool DryRun { get; init; }
    public Guid? TargetBlobObjectId { get; init; }

    // Explicit manifest target. When null the currently configured segmentation
    // version is used.
    public int? SegmentationVersion { get; init; }

    // Explicit reanalysis target. When null the currently configured analysis
    // version is used.
    public int? AnalysisVersion { get; init; }
}

public sealed record VideoFaceAnalysisBackfillResult(
    int Examined,
    int Completed,
    int Partial,
    int Failed,
    int Skipped,
    int AlreadyTerminal,
    int Tracks,
    bool DryRun,
    bool MoreWorkRemaining = false,
    string? NextCheckpointJson = null);
