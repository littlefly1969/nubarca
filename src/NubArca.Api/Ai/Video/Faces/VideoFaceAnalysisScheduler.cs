using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Video.Faces;

// VFACE-01: enqueues a single-blob face-analysis job once a temporal manifest has
// COMPLETED. Never called from initial upload — only the segmentation completion
// path, and never for failed/skipped manifests. It does NOT wait for (and is not
// triggered by) VSEM-02 visual embeddings.
//
// Idempotency key:
//   postingest:video:faces:{blobId}:{segmentationVersion}:{analysisVersion}:{detectionProfile}:{embeddingProfile}
// Blob + versions + profile stable keys only — no owner id, no person id, no
// filename, no storage key, no path. Duplicate FileItem references to one blob
// collapse onto ONE queued job; a new segmentation version, a new analysis
// version or a different face package deliberately gets its own key.
public sealed class VideoFaceAnalysisScheduler : IVideoFaceAnalysisScheduler
{
    private readonly IJobQueue _jobs;
    private readonly IAiProfileRegistry _registry;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly IOptions<VideoFaceAnalysisOptions> _faceOptions;
    private readonly ILogger<VideoFaceAnalysisScheduler> _logger;

    public VideoFaceAnalysisScheduler(
        IJobQueue jobs,
        IAiProfileRegistry registry,
        IOptions<AiOptions> aiOptions,
        IOptions<VideoFaceAnalysisOptions> faceOptions,
        ILogger<VideoFaceAnalysisScheduler> logger)
    {
        _jobs = jobs;
        _registry = registry;
        _aiOptions = aiOptions;
        _faceOptions = faceOptions;
        _logger = logger;
    }

    public async Task<bool> TryScheduleForBlobAsync(
        Guid blobObjectId, int segmentationVersion, CancellationToken cancellationToken = default)
    {
        if (!_aiOptions.Value.Enabled || !_faceOptions.Value.Enabled)
        {
            return false;
        }

        try
        {
            // The INTENDED face package must exist and host face embedding before
            // anything is queued. Backend readiness is deliberately not checked
            // here — the handler no-ops cleanly on an unavailable model.
            var configuredKey = _aiOptions.Value.FaceProfileKey;
            var profile = !string.IsNullOrWhiteSpace(configuredKey)
                ? await _registry.GetProfileByKeyAsync(configuredKey.Trim(), cancellationToken)
                : await _registry.GetDefaultProfileAsync(AiCapabilities.FaceEmbedding, cancellationToken);

            if (profile is null
                || !profile.Enabled
                || !string.Equals(profile.Capability, AiCapabilities.FaceEmbedding, StringComparison.Ordinal))
            {
                return false;
            }

            var analysisVersion = _faceOptions.Value.AnalysisVersion;
            await _jobs.EnqueueAsync(
                JobTypes.AiVideosFacesBackfill,
                new VideoFaceAnalysisJobPayload(
                    BlobObjectId: blobObjectId,
                    SegmentationVersion: segmentationVersion,
                    AnalysisVersion: analysisVersion,
                    DetectionProfileKey: profile.Key,
                    EmbeddingProfileKey: profile.Key),
                priority: null,
                idempotencyKey:
                    $"postingest:video:faces:{blobObjectId:N}:{segmentationVersion}:"
                    + $"{analysisVersion}:{profile.Key}:{profile.Key}",
                cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A scheduling hiccup must never fail the segmentation that has
            // already been committed — the bounded backfill picks the blob up.
            _logger.LogWarning(
                "video-faces: scheduling after segmentation completion failed ({ExceptionType}).",
                ex.GetType().Name);
            return false;
        }
    }
}
