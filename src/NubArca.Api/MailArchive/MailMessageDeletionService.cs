using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain.Mail;
using NubArca.Api.Storage;

namespace NubArca.Api.MailArchive;

public enum MailDeleteOutcome { Deleted, AlreadyDeleted, NotFound }

/// <summary>
/// Deletes one message, owner-scoped, in ONE transaction (contract §6):
/// tombstone → attachment links → manifest item → message row → release of
/// the document's blob reference. A crash anywhere rolls all of it back, so a
/// retry simply deletes again. Attachment FileItems are never touched: they
/// own their blob references themselves.
/// </summary>
public sealed class MailMessageDeletionService
{
    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly TimeProvider _clock;

    public MailMessageDeletionService(AppDbContext db, IBlobService blobs, TimeProvider clock)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
    }

    public async Task<MailDeleteOutcome> DeleteAsync(Guid owner, Guid messageId, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            var message = await _db.MailMessages
                .FirstOrDefaultAsync(m => m.Id == messageId && m.OwnerUserId == owner
                    && m.Status == MailMessageStatuses.Complete, cancellationToken);
            // Claim the row before touching anything: a conditional UPDATE
            // takes the row lock, so of two concurrent deletes exactly one
            // proceeds; the other waits, then matches nothing and reports
            // AlreadyDeleted instead of colliding on the tombstone. The
            // transient status is never committed (the row is deleted below).
            var claimed = message is null ? 0 : await _db.MailMessages
                .Where(m => m.Id == messageId && m.OwnerUserId == owner && m.Status == MailMessageStatuses.Complete)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MailMessageStatuses.Deleting), cancellationToken);
            if (message is null || claimed == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                _db.ChangeTracker.Clear();
                var tombstoned = await _db.MailDeletionTombstones.AsNoTracking()
                    .AnyAsync(t => t.OwnerUserId == owner && t.DeletedMessageId == messageId, cancellationToken);
                return tombstoned ? MailDeleteOutcome.AlreadyDeleted : MailDeleteOutcome.NotFound;
            }

            var fingerprint = await _db.MailArchives
                .Where(a => a.Id == message.ArchiveId)
                .Select(a => a.SourceFingerprint)
                .FirstAsync(cancellationToken);
            var now = _clock.GetUtcNow().UtcDateTime;

            var exists = await _db.MailDeletionTombstones.AnyAsync(
                t => t.OwnerUserId == owner && t.SourceFingerprint == fingerprint
                    && t.SourceMessageKey == message.SourceMessageKey, cancellationToken);
            if (!exists)
            {
                _db.MailDeletionTombstones.Add(new MailDeletionTombstone
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    SourceFingerprint = fingerprint,
                    SourceMessageKey = message.SourceMessageKey,
                    DeletedMessageId = message.Id,
                    DeletedAt = now,
                });
            }

            // Links only. The FileItems they named stay exactly as they are.
            await _db.MailAttachments
                .Where(a => a.MessageId == message.Id)
                .ExecuteDeleteAsync(cancellationToken);

            await _db.MailImportItems
                .Where(i => i.ArchiveId == message.ArchiveId && i.SourceMessageKey == message.SourceMessageKey)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, MailImportItemStatuses.Deleted)
                    .SetProperty(i => i.MessageId, (Guid?)null)
                    .SetProperty(i => i.UpdatedAt, now), cancellationToken);

            await _db.MailMessageTexts
                .Where(t => t.MessageId == message.Id)
                .ExecuteDeleteAsync(cancellationToken);

            var blobId = message.BlobObjectId;
            _db.MailMessages.Remove(message);
            await _db.SaveChangesAsync(cancellationToken);

            // Row gone first, then the reference: same order as PlateImage.
            if (blobId is Guid id)
            {
                await _blobs.ReleaseAsync(id, cancellationToken);
            }

            await _db.MailFolders
                .Where(f => f.Id == message.FolderId && f.MessageCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.MessageCount, f => f.MessageCount - 1), cancellationToken);
            await _db.MailArchives
                .Where(a => a.Id == message.ArchiveId && a.MessageCount > 0)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.MessageCount, a => a.MessageCount - 1)
                    .SetProperty(a => a.UpdatedAt, now), cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return MailDeleteOutcome.Deleted;
        });
    }
}
