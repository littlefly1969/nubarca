namespace NubArca.Api.MediaLibrary;

// Slice 3: bulk per-file media-library exclusion / restore.

// Request body shared by both endpoints. Only file ids — folders are never
// supported (folder-level membership is the separate Slice 94 rules feature).
public sealed record MediaLibraryBulkRequest(IReadOnlyList<Guid>? FileIds);

// Owner-safe aggregate result — counts only, never which specific ids changed
// (that could leak the existence of another owner's ids). requested is the
// count AFTER de-duplication; changed + unchanged + notFoundOrNotOwned always
// sum to requested.
public sealed record MediaLibraryBulkResult(
    int Requested,
    int Changed,
    int Unchanged,
    int NotFoundOrNotOwned);
