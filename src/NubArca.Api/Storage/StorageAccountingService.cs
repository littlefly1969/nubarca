using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;

namespace NubArca.Api.Storage;

public sealed class StorageAccountingService : IStorageAccountingService
{
    private readonly AppDbContext _db;
    private readonly long _defaultQuotaBytes;

    public StorageAccountingService(AppDbContext db, IOptions<BlobStorageOptions> options)
    {
        _db = db;
        _defaultQuotaBytes = options.Value.DefaultUserQuotaBytes;
    }

    public async Task<UserStorageUsage> GetForUserAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        // Single owner-scoped aggregate. Counts every owned FileItem row
        // (active + trash); purged rows are gone and so no longer counted.
        // IgnoreQueryFilters: Private Vault files still consume real storage, so
        // they count toward the owner's quota. Folding them into the single total
        // also means moving files into the vault does not change the reported
        // number — no signal about whether the vault holds content.
        var agg = await _db.FileItems.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(f => f.OwnerUserId == ownerUserId)
            .GroupBy(_ => 1)
            .Select(g => new { Used = g.Sum(f => (long?)f.SizeBytes) ?? 0L, Count = g.Count() })
            .FirstOrDefaultAsync(cancellationToken);

        var used = agg?.Used ?? 0L;
        var count = agg?.Count ?? 0;

        if (_defaultQuotaBytes <= 0)
        {
            // Unlimited — no quota / remaining figures.
            return new UserStorageUsage(used, count, null, null);
        }

        var remaining = _defaultQuotaBytes - used;
        if (remaining < 0) remaining = 0;
        return new UserStorageUsage(used, count, _defaultQuotaBytes, remaining);
    }
}
