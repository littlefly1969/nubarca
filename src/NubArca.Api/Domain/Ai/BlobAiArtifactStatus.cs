namespace NubArca.Api.Domain.Ai;

// Sparse per-(blob, profile, capability) processing status. This is the
// reindex/coverage source of truth for blob-level AI work.
//
// SPARSE BY DESIGN: a MISSING row means "not processed yet / implicit pending".
// Rows are created ONLY when a target reaches a terminal state (completed /
// skipped / failed). Creating a profile must NOT pre-materialise pending rows
// for every blob. Coverage is therefore computed against the eligible-blob set,
// not against rows in this table.
//
// `Skipped` is content-related and permanent only (see AiArtifactStatuses) —
// an unavailable provider is never recorded as skipped (that is handled in a
// later phase as a no-op, not here).
public class BlobAiArtifactStatus
{
    public Guid Id { get; set; }

    public Guid BlobObjectId { get; set; }

    public Guid ProfileId { get; set; }

    public string Capability { get; set; } = string.Empty;

    // One of AiArtifactStatuses (no stored "pending").
    public string Status { get; set; } = AiArtifactStatuses.Completed;

    // Sanitized machine-readable error code for a failed/skipped attempt.
    public string? ErrorCode { get; set; }

    // True when the outcome is permanent (a content skip, or an exhausted
    // failure that will not be retried).
    public bool IsPermanent { get; set; }

    public int AttemptCount { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
