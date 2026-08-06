using NubArca.Api.Files;

namespace NubArca.Api.Folders;

// `Folders` is the full ordered set of child folders, returned only on the
// first page (no cursor); on later file pages it is empty because the client
// already has it. `Files` is one seek-paginated page. `NextCursor` is null at
// the end of the file list; `HasMore` mirrors it as a boolean.
public sealed record FolderChildrenResponse(
    Guid? FolderId,
    IReadOnlyList<FolderSummary> Folders,
    IReadOnlyList<FileSummary> Files,
    string? NextCursor = null,
    bool HasMore = false);
