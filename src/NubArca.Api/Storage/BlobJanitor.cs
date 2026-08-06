using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Data;

namespace NubArca.Api.Storage;

// Periodically purges BlobObject rows whose final owner was removed at least
// the configured grace window ago, along with their physical blob files.
//
// Disabled by default. When disabled, ExecuteAsync logs and exits; RunOnceAsync
// returns 0 without touching the database. Enable via BlobJanitor:Enabled in
// configuration.
//
// Ordering note: inside one transaction we delete the blob's blob_metadata row
// (slice 53) and then the BlobObject row (gated atomically by "WHERE Id = $1
// AND ReferenceCount = 0"); only after that commits do we delete the physical
// file. The spec's suggested ordering (physical file first) is the reverse; we
// deviate because the reverse can leave a row whose storage_key points at a
// missing physical file, which breaks any read path that subsequently resolves
// the row. With our ordering, the worst-case failure is an orphan physical
// file on disk — disk waste only.
//
// A soft-deleted FileItem leaves ReferenceCount at 0 but deliberately keeps
// PurgeEligibleAt null while its Restrict FK preserves restorable bytes. The
// manual permanent-delete/FileItemSweeper path starts the grace window only
// after removing that retained row. Remaining FK owners are still a final
// safety net: a delete violation is caught and the blob is skipped.
public sealed class BlobJanitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<BlobJanitorOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<BlobJanitor> _logger;

    public BlobJanitor(
        IServiceScopeFactory scopeFactory,
        IOptions<BlobJanitorOptions> options,
        TimeProvider clock,
        ILogger<BlobJanitor> logger)
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
            _logger.LogInformation("BlobJanitor is disabled (BlobJanitor:Enabled = false).");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.IntervalMinutes));
        _logger.LogInformation(
            "BlobJanitor started (interval = {Interval}, grace = {GraceMinutes} min).",
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
                _logger.LogWarning(ex, "BlobJanitor tick failed");
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

    // Public for testability: returns the number of blobs successfully purged.
    // Respects the Enabled flag so the "disabled does not purge" test can
    // verify behaviour by simply running the method.
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
        var storage = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        // Slice 72: a reclaimed BlobObject may be an original (lives in the
        // original root) or a derived artifact (lives in the derived root). We
        // don't track purpose on the row, so delete from BOTH stores — both
        // DeleteAsync calls are idempotent for a missing file, and when the
        // roots are the same (single-root default) the second is a harmless
        // no-op. This prevents a split-root derived blob from leaking on disk.
        var derivedStorage = scope.ServiceProvider.GetService<IDerivedBlobStorage>();
        // Video-hls slice 1: the HLS ladder directory is keyed by the source
        // blob's sha256 and must not outlive the blob. Optional so hosts/tests
        // without the registration keep working.
        var hlsStorage = scope.ServiceProvider.GetService<HlsDerivativeStorage>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();

        var candidates = await db.BlobObjects
            .AsNoTracking()
            .Where(b => b.ReferenceCount == 0
                && b.PurgeEligibleAt != null
                && b.PurgeEligibleAt < cutoff)
            .Select(b => new { b.Id, b.StorageKey, b.Sha256, b.PurgeEligibleAt })
            .ToListAsync(cancellationToken);

        var purged = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                // Blob-derived metadata (slice 53) is owned by the blob, so it
                // must be removed in the same logical step as the blob itself.
                // We wrap both deletes in a transaction: drop the blob_metadata
                // row first (its FK Restrict would otherwise block the blob
                // delete), then delete the BlobObject under the atomic
                // ReferenceCount==0 gate. If the gate misses (a concurrent
                // re-upload bumped the count) or a remaining FK reference blocks
                // the blob delete, we roll back so the metadata row is restored.
                int rowsDeleted;
                await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
                {
                    try
                    {
                        await db.BlobMetadata
                            .Where(m => m.BlobObjectId == candidate.Id)
                            .ExecuteDeleteAsync(cancellationToken);

                        rowsDeleted = await db.BlobObjects
                            .Where(b => b.Id == candidate.Id
                                && b.ReferenceCount == 0
                                && b.PurgeEligibleAt == candidate.PurgeEligibleAt
                                && b.PurgeEligibleAt < cutoff)
                            .ExecuteDeleteAsync(cancellationToken);
                    }
                    catch (DbUpdateException ex)
                    {
                        // FK violation: a soft-deleted FileItem (or thumbnail)
                        // still references this blob. Roll back to undo the
                        // metadata delete and leave the blob in place; a future
                        // slice will handle the cascade.
                        await tx.RollbackAsync(cancellationToken);
                        _logger.LogWarning(
                            ex,
                            "BlobObject {BlobId} cannot be purged — FK reference still exists.",
                            candidate.Id);
                        continue;
                    }

                    if (rowsDeleted == 0)
                    {
                        // Lost a race: the count moved up between the scan and
                        // the delete. Roll back to restore the metadata row;
                        // the blob is staying.
                        await tx.RollbackAsync(cancellationToken);
                        continue;
                    }

                    await tx.CommitAsync(cancellationToken);
                }

                // Row gone. Delete the physical file. If this fails we have an
                // orphan blob on disk — disk waste only, no broken references.
                try
                {
                    await storage.DeleteAsync(candidate.StorageKey, cancellationToken);
                    if (derivedStorage is not null)
                    {
                        await derivedStorage.DeleteAsync(candidate.StorageKey, cancellationToken);
                    }
                    // Video-hls: remove the (regenerable) HLS ladder for this
                    // content. Idempotent; the blob_hls_derivatives row is
                    // already gone via the FK cascade on the blob delete.
                    hlsStorage?.Delete(candidate.Sha256);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "Physical blob delete failed after row purge for {BlobId}.",
                        candidate.Id);
                }

                await audit.LogAsync(
                    userId: null,
                    action: AuditActions.BlobPurge,
                    entityType: AuditEntityTypes.Blob,
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
                _logger.LogWarning(ex, "Failed to purge blob {BlobId}.", candidate.Id);
            }
        }

        return purged;
    }
}
