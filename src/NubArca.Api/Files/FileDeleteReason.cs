namespace NubArca.Api.Files;

// Explicit intent passed to IFileItemService.SoftDeleteAsync so the deleted-
// content tombstone ledger is only written for genuine user-intent deletes.
//
// The rule is deliberately fail-safe: a tombstone is recorded ONLY when the
// reason explicitly opts in (UserDelete / UserBulkDelete) AND the deleted file
// was the owner's LAST active occurrence of that exact content. Every other
// reason — and the default `Unspecified` — never records a tombstone. Callers
// must pass their intent explicitly; behaviour is never inferred from the
// caller's identity or name.
public enum FileDeleteReason
{
    // Safe default: never records a tombstone. Used by legacy/internal callers
    // and tests that have no user-delete intent to express.
    Unspecified = 0,

    // A single file explicitly deleted by its owner (DELETE /api/files/{id}).
    // May record a tombstone if it was the final active owner occurrence.
    UserDelete,

    // A folder / multi-file delete explicitly initiated by the owner (recursive
    // folder delete). May record a tombstone per content whose final active
    // owner occurrence is removed by the operation.
    UserBulkDelete,

    // Photo Organizer exact-duplicate cleanup — never a tombstone (the surviving
    // copy still holds the content; this is automatic maintenance, not intent).
    OrganizerExactDedupe,

    // Background maintenance / system cleanup — never a tombstone.
    SystemCleanup,

    // Move to Private Vault — never a tombstone (content is retained, not
    // discarded). Not currently routed through soft-delete, but named for
    // completeness and future callers.
    MoveToPrivateVault,

    // Restore / move operations — never a tombstone.
    Restore,

    // FileItemSweeper grace-window purge — never a tombstone (the tombstone
    // decision, if any, already happened at soft-delete time).
    Sweeper,

    // Blob reference-count repair — never a tombstone.
    RefcountRepair,
}

public static class FileDeleteReasonExtensions
{
    // A tombstone may be recorded only for explicit user-intent deletes. Every
    // other reason is maintenance/automatic and must never pollute the ledger.
    public static bool MayRecordTombstone(this FileDeleteReason reason)
        => reason is FileDeleteReason.UserDelete or FileDeleteReason.UserBulkDelete;

    // Safe, non-sensitive source label persisted on the tombstone row.
    public static string? ToTombstoneSource(this FileDeleteReason reason) => reason switch
    {
        FileDeleteReason.UserDelete => "manual_delete",
        FileDeleteReason.UserBulkDelete => "bulk_delete",
        _ => null,
    };
}
