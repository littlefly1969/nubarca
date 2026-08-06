namespace NubArca.Api.Aesthetics;

// SAFE, no-leak DTOs for the Aesthetics Lab API. NONE of these ever carry a
// BlobObjectId, SHA, StorageKey, path, or RawOutputJson. Media is referenced by
// derived-only URLs.

// Grid/list row.
public sealed record AestheticLabItemDto(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    int? Width,
    int? Height,
    DateTime CreatedAt,
    // Latest run status (null when never analyzed) + the headline overall score.
    string? LatestRunStatus,
    string? LatestRunErrorCode,
    double? OverallScore,
    string ProfileKey,
    string ThumbnailUrl,
    string PreviewUrl);

// One normalized metric in a run.
public sealed record AestheticMetricDto(
    string Key,
    string Group,
    double Value,
    double ScaleMin,
    double ScaleMax,
    double? Confidence,
    int Version);

// One text result in a run (only present for completed text capabilities).
public sealed record AestheticTextDto(
    string Kind,
    string Language,
    string Text,
    int? PromptTemplateVersion);

// One immutable analysis run (history entry / current).
public sealed record AestheticRunDto(
    Guid Id,
    string Status,
    string ProfileKey,
    string? ModelName,
    string? ModelRevision,
    string? RuntimeName,
    string? RuntimeVersion,
    string PreprocessingProfileKey,
    IReadOnlyList<string> RequestedCapabilities,
    IReadOnlyList<string> CompletedCapabilities,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    long? DurationMs,
    string? ErrorCode,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<AestheticMetricDto> Metrics,
    IReadOnlyList<AestheticTextDto> Texts);

// Item detail: summary + latest run + history.
public sealed record AestheticLabItemDetailDto(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    int? Width,
    int? Height,
    DateTime CreatedAt,
    string PreviewUrl,
    AestheticRunDto? LatestRun,
    IReadOnlyList<AestheticRunSummaryDto> History);

// Compact history entry (no metrics; fetch a full run via its id if needed).
public sealed record AestheticRunSummaryDto(
    Guid Id,
    string Status,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    long? DurationMs,
    string? ErrorCode,
    IReadOnlyList<string> CompletedCapabilities,
    double? OverallScore);

// A page of lab items (cursor pagination).
public sealed record AestheticLabPageDto(
    IReadOnlyList<AestheticLabItemDto> Items,
    string? NextCursor);

// Result of a manual analysis request over a batch.
public sealed record AestheticAnalysisBatchResultDto(
    IReadOnlyList<AestheticAnalysisEnqueuedDto> Enqueued,
    IReadOnlyList<AestheticAnalysisSkippedDto> Skipped);

public sealed record AestheticAnalysisEnqueuedDto(Guid ItemId, Guid RunId, string Status);

public sealed record AestheticAnalysisSkippedDto(Guid ItemId, string Reason);

// Thrown by the lab service for a client-safe validation failure on upload.
public sealed class AestheticLabValidationException : Exception
{
    public const string TooLarge = "too_large";
    public const string NotAnImage = "not_an_image";
    public const string DimensionsTooLarge = "dimensions_too_large";

    public string Code { get; }

    public AestheticLabValidationException(string code) : base(code) => Code = code;
}
