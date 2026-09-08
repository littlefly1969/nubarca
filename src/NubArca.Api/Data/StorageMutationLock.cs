using Microsoft.EntityFrameworkCore;

namespace NubArca.Api.Data;

// Per-CONTENT-IDENTITY advisory lock: the storage-side sibling of
// TreeMutationLock, which serialises the owner's logical tree. This one
// serialises the physical object, and the two are deliberately different
// locks because they protect different things — dedup means one physical
// object can be reached by several owners, so an owner-keyed lock can never
// protect it.
//
// The invariant it exists to enforce:
//
//   No destructive physical delete may overlap the interval in which a writer
//   publishes (or reuses) those bytes and establishes durable ownership.
//
// Writers take SHARED, deleters take EXCLUSIVE, both keyed by the sha256 of
// the content. That is the whole physical identity: the object lives at
// objects/{sha[0:2]}/{sha[2:4]}/{sha} and its HLS ladder at
// hls/{sha[0:2]}/{sha[2:4]}/{sha}, so ONE key covers both namespaces, and a
// print artifact addressed by PrintJob.ArtifactStorageKey is covered by the
// same key as a BlobObject holding the identical bytes.
//
// Shared writers for the same content proceed concurrently — a re-upload storm
// of the same file never serialises. Only a purge of that exact content
// excludes them, and only for as long as it takes to revalidate and unlink.
//
// Lock key strategy mirrors TreeMutationLock: hashtextextended over a
// namespaced string, yielding a stable signed int8. Collisions across distinct
// content merely serialise two unrelated purges; they can never let a delete
// and a write of the SAME content overlap, because equal content always maps
// to an equal key. Correctness is unaffected by collisions in either direction.
//
// The 'blob:' prefix keeps this key space disjoint from TreeMutationLock's, so
// a content hash can never collide with an owner id.
//
// Both variants are the `_xact_` form: the lock is released by COMMIT or
// ROLLBACK, so a crashed process cannot strand it and no caller has to
// remember to unlock. This is why the caller must already be inside a
// transaction — a session-scoped lock on a pooled connection would outlive the
// work and deadlock the next user of that connection.
//
// SQLite (every unit-level fixture) has no advisory locks and is a single
// connection, which already serialises writers, so this is a no-op there.
// Cross-process semantics are proven against real PostgreSQL in
// StorageMutationLockConcurrencyTests.
public static class StorageMutationLock
{
    private const string AcquireSharedSql =
        "SELECT pg_advisory_xact_lock_shared(hashtextextended({0}, 0))";

    private const string AcquireExclusiveSql =
        "SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))";

    // For a writer, held from BEFORE the publish/reuse decision until the
    // transaction that commits ownership ends. Must be called INSIDE an open
    // transaction.
    public static Task AcquireSharedAsync(
        AppDbContext db, string sha256, CancellationToken cancellationToken) =>
        AcquireAsync(db, AcquireSharedSql, sha256, cancellationToken);

    // For a physical deleter, held from BEFORE the final ownership
    // revalidation until after the bytes are gone. Must be called INSIDE an
    // open transaction.
    public static Task AcquireExclusiveAsync(
        AppDbContext db, string sha256, CancellationToken cancellationToken) =>
        AcquireAsync(db, AcquireExclusiveSql, sha256, cancellationToken);

    private static async Task AcquireAsync(
        AppDbContext db, string sql, string sha256, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        await db.Database.ExecuteSqlRawAsync(
            sql,
            parameters: new object[] { $"blob:{sha256}" },
            cancellationToken: cancellationToken);
    }

    // The content identity behind a storage key. Physical keys are
    // objects/{a}/{b}/{sha256}, so the last segment IS the identity; the HLS
    // ladder is keyed by that same sha. Returns null for anything that is not
    // shaped like a content-addressed key, so a caller can decide what to do
    // rather than lock on garbage.
    public static string? ContentIdentityOf(string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return null;
        }

        var lastSlash = storageKey.LastIndexOf('/');
        var candidate = lastSlash < 0 ? storageKey : storageKey[(lastSlash + 1)..];
        if (candidate.Length != 64)
        {
            return null;
        }

        foreach (var c in candidate)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isHex)
            {
                return null;
            }
        }

        return candidate;
    }
}
