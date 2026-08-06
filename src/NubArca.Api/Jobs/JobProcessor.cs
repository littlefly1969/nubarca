using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Jobs;

// Claims and runs background jobs. Scoped: one instance per worker iteration /
// per CLI run, resolving its handlers from the same DI scope.
//
// Concurrency model — conservative, dialect-agnostic (works on SQLite + PG):
// claiming is an atomic single-row UPDATE guarded by a status predicate
// (`ExecuteUpdateAsync ... WHERE Id = @id AND <eligible>`). Whichever worker's
// UPDATE affects the row wins; the loser sees 0 rows affected and moves on.
// This avoids PostgreSQL-specific `FOR UPDATE SKIP LOCKED` so SQLite tests pass
// unchanged. Jobs are processed one at a time (see JobsOptions.MaxConcurrentJobs).
//
// Lease + heartbeat (slice 89): a claimed job gets LeaseUntil = now + LeaseSeconds.
// While the handler runs, a background heartbeat (every HeartbeatSeconds) extends
// the lease, flushes the latest progress, and reads the cancellation flag. A
// running job becomes claimable again once LeaseUntil < now — so a CRASHED
// worker frees the job after a bounded delay, while a healthy long job keeps its
// lease alive. Every terminal write is guarded by `LockOwner == owner`, so a job
// reclaimed after lease expiry can never be double-completed by the old worker.
// The lease (LeaseUntil) is the single source of truth for reclaim.
public sealed class JobProcessor
{
    private const int ErrorMessageCap = 500;
    private const int ProgressMessageCap = 200;
    // Progress writes triggered by handlers are throttled to at most one per
    // this interval (the heartbeat + completion also flush the latest values),
    // so a tight handler loop collapses to a handful of DB updates.
    private static readonly TimeSpan ProgressThrottle = TimeSpan.FromSeconds(1);

    private readonly AppDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEnumerable<IJobHandler> _handlers;
    private readonly TimeProvider _clock;
    private readonly IOptions<JobsOptions> _options;
    private readonly ILogger<JobProcessor> _logger;

    public JobProcessor(
        AppDbContext db,
        IServiceScopeFactory scopeFactory,
        IEnumerable<IJobHandler> handlers,
        TimeProvider clock,
        IOptions<JobsOptions> options,
        ILogger<JobProcessor> logger)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _handlers = handlers;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    // Processes up to `maxJobs` available jobs, one at a time. Returns the number
    // processed. Stops early when no job is available.
    public async Task<int> ProcessAvailableAsync(
        int maxJobs, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var processed = 0;
        for (var i = 0; i < maxJobs; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = await ClaimNextAsync(cancellationToken);
            if (job is null)
            {
                break;
            }
            await ProcessOneAsync(job, log ?? (_ => { }), cancellationToken);
            processed++;
        }
        return processed;
    }

    // Atomically claims the next eligible job, transitioning it to `running`,
    // incrementing Attempts, and stamping a fresh lease. Returns null when
    // nothing is claimable.
    //
    // Scheduler v2 selection: normally the lowest base Priority wins (foreground
    // first). To prevent strict-priority starvation, a lower-priority job that
    // has waited >= StarvationGraceSeconds while higher-priority work kept being
    // chosen is promoted for ONE claim. Because a yielded job re-queues with
    // AvailableAt = now (its wait resets), this grants maintenance/cleanup an
    // occasional slice without letting it consistently outrank fresh foreground.
    public async Task<BackgroundJob?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        var now = _clock.GetUtcNow().UtcDateTime;
        var leaseUntil = now.AddSeconds(Math.Max(1, opts.LeaseSeconds));
        var owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

        const int poolSize = 25;

        // Pool A — highest-priority-first (ix_..._status_priority_available).
        var byPriority = await _db.BackgroundJobs
            .AsNoTracking()
            .Where(Eligible(now))
            .OrderBy(j => j.Priority).ThenBy(j => j.AvailableAt).ThenBy(j => j.CreatedAt)
            .Select(j => new Candidate(j.Id, j.Priority, j.AvailableAt))
            .Take(poolSize)
            .ToListAsync(cancellationToken);

