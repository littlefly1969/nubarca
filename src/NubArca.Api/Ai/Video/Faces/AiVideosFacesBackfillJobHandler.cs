using System.Text.Json;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Diagnostics;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Video.Faces;

// VFACE-01: drives VideoFaceAnalysisBackfillService. No business logic here —
// payload validation, gating, profile/backend resolution, one cooperative slice,
// continuation.
//
// Gating mirrors the VSEM-02 handler: cancellation, AI disabled, the video-face
// capability flag off, and an unavailable provider all no-op cleanly (at most one
// aggregate transient provider diagnostic — never per-blob rows). Profile
// selection is EXPLICIT and reuses the photo face lifecycle through
// FaceProfileResolver: payload key wins, then Ai__FaceProfileKey, then the
// face-embedding capability default — so video tracks always land in the SAME
// recognition space the photo face substrate and People use.
public sealed class AiVideosFacesBackfillJobHandler : IJobHandler
{
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly IOptions<VideoFaceAnalysisOptions> _faceOptions;
    private readonly IAiBackendResolver _resolver;
    private readonly IAiProfileRegistry _registry;
    private readonly IAiDiagnosticsWriter _diagnostics;
    private readonly VideoFaceAnalysisBackfillService _service;

    public AiVideosFacesBackfillJobHandler(
        IOptions<AiOptions> aiOptions,
        IOptions<VideoFaceAnalysisOptions> faceOptions,
        IAiBackendResolver resolver,
        IAiProfileRegistry registry,
        IAiDiagnosticsWriter diagnostics,
        VideoFaceAnalysisBackfillService service)
    {
        _aiOptions = aiOptions;
        _faceOptions = faceOptions;
        _resolver = resolver;
        _registry = registry;
        _diagnostics = diagnostics;
        _service = service;
    }

    public string JobType => JobTypes.AiVideosFacesBackfill;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<VideoFaceAnalysisJobPayload>(context.PayloadJson)
            ?? new VideoFaceAnalysisJobPayload();

        if (payload.Limit is int limit && limit <= 0)
        {
            throw new ArgumentException("Limit must be a positive integer.");
        }

        if (payload.SegmentationVersion is int segmentationVersion && segmentationVersion <= 0)
        {
            throw new ArgumentException("SegmentationVersion must be a positive integer.");
        }

        if (payload.AnalysisVersion is int analysisVersion && analysisVersion <= 0)
        {
            throw new ArgumentException("AnalysisVersion must be a positive integer.");
        }

        // One AiProfile encapsulates the whole face package here, so two
        // DIFFERENT keys cannot both be honoured. Refusing beats silently mixing
        // a detector from one recognition space with a recognizer from another.
        if (!string.IsNullOrWhiteSpace(payload.DetectionProfileKey)
            && !string.IsNullOrWhiteSpace(payload.EmbeddingProfileKey)
            && !string.Equals(
                payload.DetectionProfileKey.Trim(), payload.EmbeddingProfileKey.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "DetectionProfileKey and EmbeddingProfileKey must name the same face profile.");
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

        // The video-face capability has its OWN flag — never inferred from the
        // photo face flag or from the VSEM-02 visual-embedding flag.
        if (!_faceOptions.Value.Enabled)
        {
            await AiSkeletonJob.NoOpAsync(context, "capability-disabled");
            return;
        }

        var payloadKey = !string.IsNullOrWhiteSpace(payload.DetectionProfileKey)
            ? payload.DetectionProfileKey
            : payload.EmbeddingProfileKey;

        // WRITING tracks needs the live models, so resolution requires backend
        // readiness (unlike the read paths).
        var detection = await FaceProfileResolver.ResolveDetectorAsync(
            _resolver, payloadKey, _aiOptions.Value.FaceProfileKey, cancellationToken);
        if (!detection.IsAvailable || detection.Backend is null)
        {
            await UnavailableAsync(context, detection.Resolution.UnavailableReason, cancellationToken);
            return;
        }

        var embedding = await FaceProfileResolver.ResolveEmbedderAsync(
            _resolver, payloadKey, _aiOptions.Value.FaceProfileKey, cancellationToken);
        if (!embedding.IsAvailable || embedding.Backend is null)
        {
            await UnavailableAsync(context, embedding.Resolution.UnavailableReason, cancellationToken);
            return;
        }

        var profile = detection.Resolution.ProfileKey is { } key
            ? await _registry.GetProfileByKeyAsync(key, cancellationToken)
            : null;
        if (profile is null)
        {
            await UnavailableAsync(context, AiUnavailableReasons.NoDefaultProfile, cancellationToken);
            return;
        }

        var options = new VideoFaceAnalysisBackfillOptions
        {
            Limit = payload.Limit,
            FailedOnly = payload.FailedOnly,
            DryRun = payload.DryRun,
            TargetBlobObjectId = payload.BlobObjectId,
            SegmentationVersion = payload.SegmentationVersion,
            AnalysisVersion = payload.AnalysisVersion,
        };

        var result = await _service.RunAsync(
            detection.Backend, embedding.Backend, profile, options, context.Log, cancellationToken,
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
            // ffmpeg) and must not read as a successful analysis pass.
            throw new InvalidOperationException(
                "Video face analysis produced no tracks; all processed blobs failed.");
        }
    }

    private async Task UnavailableAsync(
        JobContext context, string? reason, CancellationToken cancellationToken)
    {
        var code = reason ?? AiUnavailableReasons.ProviderUnavailable;
        await _diagnostics.RecordProviderUnavailableAsync(
            AiCapabilities.FaceEmbedding, profileId: null, code, cancellationToken);
        await AiSkeletonJob.NoOpAsync(context, code);
    }

    internal static bool IsTerminalAllFailed(VideoFaceAnalysisBackfillResult result)
        => !result.DryRun
            && !result.MoreWorkRemaining
            && result.Completed == 0
            && result.Partial == 0
            && result.Failed > 0;
}
