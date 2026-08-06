using NubArca.Api.Domain;

namespace NubArca.Api.Organizer;

// ---------------------------------------------------------------------------
// Phase 2: "Organize photos by date" — request/options/result models. The API
// uses small string-keyed enums (stable wire vocabulary); they are parsed into
// the internal enums below by OrganizerOptions.TryParse, which is the single
// validation gate (rejects unknown values, bad scope combinations, and unsafe
// target-root names). Nothing here exposes storage internals.
// ---------------------------------------------------------------------------

public enum OrganizerScopeKind
{
    Selected,        // explicit file ids
    Folder,          // a folder's direct photos (folderId; null = root)
    FolderRecursive, // a folder and all descendants
    MediaLibrary,    // all of the owner's media-library photos
    All,             // all of the owner's photos
}

public enum OrganizerTemplate
{
    Year,            // yyyy
    YearMonth,       // yyyy/MM
    YearMonthDay,    // yyyy/MM/dd
    YearDatedDay,    // yyyy/yyyy-MM-dd
}

public enum MissingDateBehavior
{
    Skip,            // leave files without a capture date where they are
    FileCreated,     // explicitly fall back to the upload/import date
    UnknownFolder,   // move them into an "Unknown Date" folder
}

public enum ConflictPolicy
{
    Skip,            // leave the file if the target name is taken
    KeepBoth,        // append " (n)" to disambiguate (deterministic)
}

// Action decided for a single candidate (used in dry-run samples + counts).
public static class OrganizerActions
{
    public const string Move = "move";
    public const string Already = "already";
    public const string SkipMissing = "skip_missing";
    public const string SkipConflict = "skip_conflict";
    public const string ExactDuplicate = "exact_duplicate";
}

// The validated, internal options. Built only via TryParse.
public sealed record OrganizerOptions(
    OrganizerScopeKind Scope,
    Guid? FolderId,
    IReadOnlyList<Guid> FileIds,
    Guid? TargetRootFolderId,
    string? TargetRootName,
    OrganizerTemplate Template,
    MissingDateBehavior MissingBehavior,
    ConflictPolicy Conflict)
{
    public const int MaxSelectedFiles = 10_000;
    public const string DefaultTargetRootName = "Photos";

    // Parses + validates a raw request. Returns false with a safe, specific
    // error message on any invalid input so the endpoint maps it to 400.
    public static bool TryParse(PhotoOrganizerRequest request, out OrganizerOptions options, out string error)
    {
        options = null!;
        error = string.Empty;

        if (request is null)
        {
            error = "Request body is required.";
            return false;
        }

        if (!TryParseScope(request.Scope, out var scope))
        {
            error = "Invalid scope.";
            return false;
        }

        var fileIds = request.FileIds ?? Array.Empty<Guid>();
        if (scope == OrganizerScopeKind.Selected)
        {
            if (fileIds.Count == 0)
            {
                error = "At least one file id is required for the selected scope.";
                return false;
            }
            if (fileIds.Count > MaxSelectedFiles)
            {
                error = $"At most {MaxSelectedFiles} files can be organized at once.";
                return false;
            }
            if (fileIds.Any(id => id == Guid.Empty))
            {
                error = "File ids must be non-empty.";
                return false;
            }
        }

        if (!TryParseTemplate(request.Template, out var template))
        {
            error = "Invalid folder template.";
            return false;
        }
        if (!TryParseMissing(request.MissingDateBehavior, out var missing))
        {
            error = "Invalid missing-date behavior.";
            return false;
        }
        if (!TryParseConflict(request.ConflictPolicy, out var conflict))
        {
            error = "Invalid conflict policy.";
            return false;
        }

        // Target root name: a single folder segment (never a path). Empty/blank
        // means "use the chosen target folder directly". Default = "Photos".
        string? targetRootName;
        if (request.TargetRootName is null)
        {
            targetRootName = DefaultTargetRootName;
        }
        else if (string.IsNullOrWhiteSpace(request.TargetRootName))
        {
            targetRootName = null; // explicit "no extra root segment"
        }
        else if (!OrganizerPaths.IsValidSegment(request.TargetRootName.Trim()))
        {
            error = "Invalid target root folder name.";
            return false;
        }
        else
        {
            targetRootName = request.TargetRootName.Trim();
        }

        options = new OrganizerOptions(
            scope,
            scope is OrganizerScopeKind.Folder or OrganizerScopeKind.FolderRecursive ? request.FolderId : null,
            fileIds,
            request.TargetRootFolderId,
            targetRootName,
            template,
            missing,
            conflict);
        return true;
    }

    private static bool TryParseScope(string? raw, out OrganizerScopeKind scope)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "selected": scope = OrganizerScopeKind.Selected; return true;
            case "folder": scope = OrganizerScopeKind.Folder; return true;
            case "folder_recursive": scope = OrganizerScopeKind.FolderRecursive; return true;
            case "media_library": scope = OrganizerScopeKind.MediaLibrary; return true;
            case "all": scope = OrganizerScopeKind.All; return true;
            default: scope = default; return false;
        }
    }

    private static bool TryParseTemplate(string? raw, out OrganizerTemplate template)
    {
        switch (raw?.Trim())
        {
            case "yyyy": template = OrganizerTemplate.Year; return true;
            case "yyyy/MM": template = OrganizerTemplate.YearMonth; return true;
            case "yyyy/MM/dd": template = OrganizerTemplate.YearMonthDay; return true;
            case "yyyy/yyyy-MM-dd": template = OrganizerTemplate.YearDatedDay; return true;
            default: template = default; return false;
        }
    }

    private static bool TryParseMissing(string? raw, out MissingDateBehavior missing)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null: case "": case "skip": missing = MissingDateBehavior.Skip; return true;
            case "file_created": missing = MissingDateBehavior.FileCreated; return true;
            case "unknown_folder": missing = MissingDateBehavior.UnknownFolder; return true;
            default: missing = default; return false;
        }
    }

    private static bool TryParseConflict(string? raw, out ConflictPolicy conflict)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null: case "": case "keep_both": conflict = ConflictPolicy.KeepBoth; return true;
            case "skip": conflict = ConflictPolicy.Skip; return true;
            default: conflict = default; return false;
        }
    }
}

