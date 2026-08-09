namespace NubArca.Api.Domain;

// One owner-scoped Cloud Function run that removes redundant logical
// FileItems backed by the same immutable SHA-256 BlobObject. The background
// job carries only this row's id; aggregate counters are safe for the owner UI
// and audit log and never expose a hash, blob id, or logical path.
public class MediaDuplicateCleanupRun
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Status { get; set; } = MediaDuplicateCleanupStatuses.Queued;
    public int DuplicateGroupCount { get; set; }
    public int FilesRemovedCount { get; set; }
    public int FilesRetainedCount { get; set; }
    public string? ErrorSummary { get; set; }
    public Guid? JobId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class MediaDuplicateCleanupStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string? status) => status is Succeeded or Failed or Cancelled;
}
