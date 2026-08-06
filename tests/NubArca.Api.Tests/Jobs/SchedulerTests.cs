using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Jobs;
using Xunit;

namespace NubArca.Api.Tests.Jobs;

// Scheduler v2 — cooperative slicing, priority selection, starvation-grace, and
// continuation mechanics. Self-contained SQLite harness with a mutable clock so
// budgets/aging are deterministic. Uses purpose-built test handlers (a
// sliceable handler that checkpoints + yields) so the scheduling behaviour is
// isolated from the real backfill services (those are exercised end-to-end in
// MediaDerivativesSlicingTests).
public sealed class SchedulerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ServiceProvider _serviceProvider;
    private readonly MutableTimeProvider _clock;
    private readonly JobsOptions _options;

    public SchedulerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 13, 12, 0, 0, TimeSpan.Zero));
        _options = new JobsOptions
        {
            RetryDelaySeconds = 60,
            // Long lease/heartbeat so the heartbeat timer never fires during
            // these millisecond-scale tests (only the slice budget / clock and
            // seeded rows drive behaviour).
            LeaseSeconds = 600,
            HeartbeatSeconds = 300,
            // Small slice budget so a few items trigger a yield.
            MaintenanceSliceItemBudget = 3,
            MaintenanceSliceSeconds = 30,
            StarvationGraceSeconds = 300,
            ContinuationDelaySeconds = 0,
        };
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

    private Task<BackgroundJob> Row(Guid id)
        => _db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == id);

    // ---- priority selection ----------------------------------------------

    [Fact]
    public async Task Foreground_Import_Is_Claimed_Before_Queued_Maintenance_Backfill()
    {
        // Backfill enqueued FIRST (older) — FIFO alone would pick it, but the
        // foreground import must win on priority.
        var backfill = await Queue().EnqueueAsync(
            JobTypes.MediaDerivativesBackfill, new MediaDerivativesBackfillJobPayload());
        var import = await Queue().EnqueueAsync(
            JobTypes.AdminImport, new AdminImportJobPayload(Guid.NewGuid()));

        // Sanity: registry assigned the bands.
        Assert.Equal(JobScheduling.Maintenance, (await Row(backfill.Id)).Priority);
        Assert.Equal(JobScheduling.Foreground, (await Row(import.Id)).Priority);

        var claimed = await Processor().ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(JobTypes.AdminImport, claimed!.Type);
    }

    // ---- bounded starvation-grace ----------------------------------------

    [Fact]
    public async Task Maintenance_Aged_Past_Grace_Is_Promoted_Then_Foreground_Resumes()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        // A maintenance backfill that has been queued (waiting) past the grace
        // window while foreground work kept being chosen.
        var backfill = Seed(JobTypes.MediaDerivativesBackfill, JobScheduling.Maintenance,
            availableAt: now.AddSeconds(-(_options.StarvationGraceSeconds + 1)));
        // A freshly-queued foreground import.
        var import = Seed(JobTypes.AdminImport, JobScheduling.Foreground, availableAt: now);
        await _db.SaveChangesAsync();

        // Starved maintenance is promoted for ONE claim.
        var first = await Processor().ClaimNextAsync(CancellationToken.None);
        Assert.Equal(backfill, first!.Id);

        // It re-queues with AvailableAt = now (wait reset) — simulate that.
        await _db.BackgroundJobs.Where(j => j.Id == backfill).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, JobStatuses.Queued)
            .SetProperty(j => j.AvailableAt, now)
            .SetProperty(j => j.LockOwner, (string?)null)
            .SetProperty(j => j.LeaseUntil, (DateTime?)null));

        // Now neither is starved → foreground wins, as normal.
        var second = await Processor().ClaimNextAsync(CancellationToken.None);
        Assert.Equal(import, second!.Id);
    }

    [Fact]
    public async Task Maintenance_Not_Yet_Aged_Does_Not_Outrank_Foreground()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var backfill = Seed(JobTypes.MediaDerivativesBackfill, JobScheduling.Maintenance,
            availableAt: now.AddSeconds(-(_options.StarvationGraceSeconds - 5))); // not yet past grace
        var import = Seed(JobTypes.AdminImport, JobScheduling.Foreground, availableAt: now);
        await _db.SaveChangesAsync();

        var claimed = await Processor().ClaimNextAsync(CancellationToken.None);
        Assert.Equal(import, claimed!.Id);
    }

    // ---- cooperative slicing / continuation ------------------------------

    [Fact]
    public async Task Maintenance_Job_Yields_After_Item_Budget_And_Requeues_As_One_Row()
    {
        await Queue().EnqueueAsync(JobTypes.MediaDerivativesBackfill, new MediaDerivativesBackfillJobPayload());
        var handler = new SliceableHandler(JobTypes.MediaDerivativesBackfill, totalItems: 10);

        var processed = await Processor(handler).ProcessAvailableAsync(maxJobs: 1);

        Assert.Equal(1, processed);
        Assert.Equal(1, handler.SliceCalls);
        var row = await _db.BackgroundJobs.AsNoTracking().SingleAsync();
        // Still one row, re-queued as the next slice.
        Assert.Equal(JobStatuses.Queued, row.Status);
        Assert.Equal(1, row.SliceNumber);                 // ++ on continuation
        Assert.Equal(0, row.Attempts);                    // fresh retry budget
        Assert.Equal(JobYieldReasons.SliceBudget, row.YieldReason);
        Assert.Equal("3", row.CheckpointJson);            // budget = 3 processed
        Assert.Equal(3, row.ProgressCurrent);
        Assert.Equal(1, await _db.BackgroundJobs.CountAsync()); // no row explosion
    }

    [Fact]
    public async Task Sliced_Job_Resumes_From_Checkpoint_And_Eventually_Succeeds()
    {
        await Queue().EnqueueAsync(JobTypes.MediaDerivativesBackfill, new MediaDerivativesBackfillJobPayload());
        var handler = new SliceableHandler(JobTypes.MediaDerivativesBackfill, totalItems: 10);
        var processor = Processor(handler);

        // 10 items / 3 per slice → 4 slices (3,3,3,1) to finish.
        var slices = 0;
        for (var i = 0; i < 20; i++)
        {
            if (await processor.ProcessAvailableAsync(maxJobs: 1) == 0) break;
            slices++;
            var r = await _db.BackgroundJobs.AsNoTracking().SingleAsync();
            if (JobStatuses.IsTerminal(r.Status)) break;
        }

        var row = await _db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.Succeeded, row.Status);
        Assert.Equal(10, handler.TotalProcessed);    // exactly the work, once
        Assert.Equal(4, slices);                     // 3+3+3+1
        Assert.Equal(1, await _db.BackgroundJobs.CountAsync());
        Assert.Null(row.CheckpointJson);             // cleared on completion
    }

    [Fact]
    public async Task Queued_Import_Starts_After_The_Current_Derivative_Slice_Yields()
    {
        // Scenario A: a long derivative backfill is running; an import is queued
        // mid-run; the import must start after ONE slice, not the whole backfill.
        await Queue().EnqueueAsync(JobTypes.MediaDerivativesBackfill, new MediaDerivativesBackfillJobPayload());
        var derivative = new SliceableHandler(JobTypes.MediaDerivativesBackfill, totalItems: 1000);
        var import = new SpyHandler(JobTypes.AdminImport);
        var processor = Processor(derivative, import);

        // One derivative slice runs and yields (re-queued, more work remains).
        await processor.ProcessAvailableAsync(maxJobs: 1);
        var derivRow = await _db.BackgroundJobs.AsNoTracking()
            .SingleAsync(j => j.Type == JobTypes.MediaDerivativesBackfill);
        Assert.Equal(JobStatuses.Queued, derivRow.Status); // yielded, not done
        Assert.True(derivRow.SliceNumber >= 1);

        // Import arrives mid-run.
        await Queue().EnqueueAsync(JobTypes.AdminImport, new AdminImportJobPayload(Guid.NewGuid()));

        // Next claim picks the foreground import, NOT the next derivative slice.
        var claimed = await processor.ClaimNextAsync(CancellationToken.None);
        Assert.Equal(JobTypes.AdminImport, claimed!.Type);
    }

    [Fact]
    public async Task Same_Priority_Jobs_Take_Fair_Turns_Via_Requeue()
    {
        // Two maintenance jobs; the running one yields and re-queues with
        // AvailableAt = now, so the other (older) gets the next turn.
        var a = await Queue().EnqueueAsync(JobTypes.MediaDerivativesBackfill, new MediaDerivativesBackfillJobPayload());
        _clock.Advance(TimeSpan.FromSeconds(1));
        var b = await Queue().EnqueueAsync(JobTypes.StorageReconcile, new StorageReconcileJobPayload());

        // 'a' advances the clock as it works, so when it re-queues its
        // AvailableAt is genuinely LATER than 'b' (which has been waiting) — the
        // mechanism that gives same-priority jobs fair turns.
        var ha = new SliceableHandler(JobTypes.MediaDerivativesBackfill, totalItems: 1000)
        {
            AdvanceClockPerItem = (TimeSpan.FromSeconds(1), _clock),
        };
        var hb = new SliceableHandler(JobTypes.StorageReconcile, totalItems: 1000);
        var processor = Processor(ha, hb);

        // First slice: 'a' is oldest → runs, yields (AvailableAt = now+).
        await processor.ProcessAvailableAsync(maxJobs: 1);
        Assert.Equal(1, ha.SliceCalls);
        Assert.Equal(0, hb.SliceCalls);

        // Second claim: 'b' is now the oldest pending → its turn.
        var next = await processor.ClaimNextAsync(CancellationToken.None);
        Assert.Equal(b.Id, next!.Id);
    }

    [Fact]
    public async Task Wall_Clock_Budget_Triggers_A_Yield()
    {
        _options.MaintenanceSliceItemBudget = 100_000; // disable item-budget yield
        await Queue().EnqueueAsync(JobTypes.MediaDerivativesBackfill, new MediaDerivativesBackfillJobPayload());
        // Each item advances the clock by 20s; budget is 30s → yields after 2.
        var handler = new SliceableHandler(JobTypes.MediaDerivativesBackfill, totalItems: 1000)
        {
            AdvanceClockPerItem = (TimeSpan.FromSeconds(20), _clock),
        };

        await Processor(handler).ProcessAvailableAsync(maxJobs: 1);

        var row = await _db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.Queued, row.Status);
        Assert.Equal(2, handler.TotalProcessed); // 2 × 20s crosses the 30s budget
    }

    [Fact]
    public async Task MaxSlicesPerJob_Force_Completes_A_Looping_Job()
    {
        _options.MaxSlicesPerJob = 2;
        await Queue().EnqueueAsync(JobTypes.MediaDerivativesBackfill, new MediaDerivativesBackfillJobPayload());
        // Never finishes on its own (huge item count) → always wants continuation.
        var handler = new SliceableHandler(JobTypes.MediaDerivativesBackfill, totalItems: 1_000_000);
        var processor = Processor(handler);

        await processor.ProcessAvailableAsync(maxJobs: 1); // slice 0 → continuation (SliceNumber 1)
        await processor.ProcessAvailableAsync(maxJobs: 1); // slice 1 → nextSlice 2 == cap → completes

        var row = await _db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.Succeeded, row.Status);
        Assert.Equal(JobYieldReasons.MaxSlices, row.YieldReason);
    }

    [Fact]
    public async Task Cancellation_During_A_Slice_Ends_Cancelled_Without_Continuation()
    {
        var job = await Queue().EnqueueAsync(JobTypes.MediaDerivativesBackfill, new MediaDerivativesBackfillJobPayload());
        // Operator cancels before the slice runs; the engine finishes it as
        // cancelled WITHOUT invoking the handler (so no continuation row).
        Assert.True(await Queue().RequestCancellationAsync(job.Id));
        var handler = new SliceableHandler(JobTypes.MediaDerivativesBackfill, totalItems: 10);

        await Processor(handler).ProcessAvailableAsync(maxJobs: 1);

        var row = await _db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatuses.Cancelled, row.Status);
        Assert.Equal(0, row.SliceNumber);       // never advanced to a continuation
        Assert.Equal(0, handler.SliceCalls);    // handler not invoked
        Assert.Equal(1, await _db.BackgroundJobs.CountAsync());
    }

    // ---- helpers ----------------------------------------------------------

    private Guid Seed(string type, int priority, DateTime availableAt)
    {
        var id = Guid.NewGuid();
        _db.BackgroundJobs.Add(new BackgroundJob
        {
            Id = id,
            Type = type,
            Status = JobStatuses.Queued,
            PayloadJson = "{}",
            Priority = priority,
            Attempts = 0,
            MaxAttempts = 3,
            CreatedAt = availableAt,
            AvailableAt = availableAt,
            UpdatedAt = availableAt,
        });
        return id;
    }

    // A sliceable handler: processes `totalItems` units across slices, resuming
    // from the checkpoint (the count already done), checkpointing + yielding
    // when ShouldYield trips. Mirrors how the real derivative handler behaves.
    private sealed class SliceableHandler : IJobHandler
    {
        private readonly int _totalItems;
        public SliceableHandler(string type, int totalItems) { JobType = type; _totalItems = totalItems; }
        public string JobType { get; }
        public int TotalProcessed { get; private set; }
        public int SliceCalls { get; private set; }
        public (TimeSpan Delta, MutableTimeProvider Clock)? AdvanceClockPerItem { get; init; }

        public async Task ExecuteAsync(JobContext context, CancellationToken ct)
        {
            SliceCalls++;
            var done = int.TryParse(context.Checkpoint, out var d) ? d : 0;
            long thisSlice = 0;
            while (done < _totalItems)
            {
                done++;
                thisSlice++;
                TotalProcessed = done;
                AdvanceClockPerItem?.Clock.Advance(AdvanceClockPerItem.Value.Delta);
                await context.ReportProgressAsync(done, null, "working", ct);
                if (context.ShouldYield(thisSlice)) break;
            }
            if (done < _totalItems)
            {
                context.RequestContinuation(JobYieldReasons.SliceBudget, done.ToString());
            }
        }
    }

    private sealed class SpyHandler : IJobHandler
    {
        public SpyHandler(string type) => JobType = type;
        public string JobType { get; }
        public int Calls { get; private set; }
        public Task ExecuteAsync(JobContext context, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
