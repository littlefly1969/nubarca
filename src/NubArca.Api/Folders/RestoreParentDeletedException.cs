namespace NubArca.Api.Folders;

// Thrown by IFolderService.RestoreAsync / IFileItemService.RestoreAsync when
// the resource's parent folder is itself soft-deleted: restoring would attach
// the child to a tree that the user can't see. Mapped to 409 at the HTTP layer.
public sealed class RestoreParentDeletedException : Exception
{
    public Guid ParentFolderId { get; }

    public RestoreParentDeletedException(Guid parentFolderId)
        : base($"Parent folder '{parentFolderId}' is soft-deleted; restore the parent first.")
    {
        ParentFolderId = parentFolderId;
    }
}
