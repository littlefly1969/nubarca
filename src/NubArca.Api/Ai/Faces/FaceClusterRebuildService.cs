using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Faces;

// The owner-facing half of "rebuild MY automatic face groups": deciding whether
// the request can be honoured, starting exactly one job for the caller, and
// letting that same caller — and nobody else — watch it finish.
//
// Two boundaries live here and nowhere else.
//
// OWNER. The owner id is a parameter the endpoint fills from the authenticated
// principal; it is never read from a request body, and there is no overload
// that takes one from a client. The status read re-derives ownership from the
// JOB'S OWN payload rather than from a table of who asked, so a job can only
// ever be seen by the account it clusters.
//
// AUTHORITY. Nothing here needs admin.jobs.manage. A user watching their own
// recluster must not be handed the administration job console to do it, so this
// returns a narrow, safe projection of one job and refuses to answer for any
// job of another type — including the global backfill, whose id is not a secret
// but whose progress ("42 owners") is not this user's business.
public sealed class FaceClusterRebuildService
{
    private readonly AppDbContext _db;
    private readonly IJobQueue _jobs;
    private readonly IAiProfileRegistry _registry;
    private readonly IOptions<AiOptions> _options;

    public FaceClusterRebuildService(
        AppDbContext db,
        IJobQueue jobs,
        IAiProfileRegistry registry,
        IOptions<AiOptions> options)
    {
        _db = db;
        _jobs = jobs;
        _registry = registry;
        _options = options;
    }

    // Start (or join) this owner's recluster.
    //
    // Returns null with a reason when the installation cannot cluster at all —
    // AI off, face clustering off, or no face-embedding profile. Queueing a job
    // that is certain to no-op would be worse than refusing: the owner would
    // watch a progress state that means nothing and end at "completed" with the
    // groups unchanged.
    public async Task<FaceClusterRebuildStart> StartAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return FaceClusterRebuildStart.Unavailable(AiUnavailableReasons.Disabled);
        }
        if (!options.FaceClusteringEnabled)
        {
            return FaceClusterRebuildStart.Unavailable(AiUnavailableReasons.CapabilityUnsupported);
        }

        // Same profile discipline as the global backfill: configured face
        // profile first, else the face-embedding capability default. Resolved
        // HERE so the pinned key travels in the payload and the run cannot
        // silently change model between enqueue and execution.
        var configured = options.FaceProfileKey;
        var profile = !string.IsNullOrWhiteSpace(configured)
            ? await _registry.GetProfileByKeyAsync(configured!, cancellationToken)
            : await _registry.GetDefaultProfileAsync(AiCapabilities.FaceEmbedding, cancellationToken);
        if (profile is null)
        {
            return FaceClusterRebuildStart.Unavailable(AiUnavailableReasons.NoDefaultProfile);
        }

        var key = IdempotencyKeyFor(ownerUserId, profile.Key);

        // Asked before enqueueing so a second click can SAY it joined the first
        // run rather than silently returning the same id as if it had started
        // one. (Two genuinely simultaneous requests can both read null here and
        // both report alreadyQueued=false — they still converge on ONE job,
        // because the queue's idempotency key is enforced by a unique index.)
        var existing = await FindLiveAsync(key, cancellationToken);
        if (existing is not null)
        {
            return FaceClusterRebuildStart.Queued(existing.Id, existing.Status, alreadyQueued: true);
        }

        var job = await _jobs.EnqueueAsync(
            JobTypes.AiFacesClusterOwner,
            new FaceOwnerClusterJobPayload(ownerUserId, profile.Key),
            idempotencyKey: key,
            cancellationToken: cancellationToken);

        return FaceClusterRebuildStart.Queued(job.Id, job.Status, alreadyQueued: false);
    }

    // This owner's view of one recluster job. Null = the job does not exist, is
    // not a recluster, or belongs to somebody else — all the same answer on
    // purpose, so a 404 cannot be used to discover that a job id is real.
    public async Task<FaceClusterRebuildStatus?> GetStatusAsync(
        Guid ownerUserId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Id == jobId && j.Type == JobTypes.AiFacesClusterOwner)
            .Select(j => new
            {
                j.Id, j.Status, j.PayloadJson, j.ProgressCurrent, j.ProgressTotal,
                j.ProgressMessage, j.CreatedAt, j.CompletedAt, j.LastErrorCode,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            return null;
        }

        FaceOwnerClusterJobPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<FaceOwnerClusterJobPayload>(job.PayloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
        if (payload is null || payload.OwnerUserId != ownerUserId)
        {
            return null;
        }

        // Everything the panel needs and nothing else. PayloadJson, OwnerUserId,
        // LockOwner (a worker hostname), the checkpoint and the profile key all
        // stay on this side of the boundary.
        return new FaceClusterRebuildStatus(
            job.Id,
            job.Status,
            job.ProgressCurrent,
            job.ProgressTotal,
            job.ProgressMessage,
            job.CreatedAt,
            job.CompletedAt,
            job.LastErrorCode);
    }

    // Deterministic, so the SAME owner reclustering the SAME profile joins the
    // run already in flight instead of stacking a second one behind it. Terminal
    // jobs do not participate, so a finished run never blocks the next request.
    internal static string IdempotencyKeyFor(Guid ownerUserId, string profileKey) =>
        $"{JobTypes.AiFacesClusterOwner}:{ownerUserId:N}:{profileKey}";

    private Task<Domain.BackgroundJob?> FindLiveAsync(string key, CancellationToken cancellationToken) =>
        _db.BackgroundJobs.AsNoTracking()
            .Where(j => j.IdempotencyKey == key
                && (j.Status == JobStatuses.Queued || j.Status == JobStatuses.Running))
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
}

// The outcome of asking for a recluster. `UnavailableReason` is a stable token
// from AiUnavailableReasons — a machine word for the UI to translate, never a
// message and never an exception.
public sealed record FaceClusterRebuildStart(
    bool Accepted,
    Guid JobId,
    string? Status,
    bool AlreadyQueued,
    string? UnavailableReason)
{
    public static FaceClusterRebuildStart Queued(Guid jobId, string status, bool alreadyQueued) =>
        new(true, jobId, status, alreadyQueued, null);

    public static FaceClusterRebuildStart Unavailable(string reason) =>
        new(false, Guid.Empty, null, false, reason);
}

// Owner-safe job projection: counts, a handler-authored phase message, and the
// lifecycle timestamps. No payload, no owner id, no worker identity.
public sealed record FaceClusterRebuildStatus(
    Guid JobId,
    string Status,
    int? ProgressCurrent,
    int? ProgressTotal,
    string? ProgressMessage,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? LastErrorCode);
