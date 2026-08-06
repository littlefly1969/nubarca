using Microsoft.EntityFrameworkCore;

namespace NubArca.Api.Data;

// Per-owner advisory-lock helper for serialising all dangerous folder/file
// tree mutations on PostgreSQL. Acquired as the first step inside an open
// transaction; the `_xact_` variant auto-releases on commit / rollback so the
// caller never has to remember to unlock.
//
// Lock key strategy:
//   * Every mutation that touches the tree (create / move / soft-delete /
//     restore / permanent-delete of a Folder or FileItem) takes one lock per
//     owner. Two operations on the same owner serialise; two operations on
//     different owners never contend.
//   * The key is `hashtextextended(ownerUserId::text, 0)` — a stable signed
//     int8 derived from the GUID's canonical lowercase string form. Hash
//     collisions across distinct owners are negligible at personal-cloud
//     scale and would only cause occasional cross-owner serialisation, never
//     correctness loss.
//   * Granularity is intentionally per-owner (not per-folder or per-row):
//     simpler than maintaining a strict resource-locking order, deadlock-free,
//     and matches the threat model — a single user issuing concurrent tree
//     mutations from multiple tabs.
//
// SQLite (used by every unit-level test fixture) has no advisory locks. The
// in-memory single-connection model already serialises writes, so this helper
// is a no-op there. The PostgreSQL race coverage lives in dedicated
// Testcontainers tests.
public static class TreeMutationLock
{
    private const string AcquireSql = "SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))";

    // Must be called INSIDE an open transaction (`BeginTransactionAsync`).
    // Calling it outside a transaction would acquire a session-scoped lock
    // that would never be released — guarded against by the contract, not by
    // a runtime check (the lock is invisible to consumers either way).
    public static async Task AcquireAsync(
        AppDbContext db,
        Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            AcquireSql,
            parameters: new object[] { ownerUserId.ToString() },
            cancellationToken: cancellationToken);
    }
}
