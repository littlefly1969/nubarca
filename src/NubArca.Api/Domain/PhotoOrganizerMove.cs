namespace NubArca.Api.Domain;

// Phase 2: one durable manifest row per file actually moved by an organizer run.
// This is the audit trail (source → target) and the basis for a future "undo
// last run" — it records enough to move each file back to where it started.
//
// FileItemId / folder ids are INTERNAL bookkeeping and are never serialized into
// any API response. SourceName / TargetName are the logical file names the owner
// already sees; no physical path, storage key, SHA, or blob id is stored.
public class PhotoOrganizerMove
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    // The moved file (internal only).
    public Guid FileItemId { get; set; }

    // Where the file was before the move (null parent = the user's root).
    public Guid? SourceParentFolderId { get; set; }
    public string SourceName { get; set; } = string.Empty;

    // Where the file landed.
    public Guid? TargetParentFolderId { get; set; }
    public string TargetName { get; set; } = string.Empty;

    // The effective capture date used to bucket the file + how it was resolved
    // (see PhotoOrganizerDateSources). Owner-private; safe in this workflow.
    public DateTime EffectiveDateTaken { get; set; }
    public string DateTakenSource { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

// How the effective capture date used for organizing was resolved, in
// precedence order. Surfaced in dry-run samples + the manifest.
public static class PhotoOrganizerDateSources
{
    public const string UserOverride = "user_override";
    public const string MetadataOriginal = "metadata_original";
    public const string MetadataFallback = "metadata_fallback";
    public const string FileCreatedFallback = "file_created_fallback";
    public const string Missing = "missing";
}
