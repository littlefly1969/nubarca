using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Aesthetics;

// Reclaims expired/revoked TV "Beauty Lab" QR upload-session rows past their
// retention window (the ephemeral-token cleanup convention, mirroring
// StagingCleanupService). Logic never depends on this loop: resolve/upload
// re-check expiry + revocation on every request, so a session is refused the
// instant it expires or is revoked — the sweeper only frees rows. Logs COUNTS
// only, never a token/owner/filename.
public sealed class AestheticUploadSessionCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<AestheticsOptions> _options;
    private readonly ILogger<AestheticUploadSessionCleanupService> _logger;

    public AestheticUploadSessionCleanupService(
        IServiceScopeFactory scopeFactory,
        IOptions<AestheticsOptions> options,
        ILogger<AestheticUploadSessionCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.UploadSessionCleanupEnabled)
        {
            _logger.LogInformation(
                "AestheticUploadSessionCleanupService disabled (HumanAesExpert:UploadSessionCleanupEnabled = false).");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.UploadSessionCleanupIntervalMinutes));
        _logger.LogInformation(
            "AestheticUploadSessionCleanupService started (interval = {Interval}).", interval);

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
                _logger.LogWarning(ex, "AestheticUploadSessionCleanupService sweep failed; will retry.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IAestheticUploadSessionService>();
        var deleted = await sessions.CleanupExpiredAsync(cancellationToken);
        if (deleted > 0)
        {
            _logger.LogInformation(
                "AestheticUploadSessionCleanupService reclaimed {Count} expired/revoked session(s).", deleted);
        }
    }
}
