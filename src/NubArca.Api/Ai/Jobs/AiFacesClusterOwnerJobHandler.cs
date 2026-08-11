using System.Text.Json;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Diagnostics;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Jobs;

// ONE owner's automatic face groups, rebuilt because that owner asked for it.
//
// The algorithm is not here. This handler resolves a profile, reads the current
// face settings and makes exactly ONE call to FaceClusteringService — the same
// call the global backfill makes per owner — so "what clustering means" has one
// implementation and reclustering from the Cloud hub cannot drift from
// reclustering from the administration console.
//
// What makes it a different job from AiFacesClusterBackfill is the SCOPE, and
// that is the whole point: there is no owner enumeration here, no keyset over
// users, no `SELECT DISTINCT OwnerUserId`. One job is one owner. A defect that
// let this walk other people's faces would have to be written on purpose,
// because the only owner id it can see is the one in its payload, which only
// the enqueue endpoint writes and only from the authenticated caller.
//
// Confirmed people, ignored faces and the 1..6 reference templates are
// untouched: that is FaceClusteringService's existing contract, not something
// re-stated here.
public sealed class AiFacesClusterOwnerJobHandler : IJobHandler
{
    private readonly IOptions<AiOptions> _options;
    private readonly IAiProfileRegistry _registry;
    private readonly IAiDiagnosticsWriter _diagnostics;
    private readonly FaceClusteringService _clustering;
    private readonly IFaceSettingsProvider _settings;

    public AiFacesClusterOwnerJobHandler(
        IOptions<AiOptions> options,
        IAiProfileRegistry registry,
        IAiDiagnosticsWriter diagnostics,
        FaceClusteringService clustering,
        IFaceSettingsProvider settings)
    {
        _options = options;
        _registry = registry;
        _diagnostics = diagnostics;
        _clustering = clustering;
        _settings = settings;
    }

    public string JobType => JobTypes.AiFacesClusterOwner;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<FaceOwnerClusterJobPayload>(context.PayloadJson);
        if (payload is null || payload.OwnerUserId == Guid.Empty)
        {
            // A job with no owner has no correct scope to fall back to. Refusing
            // is the only safe answer: guessing would be inventing one.
            await AiSkeletonJob.NoOpAsync(context, "invalid-payload");
            return;
        }

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

        if (!options.FaceClusteringEnabled)
        {
            await AiSkeletonJob.NoOpAsync(context, "capability-disabled");
            return;
        }

        // The profile was resolved and pinned at enqueue time, so this is a
        // lookup by stable key and not a second resolution that could pick a
        // different model than the one the owner was told about.
        var profile = await _registry.GetProfileByKeyAsync(payload.ProfileKey, cancellationToken);
        if (profile is null)
        {
            await _diagnostics.RecordProviderUnavailableAsync(
                AiCapabilities.FaceClustering, profileId: null, AiUnavailableReasons.NoDefaultProfile, cancellationToken);
            await AiSkeletonJob.NoOpAsync(context, AiUnavailableReasons.NoDefaultProfile);
            return;
        }

        var settings = await _settings.GetAsync(cancellationToken);

        // One logical unit of work, so progress is 0/1 → 1/1 rather than a fake
        // subdivision of something the clustering service does not report on.
        await context.ReportProgressAsync(0, 1, "clustering faces", cancellationToken);

        var outcome = await _clustering.ClusterOwnerAsync(
            payload.OwnerUserId, profile, settings, context.Log, cancellationToken);

        // Counts only — never a face id, a person id, a file name or a vector.
        await context.ReportProgressAsync(
            1, 1,
            $"clustered {outcome.FacesConsidered} faces into {outcome.GroupsCreated} groups",
            cancellationToken);
    }
}
