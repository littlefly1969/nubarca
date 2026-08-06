namespace NubArca.Api.Uploads;

// Slice 93 — request/response DTOs for the web remote-staging upload flow.
// Every shape is safe to serialize: relative paths and stable categories only —
// no absolute server paths, storage keys, SHA-256, blob ids, payloads, or
// other internals ever cross this boundary.

// Safe client-visible limits (so the UI can preflight before uploading).
public sealed record StagingConfigResponse(
    bool Enabled,
    long MaxSessionBytes,
    long MaxFileBytes,
    int MaxFilesPerSession,
    int ChunkSizeBytes,
    int SessionTtlHours);

public sealed record StagingSessionCreateRequest(
    string? Name,
    // Null = the caller's own library. A different user requires admin.
    Guid? TargetUserId,
    Guid? DestinationFolderId,
    // deleted-content-import-skip: import options chosen up-front and carried
    // to the linked import run (default false = unchanged behaviour).
    bool SkipPreviouslyDeleted = false,
    bool SkipExistingContent = false);

public sealed record StagingManifestFile(
    string RelativePath,
    long SizeBytes,
    DateTime? LastModifiedAt);

public sealed record StagingManifestRequest(IReadOnlyList<StagingManifestFile> Files);

public sealed record StagingManifestResponse(
    Guid SessionId,
    string Status,
    int TotalFiles,
    long TotalBytes,
    int ChunkSizeBytes,
    // 0-byte manifest files are complete immediately (no chunks to upload).
    int AlreadyCompleteFiles);

// Import progress snapshot embedded in the session detail once an import run
// is linked (safe counters mirrored from the admin-import run row).
public sealed record StagingImportProgress(
    string Status,
    string? Phase,
    int ImportedFiles,
    int PendingFiles,
    int FailedFiles,
    int ConflictFiles,
    int SkippedFiles,
    // deleted-content-import-skip: disjoint from SkippedFiles.
    int SkippedPreviouslyDeletedFiles,
    int SkippedAlreadyPresentFiles,
    long ImportedBytes);

public sealed record StagingSessionResponse(
    Guid SessionId,
    string Name,
    string Status,
    Guid TargetUserId,
    Guid? DestinationFolderId,
    int TotalFiles,
    long TotalBytes,
    int ReceivedFiles,
    long ReceivedBytes,
    int VerifiedFiles,
    int FailedFiles,
    int ChunkSizeBytes,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? CompletedAt,
    string? LastErrorCode,
    string? LastErrorMessage,
    Guid? AdminImportRunId,
    StagingImportProgress? Import);

public sealed record StagingSessionListResponse(
    IReadOnlyList<StagingSessionResponse> Sessions,
    int Total);

public sealed record StagingItemDto(
    Guid ItemId,
    int Ordinal,
    string RelativePath,
    long SizeBytes,
    DateTime? LastModifiedAt,
    string Status,
    long ReceivedBytes,
    int ExpectedChunkCount,
    int ReceivedChunkCount,
    string? FailureCode,
    string? FailureMessage);

public sealed record StagingItemListResponse(
    Guid SessionId,
    IReadOnlyList<StagingItemDto> Items,
    int Total,
    int Page,
    int PageSize);

// Resume protocol: one page of not-yet-complete items with their missing
// chunk indices. The browser uploads exactly these.
public sealed record StagingMissingItem(
    Guid ItemId,
    int Ordinal,
    string RelativePath,
    long SizeBytes,
    DateTime? LastModifiedAt,
    IReadOnlyList<int> MissingChunks);

public sealed record StagingMissingResponse(
    Guid SessionId,
    int ChunkSizeBytes,
    IReadOnlyList<StagingMissingItem> Items,
    // Keyset: pass this back as afterOrdinal to get the next page.
    int? NextAfterOrdinal,
    bool HasMore);

public sealed record StagingChunkResponse(
    Guid ItemId,
    int ChunkIndex,
    bool AlreadyReceived,
    string ItemStatus,
    int ReceivedChunkCount,
    int ExpectedChunkCount);

public sealed record StagingVerifyResponse(
    Guid SessionId,
    string Status,
    int VerifiedFiles,
    // Items still missing chunks (resume and retry).
    int IncompleteFiles,
    // Items whose staged bytes were wrong/missing — their chunk state was
    // reset so the client re-uploads them.
    int CorruptFiles,
    bool ReadyToImport);

public sealed record StagingImportStartResponse(
    Guid SessionId,
    string Status,
    Guid AdminImportRunId,
    Guid JobId);

public sealed record StagingCancelResponse(
    Guid SessionId,
    string Status,
    bool CancellationRequested);

// Feature disabled / unconfigured. Mapped to HTTP 409.
public sealed class StagingUnavailableException : Exception
{
    public StagingUnavailableException(string message) : base(message) { }
}

// Invalid request content (paths, limits, chunk bounds). Mapped to HTTP 400.
public sealed class StagingValidationException : Exception
{
    public StagingValidationException(string message) : base(message) { }
}

// Valid request, wrong session state (e.g. chunk upload on a terminal
// session). Mapped to HTTP 409.
public sealed class StagingConflictException : Exception
{
    public StagingConflictException(string message) : base(message) { }
}

// The caller may not perform this operation (e.g. non-admin targeting another
// user). Mapped to HTTP 403.
public sealed class StagingForbiddenException : Exception
{
    public StagingForbiddenException(string message) : base(message) { }
}
