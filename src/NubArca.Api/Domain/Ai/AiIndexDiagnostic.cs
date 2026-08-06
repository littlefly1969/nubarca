namespace NubArca.Api.Domain.Ai;

// Generic AI diagnostic row, able to describe a failure/transient condition for
// ANY target: a blob, a document chunk, a face detection, an owner-scoped
// clustering run, an annotation, or a provider-availability event. TargetKind
// says which, and the nullable target-id columns carry the correlation id.
//
// The target-id columns are deliberately PLAIN correlation ids (no FK
// constraints): a diagnostic is an internal log row whose lifecycle must not
// couple to or block deletion of blobs/files/profiles, and it is never exposed
// per-row through any API/CLI (only aggregate counts are surfaced).
//
// MUST NOT store: stack traces, raw model payloads, raw vectors, storage keys,
// physical paths, blob SHA, or secrets. SanitizedMessage is short + truncated.
public class AiIndexDiagnostic
{
    public Guid Id { get; set; }

    public string Capability { get; set; } = string.Empty;

    // Profile the work belonged to, when applicable. Plain correlation id.
    public Guid? ProfileId { get; set; }

    // One of AiDiagnosticTargetKinds.
    public string TargetKind { get; set; } = string.Empty;

    // Nullable, heterogeneous correlation ids — at most one is typically set.
    public Guid? BlobObjectId { get; set; }
    public Guid? DocumentChunkId { get; set; }
    public Guid? FaceDetectionId { get; set; }

    // Set only for owner-scoped diagnostics (e.g. clustering).
    public Guid? OwnerUserId { get; set; }

    // Sanitized machine-readable code (e.g. exception type name).
    public string ErrorCode { get; set; } = string.Empty;

    // True = permanent (won't retry); false = transient (e.g. provider unavailable).
    public bool IsPermanent { get; set; }

    public int AttemptCount { get; set; }

    // Optional short, truncated, content-free message.
    public string? SanitizedMessage { get; set; }

    public DateTime OccurredAt { get; set; }
}
