using System.Text.Json;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Diagnostics;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Jobs;

// Phase 1: the REAL ai.photos.embeddings.backfill handler. Gating mirrors the
// skeleton handlers exactly — AI disabled / capability flag off / cancellation /
// provider unavailable all no-op (the last records at most one aggregate
// transient `provider` diagnostic, never per-blob status rows). When a backend +
// profile resolve, it drives PhotoEmbeddingBackfillService (sliceable, keyset-
// paged, checkpointed) to write BlobEmbedding rows. No pgvector and no real model
// in v0 (deterministic backend); no files are mutated.
public sealed class AiPhotosEmbeddingsBackfillJobHandler : IJobHandler
{
    private readonly IOptions<AiOptions> _options;
    private readonly IAiBackendResolver _resolver;
    private readonly IAiProfileRegistry _registry;
    private readonly IAiDiagnosticsWriter _diagnostics;
    private readonly PhotoEmbeddingBackfillService _service;

    public AiPhotosEmbeddingsBackfillJobHandler(
        IOptions<AiOptions> options,
        IAiBackendResolver resolver,
        IAiProfileRegistry registry,
        IAiDiagnosticsWriter diagnostics,
        PhotoEmbeddingBackfillService service)
    {
        _options = options;
        _resolver = resolver;
        _registry = registry;
        _diagnostics = diagnostics;
        _service = service;
    }

    public string JobType => JobTypes.AiPhotosEmbeddingsBackfill;

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

        if (!options.ImageEmbeddingsEnabled)
        {
            await AiSkeletonJob.NoOpAsync(context, "capability-disabled");
            return;
        }

        // Profile selection is EXPLICIT: an explicit payload --profile wins;
        // otherwise the configured active profile (Ai__PhotoSimilarityProfileKey)
        // — so the default backfill writes the SAME profile photo similarity
        // reads; only when neither is set do we fall back to the capability
        // default profile. No "latest installed model" heuristic. Resolution
        // (unlike the read path) requires backend readiness, since WRITING
        // embeddings needs the live model — an unavailable/absent model no-ops
        // cleanly below (no per-blob skipped/failed rows).
        var effectiveProfileKey = !string.IsNullOrWhiteSpace(payload.ProfileKey)
            ? payload.ProfileKey
            : (!string.IsNullOrWhiteSpace(options.PhotoSimilarityProfileKey)
                ? options.PhotoSimilarityProfileKey
                : null);

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

        // The resolver exposes only the profile's stable key (no GUID); fetch the
        // profile entity to get its id + dimension for writing BlobEmbedding rows.
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

        var opts = new PhotoEmbeddingBackfillOptions
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
            var reason = context.HigherPriorityWaiting
                ? JobYieldReasons.HigherPriority
                : JobYieldReasons.SliceBudget;
            context.RequestContinuation(reason, result.NextCheckpointJson);
        }
        else if (IsTerminalAllFailed(result))
        {
            // Per-item failures are intentionally aggregated, but a run that
            // produced nothing at all is a systemic failure (bad model export,
            // incompatible tensor contract, unreadable storage, etc.). Do not
            // report that state as a successful index build.
            throw new InvalidOperationException(
                "AI photo embedding backfill produced no embeddings; all processed items failed.");
        }
    }

    internal static bool IsTerminalAllFailed(PhotoEmbeddingBackfillResult result)
        => !result.DryRun
            && !result.MoreWorkRemaining
            && result.IndexedTotal == 0
            && result.FailedTotal > 0;
}
