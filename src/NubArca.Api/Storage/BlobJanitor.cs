using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Storage;

// Periodically purges BlobObject rows whose final owner was removed at least
// the configured grace window ago, along with their physical blob files.
//
// Disabled by default. When disabled, ExecuteAsync logs and exits; RunOnceAsync
// returns 0 without touching the database. Enable via BlobJanitor:Enabled in
// configuration.
//
// Ordering note: inside one transaction we delete the blob's blob_metadata row
// (slice 53), record a PendingBlobPurge carrying the storage key + sha256, and
// then delete the BlobObject row (gated atomically by "WHERE Id = $1 AND
// ReferenceCount = 0"); only after that commits do we delete the physical
// files, and only a successful unlink clears the PendingBlobPurge row.
//
// We do NOT delete the physical file first. That ordering can leave a live row
// whose storage_key points at missing bytes: a concurrent StoreAsync for the
// same sha256 observes the file as already present, skips the write, then
// resurrects the row (ReferenceCount 0 -> 1, PurgeEligibleAt cleared), and our
// gated row delete correctly declines — leaving a referenced blob with no
// bytes.
//
// The PendingBlobPurge row is what makes the safe ordering retry-safe: the
// storage key outlives the row it came from, so a failed or interrupted unlink
// is retried on a later tick instead of leaking bytes forever with no record
// that they exist. Every tick drains leftovers before scanning for new work.
// A missing physical file is SUCCESS — the goal state is "bytes absent".
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
        // Needed to release the derived face-preview blobs that die with this
        // blob's face detections.
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobService>();

        // Retry leftovers FIRST: rows whose BlobObject is already gone but whose
        // bytes survived a failed/interrupted unlink. Oldest first so a
        // persistently failing key cannot starve the ones behind it. These are
        // already-committed purge decisions, so they need no gate — only the
        // unlink has to succeed.
        var pending = await db.PendingBlobPurges
            .AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        var drained = 0;
        foreach (var leftover in pending)
        {
            if (await TryCompletePendingPurgeAsync(
                    db, storage, derivedStorage, hlsStorage,
                    leftover.BlobObjectId, leftover.StorageKey, leftover.Sha256,
                    cancellationToken))
            {
                drained++;
            }
        }

        if (drained > 0)
        {
            _logger.LogInformation(
                "BlobJanitor completed {Drained} retried physical blob purge(s).", drained);
        }

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

                        // Face previews are derived crops cached in the derived
                        // store as their OWN BlobObject rows, each holding one
                        // reference. Their FacePreview rows are about to vanish
                        // through the FaceDetection -> BlobObject cascade, which
                        // would strand those blobs at ReferenceCount 1 with no
                        // owner — invisible to this scan forever. Release them
                        // here so a later tick reclaims their bytes too.
                        var previewBlobIds = await db.FacePreviews
                            .Where(p => db.FaceDetections
                                .Any(d => d.Id == p.FaceDetectionId
                                    && d.BlobObjectId == candidate.Id))
                            .Select(p => p.BlobObjectId)
                            .ToListAsync(cancellationToken);

                        rowsDeleted = await db.BlobObjects
                            .Where(b => b.Id == candidate.Id
                                && b.ReferenceCount == 0
                                && b.PurgeEligibleAt == candidate.PurgeEligibleAt
                                && b.PurgeEligibleAt < cutoff)
                            .ExecuteDeleteAsync(cancellationToken);

                        if (rowsDeleted > 0)
                        {
                            foreach (var previewBlobId in previewBlobIds)
                            {
                                await blobs.ReleaseAsync(previewBlobId, cancellationToken);
                            }

                            // The storage key must outlive the row so a failed
                            // unlink stays retryable. Committed together with
                            // the row delete: either both happen or neither.
                            db.PendingBlobPurges.Add(new PendingBlobPurge
                            {
                                BlobObjectId = candidate.Id,
                                StorageKey = candidate.StorageKey,
                                Sha256 = candidate.Sha256,
                                CreatedAt = _clock.GetUtcNow().UtcDateTime,
                                AttemptCount = 0,
                            });
                            await db.SaveChangesAsync(cancellationToken);
                        }
                    }
                    catch (DbUpdateException ex)
                    {
                        // FK violation: a soft-deleted FileItem (or thumbnail)
                        // still references this blob. Roll back to undo the
                        // metadata delete and leave the blob in place; a future
                        // slice will handle the cascade.
                        await tx.RollbackAsync(cancellationToken);
                        // Drop the rolled-back pending record from the change
                        // tracker; leaving it Added would make the NEXT
                        // candidate's SaveChanges re-attempt this insert.
                        DetachPendingPurges(db);
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

                // The committed record is reached by ExecuteDelete/ExecuteUpdate
                // from here on, so stop tracking it.
                DetachPendingPurges(db);

                // Row gone, storage key durably recorded. Unlink the bytes; a
                // failure leaves the PendingBlobPurge row behind and the next
                // tick retries it, so nothing is lost either way.
                await TryCompletePendingPurgeAsync(
                    db, storage, derivedStorage, hlsStorage,
                    candidate.Id, candidate.StorageKey, candidate.Sha256,
                    cancellationToken);

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

    // The pending record is written once with Add/SaveChanges and touched only
    // by set-based statements afterwards, so it never needs to stay tracked.
    private static void DetachPendingPurges(AppDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<PendingBlobPurge>().ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    // Is this physical identity owned RIGHT NOW, by anything the model
    // recognises? Read fresh, under the exclusive lock, never from an earlier
    // scan — a PendingBlobPurge is an intent from an OLD ownership epoch, not
    // standing permission to delete that content forever.
    //
    // Checked by BOTH storage key and sha256: the key addresses the object and
    // the sha addresses its HLS ladder, and a blob row can be re-created for
    // the same content under a NEW id, so matching on the original BlobObjectId
    // would miss it entirely.
    private static async Task<bool> IsContentOwnedAsync(
        AppDbContext db, string storageKey, string sha256, CancellationToken cancellationToken)
    {
        if (await db.BlobObjects.AsNoTracking()
                .AnyAsync(b => b.StorageKey == storageKey || b.Sha256 == sha256, cancellationToken))
        {
            return true;
        }

        // Print artifacts share the content-addressed layout but are owned by a
        // plain column, with no BlobObject row of their own.
        return await db.PrintJobs.AsNoTracking()
            .AnyAsync(j => j.ArtifactStorageKey == storageKey, cancellationToken);
    }

    // Finishes ONE already-decided physical purge, or abandons it because the
    // content came back to life.
    //
    // The whole body runs inside a transaction holding the EXCLUSIVE
    // StorageMutationLock for this content, so no writer can publish or reuse
    // these bytes between the revalidation and the unlink. Writers hold the
    // shared lock from before their publish/reuse decision until their
    // ownership commit, so the two orderings are the only possibilities:
    // either we see their owner and spare the bytes, or they wait and then
    // publish the bytes themselves.
    //
    // Idempotent: a missing file is success, so a retry after a partial unlink
    // converges. Returns true when the record was cleared (deleted or retired),
    // false when the unlink failed and the record was KEPT for a later retry.
    private async Task<bool> TryCompletePendingPurgeAsync(
        AppDbContext db,
        IBlobStorage storage,
        IDerivedBlobStorage? derivedStorage,
        HlsDerivativeStorage? hlsStorage,
        Guid blobObjectId,
        string storageKey,
        string sha256,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            await StorageMutationLock.AcquireExclusiveAsync(db, sha256, cancellationToken);

            if (await IsContentOwnedAsync(db, storageKey, sha256, cancellationToken))
            {
                // Live again. The old intent is obsolete: retire it, or it
                // would attack the new owner on every future tick.
                await db.PendingBlobPurges
                    .Where(p => p.BlobObjectId == blobObjectId)
                    .ExecuteDeleteAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                _logger.LogInformation(
                    "BlobJanitor retired an obsolete pending purge: the content is owned again.");
                return true;
            }

            try
            {
                // Slice 72: a reclaimed blob may be an original or a derived
                // artifact and we don't track purpose on the row, so delete from
                // BOTH stores. Both calls are no-ops for a missing file, and when
                // the roots are the same the second is harmless.
                await storage.DeleteAsync(storageKey, cancellationToken);
                if (derivedStorage is not null)
                {
                    await derivedStorage.DeleteAsync(storageKey, cancellationToken);
                }
                // Video-hls: remove the (regenerable) HLS ladder for this
                // content. Idempotent; the blob_hls_derivatives row is already
                // gone via the FK cascade on the blob delete.
                hlsStorage?.Delete(sha256);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Keep the record. This is the whole point: the storage key
                // stays durable so the next tick can retry instead of leaking
                // bytes with no record that they exist. Commit the attempt
                // bookkeeping rather than rolling back, so a persistently
                // failing key is visible.
                await db.PendingBlobPurges
                    .Where(p => p.BlobObjectId == blobObjectId)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(p => p.AttemptCount, p => p.AttemptCount + 1)
                            .SetProperty(
                                p => p.LastAttemptAt,
                                _ => (DateTime?)_clock.GetUtcNow().UtcDateTime),
                        cancellationToken);
                await tx.CommitAsync(cancellationToken);
                _logger.LogWarning(ex, "Physical blob delete failed; kept for retry.");
                return false;
            }

            // Bytes are gone. Clear the record inside the SAME locked
            // transaction so the intent never outlives the deletion.
            await db.PendingBlobPurges
                .Where(p => p.BlobObjectId == blobObjectId)
                .ExecuteDeleteAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return true;
        });
    }
}
