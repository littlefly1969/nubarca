namespace NubArca.Api.Domain;

// Durable, operator-facing diagnostic for a single logical derivative target
// (one FileItem × one derivative size). It exists ONLY to explain why a
// derivative is NOT present: successful artifacts live in file_thumbnails and
// are never duplicated here. A successful (re)generation CLEARS the row, so a
// diagnostic row always corresponds to a derivative that is currently missing.
//
// The row carries no sensitive internals: no StorageKey, SHA, BlobId, owner id,
// raw metadata, GPS, secrets, or stack traces. ErrorCode is a stable machine
// code (see DerivativeErrorCodes); Message is an optional sanitized, bounded
// reason. DetectedContentType / DetectedFormat are non-sensitive snapshots of
// the source's sniffed type, kept on the row so the operator can aggregate
// failures by format ("how many are TIFF?") without re-joining.
public class DerivativeDiagnostic
{
    public Guid Id { get; set; }

    // The logical derivative target. (FileItemId, Size) is unique. Deleting the
    // FileItem cascades the diagnostic away — it is disposable state.
    public Guid FileItemId { get; set; }

    // small | medium | poster (see ThumbnailSizes).
    public string Size { get; set; } = string.Empty;

    // See Files.DerivativeStatuses: pending | skipped | not_eligible |
    // failed_transient | failed_permanent | cancelled. "succeeded" is never
    // stored — success is the presence of a file_thumbnails row plus the
    // absence of this one. Literal default to keep Domain free of a Files
    // dependency (matches how the vocab lives beside ThumbnailSizes).
    public string Status { get; set; } = "pending";

    // Stable machine code (see DerivativeErrorCodes). Null only for a bare
    // pending placeholder.
    public string? ErrorCode { get; set; }

    // Optional sanitized, bounded human reason. Never a raw exception string,
    // path, or storage key. Truncated to the column length on write.
    public string? Message { get; set; }

    // Non-sensitive source-type snapshots for operator aggregation by format.
    public string? DetectedContentType { get; set; }
    public string? DetectedFormat { get; set; }

    // How many times generation has been attempted for this target.
    public int AttemptCount { get; set; }

    public DateTime FirstAttemptedAt { get; set; }
    public DateTime LastAttemptedAt { get; set; }

    // When a transient failure becomes eligible for an automatic retry by the
    // default backfill. Null for permanent / not-eligible rows (retried only by
    // an explicit forced/retry-failed run).
    public DateTime? NextRetryAt { get; set; }

    // Generator backend + version that produced this outcome, so a future
    // backend (libvips / Rust / FFmpeg) can re-attempt rows recorded by an
    // older generator without a schema change. See DerivativeBackends /
    // DerivativeGenerators.
    public string? Backend { get; set; }
    public int GeneratorVersion { get; set; }
}
