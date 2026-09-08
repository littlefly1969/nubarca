namespace NubArca.Api.Domain;

// Durable "the row is gone, the bytes are not yet" record for ONE reclaimed
// BlobObject.
//
// BlobJanitor deletes the BlobObject row and inserts this row in the SAME
// transaction, then unlinks the physical files. Only a successful unlink
// removes this row. That ordering is what makes physical purge retry-safe:
// the storage key survives the row it came from, so a failed/crashed unlink
// is retried on a later tick instead of silently leaking bytes forever.
//
// It is deliberately NOT a second deletion authority. Nothing decides here
// WHETHER bytes may go — that decision was already made and committed by the
// janitor's ReferenceCount == 0 / PurgeEligibleAt gate. This row only carries
// the keys needed to finish the unlink.
//
// StorageKey / Sha256 are storage internals and must never reach an API, DTO,
// log, diagnostic or CLI surface.
public class PendingBlobPurge
{
    // The reclaimed blob's id. Primary key: one pending purge per blob, so a
    // re-inserted row for the same blob is impossible and retries are naturally
    // idempotent.
    public Guid BlobObjectId { get; set; }

    // Physical key in the original (and, for a derived artifact, the derived)
    // store. The whole reason this row exists.
    public string StorageKey { get; set; } = string.Empty;

    // Content hash — the HLS ladder directory is keyed by it, so the ladder
    // cannot be removed without it.
    public string Sha256 { get; set; } = string.Empty;

    // When the BlobObject row was deleted and this record took over.
    public DateTime CreatedAt { get; set; }

    // Unlink attempts so far, for operator visibility into a persistently
    // failing key (a permission problem, a read-only mount).
    public int AttemptCount { get; set; }

    public DateTime? LastAttemptAt { get; set; }
}
