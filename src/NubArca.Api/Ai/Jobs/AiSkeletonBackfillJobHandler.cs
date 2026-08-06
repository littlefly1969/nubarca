using System.Text.Json;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Diagnostics;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Jobs;

// Base for the backend-served AI skeleton backfills (everything except face
// clustering, which is a derived job, not a backend capability). TBackend is the
// capability interface the job would use in Phase 1+ (e.g. IImageEmbedder).
//
// Phase 0C behaviour — NO inference, NO domain rows ever written:
//   * AI disabled            -> no-op (no diagnostic; this is the normal state)
//   * capability flag off    -> no-op (no diagnostic)
//   * provider unavailable   -> no-op + AT MOST ONE aggregate transient
//                               `provider` diagnostic; NEVER per-blob skipped/
//                               failed rows, NEVER pending rows
//   * backend resolved       -> no-op ("skeleton-noop"); Phase 1 will process here
//   * cancellation requested -> no-op, no permanent diagnostic
public abstract class AiSkeletonBackfillJobHandler<TBackend> : IJobHandler
    where TBackend : class, IAiBackend
{
    private readonly IOptions<AiOptions> _options;
    private readonly IAiBackendResolver _resolver;
    private readonly IAiDiagnosticsWriter _diagnostics;

    protected AiSkeletonBackfillJobHandler(
        IOptions<AiOptions> options,
        IAiBackendResolver resolver,
        IAiDiagnosticsWriter diagnostics)
    {
        _options = options;
        _resolver = resolver;
        _diagnostics = diagnostics;
    }

    public abstract string JobType { get; }

    // The capability this job serves (see AiCapabilities).
    protected abstract string Capability { get; }

    // Whether the per-capability feature flag is on (see AiOptions).
    protected abstract bool CapabilityEnabled(AiOptions options);

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<AiBackfillJobPayload>(context.PayloadJson)
            ?? new AiBackfillJobPayload();
        var options = _options.Value;

        if (context.IsCancellationRequested)
        {
            // Cancellation is not a failure: no-op, no permanent diagnostic.
            await AiSkeletonJob.NoOpAsync(context, "cancelled");
            return;
        }

        if (!options.Enabled)
        {
            await AiSkeletonJob.NoOpAsync(context, "ai-disabled");
            return;
        }

        if (!CapabilityEnabled(options))
        {
            await AiSkeletonJob.NoOpAsync(context, "capability-disabled");
            return;
        }

        // Profile-driven resolution: by stable key if supplied, else the default
        // profile for this capability. The provider is decided by the profile's
        // model — never a global setting.
        var resolution = !string.IsNullOrWhiteSpace(payload.ProfileKey)
            ? await _resolver.ResolveForProfileKeyAsync<TBackend>(payload.ProfileKey!, cancellationToken)
            : await _resolver.ResolveForCapabilityAsync<TBackend>(Capability, cancellationToken);

        if (!resolution.IsAvailable)
        {
            var reason = resolution.Resolution.UnavailableReason ?? AiUnavailableReasons.ProviderUnavailable;
            // Environment/config state — record at most ONE aggregate transient
            // provider diagnostic. NEVER per-target skipped/failed rows.
            await _diagnostics.RecordProviderUnavailableAsync(Capability, profileId: null, reason, cancellationToken);
            await AiSkeletonJob.NoOpAsync(context, reason);
            return;
        }

        // A backend resolved (deterministic dev/test in Phase 0C). We still do
        // NOT touch files or write any embedding/status/annotation rows yet.
        await AiSkeletonJob.NoOpAsync(context, "skeleton-noop");
    }
}