        // Pool B — oldest-first (ix_..._status_available). Guarantees a starved
        // low-priority job is always considered even when pool A is full of
        // freshly-queued foreground jobs.
        var byAge = await _db.BackgroundJobs
            .AsNoTracking()
            .Where(Eligible(now))
            .OrderBy(j => j.AvailableAt).ThenBy(j => j.CreatedAt)
            .Select(j => new Candidate(j.Id, j.Priority, j.AvailableAt))
            .Take(poolSize)
            .ToListAsync(cancellationToken);

        var pool = byPriority.Concat(byAge)
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .ToList();
        if (pool.Count == 0)
        {
            return null;
        }

        foreach (var candidate in SelectOrder(pool, now, opts.StarvationGraceSeconds))
        {
            var rows = await _db.BackgroundJobs
                .Where(j => j.Id == candidate.Id
                    && ((j.Status == JobStatuses.Queued && j.AvailableAt <= now)
                        || (j.Status == JobStatuses.Running && j.LeaseUntil != null && j.LeaseUntil < now)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatuses.Running)
                    .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                    .SetProperty(j => j.StartedAt, now)
                    .SetProperty(j => j.LockOwner, owner)
                    .SetProperty(j => j.LeaseUntil, leaseUntil)
                    .SetProperty(j => j.HeartbeatAt, now)
                    .SetProperty(j => j.UpdatedAt, now),
                    cancellationToken);

            if (rows == 1)
            {
                return await _db.BackgroundJobs
                    .AsNoTracking()
                    .FirstAsync(j => j.Id == candidate.Id, cancellationToken);
            }
            // rows == 0: another worker claimed it first; try the next candidate.
        }

