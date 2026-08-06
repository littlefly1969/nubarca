namespace NubArca.Api.Domain;

// Slice 93: one manifest file of a remote-staging upload session. Server-side
// item + chunk state is the source of truth for resumability: the browser
// asks the server which chunks are missing and uploads only those.
public class RemoteUploadItem
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    // Manifest order (1-based). Stable keyset/pagination key — relative paths
    // can be too long to index safely (same rationale as admin_import_items).
    public int Ordinal { get; set; }

    // Validated client-relative path ("a/b/c.jpg"); never absolute.
    public string RelativePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    // Client-reported last-modified, used only to re-match local files when
    // resuming after a reload (path + size + mtime). Never trusted as
    // metadata — server-side extraction stays authoritative at import.
    public DateTime? LastModifiedAt { get; set; }

    // See RemoteUploadItemStatuses.
    public string Status { get; set; } = RemoteUploadItemStatuses.Pending;

    public long ReceivedBytes { get; set; }

    // Fixed at manifest time from the configured chunk size; every chunk is
    // exactly ChunkSizeBytes except the final one (remainder).
    public int ChunkSizeBytes { get; set; }
    public int ExpectedChunkCount { get; set; }
    public int ReceivedChunkCount { get; set; }

    // Sanitized failure category + short message (see RemoteUploadFailureCodes).
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// pending → uploading → uploaded → verified. failed (verification/IO) and
// skipped (user chose to skip a drifted local file on resume) are terminal
// per-item; a failed item returns to pending when its chunks are re-uploaded.
public static class RemoteUploadItemStatuses
{
    public const string Pending = "pending";
    public const string Uploading = "uploading";
    public const string Uploaded = "uploaded";
    public const string Verified = "verified";
    public const string Failed = "failed";
    public const string Skipped = "skipped";

    public static bool IsKnown(string? status) => status is
        Pending or Uploading or Uploaded or Verified or Failed or Skipped;
}

public static class RemoteUploadFailureCodes
{
    public const string ChunksMissing = "chunks_missing";
    public const string SizeMismatch = "size_mismatch";
    public const string FileMissing = "file_missing";
    public const string IoError = "io_error";
}

// Slice 93: one received chunk. Composite key (ItemId, ChunkIndex) — the row's
// existence IS the receipt record, which makes chunk upload naturally
// idempotent (an insert that loses the unique race means the chunk was
// already received). The byte offset is derivable (ChunkIndex * item
// ChunkSizeBytes), so it is deliberately not stored.
public class RemoteUploadChunk
{
    public Guid ItemId { get; set; }
    public int ChunkIndex { get; set; }
    public int SizeBytes { get; set; }
    public DateTime ReceivedAt { get; set; }
}
