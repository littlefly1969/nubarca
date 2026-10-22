namespace NubArca.Api.Domain.Ai;

// Where ONE owner-private document source stands in ONE indexing phase.
//
// The operational record the background job writes as it works, so indexing is
// observable and resumable rather than a CLI counter that vanishes with the
// process. It is NOT authority about content: `document_texts`, chunks and
// embeddings are still what retrieval reads, through live eligibility.
//
// A MISSING ROW MEANS IMPLICIT PENDING. Rows are written only for sources the
// job has actually touched; nothing materializes a pending row per candidate.
// A row whose SourceRevision or ProfileId no longer matches what the job would
// use today is stale, and stale is pending too.
//
// Contains no names, no paths, no text and no messages: ReasonCode is a
// sanitized token from a closed vocabulary.
public class DocumentSourceState
{
    public Guid Id { get; set; }

    // `(SourceKind, SourceId)` — see DocumentSourceKinds and the source
    // contract. For the `file` kind SourceId is the FileItem id.
    public string SourceKind { get; set; } = DocumentSourceKinds.File;

    public Guid SourceId { get; set; }

    // Denormalized owner, re-asserted from the live source on every write.
    public Guid OwnerUserId { get; set; }

    // One of DocumentSourcePhases.
    public string Phase { get; set; } = string.Empty;

    // One of DocumentSourceStatuses.
    public string Status { get; set; } = string.Empty;

    // Retryable failures since the source's revision last changed or the phase
    // last completed. Environment states (a model that is not installed) and
    // cancellation never count.
    public int Attempts { get; set; }

    // Sanitized reason token for the last non-completed outcome, or null.
    public string? ReasonCode { get; set; }

    // WHICH REVISION of the source this state describes (for `file`, the
    // content-addressed blob id). Internal only, never in a DTO or a log.
    public string SourceRevision { get; set; } = string.Empty;

    // The profile the phase ran with (extraction, text-embedding or visual
    // embedding profile), and the format version where one applies (the chunk
    // format). A completed row under a different profile or version is stale.
    public Guid? ProfileId { get; set; }

    public int? FormatVersion { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public static class DocumentSourcePhases
{
    /// Native extraction or OCR into `document_texts`.
    public const string Extract = "extract";

    /// Chunking into `document_chunks`.
    public const string Chunk = "chunk";

    /// Text embeddings for the domain's configured profile.
    public const string Embed = "embed";

    /// The visual page index, when the capability is enabled.
    public const string Visual = "visual";

    public static readonly IReadOnlyList<string> All = new[] { Extract, Chunk, Embed, Visual };
}

public static class DocumentSourceStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";

    /// A permanent CONTENT verdict about this revision. Not retried until the
    /// source's bytes change.
    public const string Skipped = "skipped";

    /// A retryable failure. Retried by later runs up to the attempt budget.
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All =
        new[] { Pending, Running, Completed, Skipped, Failed };
}
