using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Storage;

namespace NubArca.Api.Lithophanes;

// All three artifact kinds are real owners for reconciliation and every
// destructive storage path. Ownership is committed with StoragePublish.
public static class LithophaneArtifacts
{
    public static IQueryable<string> OwnedKeys(AppDbContext db) =>
        db.LithophaneProjects.Select(x => x.PixelMapStorageKey!).Where(x => x != null)
            .Union(db.LithophaneProjects.Select(x => x.ArtifactStorageKey!).Where(x => x != null))
            .Union(db.LithophaneStrips.Select(x => x.StorageKey));

    public static Task<bool> OwnsAsync(AppDbContext db, string key, CancellationToken ct) =>
        OwnedKeys(db).AnyAsync(x => x == key, ct);

    public static async Task DeleteUnownedAsync(AppDbContext db, IDerivedBlobStorage store, string key, CancellationToken ct)
    {
        var identity = StorageMutationLock.ContentIdentityOf(key);
        if (identity is null) return;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await StorageMutationLock.AcquireExclusiveAsync(db, identity, ct);
            if (!await db.BlobObjects.AnyAsync(x => x.StorageKey == key || x.Sha256 == identity, ct)
                && !await db.PrintJobs.AnyAsync(x => x.ArtifactStorageKey == key, ct)
                && !await OwnsAsync(db, key, ct))
                await store.DeleteAsync(key, ct);
            await tx.CommitAsync(ct);
        });
    }
}
