using Microsoft.Extensions.Options;

namespace NubArca.Api.Uploads;

// Reclaims abandoned resumable uploads: expired rows, their chunk receipts and
// their temporary files, plus temporary files whose row is already gone.
// On by default (ResumableUploads:CleanupEnabled) because what it deletes is
// never user content, only partial transfers nobody finished. Logs counts only.
public sealed class ResumableUploadSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<ResumableUploadOptions> _options;
    private readonly ILogger<ResumableUploadSweeper> _logger;

    public ResumableUploadSweeper(
        IServiceScopeFactory scopeFactory,
        IOptions<ResumableUploadOptions> options,
        ILogger<ResumableUploadSweeper> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled || !options.CleanupEnabled)
        {
            _logger.LogInformation("ResumableUploadSweeper is disabled.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.CleanupIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var uploads = scope.ServiceProvider.GetRequiredService<IResumableUploadService>();
                var swept = await uploads.SweepExpiredAsync(stoppingToken);
                if (swept > 0)
                {
                    _logger.LogInformation("ResumableUploadSweeper reclaimed {Count} expired upload(s).", swept);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ResumableUploadSweeper pass failed ({Type}).", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
