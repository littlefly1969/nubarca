using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Files;

// Owner-scoped ledger of intentionally-deleted exact content.
//
// The ONLY writer is RecordFinalOccurrenceDeletionAsync, called from
// FileItemService.SoftDeleteAsync AFTER the soft-delete row update but BEFORE
// the enclosing transaction commits (it shares the same scoped AppDbContext, so
// the ledger write commits atomically with the delete). It records a tombstone
// only when BOTH hold:
//   1. the delete reason explicitly opts in (user-intent delete), and
//   2. the owner has NO remaining active occurrence of that exact content —
//      counting the WHOLE owner library including Private Vault, so retaining a
//      copy anywhere (incl. the vault) suppresses the tombstone without ever
//      revealing that a vault copy exists.
public interface IDeletedContentTombstoneService
{
    // Records/updates a tombstone iff `reason` opts in and the just-deleted file
    // was the owner's final active occurrence of the blob's content. Safe to
    // call for every soft-delete; a no-op for maintenance/automatic reasons.
    Task RecordFinalOccurrenceDeletionAsync(
        Guid ownerUserId,
        Guid blobObjectId,
        FileDeleteReason reason,
        string? fileNameSnapshot,
        string? deletedFromPathSnapshot,
        CancellationToken cancellationToken = default);
}

public sealed class DeletedContentTombstoneService : IDeletedContentTombstoneService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IOptions<DeletedContentOptions> _options;
    private readonly ILogger<DeletedContentTombstoneService>? _logger;

    public DeletedContentTombstoneService(
        AppDbContext db,
        TimeProvider clock,
        IOptions<DeletedContentOptions> options,
        ILogger<DeletedContentTombstoneService>? logger = null)
    {
        _db = db;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public async Task RecordFinalOccurrenceDeletionAsync(
        Guid ownerUserId,
        Guid blobObjectId,
        FileDeleteReason reason,
        string? fileNameSnapshot,
        string? deletedFromPathSnapshot,
        CancellationToken cancellationToken = default)
    {
        // Intent gate: only explicit user-intent deletes can ever tombstone.
        if (!reason.MayRecordTombstone())
        {
            return;
        }

        // Final-occurrence gate: count ALL remaining active owner occurrences of
        // this exact content, INCLUDING Private Vault (IgnoreQueryFilters). The
        // just-deleted row already has DeletedAt set in this transaction, so a
        // DeletedAt == null filter naturally excludes it. If the owner still
        // holds the content anywhere, we record nothing (and never signal that
        // a retained copy might be in the vault).
        var remaining = await _db.FileItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(f => f.OwnerUserId == ownerUserId
                && f.BlobObjectId == blobObjectId
                && f.DeletedAt == null, cancellationToken);
        if (remaining > 0)
        {
            return;
        }

        // Resolve the blob's content hash → opaque keyed fingerprint. The raw
        // SHA never leaves this method.
        var sha256 = await _db.BlobObjects
            .AsNoTracking()
            .Where(b => b.Id == blobObjectId)
            .Select(b => b.Sha256)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrEmpty(sha256))
        {
            return;
        }

        var fingerprint = ContentFingerprint.Compute(_options.Value.Pepper, sha256);
        var now = _clock.GetUtcNow().UtcDateTime;
        var source = reason.ToTombstoneSource();

        var existing = await _db.OwnerDeletedContentTombstones
            .FirstOrDefaultAsync(t => t.OwnerUserId == ownerUserId
                && t.FingerprintScheme == ContentFingerprint.Scheme
                && t.ContentFingerprint == fingerprint, cancellationToken);

        if (existing is not null)
        {
            existing.LastDeletedAt = now;
            existing.DeletedCount += 1;
            existing.LastFileNameSnapshot = fileNameSnapshot;
            existing.LastDeletedFromPathSnapshot = deletedFromPathSnapshot;
            existing.Source = source;
            existing.UpdatedAt = now;
        }
        else
        {
            _db.OwnerDeletedContentTombstones.Add(new OwnerDeletedContentTombstone
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                ContentFingerprint = fingerprint,
                FingerprintScheme = ContentFingerprint.Scheme,
                FirstDeletedAt = now,
                LastDeletedAt = now,
                DeletedCount = 1,
                LastFileNameSnapshot = fileNameSnapshot,
                LastDeletedFromPathSnapshot = deletedFromPathSnapshot,
                Source = source,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (existing is null)
        {
            // Lost a race with a concurrent final-occurrence delete of the same
            // content: the row now exists. Detach our stale insert and fold this
            // deletion into the winner's row as an update.
            foreach (var entry in _db.ChangeTracker.Entries<OwnerDeletedContentTombstone>().ToList())
            {
                entry.State = EntityState.Detached;
            }

            var winner = await _db.OwnerDeletedContentTombstones
                .FirstOrDefaultAsync(t => t.OwnerUserId == ownerUserId
                    && t.FingerprintScheme == ContentFingerprint.Scheme
                    && t.ContentFingerprint == fingerprint, cancellationToken);
            if (winner is null)
            {
                _logger?.LogWarning("deleted-content: tombstone upsert race left no row for owner.");
                return;
            }
            winner.LastDeletedAt = now;
            winner.DeletedCount += 1;
            winner.LastFileNameSnapshot = fileNameSnapshot;
            winner.LastDeletedFromPathSnapshot = deletedFromPathSnapshot;
            winner.Source = source;
            winner.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
