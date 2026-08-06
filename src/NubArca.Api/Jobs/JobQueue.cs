using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Jobs;

public sealed class JobQueue : IJobQueue
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IOptions<JobsOptions> _options;

    public JobQueue(AppDbContext db, TimeProvider clock, IOptions<JobsOptions> options)
    {
        _db = db;
        _clock = clock;
        _options = options;
    }

    public async Task<BackgroundJob> EnqueueAsync<TPayload>(
        string type,
        TPayload payload,
        int? maxAttempts = null,
        int? priority = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        // Default priority comes from the scheduler registry for the job type
        // (foreground for imports, maintenance for backfills) unless the caller
        // pins one explicitly.
        var resolvedPriority = priority ?? JobScheduling.DefaultPriorityFor(type);
        // Idempotent enqueue: if a not-yet-finished job with the same key
        // exists, return it rather than creating a duplicate.
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var existing = await _db.BackgroundJobs
                .Where(j => j.IdempotencyKey == idempotencyKey
                    && (j.Status == JobStatuses.Queued || j.Status == JobStatuses.Running))
                .OrderBy(j => j.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                return existing;
            }
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var job = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            Type = type,
            Status = JobStatuses.Queued,
            // Serialize by the RUNTIME type: identical output for the existing
            // concrete-record callers (runtime type == TPayload), but correct
            // for a caller that passes a payload typed as `object` (the admin
            // console's catalogued commands) — otherwise System.Text.Json would
            // emit "{}" for the statically-object payload.
            PayloadJson = payload is null
                ? "null"
                : JsonSerializer.Serialize(payload, payload.GetType()),
            Attempts = 0,
            MaxAttempts = maxAttempts ?? _options.Value.DefaultMaxAttempts,
            Priority = resolvedPriority,
            CreatedAt = now,
            AvailableAt = now,
            IdempotencyKey = idempotencyKey,
            UpdatedAt = now,
        };

        _db.BackgroundJobs.Add(job);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (!string.IsNullOrEmpty(idempotencyKey))
        {
            // Lost a race against a concurrent LIVE enqueue with the same key
            // (partial unique index). Return the active winner; terminal rows
            // deliberately do not participate and can never win this race.
            _db.Entry(job).State = EntityState.Detached;
            var winner = await _db.BackgroundJobs
                .Where(j => j.IdempotencyKey == idempotencyKey
                    && (j.Status == JobStatuses.Queued || j.Status == JobStatuses.Running))
                .OrderBy(j => j.CreatedAt)
                .FirstAsync(cancellationToken);
            return winner;
        }

        return job;
    }

    public async Task<JobQueueSnapshot> GetSnapshotAsync(
        int recentLimit = 20, CancellationToken cancellationToken = default)
    {
        var counts = await _db.BackgroundJobs
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int CountFor(string status) =>
            counts.FirstOrDefault(c => c.Status == status)?.Count ?? 0;

        var recent = await _db.BackgroundJobs
            .OrderByDescending(j => j.CreatedAt)
            .Take(Math.Clamp(recentLimit, 1, 200))
            .Select(j => new JobSummary(
                j.Id, j.Type, j.Status, j.Attempts, j.MaxAttempts,
                j.CreatedAt, j.CompletedAt, j.LastErrorCode,
                j.ProgressCurrent, j.ProgressTotal, j.ProgressMessage,
                j.CancellationRequested))
            .ToListAsync(cancellationToken);

        return new JobQueueSnapshot(
            CountFor(JobStatuses.Queued),
            CountFor(JobStatuses.Running),
            CountFor(JobStatuses.Succeeded),
            CountFor(JobStatuses.Failed),
            CountFor(JobStatuses.Cancelled),
            recent);
    }

    public async Task<bool> RequestCancellationAsync(
        Guid jobId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        // Only non-terminal jobs can be cancelled; terminal rows are untouched.
        var rows = await _db.BackgroundJobs
            .Where(j => j.Id == jobId
                && (j.Status == JobStatuses.Queued || j.Status == JobStatuses.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.CancellationRequested, true)
                .SetProperty(j => j.UpdatedAt, now),
                cancellationToken);
        return rows > 0;
    }

    public async Task<AdminJobPage> ListAdminJobsAsync(
        AdminJobFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.BackgroundJobs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            query = query.Where(j => j.Status == filter.Status);
        }
        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            query = query.Where(j => j.Type == filter.Type);
        }

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            // Newest first; Id tie-break keeps paging stable across equal timestamps.
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ToSummary)
            .ToListAsync(cancellationToken);

        // Status counters are always over the WHOLE table (not the filtered
        // page) so the dashboard header is stable as filters change.
        var counts = await _db.BackgroundJobs
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        int CountFor(string s) => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0;

        return new AdminJobPage(
            items, page, pageSize, total,
            new JobStatusCounts(
                CountFor(JobStatuses.Queued),
                CountFor(JobStatuses.Running),
                CountFor(JobStatuses.Succeeded),
                CountFor(JobStatuses.Failed),
                CountFor(JobStatuses.Cancelled)));
    }

    public async Task<AdminJobSummary?> GetAdminJobAsync(
        Guid jobId, CancellationToken cancellationToken = default)
        => await _db.BackgroundJobs
            .AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(ToSummary)
            .FirstOrDefaultAsync(cancellationToken);

    // Single safe projection reused by list + detail. Defined as an expression
    // so it translates to SQL (the SELECT never materializes PayloadJson or
    // LockOwner).
    private static readonly System.Linq.Expressions.Expression<Func<BackgroundJob, AdminJobSummary>> ToSummary =
        j => new AdminJobSummary(
            j.Id, j.Type, j.Status, j.Priority, j.Attempts, j.MaxAttempts,
            j.CreatedAt, j.AvailableAt, j.StartedAt, j.CompletedAt, j.UpdatedAt,
            j.LeaseUntil, j.HeartbeatAt, j.CancellationRequested,
            j.ProgressCurrent, j.ProgressTotal, j.ProgressMessage,
            j.LastErrorCode, j.LastErrorMessage,
            j.SliceNumber, j.YieldReason);
}
