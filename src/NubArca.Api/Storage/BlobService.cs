using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using Npgsql;

namespace NubArca.Api.Storage;

public sealed class BlobService : IBlobService
{
    private const string Sha256UniqueIndex = "ux_blob_objects_sha256";

    private readonly IBlobStorage _storage;
    private readonly IBlobStorage _derivedStorage;
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    // Slice 72: `derivedStorage` is the physical store for derived media
    // artifacts. Optional so the many direct-construction test sites keep
    // compiling; when null it falls back to the original store, which is the
    // single-root default (Storage:DerivedRootPath unset). DI passes the
    // registered IDerivedBlobStorage.
    public BlobService(
        IBlobStorage storage,
        AppDbContext db,
        TimeProvider clock,
        IDerivedBlobStorage? derivedStorage = null)
    {
        _storage = storage;
        _derivedStorage = derivedStorage ?? storage;
        _db = db;
        _clock = clock;
    }

    public async Task<BlobObject> StoreAsync(Stream content, CancellationToken cancellationToken = default)
        => (await StoreCoreAsync(_storage, content, cancellationToken)).Blob;

    // Slice 82: timing-instrumented variant for the admin import diagnostics.
    public Task<BlobStoreResult> StoreMeasuredAsync(Stream content, CancellationToken cancellationToken = default)
        => StoreCoreAsync(_storage, content, cancellationToken);

    // Slice 72: identical dedup/refcount path as StoreAsync, but the physical
    // bytes go to the derived store. WriteAsync is idempotent at the byte
    // level, so even when the BlobObject row already exists (shared SHA with an
    // original, or a regeneration of an existing artifact) the bytes are
    // ensured present in the derived root.
    public async Task<BlobObject> StoreDerivedAsync(Stream content, CancellationToken cancellationToken = default)
        => (await StoreCoreAsync(_derivedStorage, content, cancellationToken)).Blob;

    private async Task<BlobStoreResult> StoreCoreAsync(
        IBlobStorage target, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Stage first, OUTSIDE any transaction. Reading and hashing the source
        // can take minutes for a large upload and must never hold a database
        // connection, let alone a lock. The staged bytes are not yet visible
        // under their content-addressed key, so nothing can adopt them and
        // nothing can purge them.
        var staged = await target.StageAsync(content, cancellationToken);
        await using var _ = staged.ConfigureAwait(false);

        var blobDbStart = Stopwatch.GetTimestamp();
        // Now the identity is known, so the publish/reuse decision and the
        // ownership commit can be taken together under a shared lock.
        var (blob, write) = await PublishAndPersistAsync(target, staged, cancellationToken);
        var blobDbMillis = (long)Stopwatch.GetElapsedTime(blobDbStart).TotalMilliseconds;

        return new BlobStoreResult(
            blob,
            new BlobIngestTimings(write.ReadMillis, write.HashMillis, write.WriteMillis, blobDbMillis));
    }

    // The protected critical section: shared StorageMutationLock on the content
    // identity, then the publish/reuse decision, then the ownership commit —
    // all in ONE transaction, so an exclusive purge of this content can be
    // neither between the decision and the commit nor between the commit and
    // the bytes becoming reachable.
    //
    // Joins an ambient transaction when the caller already opened one (the lock
    // then lives until that outer transaction ends, which is strictly safer);
    // otherwise opens its own.
    private async Task<(BlobObject Blob, BlobWriteResult Write)> PublishAndPersistAsync(
        IBlobStorage target, StagedBlobWrite staged, CancellationToken cancellationToken)
    {
        if (_db.Database.CurrentTransaction is not null)
        {
            await StorageMutationLock.AcquireSharedAsync(_db, staged.Sha256, cancellationToken);
            var joinedWrite = await target.PublishAsync(staged, cancellationToken);
            return (await PersistBlobAsync(joinedWrite, cancellationToken), joinedWrite);
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await StorageMutationLock.AcquireSharedAsync(_db, staged.Sha256, cancellationToken);
            var write = await target.PublishAsync(staged, cancellationToken);
            var blob = await PersistBlobAsync(write, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return (blob, write);
        });
    }

