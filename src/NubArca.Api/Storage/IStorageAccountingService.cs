namespace NubArca.Api.Storage;

// Slice 65: owner-scoped logical storage accounting. "Logical" = the sum of
// the user's own FileItem.SizeBytes, including trashed-but-not-purged files,
// matching exactly what the upload quota enforces. Never reflects the global
// deduplicated physical footprint and never exposes other users' figures.
public interface IStorageAccountingService
{
    Task<UserStorageUsage> GetForUserAsync(Guid ownerUserId, CancellationToken cancellationToken = default);
}

// usedBytes / fileCount count ALL of the owner's FileItem rows (active +
// trash); permanently deleting a file removes its row and frees the space.
// quotaBytes / remainingBytes are null when no quota is configured
// (unlimited). No ids, names, paths, or storage internals.
public sealed record UserStorageUsage(
    long UsedBytes,
    int FileCount,
    long? QuotaBytes,
    long? RemainingBytes);
