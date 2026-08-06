using System.Text.Json;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Diagnostics;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Jobs;

// Face Substrate v0: the REAL ai.faces.detect.backfill handler. Gating mirrors
// the photo backfill exactly — AI disabled / FaceDetectionEnabled off /
// cancellation / provider unavailable all no-op (the last records at most one
// aggregate transient `provider` diagnostic, never per-blob status rows). When a
// backend + profile resolve, it drives FaceDetectionBackfillService (sliceable,
// keyset-paged, checkpointed) to persist FaceDetection rows + detection status.
//
// The face PACKAGE is modeled under the face-embedding capability (one AiProfile
// = detector + recognizer), so detection resolves that SAME profile — detection
// and embedding therefore write rows under one consistent ProfileId.
public sealed class AiFacesDetectBackfillJobHandler : IJobHandler
{
    private readonly IOptions<AiOptions> _options;
    private readonly IAiBackendResolver _resolver;
    private readonly IAiProfileRegistry _registry;
    private readonly IAiDiagnosticsWriter _diagnostics;
    private readonly FaceDetectionBackfillService _service;
    private readonly IJobQueue _jobs;

    public AiFacesDetectBackfillJobHandler(
        IOptions<AiOptions> options,
        IAiBackendResolver resolver,
        IAiProfileRegistry registry,
        IAiDiagnosticsWriter diagnostics,
        FaceDetectionBackfillService service,
        IJobQueue jobs)
    {
        _options = options;
        _resolver = resolver;
        _registry = registry;
        _diagnostics = diagnostics;
        _service = service;
        _jobs = jobs;
    }

    public string JobType => JobTypes.AiFacesDetectBackfill;

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

        if (!options.FaceDetectionEnabled)
        {
            await AiSkeletonJob.NoOpAsync(context, "capability-disabled");
            return;
        }

        var resolution = await FaceProfileResolver.ResolveDetectorAsync(
            _resolver, payload.ProfileKey, options.FaceProfileKey, cancellationToken);

        if (!resolution.IsAvailable || resolution.Backend is null)
        {
            var reason = resolution.Resolution.UnavailableReason ?? AiUnavailableReasons.ProviderUnavailable;
            await _diagnostics.RecordProviderUnavailableAsync(
                AiCapabilities.FaceDetection, profileId: null, reason, cancellationToken);
            await AiSkeletonJob.NoOpAsync(context, reason);
            return;
        }

        var profile = resolution.Resolution.ProfileKey is { } key
            ? await _registry.GetProfileByKeyAsync(key, cancellationToken)
            : null;
        if (profile is null)
        {
            await _diagnostics.RecordProviderUnavailableAsync(
                AiCapabilities.FaceDetection, profileId: null, AiUnavailableReasons.NoDefaultProfile, cancellationToken);
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
        else if (payload.BlobObjectId is { } blobId && !payload.DryRun)
        {
            if (result.Failed > 0)
            {
                throw new InvalidOperationException("Targeted face detection failed.");
            }

            // Chain recognition only after the detection transaction completed.
            // Zero-face images need no embedding job; the detection completion
            // marker already makes the targeted pipeline terminal/idempotent.
            if (result.Produced > 0 && options.FaceEmbeddingsEnabled)
            {
                await _jobs.EnqueueAsync(
                    JobTypes.AiFacesEmbeddingsBackfill,
                    new AiBackfillJobPayload(ProfileKey: profile.Key, BlobObjectId: blobId),
                    priority: context.Priority,
                    idempotencyKey: $"postingest:faces:embed:{blobId:N}:{profile.Key}",
                    cancellationToken: cancellationToken);
            }
        }
    }
}
