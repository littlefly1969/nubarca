using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Storage;

namespace NubArca.Api.Files;

// Periodically hard-deletes FileItem rows whose DeletedAt is older than the
// configured grace window, along with any ShareLink rows referencing them.
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
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();

        // IgnoreQueryFilters: soft-deleted Private Vault files must be purged by
        // the sweeper too — otherwise their rows linger forever, the FK Restrict
        // pins their blob, and BlobJanitor can never reclaim the bytes. Purging
        // releases the reference exactly like any other file.
        var candidates = await db.FileItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(f => f.DeletedAt != null && f.DeletedAt < cutoff)
            .Select(f => new { f.Id, f.OwnerUserId, f.BlobObjectId })
            .ToListAsync(cancellationToken);

        var purged = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

                // Capture thumbnail blob ids before deleting their rows so we
                // can release their ReferenceCount after the FileItem row is
                // gone (BlobJanitor reclaims the rows + physical files on a
                // subsequent tick).
                var thumbnailBlobIds = await db.FileThumbnails
                    .Where(t => t.FileItemId == candidate.Id)
                    .Select(t => t.BlobObjectId)
                    .ToListAsync(cancellationToken);

                // Delete dependent share_links + thumbnails + user metadata + album memberships
                // first (all FK Restrict to FileItem).
                await db.ShareLinks
                    .Where(s => s.FileItemId == candidate.Id)
                    .ExecuteDeleteAsync(cancellationToken);
                await db.FileThumbnails
                    .Where(t => t.FileItemId == candidate.Id)
                    .ExecuteDeleteAsync(cancellationToken);
                await db.FileItemUserMetadata
                    .Where(m => m.FileItemId == candidate.Id)
                    .ExecuteDeleteAsync(cancellationToken);
                await db.AlbumItems
                    .Where(ai => ai.FileItemId == candidate.Id)
                    .ExecuteDeleteAsync(cancellationToken);
                // Slice 94: the owner-scoped GPS projection dies with the file.
                await db.FileItemLocations
                    .Where(l => l.FileItemId == candidate.Id)
                    .ExecuteDeleteAsync(cancellationToken);

                // Atomic gate: only delete if the file is still soft-deleted
                // past the cutoff. Defends against a future restore mechanism.
                var rowsDeleted = await db.FileItems
                    .IgnoreQueryFilters()
                    .Where(f => f.Id == candidate.Id
                        && f.DeletedAt != null
                        && f.DeletedAt < cutoff)
                    .ExecuteDeleteAsync(cancellationToken);

                if (rowsDeleted == 0)
                {
                    // Row was restored between scan and delete (no path to
                    // that today, but defensive). Roll back the share_links +
                    // thumbnails delete so they don't vanish under a now-live file.
                    await tx.RollbackAsync(cancellationToken);
                    continue;
                }

                // Release ReferenceCount for each thumbnail blob. Atomic UPDATE
                // with WHERE > 0 prevents negative counts and is idempotent.
                foreach (var thumbBlobId in thumbnailBlobIds)
                {
                    await blobs.ReleaseAsync(thumbBlobId, cancellationToken);
                }

                // The original active reference was released at soft-delete.
                // Now that the retained, restorable FileItem row is gone,
                // start the physical purge grace window from this hard purge.
                await blobs.MarkPurgeEligibleIfUnreferencedAsync(
                    candidate.BlobObjectId,
                    cancellationToken);

                await tx.CommitAsync(cancellationToken);

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
