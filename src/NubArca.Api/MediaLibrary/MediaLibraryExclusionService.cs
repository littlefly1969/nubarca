using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.MediaLibrary;

public interface IMediaLibraryExclusionService
{
    // Active → Excluded for the owner's own, non-vault, non-deleted files.
    Task<MediaLibraryBulkResult> ExcludeAsync(
        Guid ownerUserId, IReadOnlyList<Guid> fileIds, CancellationToken cancellationToken = default);

    // Excluded → Active for the owner's own, non-vault, non-deleted files.
    Task<MediaLibraryBulkResult> RestoreAsync(
        Guid ownerUserId, IReadOnlyList<Guid> fileIds, CancellationToken cancellationToken = default);
}

// Slice 3 (media organization): the ONLY writer of FileItem.MediaLibraryState.
// A DB-only logical toggle — blob bytes, derivatives, embeddings, album
// membership, metadata, and ParentFolderId are never touched. No AI jobs are
// scheduled: excluding a file simply makes it non-eligible for NEW work (the
// candidate queries re-check state), and restoring re-uses whatever artifacts
// already exist. Idempotent and owner-scoped; operates through the default
// (vault-filtered) FileItems set, so Private Vault content is never affected.
public sealed class MediaLibraryExclusionService : IMediaLibraryExclusionService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public MediaLibraryExclusionService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public Task<MediaLibraryBulkResult> ExcludeAsync(
        Guid ownerUserId, IReadOnlyList<Guid> fileIds, CancellationToken cancellationToken = default)
        => TransitionAsync(ownerUserId, fileIds, MediaLibraryState.Active, MediaLibraryState.Excluded, cancellationToken);

    public Task<MediaLibraryBulkResult> RestoreAsync(
        Guid ownerUserId, IReadOnlyList<Guid> fileIds, CancellationToken cancellationToken = default)
        => TransitionAsync(ownerUserId, fileIds, MediaLibraryState.Excluded, MediaLibraryState.Active, cancellationToken);

    private async Task<MediaLibraryBulkResult> TransitionAsync(
        Guid ownerUserId,
        IReadOnlyList<Guid> fileIds,
        MediaLibraryState from,
        MediaLibraryState to,
        CancellationToken cancellationToken)
    {
        var distinct = (fileIds ?? Array.Empty<Guid>()).Distinct().ToList();
        if (distinct.Count == 0)
        {
            return new MediaLibraryBulkResult(0, 0, 0, 0);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            // Owner-scoped, non-deleted, non-vault (the default global filter).
            var owned = await _db.FileItems
                .Where(f => distinct.Contains(f.Id)
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null)
                .Select(f => new { f.Id, f.MediaLibraryState })
                .ToListAsync(cancellationToken);

            var toChange = owned.Where(x => x.MediaLibraryState == from).Select(x => x.Id).ToList();
            var changed = 0;
            if (toChange.Count > 0)
            {
                changed = await _db.FileItems
                    .Where(f => toChange.Contains(f.Id) && f.OwnerUserId == ownerUserId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(f => f.MediaLibraryState, _ => to)
                        .SetProperty(f => f.MediaLibraryStateChangedAt, _ => (DateTime?)now),
                        cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);

            var unchanged = owned.Count - changed;
            var notFound = distinct.Count - owned.Count;
            return new MediaLibraryBulkResult(distinct.Count, changed, unchanged, notFound);
        });
    }
}
