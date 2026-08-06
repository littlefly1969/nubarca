using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Jobs;

// Slice 70 — queue + processor mechanics. Self-contained SQLite harness with a
// mutable clock so retry/stale timing is deterministic. Uses test handlers
// (spy / always-fail) so the mechanics are isolated from the real backfill
// services (those are exercised end-to-end in JobsCliTests).
public sealed class BackgroundJobsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ServiceProvider _serviceProvider;
    private readonly MutableTimeProvider _clock;
    private readonly JobsOptions _options;

    public BackgroundJobsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _clock = new MutableTimeProvider(new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero));
        // Long lease/heartbeat so the background heartbeat timer never fires
        // during these millisecond-scale tests; lease/heartbeat mechanics are
        // verified directly via HeartbeatAsync + seeded expired-lease rows.
        _options = new JobsOptions
        {
            RetryDelaySeconds = 60,
            LeaseSeconds = 600,
            HeartbeatSeconds = 300,
        };
        // The processor creates a fresh AppDbContext (on the same shared in-
        // memory connection) for lease/heartbeat/progress writes.
        _serviceProvider = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private IServiceScopeFactory ScopeFactory => _serviceProvider.GetRequiredService<IServiceScopeFactory>();

    private JobQueue Queue() => new(_db, _clock, Options.Create(_options));

    private JobProcessor Processor(params IJobHandler[] handlers)
        => new(_db, ScopeFactory, handlers, _clock, Options.Create(_options),
            NullLogger<JobProcessor>.Instance);

    // ---- enqueue ----------------------------------------------------------

    [Fact]
    public async Task Enqueue_Creates_Queued_Row()
    {
        var job = await Queue().EnqueueAsync(
            JobTypes.MetadataEmbeddedBackfill, new MetadataBackfillJobPayload(Limit: 5));

        var row = await _db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatuses.Queued, row.Status);
        Assert.Equal(JobTypes.MetadataEmbeddedBackfill, row.Type);
        Assert.Equal(0, row.Attempts);
        Assert.Contains("\"Limit\":5", row.PayloadJson);
    }

    [Fact]
    public async Task Enqueue_With_Idempotency_Key_Dedups()
    {
        var q = Queue();
        var a = await q.EnqueueAsync(JobTypes.StorageReconcile,
            new StorageReconcileJobPayload(), idempotencyKey: "recon-nightly");
        var b = await q.EnqueueAsync(JobTypes.StorageReconcile,
            new StorageReconcileJobPayload(), idempotencyKey: "recon-nightly");

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(1, await _db.BackgroundJobs.CountAsync());
    }

    [Theory]
    [InlineData(JobStatuses.Succeeded)]
    [InlineData(JobStatuses.Failed)]
    [InlineData(JobStatuses.Cancelled)]
    public async Task Enqueue_With_Idempotency_Key_Creates_New_Job_After_Terminal(
        string terminalStatus)
    {
        var q = Queue();
        var first = await q.EnqueueAsync(JobTypes.StorageReconcile,
            new StorageReconcileJobPayload(), idempotencyKey: "recon-repeatable");
        first.Status = terminalStatus;
        first.CompletedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync();

        var second = await q.EnqueueAsync(JobTypes.StorageReconcile,
            new StorageReconcileJobPayload(), idempotencyKey: "recon-repeatable");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(JobStatuses.Queued, second.Status);
        Assert.Equal(2, await _db.BackgroundJobs.CountAsync(j =>
            j.IdempotencyKey == "recon-repeatable"));
    }

    [Fact]
    public async Task Payload_Contains_Only_Flags_No_Sensitive_Fields()
    {
        var job = await Queue().EnqueueAsync(
            JobTypes.MediaDerivativesBackfill,
            new MediaDerivativesBackfillJobPayload(Limit: 10, DryRun: true));

        var row = await _db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, row.PayloadJson, StringComparison.OrdinalIgnoreCase);
        }
        // Sanity: payload is just the documented flags.
        Assert.Contains("\"Limit\":10", row.PayloadJson);
        Assert.Contains("\"DryRun\":true", row.PayloadJson);
    }

    // ---- processing -------------------------------------------------------

    [Fact]
    public async Task RunOnce_Processes_Queued_Job_And_Marks_Succeeded()
    {
        await Queue().EnqueueAsync(JobTypes.MetadataEmbeddedBackfill,
            new MetadataBackfillJobPayload());
        var spy = new SpyHandler(JobTypes.MetadataEmbeddedBackfill);

        var processed = await Processor(spy).ProcessAvailableAsync(10);

        Assert.Equal(1, processed);
        Assert.Equal(1, spy.Calls);
        var row = await _db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.Succeeded, row.Status);
        Assert.NotNull(row.CompletedAt);
        Assert.Null(row.LastErrorCode);
        Assert.Equal(1, row.Attempts);
    }

    [Fact]
    public async Task Failing_Job_Retries_Until_MaxAttempts_Then_Failed()
    {
        await Queue().EnqueueAsync(JobTypes.MetadataEmbeddedBackfill,
            new MetadataBackfillJobPayload(), maxAttempts: 2);
        var fail = new AlwaysFailHandler(JobTypes.MetadataEmbeddedBackfill);
        var processor = Processor(fail);

        // Attempt 1 → requeued with future AvailableAt.
        await processor.ProcessAvailableAsync(10);
        var afterFirst = await _db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.Queued, afterFirst.Status);
        Assert.Equal(1, afterFirst.Attempts);
        Assert.NotNull(afterFirst.LastErrorCode);

        // Not yet available → no processing.
        Assert.Equal(0, await processor.ProcessAvailableAsync(10));

        // Advance past the retry delay → attempt 2 hits MaxAttempts → failed.
        _clock.Advance(TimeSpan.FromSeconds(61));
        await processor.ProcessAvailableAsync(10);
        var afterSecond = await _db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.Failed, afterSecond.Status);
        Assert.Equal(2, afterSecond.Attempts);
        Assert.Equal("InvalidOperationException", afterSecond.LastErrorCode);
        // Sanitized: no stack-trace markers.
        Assert.DoesNotContain(" at ", afterSecond.LastErrorMessage ?? "");
    }

    [Fact]
    public async Task Two_Claims_Do_Not_Process_Same_Job_Twice()
    {
        await Queue().EnqueueAsync(JobTypes.StorageReconcile, new StorageReconcileJobPayload());
        var processor = Processor(new SpyHandler(JobTypes.StorageReconcile));

        var first = await processor.ClaimNextAsync(CancellationToken.None);
        var second = await processor.ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second); // already running, not stale → not claimable
    }

    // Slice 90: the legacy LockedAt stale-window was removed; reclaim is now
    // purely lease-based — see Expired_Lease_Running_Job_Is_Reclaimed below.

    [Fact]
    public async Task Unknown_Job_Type_Fails_Permanently()
    {
        await Queue().EnqueueAsync("nonexistent.type", new { });
        // No handler registered for that type.
        var processed = await Processor(new SpyHandler(JobTypes.StorageReconcile))
            .ProcessAvailableAsync(10);

        Assert.Equal(1, processed);
        var row = await _db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.Failed, row.Status);
        Assert.Equal("UnknownJobType", row.LastErrorCode);
    }

    [Fact]
    public async Task Snapshot_Is_NoLeak_And_Omits_Payload()
    {
        await Queue().EnqueueAsync(JobTypes.MetadataEmbeddedBackfill,
            new MetadataBackfillJobPayload(Limit: 3));

        var snapshot = await Queue().GetSnapshotAsync();
        Assert.Equal(1, snapshot.Queued);
        Assert.Single(snapshot.Recent);

        // JobSummary has no payload field — serialize it and scan for needles
        // plus the word "payload" to guard against accidental future leakage.
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Worker_Is_Disabled_By_Default()
    {
        Assert.False(new JobsOptions().WorkerEnabled);
    }

    // ---- slice 89: lease / heartbeat / cancellation / progress -----------

    [Fact]
    public async Task Claim_Sets_Lease_Owner_And_Expiry()
    {
        await Queue().EnqueueAsync(JobTypes.StorageReconcile, new StorageReconcileJobPayload());
        var now = _clock.GetUtcNow().UtcDateTime;

        var claimed = await Processor().ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(JobStatuses.Running, claimed!.Status);
        Assert.False(string.IsNullOrEmpty(claimed.LockOwner));
        Assert.NotNull(claimed.LeaseUntil);
        Assert.NotNull(claimed.HeartbeatAt);
        Assert.Equal(now.AddSeconds(_options.LeaseSeconds), claimed.LeaseUntil);
    }

    [Fact]
    public async Task Heartbeat_Extends_Lease_For_Owner_Only()
    {
        await Queue().EnqueueAsync(JobTypes.StorageReconcile, new StorageReconcileJobPayload());
        var processor = Processor();
        var claimed = await processor.ClaimNextAsync(CancellationToken.None);
        var owner = claimed!.LockOwner!;
        var firstLease = claimed.LeaseUntil!.Value;

        _clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(await processor.HeartbeatAsync(claimed.Id, owner));

        var row = await _db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.Id);
        Assert.True(row.LeaseUntil > firstLease); // extended
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, row.HeartbeatAt);

        // A different owner cannot heartbeat (lost-lease guard).
        Assert.False(await processor.HeartbeatAsync(claimed.Id, "someone-else"));
    }

    [Fact]
    public async Task Succeeded_Job_Is_Not_Claimed_Again()
    {
        await Queue().EnqueueAsync(JobTypes.StorageReconcile, new StorageReconcileJobPayload());
        var processor = Processor(new SpyHandler(JobTypes.StorageReconcile));

        await processor.ProcessAvailableAsync(10);
        var again = await processor.ClaimNextAsync(CancellationToken.None);

        Assert.Null(again);
        var row = await _db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.Succeeded, row.Status);
        Assert.Null(row.LeaseUntil); // lease cleared on completion
    }

    [Fact]
    public async Task Expired_Lease_Running_Job_Is_Reclaimed()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        _db.BackgroundJobs.Add(new BackgroundJob
        {
            Id = Guid.NewGuid(),
            Type = JobTypes.StorageReconcile,
            Status = JobStatuses.Running,
            PayloadJson = "{}",
            Attempts = 1,
            MaxAttempts = 3,
            CreatedAt = now.AddMinutes(-10),
            AvailableAt = now.AddMinutes(-10),
            LockOwner = "dead-worker",
            LeaseUntil = now.AddSeconds(-1), // lease just expired
            HeartbeatAt = now.AddMinutes(-5),
            UpdatedAt = now.AddMinutes(-5),
        });
        await _db.SaveChangesAsync();

        var claimed = await Processor().ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(2, claimed!.Attempts);          // incremented on reclaim
        Assert.NotEqual("dead-worker", claimed.LockOwner); // new owner
    }

    [Fact]
    public async Task Unexpired_Lease_Running_Job_Is_Not_Reclaimed()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        _db.BackgroundJobs.Add(new BackgroundJob
        {
            Id = Guid.NewGuid(),
            Type = JobTypes.StorageReconcile,
            Status = JobStatuses.Running,
            PayloadJson = "{}",
            Attempts = 1,
            MaxAttempts = 3,
            CreatedAt = now.AddMinutes(-1),
            AvailableAt = now.AddMinutes(-1),
            LockOwner = "live-worker",
            LeaseUntil = now.AddSeconds(120), // still valid
            HeartbeatAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync();

        Assert.Null(await Processor().ClaimNextAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_Requested_Before_Run_Finishes_As_Cancelled_Without_Handler()
    {
        var job = await Queue().EnqueueAsync(JobTypes.StorageReconcile, new StorageReconcileJobPayload());
        Assert.True(await Queue().RequestCancellationAsync(job.Id));

        var spy = new SpyHandler(JobTypes.StorageReconcile);
        var processed = await Processor(spy).ProcessAvailableAsync(10);

        Assert.Equal(1, processed);
        Assert.Equal(0, spy.Calls); // handler never invoked
        var row = await _db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.Cancelled, row.Status);
        Assert.NotNull(row.CompletedAt);
        Assert.Null(row.LeaseUntil);
    }

    [Fact]
    public async Task RequestCancellation_Is_NoOp_For_Terminal_Jobs()
    {
        await Queue().EnqueueAsync(JobTypes.StorageReconcile, new StorageReconcileJobPayload());
        await Processor(new SpyHandler(JobTypes.StorageReconcile)).ProcessAvailableAsync(10);
        var row = await _db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.Succeeded, row.Status);

        Assert.False(await Queue().RequestCancellationAsync(row.Id));
        Assert.False(await Queue().RequestCancellationAsync(Guid.NewGuid())); // missing
    }

    [Fact]
    public async Task Handler_Receives_Context_With_JobId_Payload_And_Cancellation_Flag()
    {
        var job = await Queue().EnqueueAsync(
            JobTypes.MediaDerivativesBackfill,
            new MediaDerivativesBackfillJobPayload(Limit: 7, DryRun: true));
        var handler = new CapturingHandler(JobTypes.MediaDerivativesBackfill);

        await Processor(handler).ProcessAvailableAsync(10);

        Assert.Equal(job.Id, handler.SeenJobId);
        Assert.Contains("\"Limit\":7", handler.SeenPayload);
        Assert.False(handler.SeenCancellationRequested); // not requested → false
    }

    [Fact]
    public async Task Handler_Can_Update_Progress_Safely()
    {
        await Queue().EnqueueAsync(JobTypes.StorageReconcile, new StorageReconcileJobPayload());
        var handler = new CapturingHandler(
            JobTypes.StorageReconcile,
            async ctx =>
            {
                // Tight-loop-style updates collapse to a few writes; the final
                // values are flushed on completion regardless of throttling.
                for (var i = 1; i <= 5; i++)
                {
                    await ctx.ReportProgressAsync(i, 5, $"step {i}");
                }
            });

        await Processor(handler).ProcessAvailableAsync(10);

        var row = await _db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.Succeeded, row.Status);
        Assert.Equal(5, row.ProgressCurrent);
        Assert.Equal(5, row.ProgressTotal);
        Assert.Equal("step 5", row.ProgressMessage);
    }

    // ---- test helpers -----------------------------------------------------

    private sealed class SpyHandler : IJobHandler
    {
        public SpyHandler(string type) => JobType = type;
        public string JobType { get; }
        public int Calls { get; private set; }
        public JobContext? LastContext { get; private set; }

        public Task ExecuteAsync(JobContext context, CancellationToken ct)
        {
            Calls++;
            LastContext = context;
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysFailHandler : IJobHandler
    {
        public AlwaysFailHandler(string type) => JobType = type;
        public string JobType { get; }

        public Task ExecuteAsync(JobContext context, CancellationToken ct)
            => throw new InvalidOperationException("deliberate handler failure");
    }

    // Records the context surface it was handed, and (optionally) reports
    // progress, so tests can assert the handler-facing API works.
    private sealed class CapturingHandler : IJobHandler
    {
        private readonly Func<JobContext, Task>? _body;
        public CapturingHandler(string type, Func<JobContext, Task>? body = null)
        {
            JobType = type;
            _body = body;
        }
        public string JobType { get; }
        public Guid SeenJobId { get; private set; }
        public string? SeenPayload { get; private set; }
        public bool SeenCancellationRequested { get; private set; }

        public async Task ExecuteAsync(JobContext context, CancellationToken ct)
        {
            SeenJobId = context.JobId;
            SeenPayload = context.PayloadJson;
            SeenCancellationRequested = context.IsCancellationRequested;
            if (_body is not null)
            {
                await _body(context);
            }
        }
    }
}

// Minimal controllable clock for deterministic retry/stale timing.
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public MutableTimeProvider(DateTimeOffset start) => _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now += delta;
}
