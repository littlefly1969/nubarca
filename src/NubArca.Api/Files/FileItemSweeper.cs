using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Data;

namespace NubArca.Api.Files;

// Periodically purges FileItem rows whose DeletedAt is older than the
// configured grace window.
//
// It owns the SCHEDULE, not the semantics: the actual deletion is
// IFileItemService.PurgeTrashedFileAsync, the same canonical purge that
// individual permanent delete and Empty Trash use. Retention expiry therefore
// produces exactly the same permanent result as a manual Empty Trash, and a
// dependent row added to the cascade is picked up by all three triggers at
// once. Do not reintroduce a retention-only deletion path here.
//
// This is the missing half of the soft-delete loop: slice 15 decrements
// BlobObject.ReferenceCount when a file is soft-deleted but leaves the
// FileItem row in place, so the FK Restrict on FileItem -> BlobObject prevents
// BlobJanitor from purging the blob. After FileItemSweeper runs, the blob
// finally has no dependent rows and BlobJanitor can reclaim its row + physical
// file on its next tick.
//
// The two grace windows are independent: this one starts at soft-delete;
// BlobJanitor's starts only after this service hard-deletes the retained row.
//
// Disabled by default. Configure via FileItemSweeper:Enabled.
public sealed class FileItemSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<FileItemSweeperOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<FileItemSweeper> _logger;

    public FileItemSweeper(
        IServiceScopeFactory scopeFactory,
        IOptions<FileItemSweeperOptions> options,
        TimeProvider clock,
        ILogger<FileItemSweeper> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation("FileItemSweeper is disabled (FileItemSweeper:Enabled = false).");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.IntervalMinutes));
        _logger.LogInformation(
            "FileItemSweeper started (interval = {Interval}, grace = {GraceMinutes} min).",
            interval,
            options.GraceMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FileItemSweeper tick failed");
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

    // Public for testability. Respects the Enabled flag so a "disabled does
    // not purge" test can verify behaviour by simply running the method.
    // Returns the number of FileItem rows successfully purged.
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return 0;
        }

        var graceMinutes = Math.Max(0, options.GraceMinutes);
        var cutoff = _clock.GetUtcNow().UtcDateTime.AddMinutes(-graceMinutes);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();

        // IgnoreQueryFilters: soft-deleted Private Vault files must be purged by
        // the sweeper too — otherwise their rows linger forever, the FK Restrict
        // pins their blob, and BlobJanitor can never reclaim the bytes. Purging
        // releases the reference exactly like any other file.
        var candidates = await db.FileItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(f => f.DeletedAt != null && f.DeletedAt < cutoff)
            .Select(f => new { f.Id, f.OwnerUserId })
            .ToListAsync(cancellationToken);

        var purged = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                // Retention expiry is NOT its own deletion path: it runs the
                // same canonical purge that individual permanent delete and
                // Empty Trash run, so all three converge on identical results.
                // Passing the cutoff keeps it in the atomic delete gate, so a
                // restore that lands between the scan and the delete wins and
                // this candidate is simply skipped.
                var didPurge = await files.PurgeTrashedFileAsync(
                    candidate.OwnerUserId,
                    candidate.Id,
                    cutoff,
                    cancellationToken);

                if (!didPurge)
                {
                    continue;
                }

                await audit.LogAsync(
                    userId: candidate.OwnerUserId,
                    action: AuditActions.FilePurge,
                    entityType: AuditEntityTypes.File,
                    entityId: candidate.Id,
                    ipAddress: null,
                    metadata: null,
                    cancellationToken: cancellationToken);

                purged++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to purge FileItem {FileItemId}.", candidate.Id);
            }
        }

        return purged;
    }
}