// Raw request body (camelCase JSON). All fields validated by OrganizerOptions.TryParse.
public sealed record PhotoOrganizerRequest(
    string? Scope,
    Guid? FolderId,
    IReadOnlyList<Guid>? FileIds,
    Guid? TargetRootFolderId,
    string? TargetRootName,
    string? Template,
    string? MissingDateBehavior,
    string? ConflictPolicy);

// Per-source candidate breakdown (counts only).
public sealed record OrganizerSourceCounts(
    int UserOverride,
    int MetadataOriginal,
    int MetadataFallback,
    int FileCreatedFallback,
    int Missing);

// Aggregate dry-run / run summary. Counts only — no ids, paths, or internals.
public sealed record OrganizerSummary(
    int CandidateCount,
    int WithDateCount,
    int MissingDateCount,
    int ToMoveCount,
    int AlreadyOrganizedCount,
    int SkippedMissingCount,
    int SkippedConflictCount,
    int ExactDuplicateRemovedCount,
    int FoldersToCreateCount,
    int EstimatedOperations,
    OrganizerSourceCounts BySource);

// One safe dry-run sample. Paths are the owner's own logical paths (already
// visible in the file UI); effectiveDate is owner-private and allowed here.
public sealed record OrganizerSample(
    string Name,
    string CurrentPath,
    string TargetPath,
    DateTime? EffectiveDateTaken,
    string DateTakenSource,
    string Action);

public sealed record PhotoOrganizerDryRunResponse(
    OrganizerSummary Summary,
    IReadOnlyList<OrganizerSample> Samples);

public sealed record PhotoOrganizerRunResponse(
    Guid RunId,
    Guid? JobId,
    string Status);

// Safe run-status projection for the owner UI. No file ids, no physical paths.
public sealed record PhotoOrganizerRunStatusResponse(
    Guid RunId,
    string Kind,
    string Status,
    bool CancellationPending,
    string Template,
    string? TargetRootName,
    string Scope,
    int CandidateCount,
    int MovedCount,
    int AlreadyOrganizedCount,
    int SkippedMissingDateCount,
    int SkippedConflictCount,
    int ExactDuplicateRemovedCount,
    int FailedCount,
    int FoldersCreatedCount,
    string? ErrorSummary,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt);

// Wire-name helpers so the persisted run + responses share one vocabulary.
public static class OrganizerTemplateNames
{
    public static string ToWire(OrganizerTemplate t) => t switch
    {
        OrganizerTemplate.Year => "yyyy",
        OrganizerTemplate.YearMonth => "yyyy/MM",
        OrganizerTemplate.YearMonthDay => "yyyy/MM/dd",
        _ => "yyyy/yyyy-MM-dd",
    };
}

public static class OrganizerScopeNames
{
    public static string ToWire(OrganizerScopeKind s) => s switch
    {
        OrganizerScopeKind.Selected => "selected",
        OrganizerScopeKind.Folder => "folder",
        OrganizerScopeKind.FolderRecursive => "folder_recursive",
        OrganizerScopeKind.MediaLibrary => "media_library",
        _ => "all",
    };
}
