using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Uploads;

// Slice 93: reclaims remote-staging disk space. Staging is TEMPORARY
// acquisition space, so the sweeper:
//   1. marks overdue non-terminal sessions `expired` (importing sessions are
//      exempt — the import job owns them until it finalizes);
//   2. hard-deletes terminal sessions (imported/failed/cancelled/expired) that
//      are past their ExpiresAt, including their chunk/item rows and staging
//      directories.
//
// Disabled by default (Staging:CleanupEnabled), consistent with the janitor /
// sweeper convention. The DELETE endpoint always allows manual discard. Logs
// counts only — never a path.
public sealed class StagingCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<StagingOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<StagingCleanupService> _logger;

    public StagingCleanupService(
        IServiceScopeFactory scopeFactory,
        IOptions<StagingOptions> options,
        TimeProvider clock,
        ILogger<StagingCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled || !options.CleanupEnabled)
        {
            _logger.LogInformation("StagingCleanupService is disabled (Staging:CleanupEnabled = false).");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.CleanupIntervalMinutes));
        _logger.LogInformation("StagingCleanupService started (interval = {Interval}).", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Staging cleanup pass failed ({Type}).", ex.GetType().Name);
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

    // One cleanup pass; also driven directly by tests. Returns the number of
    // sessions hard-deleted.
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var staging = scope.ServiceProvider.GetRequiredService<IStagingUploadService>() as StagingUploadService;
        var now = _clock.GetUtcNow().UtcDateTime;

        // 1. Expire overdue non-terminal sessions (never an importing one —
        // its import job finalizes it).
        var expired = await db.RemoteUploadSessions
            .Where(s => s.ExpiresAt < now
                && s.Status != RemoteUploadSessionStatuses.Importing
                && s.Status != RemoteUploadSessionStatuses.Imported
                && s.Status != RemoteUploadSessionStatuses.Failed
                && s.Status != RemoteUploadSessionStatuses.Cancelled
                && s.Status != RemoteUploadSessionStatuses.Expired)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, RemoteUploadSessionStatuses.Expired)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);

        // 2. Reclaim terminal sessions past their expiry: rows + staging dirs.
        var reclaimable = await db.RemoteUploadSessions.AsNoTracking()
            .Where(s => s.ExpiresAt < now
                && (s.Status == RemoteUploadSessionStatuses.Imported
                    || s.Status == RemoteUploadSessionStatuses.Failed
                    || s.Status == RemoteUploadSessionStatuses.Cancelled
                    || s.Status == RemoteUploadSessionStatuses.Expired))
            .OrderBy(s => s.ExpiresAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        var deleted = 0;
        foreach (var session in reclaimable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (staging is null) break;
            await staging.DeleteSessionRowsAndFilesAsync(session, cancellationToken);
            deleted++;
        }

        if (expired > 0 || deleted > 0)
        {
            _logger.LogInformation(
                "Staging cleanup: expired {Expired} session(s), reclaimed {Deleted}.",
                expired, deleted);
        }
        return deleted;
    }
}