    // Slice 95: NO explicit transaction. Every statement here is individually
    // atomic — the refcount increment is one guarded UPDATE, the insert is one
    // INSERT whose unique sha256 index arbitrates concurrent writers, and the
    // collision recovery is the same increment retry as before. Wrapping them
    // in BEGIN/COMMIT added two extra database round trips per stored file
    // (the dominant per-file "Blob DB" cost on a large import) without adding
    // any atomicity the statements don't already have. Crash behaviour is
    // unchanged: a refcount incremented for a file whose FileItem never
    // commits is released by the caller's catch path (or, worst case, leaks
    // one reference exactly as the transactional version did, since the blob
    // tx always committed before the FileItem tx).
    private async Task<BlobObject> PersistBlobAsync(
        BlobWriteResult write, CancellationToken cancellationToken)
    {
        var affected = await _db.BlobObjects
            .Where(b => b.Sha256 == write.Sha256)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(b => b.ReferenceCount, b => b.ReferenceCount + 1)
                    .SetProperty(b => b.PurgeEligibleAt, _ => null),
                cancellationToken);

        if (affected > 0)
        {
            return await ReadByShaAsync(write.Sha256, cancellationToken);
        }

        var fresh = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = write.Sha256,
            SizeBytes = write.SizeBytes,
            StorageKey = write.StorageKey,
            ReferenceCount = 1,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.BlobObjects.Add(fresh);

