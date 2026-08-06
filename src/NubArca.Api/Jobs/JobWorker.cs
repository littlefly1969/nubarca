using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Jobs;

// In-process background worker. OFF by default — only registered as a hosted
// service when Jobs:WorkerEnabled = true (see Program.cs). When disabled (or
// when the constructor sees the flag off) it logs and exits without touching
// the database, mirroring BlobJanitor / FileItemSweeper.
//
// Each poll creates its own DI scope so the scoped JobProcessor + handlers get
// a fresh AppDbContext.
public sealed class JobWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<JobsOptions> _options;
    private readonly ILogger<JobWorker> _logger;

    public JobWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<JobsOptions> options,
        ILogger<JobWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.WorkerEnabled)
        {
            _logger.LogInformation("JobWorker is disabled (Jobs:WorkerEnabled = false).");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, opts.PollIntervalSeconds));
        var slots = Math.Clamp(opts.MaxConcurrentJobs, 1, 8);
        _logger.LogInformation(
            "JobWorker started; polling every {Seconds}s, batch {Batch}, slots {Slots}.",
            interval.TotalSeconds, opts.BatchSize, slots);

        await RunWorkerSlotsAsync(slots, RunSlotAsync, stoppingToken);

        async Task RunSlotAsync(int slot, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
                    var processed = await processor.ProcessAvailableAsync(
                        opts.BatchSize, log: null, cancellationToken);
                    if (processed > 0)
                    {
                        _logger.LogInformation(
                            "JobWorker slot {Slot} processed {Count} job(s).", slot, processed);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let a single slot iteration crash the worker loop.
                    _logger.LogError(ex,
                        "JobWorker slot {Slot} poll failed: {Code}.", slot, ex.GetType().Name);
                }

                try
                {
                    await Task.Delay(interval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    internal static Task RunWorkerSlotsAsync(
        int slots,
        Func<int, CancellationToken, Task> runSlot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runSlot);
        var boundedSlots = Math.Clamp(slots, 1, 8);
        return Task.WhenAll(Enumerable.Range(1, boundedSlots)
            .Select(slot => runSlot(slot, cancellationToken)));
    }
}
