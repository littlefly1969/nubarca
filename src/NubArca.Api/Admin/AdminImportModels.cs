namespace NubArca.Api.Admin;

// Slice 81 — request/response DTOs for the admin server-side import workflow.
// Every shape is deliberately safe to serialize: no absolute physical paths,
// storage keys, SHA-256, blob ids, or other internals. Roots are referenced by
// an opaque RootId + a display Label; locations are safe relative paths.

public sealed record AdminImportRootDto(string RootId, string Label);

// Slice 83: the configured throttle knobs, surfaced so the admin UI can show
// how imports are paced. 0 means "off / unlimited".
public sealed record AdminImportThrottleConfig(
    int DelayBetweenFilesMs,
    long MaxBytesPerSecond,
    int MaxRunMinutes,
    int YieldEveryFiles);

public sealed record AdminImportRootsResponse(
    bool Enabled,
    bool Configured,
    IReadOnlyList<AdminImportRootDto> Roots,
    AdminImportThrottleConfig Throttle);

public sealed record AdminImportDirectoryEntry(
    string Name,
    string RelativePath,
    int ChildDirectoryCount,
    int FileCount);

public sealed record AdminImportBrowseResponse(
    string RootId,
    string RelativePath,
    string? ParentRelativePath,
    IReadOnlyList<AdminImportDirectoryEntry> Directories);

public sealed record AdminImportUserDto(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsAdmin,
    bool IsActive);

public sealed record AdminImportFolderDto(Guid Id, string Name);

public sealed record AdminImportFoldersResponse(
    Guid TargetUserId,
    Guid? ParentFolderId,
    IReadOnlyList<AdminImportFolderDto> Folders);

public sealed record AdminImportPreviewRequest(
    string RootId,
    string? RelativePath,
    Guid TargetUserId,
    Guid? DestinationFolderId);

public sealed record AdminImportPreviewResponse(
    int TotalFiles,
    int TotalDirectories,
    long TotalBytes,
    int SkippedSymlinks,
    int SkippedUnsupported,
    int UnreadableCount,
    bool Truncated,
    IReadOnlyList<string> Warnings);

public sealed record AdminImportRunRequest(
    string RootId,
    string? RelativePath,
    Guid TargetUserId,
    Guid? DestinationFolderId,
    // deleted-content-import-skip: server-applied import options (default false
    // = unchanged behaviour). "Previously deleted" reads the owner's deleted-
    // content tombstone ledger; "existing content" checks the owner's active
    // normal library (Private Vault excluded). Exact content only, no filename.
    bool SkipPreviouslyDeleted = false,
    bool SkipExistingContent = false);

public sealed record AdminImportRunResponse(
    Guid ImportRunId,
    Guid? JobId,
    string Status);

// Slice 82 — L1 aggregate metrics, derived from persisted counters + timestamps.
// Any field is null when not computable (e.g. run not finished, zero files).
public sealed record AdminImportRunMetrics(
    long? DurationMillis,
    double? FilesPerSecond,
    double? BytesPerSecond,
    double? ConflictPercent,
    double? SkippedPercent,
    double? FailedPercent,
    long? AverageImportedFileBytes);

// Slice 82 — L2 per-phase timing totals (ms) for the whole run. Null = not
// measured (old runs, or no files imported yet).
public sealed record AdminImportPhaseTimings(
    long? ReadMillis,
    long? HashMillis,
    long? WriteMillis,
    long? BlobDbMillis,
    // Slice 95: minimal media detection (split from Metadata, which now
    // measures full embedded extraction only — 0 on the deferred path).
    long? DetectMillis,
    long? MetadataMillis,
    long? FileItemMillis,
    long? ThumbnailMillis,
    long? FolderMillis,
    // Slice 95: import-item bookkeeping (page claims + terminal marks).
    long? ItemDbMillis);

// Slice 84: a safe sample of a conflict/already-imported file. RelativePath is
// the source/destination relative path (never absolute). Reason categories:
//   "preexisting"               — collided with a file that predates this run.
//   "already-imported-this-run" — re-detected by a resume/retry of this run.
// Slice 92: derived from the persisted import items (detail view only).
public sealed record AdminImportConflictSample(string RelativePath, string Reason);

public sealed record AdminImportRunStatusResponse(
    Guid ImportRunId,
    Guid? JobId,
    string Status,
    bool CancelRequested,
    // Slice 92: sub-phase while running ("scanning" | "importing"; else null).
    string? Phase,
    // Safe descriptors of what the run targeted (no absolute paths).
    string RootId,
    string SourceRelativePath,
    Guid TargetUserId,
    string? TargetUserEmail,
    Guid? DestinationFolderId,
    int ScannedFiles,
    // Slice 92: manifest files not yet processed (scan done, import pending).
    int PendingFiles,
    int ImportedFiles,
    int SkippedFiles,
    // deleted-content-import-skip: disjoint from SkippedFiles.
    int SkippedPreviouslyDeletedFiles,
    int SkippedAlreadyPresentFiles,
    int FailedFiles,
    int ConflictFiles,
    // Subset of ImportedFiles re-detected on resume (not fresh ingestion).
    int AlreadyImportedFiles,
    // Files frozen unprocessed when the run was cancelled.
    int CancelledFiles,
    long ImportedBytes,
    long TotalBytes,
    int TotalDirectories,
    string? CurrentRelativePath,
    string? Error,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    // Slice 92: when the scan phase fully persisted the manifest.
    DateTime? ScanCompletedAt,
    AdminImportRunMetrics Metrics,
    AdminImportPhaseTimings Timings,
    IReadOnlyList<AdminImportConflictSample> ConflictSamples);

// Slice 92 — one safe manifest item row. Relative paths + stable categories
// only: no FileItemId, absolute path, storage key, SHA, or blob id ever.
public sealed record AdminImportItemDto(
    string RelativePath,
    string Kind,
    long SizeBytes,
    string Status,
    string? FailureCategory,
    string? FailureMessage,
    string? ConflictCategory,
    int Attempts,
    DateTime? SourceModifiedAt,
    DateTime? CompletedAt);

public sealed record AdminImportItemListResponse(
    Guid ImportRunId,
    IReadOnlyList<AdminImportItemDto> Items,
    int Total,
    int Page,
    int PageSize);

// Slice 92 — result of requesting a derivatives backfill for a run.
public sealed record AdminImportEnqueueDerivativesResponse(
    Guid ImportRunId,
    Guid JobId,
    string JobStatus);

public sealed record AdminImportRunListResponse(
    IReadOnlyList<AdminImportRunStatusResponse> Runs,
    int Total,
    int Limit,
    int Offset);

public sealed record AdminImportCancelResponse(
    bool CancellationRequested,
    string Status);

// The feature is disabled, or enabled but no roots are configured. Mapped to
// HTTP 409 with a clear, non-sensitive message.
public sealed class AdminImportUnavailableException : Exception
{
    public AdminImportUnavailableException(string message) : base(message) { }
}

// The request referenced an unknown root, an escaping/traversing path, an
// internal storage location, or a missing target user/folder. Mapped to HTTP
// 400 (validation) or 404 (missing target), per the throw site.
public sealed class AdminImportValidationException : Exception
{
    public AdminImportValidationException(string message) : base(message) { }
}
