namespace NubArca.Api.Folders;

// Listing projection for soft-deleted Folder rows shown in the trash UI.
// Carries the parent folder id so a client can show "originally located in
// ..." context. Deliberately omits OwnerUserId.
public sealed record FolderTrashSummary(
    Guid Id,
    string Name,
    Guid? ParentFolderId,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime DeletedAt);
