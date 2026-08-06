using System.Text.Json;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Diagnostics;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Jobs;

// Face Substrate v0: the REAL ai.faces.embeddings.backfill handler. Same gating
// discipline as detection, but gated on FaceEmbeddingsEnabled and resolving an
// IFaceEmbedder for the SAME face-package profile detection used (so embeddings
// key to the detections' ProfileId). Drives FaceEmbeddingBackfillService.
public sealed class AiFacesEmbeddingsBackfillJobHandler : IJobHandler
{
    private readonly IOptions<AiOptions> _options;
    private readonly IAiBackendResolver _resolver;
    private readonly IAiProfileRegistry _registry;
    private readonly IAiDiagnosticsWriter _diagnostics;
    private readonly FaceEmbeddingBackfillService _service;

    public AiFacesEmbeddingsBackfillJobHandler(
        IOptions<AiOptions> options,
        IAiBackendResolver resolver,
        IAiProfileRegistry registry,
        IAiDiagnosticsWriter diagnostics,
        FaceEmbeddingBackfillService service)
    {
        _options = options;
        _resolver = resolver;
        _registry = registry;
        _diagnostics = diagnostics;
        _service = service;
    }

    public string JobType => JobTypes.AiFacesEmbeddingsBackfill;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<AiBackfillJobPayload>(context.PayloadJson)
            ?? new AiBackfillJobPayload();
        var options = _options.Value;

        if (context.IsCancellationRequested)
        {
            await AiSkeletonJob.NoOpAsync(context, "cancelled");
            return;
        }

        if (!options.Enabled)
        {
            await AiSkeletonJob.NoOpAsync(context, "ai-disabled");
            return;
        }

        if (!options.FaceEmbeddingsEnabled)
        {
            await AiSkeletonJob.NoOpAsync(context, "capability-disabled");
            return;
        }

        var resolution = await FaceProfileResolver.ResolveEmbedderAsync(
            _resolver, payload.ProfileKey, options.FaceProfileKey, cancellationToken);

        if (!resolution.IsAvailable || resolution.Backend is null)
        {
            var reason = resolution.Resolution.UnavailableReason ?? AiUnavailableReasons.ProviderUnavailable;
            await _diagnostics.RecordProviderUnavailableAsync(
                AiCapabilities.FaceEmbedding, profileId: null, reason, cancellationToken);
            await AiSkeletonJob.NoOpAsync(context, reason);
            return;
        }

        var profile = resolution.Resolution.ProfileKey is { } key
            ? await _registry.GetProfileByKeyAsync(key, cancellationToken)
            : null;
        if (profile is null)
        {
            await _diagnostics.RecordProviderUnavailableAsync(
                AiCapabilities.FaceEmbedding, profileId: null, AiUnavailableReasons.NoDefaultProfile, cancellationToken);
            await AiSkeletonJob.NoOpAsync(context, AiUnavailableReasons.NoDefaultProfile);
            return;
        }

        var opts = new FaceBackfillOptions
        {
            Limit = payload.Limit,
            DryRun = payload.DryRun,
            TargetBlobObjectId = payload.BlobObjectId,
        };

        var result = await _service.RunAsync(
            resolution.Backend, profile, opts, context.Log, cancellationToken,
            progress: (processed, total, message, ct) => context.ReportProgressAsync(processed, total, message, ct),
            checkpointJson: context.Checkpoint,
            shouldYield: processedThisSlice => context.ShouldYield(processedThisSlice));

        if (result.MoreWorkRemaining)
        {
            var reason = context.HigherPriorityWaiting ? JobYieldReasons.HigherPriority : JobYieldReasons.SliceBudget;
            context.RequestContinuation(reason, result.NextCheckpointJson);
        }
        else if (payload.BlobObjectId is not null && !payload.DryRun
                 && result.Produced == 0 && result.Failed > 0)
        {
            throw new InvalidOperationException("Targeted face embedding failed.");
        }
    }
}