        return null;
    }

    // Eligible: queued + available, OR running with an EXPIRED lease
    // (LeaseUntil < now) — a crashed worker's job becomes claimable again.
    private static System.Linq.Expressions.Expression<Func<BackgroundJob, bool>> Eligible(DateTime now)
        => j => (j.Status == JobStatuses.Queued && j.AvailableAt <= now)
            || (j.Status == JobStatuses.Running && j.LeaseUntil != null && j.LeaseUntil < now);

    private readonly record struct Candidate(Guid Id, int Priority, DateTime AvailableAt);

    // Orders the candidate pool for claiming. Lowest base Priority first; but if
    // a lower-priority candidate has waited >= grace, promote the most-starved
    // one to the front (anti-starvation). The rest stay as fallback so a claim
    // race on the promoted job falls through gracefully.
    private static IReadOnlyList<Candidate> SelectOrder(
        List<Candidate> pool, DateTime now, int starvationGraceSeconds)
    {
        var ordered = pool
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.AvailableAt)
            .ThenBy(c => c.Id) // stable, deterministic tie-break
            .ToList();

        if (starvationGraceSeconds <= 0)
        {
            return ordered;
        }

        var best = ordered[0];
        var graceCutoff = now.AddSeconds(-starvationGraceSeconds);
        var starved = pool
            .Where(c => c.Priority > best.Priority && c.AvailableAt <= graceCutoff)
            .OrderBy(c => c.AvailableAt)
            .ThenBy(c => c.Priority)
            .Select(c => (Candidate?)c)
            .FirstOrDefault();

        if (starved is Candidate s)
        {
            ordered.RemoveAll(c => c.Id == s.Id);
            ordered.Insert(0, s);
        }

        return ordered;
    }

    private async Task ProcessOneAsync(
        BackgroundJob job, Action<string> log, CancellationToken cancellationToken)
    {
        var owner = job.LockOwner ?? string.Empty;

        var handler = _handlers.FirstOrDefault(h => h.JobType == job.Type);
        if (handler is null)
        {
            _logger.LogWarning("Job {JobId} has no handler for type {JobType}; failing permanently.",
                job.Id, job.Type);
            await FailAsync(job.Id, owner, "UnknownJobType", $"No handler registered for '{job.Type}'.",
                permanent: true, cancellationToken);
            return;
        }

        // Cancellation requested before we started running it → finish as
        // cancelled without invoking the handler.
        if (job.CancellationRequested)
        {
            await MarkCancelledAsync(job.Id, owner, null, null, null, cancellationToken);
            return;
        }

        // linkedCts fires on worker shutdown OR cooperative cancellation OR a
        // lost lease (heartbeat observed another owner) — the handler stops in
        // all three cases; the outcome is disambiguated below.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Shared latest-progress state, guarded so the handler thread (progress
        // reports) and the heartbeat timer never touch the lease context at once.
        var gate = new SemaphoreSlim(1, 1);
        int? latestCurrent = null, latestTotal = null;
        string? latestMessage = null;
        DateTime? lastProgressWrite = null;

        // A dedicated context for lease/heartbeat/progress writes so we never
        // use the handler's (shared, scoped) AppDbContext concurrently.
        using var leaseScope = _scopeFactory.CreateScope();
        var leaseDb = leaseScope.ServiceProvider.GetRequiredService<AppDbContext>();

        async Task PersistProgressAsync(int? cur, int? tot, string? msg, bool force, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (cur.HasValue) latestCurrent = cur;
                if (tot.HasValue) latestTotal = tot;
                if (msg is not null) latestMessage = Truncate(msg, ProgressMessageCap);

                var now = _clock.GetUtcNow().UtcDateTime;
                if (!force && lastProgressWrite is DateTime last && now - last < ProgressThrottle)
                {
                    return; // throttled; the heartbeat/completion will flush it
                }
                lastProgressWrite = now;
                await WriteProgressAsync(leaseDb, job.Id, owner, latestCurrent, latestTotal, latestMessage, now, ct);
            }
            finally
            {
                gate.Release();
            }
        }

        // Scheduler v2: per-slice budget. Foreground jobs self-pace and are
        // never preempted (unbounded budget); maintenance and lower run in
        // bounded slices so a queued foreground import waits at most one slice.
        var sliceStart = _clock.GetUtcNow();
        DateTimeOffset? sliceDeadline = JobScheduling.IsForeground(job.Priority)
            ? null
            : sliceStart.AddSeconds(Math.Max(1, _options.Value.MaintenanceSliceSeconds));
        int? sliceItemBudget = JobScheduling.IsForeground(job.Priority)
            ? null
            : Math.Max(1, _options.Value.MaintenanceSliceItemBudget);

        var context = new JobContext(
            job.Id, job.PayloadJson, log, linkedCts.Token,
            (cur, tot, msg, ct) => PersistProgressAsync(cur, tot, msg, force: false, ct),
            _clock, job.Priority, job.CheckpointJson, job.SliceNumber, sliceDeadline, sliceItemBudget);

        // Heartbeat loop: own CTS so we can stop it the instant the handler ends.
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
        var heartbeatTask = RunHeartbeatLoopAsync(
            leaseDb, context, job.Id, owner, job.Priority, gate,
            () => (latestCurrent, latestTotal, latestMessage),
            linkedCts, heartbeatCts.Token);

        var shutdown = false;
        var lostLease = false;
        var cancelledCoop = false;
        Exception? failure = null;

        try
        {
            await handler.ExecuteAsync(context, linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                shutdown = true; // worker stopping → leave the job running; its lease expires
            }
            else if (await IsCancellationRequestedAsync(job.Id))
            {
                cancelledCoop = true;
            }
            else if (linkedCts.IsCancellationRequested)
            {
                lostLease = true; // heartbeat observed another owner; abandon quietly
            }
            else
            {
                failure = new OperationCanceledException("Handler cancelled unexpectedly.");
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { /* heartbeat shutdown is best-effort */ }
        }

        if (shutdown)
        {
            // Don't write a terminal state; the lease will expire and another
            // worker (or the next poll) reclaims the job.
            throw new OperationCanceledException(cancellationToken);
        }
        if (lostLease)
        {
            return; // the reclaiming worker is now authoritative for this job
        }

        if (cancelledCoop)
        {
            _logger.LogInformation("Job {JobId} ({JobType}) cancelled cooperatively.", job.Id, job.Type);
            await MarkCancelledAsync(job.Id, owner, latestCurrent, latestTotal, latestMessage, cancellationToken);
            return;
        }

        if (failure is not null)
        {
            var code = failure.GetType().Name;
            var message = Truncate(failure.Message, ErrorMessageCap);
            var permanent = job.Attempts >= job.MaxAttempts;
            _logger.LogWarning(
                "Job {JobId} ({JobType}) attempt {Attempt}/{Max} failed: {Code}. {Outcome}",
                job.Id, job.Type, job.Attempts, job.MaxAttempts, code,
                permanent ? "No retries left; marking failed." : "Will retry.");
            await FailAsync(job.Id, owner, code, message, permanent, cancellationToken);
            return;
        }

        var now2 = _clock.GetUtcNow().UtcDateTime;

        // Cooperative slicing: the handler stopped at a safe checkpoint with
        // more work remaining → re-queue THIS row as the next slice instead of
        // completing. One logical job stays one row.
        if (context.ContinuationRequested)
        {
            await ContinueAsync(job, owner, context, latestCurrent, latestTotal, latestMessage, now2, cancellationToken);
            return;
        }

        // Success — flush final progress and mark succeeded (owner-guarded).
        // The checkpoint is internal resume state, meaningless once complete, so
        // it is cleared here.
        await _db.BackgroundJobs
            .Where(j => j.Id == job.Id && j.LockOwner == owner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatuses.Succeeded)
                .SetProperty(j => j.CompletedAt, now2)
                .SetProperty(j => j.LockOwner, (string?)null)
                .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                .SetProperty(j => j.CheckpointJson, (string?)null)
                .SetProperty(j => j.LastErrorCode, (string?)null)
                .SetProperty(j => j.LastErrorMessage, (string?)null)
                .SetProperty(j => j.ProgressCurrent, latestCurrent)
                .SetProperty(j => j.ProgressTotal, latestTotal)
                .SetProperty(j => j.ProgressMessage, latestMessage)
                .SetProperty(j => j.UpdatedAt, now2),
                cancellationToken);
    }

    // Re-queues a yielded job as its next slice (owner-guarded). Status →
    // queued, AvailableAt = now + ContinuationDelay (so a waiting equal-priority
    // peer still goes first), SliceNumber++, CheckpointJson persisted, Attempts
    // reset to 0 (a checkpoint is forward progress, not a retry — each slice
    // gets a fresh retry budget). A MaxSlicesPerJob backstop force-completes a
    // livelooping job (remaining work is left for a future enqueue).
    private async Task ContinueAsync(
        BackgroundJob job, string owner, JobContext context,
        int? cur, int? tot, string? msg, DateTime now, CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        var nextSlice = job.SliceNumber + 1;

        if (opts.MaxSlicesPerJob > 0 && nextSlice >= opts.MaxSlicesPerJob)
        {
            _logger.LogWarning(
                "Job {JobId} ({JobType}) hit MaxSlicesPerJob={Max}; completing with work possibly remaining.",
                job.Id, job.Type, opts.MaxSlicesPerJob);
            await _db.BackgroundJobs
                .Where(j => j.Id == job.Id && j.LockOwner == owner)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatuses.Succeeded)
                    .SetProperty(j => j.CompletedAt, now)
                    .SetProperty(j => j.LockOwner, (string?)null)
                    .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                    .SetProperty(j => j.YieldReason, JobYieldReasons.MaxSlices)
                    .SetProperty(j => j.ProgressCurrent, cur)
                    .SetProperty(j => j.ProgressTotal, tot)
                    .SetProperty(j => j.ProgressMessage, msg)
                    .SetProperty(j => j.UpdatedAt, now),
                    cancellationToken);
            return;
        }

        var availableAt = now.AddSeconds(Math.Max(0, opts.ContinuationDelaySeconds));
        var reason = Truncate(context.ContinuationReason ?? JobYieldReasons.SliceBudget, 40);
        var checkpoint = context.ContinuationCheckpoint;
        await _db.BackgroundJobs
            .Where(j => j.Id == job.Id && j.LockOwner == owner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatuses.Queued)
                .SetProperty(j => j.AvailableAt, availableAt)
                .SetProperty(j => j.SliceNumber, nextSlice)
                .SetProperty(j => j.CheckpointJson, checkpoint)
                .SetProperty(j => j.YieldReason, reason)
                .SetProperty(j => j.Attempts, 0)
                .SetProperty(j => j.LockOwner, (string?)null)
                .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                .SetProperty(j => j.LastErrorCode, (string?)null)
                .SetProperty(j => j.LastErrorMessage, (string?)null)
                .SetProperty(j => j.ProgressCurrent, cur)
                .SetProperty(j => j.ProgressTotal, tot)
                .SetProperty(j => j.ProgressMessage, msg)
                .SetProperty(j => j.UpdatedAt, now),
                cancellationToken);
    }

    // Periodically extends the lease, flushes the latest progress, and reads the
    // cancellation flag. Runs on its OWN context (leaseDb) and is fully gated, so
    // it never collides with the handler's DbContext or with progress writes.
    private async Task RunHeartbeatLoopAsync(
        AppDbContext leaseDb,
        JobContext context,
        Guid jobId,
        string owner,
        int jobPriority,
        SemaphoreSlim gate,
        Func<(int?, int?, string?)> latest,
        CancellationTokenSource linkedCts,
        CancellationToken heartbeatToken)
    {
        var opts = _options.Value;
        var interval = TimeSpan.FromSeconds(Math.Clamp(opts.HeartbeatSeconds, 1, Math.Max(1, opts.LeaseSeconds - 1)));

        while (!heartbeatToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, heartbeatToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await gate.WaitAsync(heartbeatToken);
                try
                {
                    var now = _clock.GetUtcNow().UtcDateTime;
                    var leaseUntil = now.AddSeconds(Math.Max(1, opts.LeaseSeconds));
                    var (cur, tot, msg) = latest();

                    var rows = await leaseDb.BackgroundJobs
                        .Where(j => j.Id == jobId && j.LockOwner == owner && j.Status == JobStatuses.Running)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.HeartbeatAt, now)
                            .SetProperty(j => j.LeaseUntil, leaseUntil)
                            .SetProperty(j => j.ProgressCurrent, cur)
                            .SetProperty(j => j.ProgressTotal, tot)
                            .SetProperty(j => j.ProgressMessage, msg)
                            .SetProperty(j => j.UpdatedAt, now),
                            heartbeatToken);

                    if (rows == 0)
                    {
                        // We no longer own the job (reclaimed after a missed
                        // heartbeat, or it was finished). Stop the handler.
                        linkedCts.Cancel();
                        break;
                    }

                    var cancelRequested = await leaseDb.BackgroundJobs
                        .AsNoTracking()
                        .Where(j => j.Id == jobId)
                        .Select(j => j.CancellationRequested)
                        .FirstOrDefaultAsync(heartbeatToken);
                    if (cancelRequested)
                    {
                        context.MarkCancellationRequested();
                        linkedCts.Cancel();
                        break;
                    }

                    // Scheduler v2: best-effort early yield. If a higher-priority
                    // job is now waiting, ask a sliceable handler to checkpoint
                    // and yield before its full slice budget. The slice budget is
                    // still the hard guarantee; foreground jobs never yield here.
                    if (!JobScheduling.IsForeground(jobPriority))
                    {
                        var higherWaiting = await leaseDb.BackgroundJobs
                            .AsNoTracking()
                            .AnyAsync(j => j.Status == JobStatuses.Queued
                                && j.AvailableAt <= now
                                && j.Priority < jobPriority, heartbeatToken);
                        if (higherWaiting)
                        {
                            context.MarkHigherPriorityWaiting();
                        }
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A transient heartbeat failure must not kill the running job;
                // try again next tick. Never logs payloads/keys.
                _logger.LogWarning("Job {JobId} heartbeat failed: {Code}.", jobId, ex.GetType().Name);
            }
        }
    }

    // Public, testable heartbeat: extends the lease + stamps HeartbeatAt for an
    // owned running job. Returns false if the caller no longer owns it.
    public async Task<bool> HeartbeatAsync(
        Guid jobId, string owner, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var leaseUntil = now.AddSeconds(Math.Max(1, _options.Value.LeaseSeconds));
        var rows = await _db.BackgroundJobs
            .Where(j => j.Id == jobId && j.LockOwner == owner && j.Status == JobStatuses.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.HeartbeatAt, now)
                .SetProperty(j => j.LeaseUntil, leaseUntil)
                .SetProperty(j => j.UpdatedAt, now),
                cancellationToken);
        return rows == 1;
    }

    private static async Task WriteProgressAsync(
        AppDbContext db, Guid jobId, string owner,
        int? cur, int? tot, string? msg, DateTime now, CancellationToken ct)
    {
        await db.BackgroundJobs
            .Where(j => j.Id == jobId && j.LockOwner == owner && j.Status == JobStatuses.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.ProgressCurrent, cur)
                .SetProperty(j => j.ProgressTotal, tot)
                .SetProperty(j => j.ProgressMessage, msg)
                .SetProperty(j => j.UpdatedAt, now),
                ct);
    }

    private async Task<bool> IsCancellationRequestedAsync(Guid jobId)
        => await _db.BackgroundJobs
            .AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => j.CancellationRequested)
            .FirstOrDefaultAsync(CancellationToken.None);

    private async Task MarkCancelledAsync(
        Guid jobId, string owner, int? cur, int? tot, string? msg, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await _db.BackgroundJobs
            .Where(j => j.Id == jobId && j.LockOwner == owner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatuses.Cancelled)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.LockOwner, (string?)null)
                .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                .SetProperty(j => j.ProgressCurrent, cur)
                .SetProperty(j => j.ProgressTotal, tot)
                .SetProperty(j => j.ProgressMessage, msg)
                .SetProperty(j => j.UpdatedAt, now),
                cancellationToken);
    }

    private async Task FailAsync(
        Guid jobId, string owner, string code, string message, bool permanent, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        if (permanent)
        {
            await _db.BackgroundJobs
                .Where(j => j.Id == jobId && j.LockOwner == owner)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatuses.Failed)
                    .SetProperty(j => j.CompletedAt, now)
                    .SetProperty(j => j.LockOwner, (string?)null)
                    .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                    .SetProperty(j => j.LastErrorCode, code)
                    .SetProperty(j => j.LastErrorMessage, message)
                    .SetProperty(j => j.UpdatedAt, now),
                    cancellationToken);
        }
        else
        {
            var availableAt = now.AddSeconds(Math.Max(1, _options.Value.RetryDelaySeconds));
            await _db.BackgroundJobs
                .Where(j => j.Id == jobId && j.LockOwner == owner)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatuses.Queued)
                    .SetProperty(j => j.AvailableAt, availableAt)
                    .SetProperty(j => j.LockOwner, (string?)null)
                    .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                    .SetProperty(j => j.LastErrorCode, code)
                    .SetProperty(j => j.LastErrorMessage, message)
                    .SetProperty(j => j.UpdatedAt, now),
                    cancellationToken);
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
