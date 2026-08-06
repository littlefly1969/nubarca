using System.Text.Json;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Diagnostics;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Video;

// VSEM-02: drives VideoSemanticEmbeddingBackfillService. No business logic
// here — payload validation, gating, profile/backend resolution, one
// cooperative slice, continuation.
//
// Gating mirrors the photo-embeddings handler: cancellation, AI disabled, the
// video capability flag off, and an unavailable provider all no-op cleanly (at
// most one aggregate transient provider diagnostic — never per-blob rows).
// Profile selection is EXPLICIT and reuses the photo image-embedding
// lifecycle: payload --profile wins, then Ai__PhotoSimilarityProfileKey, then
// the capability default — so video samples always land in the SAME embedding
// space the photo index and its paired text tower use.
public sealed class AiVideosEmbeddingsBackfillJobHandler : IJobHandler
{
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly IOptions<VideoVisualEmbeddingOptions> _videoOptions;
    private readonly IAiBackendResolver _resolver;
    private readonly IAiProfileRegistry _registry;
    private readonly IAiDiagnosticsWriter _diagnostics;
    private readonly VideoSemanticEmbeddingBackfillService _service;

    public AiVideosEmbeddingsBackfillJobHandler(
        IOptions<AiOptions> aiOptions,
        IOptions<VideoVisualEmbeddingOptions> videoOptions,
        IAiBackendResolver resolver,
        IAiProfileRegistry registry,
        IAiDiagnosticsWriter diagnostics,
        VideoSemanticEmbeddingBackfillService service)
    {
        _aiOptions = aiOptions;
        _videoOptions = videoOptions;
        _resolver = resolver;
        _registry = registry;
        _diagnostics = diagnostics;
        _service = service;
    }

    public string JobType => JobTypes.AiVideosEmbeddingsBackfill;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<VideoSemanticEmbeddingsJobPayload>(context.PayloadJson)
            ?? new VideoSemanticEmbeddingsJobPayload();

        if (payload.Limit is int limit && limit <= 0)
        {
            throw new ArgumentException("Limit must be a positive integer.");
        }

        if (payload.SegmentationVersion is int version && version <= 0)
        {
            throw new ArgumentException("SegmentationVersion must be a positive integer.");
        }

        if (context.IsCancellationRequested)
        {
            await AiSkeletonJob.NoOpAsync(context, "cancelled");
            return;
        }

        if (!_aiOptions.Value.Enabled)
        {
            await AiSkeletonJob.NoOpAsync(context, "ai-disabled");
            return;
        }

        // The video capability has its OWN flag — never inferred from the
        // photo-embedding flag.
        if (!_videoOptions.Value.Enabled)
        {
            await AiSkeletonJob.NoOpAsync(context, "capability-disabled");
            return;
        }

        var effectiveProfileKey = !string.IsNullOrWhiteSpace(payload.ProfileKey)
            ? payload.ProfileKey
            : (!string.IsNullOrWhiteSpace(_aiOptions.Value.PhotoSimilarityProfileKey)
                ? _aiOptions.Value.PhotoSimilarityProfileKey
                : null);

        // WRITING embeddings needs the live model, so resolution requires
        // backend readiness (unlike the read paths).
        var resolution = !string.IsNullOrWhiteSpace(effectiveProfileKey)
            ? await _resolver.ResolveForProfileKeyAsync<IImageEmbedder>(effectiveProfileKey!, cancellationToken)
            : await _resolver.ResolveForCapabilityAsync<IImageEmbedder>(AiCapabilities.ImageEmbedding, cancellationToken);

        if (!resolution.IsAvailable || resolution.Backend is null)
        {
            var reason = resolution.Resolution.UnavailableReason ?? AiUnavailableReasons.ProviderUnavailable;
            await _diagnostics.RecordProviderUnavailableAsync(
                AiCapabilities.ImageEmbedding, profileId: null, reason, cancellationToken);
            await AiSkeletonJob.NoOpAsync(context, reason);
            return;
        }

        var profile = resolution.Resolution.ProfileKey is { } key
            ? await _registry.GetProfileByKeyAsync(key, cancellationToken)
            : null;
        if (profile is null)
        {
            await _diagnostics.RecordProviderUnavailableAsync(
                AiCapabilities.ImageEmbedding, profileId: null, AiUnavailableReasons.NoDefaultProfile, cancellationToken);
            await AiSkeletonJob.NoOpAsync(context, AiUnavailableReasons.NoDefaultProfile);
            return;
        }

        var options = new VideoSemanticEmbeddingBackfillOptions
        {
            Limit = payload.Limit,
            FailedOnly = payload.FailedOnly,
            DryRun = payload.DryRun,
            TargetBlobObjectId = payload.BlobObjectId,
            SegmentationVersion = payload.SegmentationVersion,
        };

        var result = await _service.RunAsync(
            resolution.Backend, profile, options, context.Log, cancellationToken,
            progress: (processed, total, message, ct) =>
                context.ReportProgressAsync(processed, total, message, ct),
            checkpointJson: context.Checkpoint,
            shouldYield: processedThisSlice => context.ShouldYield(processedThisSlice),
            jobId: context.JobId);

        if (result.MoreWorkRemaining)
        {
            var reason = context.HigherPriorityWaiting
                ? JobYieldReasons.HigherPriority
                : JobYieldReasons.SliceBudget;
            context.RequestContinuation(reason, result.NextCheckpointJson);
        }
        else if (IsTerminalAllFailed(result))
        {
            // Per-blob failures are aggregated, but a run that produced nothing
            // at all is systemic (bad model export, unreadable storage, missing
            // ffmpeg) and must not read as a successful index build.
            throw new InvalidOperationException(
                "Video embedding backfill produced no embeddings; all processed blobs failed.");
        }
    }

    internal static bool IsTerminalAllFailed(VideoSemanticEmbeddingBackfillResult result)
        => !result.DryRun
            && !result.MoreWorkRemaining
            && result.Completed == 0
            && result.Partial == 0
            && result.Failed > 0;
}
