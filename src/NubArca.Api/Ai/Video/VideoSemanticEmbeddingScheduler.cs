using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Video;

// VSEM-02: enqueues a single-blob embedding job once a temporal manifest has
// COMPLETED. Never called from initial upload — only the segmentation
// completion path (and never for failed/skipped manifests).
//
// Idempotency key: postingest:video:embed:{blobId}:{segmentationVersion}:{profileKey}.
// Blob + version + profile stable key only — no owner id, no filename, no
// storage key, no path. Duplicate FileItem references to one blob collapse
// onto ONE queued job; a new segmentation version or a different profile
// deliberately gets its own key.
public sealed class VideoSemanticEmbeddingScheduler : IVideoSemanticEmbeddingScheduler
{
    private readonly IJobQueue _jobs;
    private readonly IAiProfileRegistry _registry;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly IOptions<VideoVisualEmbeddingOptions> _videoOptions;
    private readonly ILogger<VideoSemanticEmbeddingScheduler> _logger;

    public VideoSemanticEmbeddingScheduler(
        IJobQueue jobs,
        IAiProfileRegistry registry,
        IOptions<AiOptions> aiOptions,
        IOptions<VideoVisualEmbeddingOptions> videoOptions,
        ILogger<VideoSemanticEmbeddingScheduler> logger)
    {
        _jobs = jobs;
        _registry = registry;
        _aiOptions = aiOptions;
        _videoOptions = videoOptions;
        _logger = logger;
    }

    public async Task<bool> TryScheduleForBlobAsync(
        Guid blobObjectId, int segmentationVersion, CancellationToken cancellationToken = default)
    {
        if (!_aiOptions.Value.Enabled || !_videoOptions.Value.Enabled)
        {
            return false;
        }

        try
        {
            // The INTENDED profile must exist and host image embeddings before
            // anything is queued. Backend readiness is deliberately not checked
            // here — the handler no-ops cleanly on an unavailable model.
            var configuredKey = _aiOptions.Value.PhotoSimilarityProfileKey;
            var profile = !string.IsNullOrWhiteSpace(configuredKey)
                ? await _registry.GetProfileByKeyAsync(configuredKey.Trim(), cancellationToken)
                : await _registry.GetDefaultProfileAsync(AiCapabilities.ImageEmbedding, cancellationToken);

            if (profile is null
                || !profile.Enabled
                || !string.Equals(profile.Capability, AiCapabilities.ImageEmbedding, StringComparison.Ordinal))
            {
                return false;
            }

            await _jobs.EnqueueAsync(
                JobTypes.AiVideosEmbeddingsBackfill,
                new VideoSemanticEmbeddingsJobPayload(
                    BlobObjectId: blobObjectId, SegmentationVersion: segmentationVersion),
                priority: null,
                idempotencyKey:
                    $"postingest:video:embed:{blobObjectId:N}:{segmentationVersion}:{profile.Key}",
                cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A scheduling hiccup must never fail the segmentation that has
            // already been committed — the bounded backfill picks the blob up.
            _logger.LogWarning(
                "video-embed: scheduling after segmentation completion failed ({ExceptionType}).",
                ex.GetType().Name);
            return false;
        }
    }
}