        // Shared locks do not exclude each other, so two writers of the SAME
        // content still race here — by design; they are both publishing the
        // identical bytes. A savepoint keeps that ordinary race recoverable now
        // that this runs inside a transaction: without it PostgreSQL would mark
        // the whole transaction aborted and the increment retry could not run.
        var transaction = _db.Database.CurrentTransaction;
        const string savepoint = "blob_insert";
        if (transaction is not null)
        {
            await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return fresh;
        }
        catch (DbUpdateException ex) when (IsSha256UniqueViolation(ex))
        {
            // Concurrent same-SHA insert won the unique index — retry as an
            // atomic increment of the winner's row.
            _db.Entry(fresh).State = EntityState.Detached;
            if (transaction is not null)
            {
                await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            }
            return await IncrementExistingAsync(write.Sha256, cancellationToken);
        }
    }

    private async Task<BlobObject> IncrementExistingAsync(string sha256, CancellationToken cancellationToken)
    {
        var affected = await _db.BlobObjects
            .Where(b => b.Sha256 == sha256)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(b => b.ReferenceCount, b => b.ReferenceCount + 1)
                    .SetProperty(b => b.PurgeEligibleAt, _ => null),
                cancellationToken);

        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"BlobObject row for sha256={sha256} disappeared between insert conflict and increment retry.");
        }

        return await ReadByShaAsync(sha256, cancellationToken);
    }

    public async Task<Stream> OpenContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
    {
        var blob = await _db.BlobObjects
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == blobObjectId, cancellationToken);

        if (blob is null)
        {
            // Should not happen in practice: FileItem.BlobObjectId FK is RESTRICT,
            // so a referenced BlobObject cannot be deleted. Surface as a corruption
            // signal rather than a silent null.
            throw new InvalidOperationException(
                $"BlobObject '{blobObjectId}' was not found.");
        }

        return await _storage.OpenReadAsync(blob.StorageKey, cancellationToken);
    }

    public async Task<Stream?> OpenDerivedContentAsync(
        Guid blobObjectId, CancellationToken cancellationToken = default)
    {
        var blob = await _db.BlobObjects
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == blobObjectId, cancellationToken);

        if (blob is null)
        {
            return null;
        }

        // Probe the derived root. A miss is expected and recoverable (wiped
        // cache, or a pre-slice-72 artifact still in the original root): return
        // null so the caller regenerates into the derived root rather than
        // throwing a corruption signal.
        if (!await _derivedStorage.ExistsAsync(blob.StorageKey, cancellationToken))
        {
            return null;
        }

        return await _derivedStorage.OpenReadAsync(blob.StorageKey, cancellationToken);
    }

    public async Task ReleaseAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        // Atomic decrement-iff-positive. If the row is missing or already at 0,
        // the WHERE matches 0 rows and ExecuteUpdateAsync is a no-op. Two
        // concurrent releases on the same row serialise at the row lock; the
        // ReferenceCount > 0 predicate guarantees neither can take it negative.
        // Expressions observe the pre-update value, so ReferenceCount == 1 is
        // exactly the transition to zero that starts the purge grace window.
        await _db.BlobObjects
            .Where(b => b.Id == blobObjectId && b.ReferenceCount > 0)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(b => b.ReferenceCount, b => b.ReferenceCount - 1)
                    .SetProperty(
                        b => b.PurgeEligibleAt,
                        b => b.ReferenceCount == 1 ? now : b.PurgeEligibleAt),
                cancellationToken);
    }

    public async Task MarkPurgeEligibleIfUnreferencedAsync(
        Guid blobObjectId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await _db.BlobObjects
            .Where(b => b.Id == blobObjectId && b.ReferenceCount == 0)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(b => b.PurgeEligibleAt, _ => (DateTime?)now),
                cancellationToken);
    }

    public async Task<BlobObject> AcquireExistingAsync(
        Guid blobObjectId, CancellationToken cancellationToken = default)
    {
        // Atomic increment for a known-existing blob id — no bytes read/hashed.
        // Guarded by the id predicate; 0 rows means the blob does not exist.
        var affected = await _db.BlobObjects
            .Where(b => b.Id == blobObjectId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(b => b.ReferenceCount, b => b.ReferenceCount + 1)
                    .SetProperty(b => b.PurgeEligibleAt, _ => null),
                cancellationToken);
        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"BlobObject '{blobObjectId}' does not exist; cannot acquire a reference.");
        }

        var blob = await _db.BlobObjects.AsNoTracking()
            .FirstAsync(b => b.Id == blobObjectId, cancellationToken);
        return blob;
    }

    // Slice 96: copy-by-key repair (original root → derived root) for derived
    // artifacts displaced by a root split. Pure byte streaming — no image
    // decode, no DB mutation. Race-safe: WriteAsync stages into a temp file
    // and atomically renames into place; a concurrent request that restored
    // the same key first simply makes this call observe AlreadyExisted. The
    // content is re-hashed during the copy, so a corrupt source (bytes no
    // longer matching their storage key) cannot be installed under the
    // expected key — it lands as an unreferenced object the reconcile CLI
    // reports, and this method returns false so the caller regenerates.
    public async Task<bool> TryRestoreDerivedFromOriginalAsync(
        Guid blobObjectId, CancellationToken cancellationToken = default)
    {
        if (ReferenceEquals(_derivedStorage, _storage))
        {
            // Single-root deployment: the derived "miss" already proved the
            // bytes are absent from the only root there is.
            return false;
        }

        var blob = await _db.BlobObjects
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == blobObjectId, cancellationToken);
        if (blob is null)
        {
            return false;
        }

        // Byte-placement repair for ownership that ALREADY exists and is
        // durable, so nothing new is committed here. It still runs under the
        // shared lock: a purge of this same content must not unlink the source
        // (or the copy) while we are re-placing it.
        return await StoragePublish.RepairPlacementAsync(
            _db,
            blob.Sha256,
            async ct =>
            {
                if (await _derivedStorage.ExistsAsync(blob.StorageKey, ct))
                {
                    return true; // raced: someone else already restored it
                }

                if (!await _storage.ExistsAsync(blob.StorageKey, ct))
                {
                    return false; // missing from both roots — only regeneration helps
                }

                await using var source = await _storage.OpenReadAsync(blob.StorageKey, ct);
                var staged = await _derivedStorage.StageAsync(source, ct);
                await using var stagedScope = staged.ConfigureAwait(false);
                var write = await _derivedStorage.PublishAsync(staged, ct);
                return string.Equals(write.StorageKey, blob.StorageKey, StringComparison.Ordinal);
            },
            cancellationToken);
    }

    private Task<BlobObject> ReadByShaAsync(string sha256, CancellationToken cancellationToken)
    {
        return _db.BlobObjects
            .AsNoTracking()
            .FirstAsync(b => b.Sha256 == sha256, cancellationToken);
    }

    private static bool IsSha256UniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation
            && pg.ConstraintName == Sha256UniqueIndex;
    }
}
