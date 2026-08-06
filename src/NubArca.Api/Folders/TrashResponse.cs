using NubArca.Api.Files;

namespace NubArca.Api.Folders;

// Response shape for GET /api/trash and GET /api/trash/folders/{id}/children.
public sealed record TrashResponse(
    IReadOnlyList<FolderTrashSummary> Folders,
    IReadOnlyList<FileTrashSummary> Files);
