using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Video;

// VSEM-01: enqueues a single-blob segmentation job once authoritative video
// metadata exists.
//
// Idempotency key: postingest:video:segments:{blobId}:{segmentationVersion}.
// Blob + version only — no owner id, no filename, no storage key, no path. Two
// uploads of the same bytes, or a re-probe of the same blob, therefore collapse
// onto ONE queued job; a NEW segmentation version deliberately gets its own key
// so a reindex is schedulable while the old manifest still stands.
public sealed class VideoSemanticSegmentationScheduler : IVideoSemanticSegmentationScheduler
{
    private readonly IJobQueue _jobs;
    private readonly IOptions<VideoSemanticSegmentationOptions> _options;
    private readonly ILogger<VideoSemanticSegmentationScheduler> _logger;

    public VideoSemanticSegmentationScheduler(
        IJobQueue jobs,
        IOptions<VideoSemanticSegmentationOptions> options,
        ILogger<VideoSemanticSegmentationScheduler> logger)
    {
        _jobs = jobs;
        _options = options;
        _logger = logger;
    }

    public async Task<bool> TryScheduleForBlobAsync(
        Guid blobObjectId, CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return false;
        }

        var version = options.SegmentationVersion;
        try
        {
            await _jobs.EnqueueAsync(
                JobTypes.AiVideosSegmentsBackfill,
                new VideoSemanticSegmentsJobPayload(
                    BlobObjectId: blobObjectId, SegmentationVersion: version),
                priority: null,
                idempotencyKey: $"postingest:video:segments:{blobObjectId:N}:{version}",
                cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A scheduling hiccup must never fail the metadata probe that has
            // already been committed. The bounded backfill picks the blob up
            // later — the candidate query is the durable source of truth.
            _logger.LogWarning(
                "video-segments: scheduling after metadata completion failed ({ExceptionType}).",
                ex.GetType().Name);
            return false;
        }
    }
}
