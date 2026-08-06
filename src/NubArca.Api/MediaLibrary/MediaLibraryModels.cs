namespace NubArca.Api.MediaLibrary;

// Slice 94 — DTOs for media-library rules and effective state. Safe shapes:
// folder ids/names the owner already sees in their browser, rule fields, and
// counts — never paths, storage keys, blob ids, or coordinates.

public enum MediaKind
{
    Photo,
    Video,
}

public sealed record MediaLibraryRuleDto(
    Guid Id,
    Guid FolderId,
    string FolderName,
    string RuleType,
    bool AppliesToPhotos,
    bool AppliesToVideos,
    bool AppliesToChildren,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record MediaLibraryRulesResponse(
    IReadOnlyList<MediaLibraryRuleDto> Rules);

// Upsert request: one rule per folder.
public sealed record MediaLibraryRuleRequest(
    Guid FolderId,
    string RuleType,
    bool AppliesToPhotos,
    bool AppliesToVideos,
    bool AppliesToChildren);

// Where a kind's effective state comes from (for the UI badge):
//   default   — no applicable rule; media is included.
//   rule      — an explicit rule on THIS folder.
//   inherited — a rule on an ancestor folder (SourceFolder* identify it).
public sealed record MediaLibraryEffectiveKind(
    bool Excluded,
    string Source,
    Guid? SourceFolderId,
    string? SourceFolderName);

public static class MediaLibraryEffectiveSources
{
    public const string Default = "default";
    public const string Rule = "rule";
    public const string Inherited = "inherited";
}

public sealed record MediaLibraryEffectiveResponse(
    Guid FolderId,
    MediaLibraryEffectiveKind Photos,
    MediaLibraryEffectiveKind Videos,
    // The folder's own rule, when one exists (so the UI can edit/remove it).
    MediaLibraryRuleDto? Rule);

// Owner-scoped diagnostics: media-library eligibility + metadata-extraction
// coverage over the blobs the owner's active files reference. Counts only.
public sealed record MediaLibraryStatsResponse(
    int PhotosEligible,
    int PhotosExcluded,
    int VideosEligible,
    int VideosExcluded,
    int RuleCount,
    int BlobsTotal,
    int BlobsExtracted,
    int BlobsExtractionPending,
    int BlobsExtractionFailed,
    int BlobsWithDateTaken,
    int BlobsWithGps);

// Invalid rule input (unknown type, no kind selected). Mapped to HTTP 400.
public sealed class MediaLibraryValidationException : Exception
{
    public MediaLibraryValidationException(string message) : base(message) { }
}
