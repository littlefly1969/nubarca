namespace NubArca.Api.Domain;

// One REPLAYABLE owner ingestion operation (mobile-sync-v1).
//
// Purpose: make an ambiguous retry of a single upload safe WITHOUT ever making
// the client authoritative for content identity. A device whose success
// response was lost re-sends the SAME opaque operation key; this row is how the
// server answers with the ORIGINAL logical result instead of ingesting twice.
//
// What this row deliberately is NOT:
//   * NOT blob identity — physical deduplication remains exclusively the
//     SHA-256 content-addressed BlobObject model (BlobService). Two different
//     keys carrying identical bytes still deduplicate through the normal path;
//     two different contents never share a key-to-result mapping.
//   * NOT authorization — the owner below comes from the authenticated cookie
//     on every request, never from the client payload, and the unique scope is
//     (OwnerUserId, OperationKey), so the same key under two owners addresses
//     two independent operations.
//
// Lifecycle: a claim inserts a Pending row (the unique index arbitrates
// concurrent claims). Success flips Status to Completed and records the ONE
// FileItem that ingestion produced. Any failure before completion deletes the
// row so a later attempt starts clean ("a failed operation is never cached as
// successful"). LeaseExpiresAt bounds how long a crashed claim blocks its key:
// past the lease a stale Pending row may be taken over, relying — as the last
// resort — on the ordinary same-name sibling rule to keep exactly one FileItem.
public class UploadOperation
{
    public Guid Id { get; set; }

    // The authenticated owner this operation belongs to. Every read, claim and
    // completion is scoped by this id.
    public Guid OwnerUserId { get; set; }

    // Opaque, non-secret client-chosen operation identity. Stable across the
    // retries of ONE logical upload; different for different logical uploads.
    public string OperationKey { get; set; } = string.Empty;

    // "pending" | "completed" (see Uploads.UploadOperationStatus).
    public string Status { get; set; } = UploadOperationStatus.Pending;

    // The single FileItem this operation produced. Null while pending.
    public Guid? FileItemId { get; set; }

    public DateTime CreatedAt { get; set; }

    // Crash-recovery bound only. It is NOT an upload deadline and never cancels
    // anything; it only says how old an uncompleted claim may be before
    // another request may take the key over.
    public DateTime LeaseExpiresAt { get; set; }
}

public static class UploadOperationStatus
{
    public const string Pending = "pending";
    public const string Completed = "completed";
}