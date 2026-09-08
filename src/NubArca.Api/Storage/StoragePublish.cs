using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;

namespace NubArca.Api.Storage;

// The one way to turn staged bytes into an owned physical object.
//
// Every writer that will record a storage key somewhere durable — a
// BlobObject row, PrintJob.ArtifactStorageKey, a blob_hls_derivatives row —
// goes through here, so the publish/reuse decision and the ownership commit
// always share a single transaction holding a shared StorageMutationLock on
// the content identity. A concurrent purge of the same content takes the
// exclusive lock and therefore cannot run between the two.
//
// The expensive part (reading and hashing the source) belongs to StageAsync
// and happens BEFORE any of this, outside the transaction.
public static class StoragePublish
{
    // Publishes `staged` and commits its ownership atomically.
    //
    // `commitOwnership` runs with the lock held and the bytes already visible;
    // it must perform the durable write that makes something own this object
    // (and must not commit the transaction itself).
    //
    // Joins an ambient transaction when the caller already has one — the lock
    // then lives until that outer transaction ends, which is strictly safer —
    // and otherwise opens and commits its own.
    public static async Task<BlobWriteResult> PublishOwnedAsync(
        AppDbContext db,
        IBlobStorage store,
        StagedBlobWrite staged,
        Func<BlobWriteResult, CancellationToken, Task> commitOwnership,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(commitOwnership);

        if (db.Database.CurrentTransaction is not null)
        {
            await StorageMutationLock.AcquireSharedAsync(db, staged.Sha256, cancellationToken);
            var joined = await store.PublishAsync(staged, cancellationToken);
            await commitOwnership(joined, cancellationToken);
            return joined;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            await StorageMutationLock.AcquireSharedAsync(db, staged.Sha256, cancellationToken);
            var write = await store.PublishAsync(staged, cancellationToken);
            await commitOwnership(write, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return write;
        });
    }

    // Runs `work` under a shared StorageMutationLock on `sha256`, in a
    // transaction, joining an ambient one when present.
    //
    // The general form behind the two named helpers. Use it for a writer whose
    // physical artifact lives in a namespace derived from the same content
    // identity but is not a content-addressed object itself — the HLS ladder,
    // which is keyed by sha and whose durable state is a blob_hls_derivatives
    // row. Publishing the ladder and committing that row must be atomic against
    // a purge, or the database can claim a ladder that has just been unlinked.
    public static async Task<T> UnderSharedLockAsync<T>(
        AppDbContext db,
        string sha256,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(work);

        // The transaction below exists ONLY to scope the advisory lock. Where
        // the provider has no advisory locks there is nothing to scope, and
        // opening one anyway is not merely wasteful: several scoped contexts
        // can share one SQLite connection, each sees its own CurrentTransaction
        // as null, and the second BeginTransaction fails with "does not support
        // nested transactions". Nothing is committed here, so skipping the
        // transaction loses no guarantee.
        if (!db.Database.IsNpgsql())
        {
            return await work(cancellationToken);
        }

        if (db.Database.CurrentTransaction is not null)
        {
            await StorageMutationLock.AcquireSharedAsync(db, sha256, cancellationToken);
            return await work(cancellationToken);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            await StorageMutationLock.AcquireSharedAsync(db, sha256, cancellationToken);
            var result = await work(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return result;
        });
    }

    // Ensures already-owned bytes are present under their key, without
    // establishing new ownership — the byte-placement repair case.
    //
    // The owner already exists and is durable, so nothing new is committed;
    // but the copy still must not race a purge of the same content, so it runs
    // under the same shared lock. `ensure` returns whether the bytes ended up
    // present.
    public static async Task<bool> RepairPlacementAsync(
        AppDbContext db,
        string sha256,
        Func<CancellationToken, Task<bool>> ensure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ensure);

        // The transaction below exists ONLY to scope the advisory lock. Where
        // the provider has no advisory locks there is nothing to scope, and
        // opening one anyway is not merely wasteful: several scoped contexts
        // can share one SQLite connection, each sees its own CurrentTransaction
        // as null, and the second BeginTransaction fails with "does not support
        // nested transactions". Nothing is committed here, so skipping the
        // transaction loses no guarantee.
        if (!db.Database.IsNpgsql())
        {
            return await ensure(cancellationToken);
        }

        if (db.Database.CurrentTransaction is not null)
        {
            await StorageMutationLock.AcquireSharedAsync(db, sha256, cancellationToken);
            return await ensure(cancellationToken);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            await StorageMutationLock.AcquireSharedAsync(db, sha256, cancellationToken);
            var ok = await ensure(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return ok;
        });
    }
}
