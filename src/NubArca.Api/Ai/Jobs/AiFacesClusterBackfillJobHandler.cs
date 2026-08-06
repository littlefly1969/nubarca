using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Diagnostics;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Jobs;

// Face clustering is a DERIVED, owner-scoped computation over face embeddings —
// not a backend capability — so it resolves a face-embedding profile rather than
// an IAiBackend. People v0: the REAL handler enumerates owners who have
// clusterable faces and rebuilds each owner's SUGGESTED clusters via
// FaceClusteringService (owner + profile scoped, Private-Vault excluded,
// user-confirmed/ignored preserved). Gated by FaceClusteringEnabled; sliceable
// (keyset over owners); idempotent. NEVER cross-owner.
public sealed class AiFacesClusterBackfillJobHandler : IJobHandler
{
    private const int OwnerPageSize = 25;

    private readonly IOptions<AiOptions> _options;
    private readonly IAiProfileRegistry _registry;
    private readonly IAiDiagnosticsWriter _diagnostics;
    private readonly AppDbContext _db;
    private readonly FaceClusteringService _clustering;
    private readonly IFaceSettingsProvider _settings;

    public AiFacesClusterBackfillJobHandler(
        IOptions<AiOptions> options,
        IAiProfileRegistry registry,
        IAiDiagnosticsWriter diagnostics,
        AppDbContext db,
        FaceClusteringService clustering,
        IFaceSettingsProvider settings)
    {
        _options = options;
        _registry = registry;
        _diagnostics = diagnostics;
        _db = db;
        _clustering = clustering;
        _settings = settings;
    }

    public string JobType => JobTypes.AiFacesClusterBackfill;

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

        if (!options.FaceClusteringEnabled)
        {
            await AiSkeletonJob.NoOpAsync(context, "capability-disabled");
            return;
        }

        // Same face-package profile discipline as detect/embed: payload > config >
        // face-embedding capability default.
        var key = !string.IsNullOrWhiteSpace(payload.ProfileKey)
            ? payload.ProfileKey
            : (!string.IsNullOrWhiteSpace(options.FaceProfileKey) ? options.FaceProfileKey : null);
        var profile = key is not null
            ? await _registry.GetProfileByKeyAsync(key!, cancellationToken)
            : await _registry.GetDefaultProfileAsync(AiCapabilities.FaceEmbedding, cancellationToken);

        if (profile is null)
        {
            await _diagnostics.RecordProviderUnavailableAsync(
                AiCapabilities.FaceClustering, profileId: null, AiUnavailableReasons.NoDefaultProfile, cancellationToken);
            await AiSkeletonJob.NoOpAsync(context, AiUnavailableReasons.NoDefaultProfile);
            return;
        }

        var settings = await _settings.GetAsync(cancellationToken);
        var checkpoint = FaceBackfillCheckpoint.TryParse(context.Checkpoint) ?? FaceBackfillCheckpoint.Initial;
        var cursor = checkpoint.CursorBlobId ?? Guid.Empty; // repurposed: last owner id
        var ownersTotal = checkpoint.ProcessedTotal;
        var groupsTotal = checkpoint.ProducedTotal;

        var ownersThisSlice = 0;
        var yielded = false;
        var exhausted = false;

        while (!exhausted && !yielded)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var owners = await (
                from f in _db.FileItems.AsNoTracking()
                join d in _db.FaceDetections.AsNoTracking() on f.BlobObjectId equals d.BlobObjectId
                where f.DeletedAt == null
                    && f.OwnerUserId > cursor
                    && d.ProfileId == profile.Id
                    && _db.FaceEmbeddings.Any(e => e.FaceDetectionId == d.Id && e.ProfileId == profile.Id)
                orderby f.OwnerUserId
                select f.OwnerUserId)
                .Distinct()
                .Take(OwnerPageSize)
                .ToListAsync(cancellationToken);

            if (owners.Count == 0)
            {
                exhausted = true;
                break;
            }

            foreach (var ownerId in owners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cursor = ownerId;
                ownersTotal++;
                ownersThisSlice++;

                var outcome = await _clustering.ClusterOwnerAsync(ownerId, profile, settings, context.Log, cancellationToken);
                groupsTotal += outcome.GroupsCreated;

                await context.ReportProgressAsync(
                    ownersTotal, null, $"clustering faces ({ownersTotal} owners, {groupsTotal} groups)", cancellationToken);

                if (context.ShouldYield(ownersThisSlice)) { yielded = true; break; }
            }

            if (!yielded && owners.Count < OwnerPageSize)
            {
                exhausted = true;
            }
        }

        if (yielded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = new FaceBackfillCheckpoint(
                FaceBackfillCheckpoint.CurrentVersion, cursor, ownersTotal, groupsTotal, 0, 0).Serialize();
            var reason = context.HigherPriorityWaiting ? JobYieldReasons.HigherPriority : JobYieldReasons.SliceBudget;
            context.RequestContinuation(reason, next);
        }
    }
}
