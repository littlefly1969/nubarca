using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Albums.Sharing;

// SHARE-COPY-01: retires pending album transfers that can never be accepted and
// releases the blob references their manifests hold.
//
// This is not optional housekeeping. A pending manifest deliberately OWNS one
// blob reference per item so the bytes survive the sender deleting the source.
// Without something that eventually releases them, an offer nobody ever answers
// would pin those bytes forever — invisible to the janitor, which only reclaims
// zero-reference blobs.
//
// Two reasons a pending transfer is retired, both handled by
// IAlbumTransferService.ExpirePendingAsync:
//   * its window elapsed;
//   * its SENDER was disabled — a security rule rather than a timeout, since a
//     disabled account's pending operations must not stay completable.
//
// Declined and cancelled transfers release their references synchronously in
// the same transaction as the decision, so they never reach this service.
//
// Enabled by default, unlike BlobJanitor: this only releases reference counts
// and never deletes bytes, so the worst case of running it is that an expired
// offer's blobs become eligible for the janitor's own grace window.
public sealed class AlbumTransferCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<AlbumTransferCleanupOptions> _options;
    private readonly ILogger<AlbumTransferCleanupService> _logger;

    public AlbumTransferCleanupService(
        IServiceScopeFactory scopeFactory,
        IOptions<AlbumTransferCleanupOptions> options,
        ILogger<AlbumTransferCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation(
                "AlbumTransferCleanup is disabled (AlbumTransferCleanup:Enabled = false).");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.IntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var retired = await RunOnceAsync(stoppingToken);
                if (retired > 0)
                {
                    // Counts only — never a transfer id, user id or file name.
                    _logger.LogInformation(
                        "AlbumTransferCleanup retired {Count} pending transfer(s).", retired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed sweep must never take the host down: the references
                // stay held and the next tick tries again.
                _logger.LogError(ex, "AlbumTransferCleanup sweep failed.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Idempotent: safe to call repeatedly and concurrently with a recipient
    // answering. Each transfer is retired in its own transaction under a
    // conditional state claim, so a recipient's decision always wins the race.
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var transfers = scope.ServiceProvider.GetRequiredService<IAlbumTransferService>();
        return await transfers.ExpirePendingAsync(cancellationToken);
    }
}

public sealed class AlbumTransferCleanupOptions
{
    public const string SectionName = "AlbumTransferCleanup";

    public bool Enabled { get; set; } = true;

    public int IntervalMinutes { get; set; } = 60;
}
